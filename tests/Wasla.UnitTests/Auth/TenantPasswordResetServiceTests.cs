using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Auth;
using Wasla.Application.Abstractions.Email;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Email;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Services;

namespace Wasla.UnitTests.Auth;

public sealed class TenantPasswordResetServiceTests : IDisposable
{
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly TenantDbHarness _harness = new();
    private readonly CapturingEmailSender _emailSender = new();
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 6, 24, 1, 0, 0, TimeSpan.Zero));
    private readonly CapturingLogger<TenantPasswordResetService> _logger = new();

    public void Dispose()
    {
        _harness.Dispose();
    }

    [Fact]
    public async Task RequestReset_KnownActiveEmail_CreatesHashedTokenAndSensitiveEmail()
    {
        await SeedUserAsync(_tenantA, "owner@example.test", "OldPassword123!", isActive: true);
        var service = CreateService();
        string? rawToken = null;
        const string resetUrlPrefix = "http://tenant.wasla.local:5200/auth/reset-password?token=";

        var result = await service.RequestResetAsync(
            _tenantA,
            "owner@example.test",
            token =>
            {
                rawToken = token;
                return resetUrlPrefix + Uri.EscapeDataString(token);
            },
            "tr-TR",
            TestContext.Current.CancellationToken);

        Assert.Equal(TenantPasswordResetRequestStatus.EmailSent, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(rawToken));

        await using var db = await _harness.CreateAsync(_tenantA, TestContext.Current.CancellationToken);
        var tokenRow = Assert.Single(await db.PasswordResetTokens.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(TenantPasswordResetService.Sha256Base64HashLength, tokenRow.TokenHash.Length);
        Assert.Equal(TenantPasswordResetService.HashToken(rawToken!), tokenRow.TokenHash);
        Assert.NotEqual(rawToken, tokenRow.TokenHash);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddMinutes(TenantPasswordResetService.TokenExpiryMinutes), tokenRow.ExpiresAtUtc);
        Assert.Null(tokenRow.UsedAtUtc);

        var message = Assert.Single(_emailSender.Messages);
        Assert.True(message.IsSensitive);
        Assert.Contains("Wasla şifre sıfırlama", message.Subject);
        Assert.Contains(resetUrlPrefix, message.TextBody);
        Assert.Contains(Uri.EscapeDataString(rawToken!), message.TextBody);
        Assert.Contains(resetUrlPrefix, message.HtmlBody);
    }

    [Theory]
    [InlineData("unknown@example.test")]
    [InlineData("inactive@example.test")]
    public async Task RequestReset_UnknownOrInactiveEmail_ReturnsExplicitStatusAndSendsNoEmail(string email)
    {
        await SeedUserAsync(_tenantA, "inactive@example.test", "OldPassword123!", isActive: false);
        var service = CreateService();

        var result = await service.RequestResetAsync(
            _tenantA,
            email,
            token => "https://tenant.wasla.local/auth/reset-password?token=" + token,
            "en-US",
            TestContext.Current.CancellationToken);

        var expected = email.StartsWith("inactive", StringComparison.Ordinal)
            ? TenantPasswordResetRequestStatus.UserInactive
            : TenantPasswordResetRequestStatus.UserNotFound;
        Assert.Equal(expected, result.Status);
        Assert.Empty(_emailSender.Messages);

        await using var db = await _harness.CreateAsync(_tenantA, TestContext.Current.CancellationToken);
        Assert.Empty(await db.PasswordResetTokens.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RequestReset_NewRequestInvalidatesOlderActiveTokensOnly()
    {
        var user = await SeedUserAsync(_tenantA, "owner@example.test", "OldPassword123!", isActive: true);
        var now = _clock.GetUtcNow().UtcDateTime;
        var active = await AddTokenAsync(_tenantA, user.Id, TestToken(1), expiresAtUtc: now.AddMinutes(30));
        var expired = await AddTokenAsync(_tenantA, user.Id, TestToken(2), expiresAtUtc: now.AddMinutes(-1));
        var used = await AddTokenAsync(_tenantA, user.Id, TestToken(3), expiresAtUtc: now.AddMinutes(30), usedAtUtc: now.AddMinutes(-5));
        var service = CreateService();

        await service.RequestResetAsync(
            _tenantA,
            "owner@example.test",
            token => "https://tenant.wasla.local/auth/reset-password?token=" + token,
            "en-US",
            TestContext.Current.CancellationToken);

        await using var db = await _harness.CreateAsync(_tenantA, TestContext.Current.CancellationToken);
        var tokens = await db.PasswordResetTokens.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(4, tokens.Count);
        Assert.NotNull(tokens.Single(t => t.Id == active.Id).UsedAtUtc);
        Assert.Null(tokens.Single(t => t.Id == expired.Id).UsedAtUtc);
        Assert.NotNull(tokens.Single(t => t.Id == used.Id).UsedAtUtc);
        Assert.Single(tokens, t => t.UsedAtUtc is null && t.ExpiresAtUtc > now);
    }

    [Fact]
    public async Task RequestReset_EmailFailureReturnsDeliveryFailedAndInvalidatesNewTokenWithoutSensitiveLogs()
    {
        await SeedUserAsync(_tenantA, "owner@example.test", "OldPassword123!", isActive: true);
        _emailSender.ExceptionToThrow = new InvalidOperationException("SMTP unavailable");
        var service = CreateService();
        string? rawToken = null;
        string? resetUrl = null;

        var result = await service.RequestResetAsync(
            _tenantA,
            "owner@example.test",
            token =>
            {
                rawToken = token;
                resetUrl = "https://tenant.wasla.local/auth/reset-password?token=" + token;
                return resetUrl;
            },
            "en-US",
            TestContext.Current.CancellationToken);

        Assert.Equal(TenantPasswordResetRequestStatus.EmailDeliveryFailed, result.Status);

        await using var db = await _harness.CreateAsync(_tenantA, TestContext.Current.CancellationToken);
        var tokenRow = Assert.Single(await db.PasswordResetTokens.ToListAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(tokenRow.UsedAtUtc);

        var combinedLogs = string.Join(Environment.NewLine, _logger.Messages);
        Assert.Contains("Password reset email delivery failed", combinedLogs);
        Assert.Contains("FailureCategory=ConfigurationInvalid", combinedLogs);
        Assert.DoesNotContain(rawToken!, combinedLogs);
        Assert.DoesNotContain(resetUrl!, combinedLogs);
    }

    [Fact]
    public async Task RequestReset_TenantARequestCannotResolveTenantBUser()
    {
        await SeedUserAsync(_tenantB, "owner@example.test", "OldPassword123!", isActive: true);
        var service = CreateService();

        var result = await service.RequestResetAsync(
            _tenantA,
            "owner@example.test",
            token => "https://tenant-a.wasla.local/auth/reset-password?token=" + token,
            "en-US",
            TestContext.Current.CancellationToken);

        Assert.Equal(TenantPasswordResetRequestStatus.UserNotFound, result.Status);
        Assert.Empty(_emailSender.Messages);

        await using var dbA = await _harness.CreateAsync(_tenantA, TestContext.Current.CancellationToken);
        await using var dbB = await _harness.CreateAsync(_tenantB, TestContext.Current.CancellationToken);
        Assert.Empty(await dbA.PasswordResetTokens.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await dbB.PasswordResetTokens.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResetPassword_ValidTokenChangesPasswordMarksTokenUsedAndInvalidatesOtherActiveTokens()
    {
        var oldPassword = "OldPassword123!";
        var newPassword = "NewPassword123!";
        var user = await SeedUserAsync(_tenantA, "owner@example.test", oldPassword, isActive: true);
        var rawToken = TestToken(4);
        var token = await AddTokenAsync(_tenantA, user.Id, rawToken, expiresAtUtc: _clock.GetUtcNow().UtcDateTime.AddMinutes(30));
        var otherActive = await AddTokenAsync(_tenantA, user.Id, TestToken(5), expiresAtUtc: _clock.GetUtcNow().UtcDateTime.AddMinutes(30));
        var service = CreateService();

        var result = await service.ResetPasswordAsync(_tenantA, rawToken, newPassword, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        await using var db = await _harness.CreateAsync(_tenantA, TestContext.Current.CancellationToken);
        var updatedUser = await db.AppUsers.SingleAsync(u => u.Id == user.Id, TestContext.Current.CancellationToken);
        Assert.False(BCrypt.Net.BCrypt.Verify(oldPassword, updatedUser.PasswordHash));
        Assert.True(BCrypt.Net.BCrypt.Verify(newPassword, updatedUser.PasswordHash));
        Assert.NotNull(await db.PasswordResetTokens.Where(t => t.Id == token.Id).Select(t => t.UsedAtUtc).SingleAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(await db.PasswordResetTokens.Where(t => t.Id == otherActive.Id).Select(t => t.UsedAtUtc).SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResetPassword_UsedTokenCannotBeReused()
    {
        var user = await SeedUserAsync(_tenantA, "owner@example.test", "OldPassword123!", isActive: true);
        var rawToken = TestToken(6);
        await AddTokenAsync(_tenantA, user.Id, rawToken, expiresAtUtc: _clock.GetUtcNow().UtcDateTime.AddMinutes(30));
        var service = CreateService();

        var first = await service.ResetPasswordAsync(_tenantA, rawToken, "NewPassword123!", TestContext.Current.CancellationToken);
        var second = await service.ResetPasswordAsync(_tenantA, rawToken, "AnotherPassword123!", TestContext.Current.CancellationToken);

        Assert.True(first.IsSuccess);
        Assert.Equal(TenantPasswordResetOutcome.InvalidToken, second.Outcome);
    }

    [Fact]
    public async Task ResetPassword_InvalidTokensFailWithGenericInvalidToken()
    {
        var user = await SeedUserAsync(_tenantA, "owner@example.test", "OldPassword123!", isActive: true);
        var expiredToken = TestToken(7);
        await AddTokenAsync(_tenantA, user.Id, expiredToken, expiresAtUtc: _clock.GetUtcNow().UtcDateTime.AddMinutes(-1));
        var service = CreateService();

        var expired = await service.ResetPasswordAsync(_tenantA, expiredToken, "NewPassword123!", TestContext.Current.CancellationToken);
        var tampered = await service.ResetPasswordAsync(_tenantA, TestToken(8), "NewPassword123!", TestContext.Current.CancellationToken);
        var malformed = await service.ResetPasswordAsync(_tenantA, "not a token", "NewPassword123!", TestContext.Current.CancellationToken);

        Assert.Equal(TenantPasswordResetOutcome.InvalidToken, expired.Outcome);
        Assert.Equal(TenantPasswordResetOutcome.InvalidToken, tampered.Outcome);
        Assert.Equal(TenantPasswordResetOutcome.InvalidToken, malformed.Outcome);
    }

    [Fact]
    public async Task ResetPassword_CrossTenantAndInactiveUserTokensFail()
    {
        var tenantBUser = await SeedUserAsync(_tenantB, "owner@example.test", "OldPassword123!", isActive: true);
        var tenantBToken = TestToken(9);
        await AddTokenAsync(_tenantB, tenantBUser.Id, tenantBToken, expiresAtUtc: _clock.GetUtcNow().UtcDateTime.AddMinutes(30));
        var inactiveUser = await SeedUserAsync(_tenantA, "inactive@example.test", "OldPassword123!", isActive: false);
        var inactiveToken = TestToken(10);
        await AddTokenAsync(_tenantA, inactiveUser.Id, inactiveToken, expiresAtUtc: _clock.GetUtcNow().UtcDateTime.AddMinutes(30));
        var service = CreateService();

        var crossTenant = await service.ResetPasswordAsync(_tenantA, tenantBToken, "NewPassword123!", TestContext.Current.CancellationToken);
        var inactive = await service.ResetPasswordAsync(_tenantA, inactiveToken, "NewPassword123!", TestContext.Current.CancellationToken);

        Assert.Equal(TenantPasswordResetOutcome.InvalidToken, crossTenant.Outcome);
        Assert.Equal(TenantPasswordResetOutcome.InvalidToken, inactive.Outcome);
    }

    [Fact]
    public async Task ResetPassword_InvalidPasswordUsesSharedPolicyAndDoesNotConsumeToken()
    {
        var user = await SeedUserAsync(_tenantA, "owner@example.test", "OldPassword123!", isActive: true);
        var rawToken = TestToken(11);
        var token = await AddTokenAsync(_tenantA, user.Id, rawToken, expiresAtUtc: _clock.GetUtcNow().UtcDateTime.AddMinutes(30));
        var service = CreateService();

        var result = await service.ResetPasswordAsync(_tenantA, rawToken, "short", TestContext.Current.CancellationToken);

        Assert.Equal(TenantPasswordResetOutcome.InvalidPassword, result.Outcome);
        Assert.NotEmpty(result.ValidationErrors);
        await using var db = await _harness.CreateAsync(_tenantA, TestContext.Current.CancellationToken);
        Assert.Null(await db.PasswordResetTokens.Where(t => t.Id == token.Id).Select(t => t.UsedAtUtc).SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResetPassword_SaveFailureRollsBackPasswordAndTokenConsumption()
    {
        var oldPassword = "OldPassword123!";
        var user = await SeedUserAsync(_tenantA, "owner@example.test", oldPassword, isActive: true);
        var rawToken = TestToken(12);
        var token = await AddTokenAsync(_tenantA, user.Id, rawToken, expiresAtUtc: _clock.GetUtcNow().UtcDateTime.AddMinutes(30));
        _harness.ThrowOnSaveForTenant(_tenantA);
        var service = CreateService();

        var result = await service.ResetPasswordAsync(_tenantA, rawToken, "NewPassword123!", TestContext.Current.CancellationToken);

        Assert.Equal(TenantPasswordResetOutcome.InvalidToken, result.Outcome);
        _harness.DisableThrowOnSaveForTenant(_tenantA);
        await using var db = await _harness.CreateAsync(_tenantA, TestContext.Current.CancellationToken);
        var reloadedUser = await db.AppUsers.SingleAsync(u => u.Id == user.Id, TestContext.Current.CancellationToken);
        var reloadedToken = await db.PasswordResetTokens.SingleAsync(t => t.Id == token.Id, TestContext.Current.CancellationToken);
        Assert.True(BCrypt.Net.BCrypt.Verify(oldPassword, reloadedUser.PasswordHash));
        Assert.Null(reloadedToken.UsedAtUtc);
    }

    [Fact]
    public async Task SensitiveEmailBodyPreviewRemainsSuppressed()
    {
        var logger = new CapturingLogger<LogEmailSender>();
        var sender = new LogEmailSender(logger);
        const string resetUrl = "https://tenant.wasla.local/auth/reset-password?token=secret-token";

        await sender.SendAsync(new EmailMessage
        {
            ToEmail = "owner@example.test",
            Subject = "Password reset",
            HtmlBody = $"<a href=\"{resetUrl}\">Reset</a>",
            TextBody = $"Reset: {resetUrl}",
            IsSensitive = true
        }, TestContext.Current.CancellationToken);

        var line = Assert.Single(logger.Messages);
        Assert.Contains("RecipientDomain=example.test", line, StringComparison.Ordinal);
        Assert.DoesNotContain(resetUrl, line, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", line, StringComparison.Ordinal);
        Assert.DoesNotContain("owner@", line, StringComparison.Ordinal);
    }

    private TenantPasswordResetService CreateService() => new(
        _harness,
        _emailSender,
        new PasswordResetEmailFactory(new EmailTemplateRenderer(), Options.Create(new EmailOptions { FromName = "Wasla Test" })),
        new DefaultPasswordPolicy(),
        Options.Create(new EmailOptions
        {
            Provider = "Smtp",
            FromEmail = "sender@example.test",
            FromName = "Wasla Test",
            SmtpHost = "smtp.example.test",
            SmtpPort = 587,
            SmtpUsername = "sender@example.test",
            SmtpPassword = "smtp-password"
        }),
        _clock,
        _logger);

    private async Task<AppUser> SeedUserAsync(Guid tenantId, string email, string password, bool isActive)
    {
        await using var db = await _harness.CreateAsync(tenantId, TestContext.Current.CancellationToken);
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            FullName = "Owner",
            Role = UserRole.Owner,
            IsActive = isActive
        };
        db.AppUsers.Add(user);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user;
    }

    private async Task<PasswordResetToken> AddTokenAsync(
        Guid tenantId,
        Guid userId,
        string rawToken,
        DateTime expiresAtUtc,
        DateTime? usedAtUtc = null)
    {
        await using var db = await _harness.CreateAsync(tenantId, TestContext.Current.CancellationToken);
        var token = new PasswordResetToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = TenantPasswordResetService.HashToken(rawToken),
            ExpiresAtUtc = expiresAtUtc,
            UsedAtUtc = usedAtUtc,
            CreatedAtUtc = _clock.GetUtcNow().UtcDateTime
        };
        db.PasswordResetTokens.Add(token);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return token;
    }

    private static string TestToken(byte seed)
    {
        var bytes = Enumerable.Repeat(seed, TenantPasswordResetService.RawTokenByteLength).ToArray();
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private sealed class TenantDbHarness : ITenantDbContextFactory, IDisposable
    {
        private readonly Dictionary<Guid, SqliteConnection> _connections = new();
        private readonly HashSet<Guid> _throwOnSaveTenants = new();

        public async Task<TenantDbContext> CreateAsync(Guid customerId, CancellationToken ct)
        {
            if (!_connections.TryGetValue(customerId, out var connection))
            {
                connection = new SqliteConnection("Data Source=:memory:");
                await connection.OpenAsync(ct);
                _connections.Add(customerId, connection);

                await CreateSchemaAsync(connection, ct);
            }

            return CreateContext(customerId, connection);
        }

        public void ThrowOnSaveForTenant(Guid tenantId) => _throwOnSaveTenants.Add(tenantId);

        public void DisableThrowOnSaveForTenant(Guid tenantId) => _throwOnSaveTenants.Remove(tenantId);

        private TenantDbContext CreateContext(Guid tenantId, SqliteConnection connection)
        {
            var options = new DbContextOptionsBuilder<TenantDbContext>()
                .UseSqlite(connection)
                .Options;

            return _throwOnSaveTenants.Contains(tenantId)
                ? new ThrowingTenantDbContext(options)
                : new TenantDbContext(options);
        }

        private static async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken ct)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE AppUsers (
                    Id TEXT NOT NULL CONSTRAINT PK_AppUsers PRIMARY KEY,
                    Email TEXT NOT NULL,
                    PasswordHash TEXT NOT NULL,
                    FullName TEXT NOT NULL,
                    Role INTEGER NOT NULL,
                    BranchId TEXT NULL,
                    IsActive INTEGER NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    LastLoginAt TEXT NULL
                );

                CREATE UNIQUE INDEX IX_AppUsers_Email ON AppUsers (Email);

                CREATE TABLE PasswordResetTokens (
                    Id TEXT NOT NULL CONSTRAINT PK_PasswordResetTokens PRIMARY KEY,
                    UserId TEXT NOT NULL,
                    TokenHash TEXT NOT NULL,
                    ExpiresAtUtc TEXT NOT NULL,
                    UsedAtUtc TEXT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    CONSTRAINT FK_PasswordResetTokens_AppUsers_UserId FOREIGN KEY (UserId) REFERENCES AppUsers (Id) ON DELETE CASCADE
                );

                CREATE INDEX IX_PasswordResetTokens_ExpiresAtUtc ON PasswordResetTokens (ExpiresAtUtc);
                CREATE UNIQUE INDEX IX_PasswordResetTokens_TokenHash ON PasswordResetTokens (TokenHash);
                CREATE INDEX IX_PasswordResetTokens_UserId ON PasswordResetTokens (UserId);
                """;
            await command.ExecuteNonQueryAsync(ct);
        }

        public void Dispose()
        {
            foreach (var connection in _connections.Values)
                connection.Dispose();
        }
    }

    private sealed class ThrowingTenantDbContext : TenantDbContext
    {
        public ThrowingTenantDbContext(DbContextOptions<TenantDbContext> options) : base(options)
        {
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Simulated save failure");
        }
    }

    private sealed class CapturingEmailSender : IEmailSender
    {
        public List<EmailMessage> Messages { get; } = new();

        public Exception? ExceptionToThrow { get; set; }

        public Task SendAsync(EmailMessage message, CancellationToken ct)
        {
            if (ExceptionToThrow is not null)
                throw ExceptionToThrow;

            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
