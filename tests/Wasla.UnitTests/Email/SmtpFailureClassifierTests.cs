using System.Net.Sockets;
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

namespace Wasla.UnitTests.Email;

public sealed class SmtpFailureClassifierTests
{
    [Fact]
    public void Classify_AuthenticationException_ReturnsAuthenticationFailed()
    {
        var exception = new MailKit.Security.AuthenticationException("authentication failed");

        var category = SmtpFailureClassifier.Classify(exception);

        Assert.Equal(SmtpFailureCategory.AuthenticationFailed, category);
    }

    [Fact]
    public void Classify_SocketException_ReturnsConnectionFailed()
    {
        var exception = new IOException("io failed", new SocketException((int)SocketError.ConnectionRefused));

        var category = SmtpFailureClassifier.Classify(exception);

        Assert.Equal(SmtpFailureCategory.ConnectionFailed, category);
    }

    [Fact]
    public void Classify_TimeoutException_ReturnsTimeout()
    {
        var category = SmtpFailureClassifier.Classify(new TimeoutException("timed out"));

        Assert.Equal(SmtpFailureCategory.Timeout, category);
    }

    [Fact]
    public void Classify_UnknownException_ReturnsUnknown()
    {
        var category = SmtpFailureClassifier.Classify(new ApplicationException("unknown"));

        Assert.Equal(SmtpFailureCategory.Unknown, category);
    }

    [Fact]
    public void SenderMatchDiagnostic_DoesNotExposeAddresses()
    {
        var options = new EmailOptions
        {
            FromEmail = "sender@example.test",
            SmtpUsername = "sender@example.test"
        };

        Assert.True(EmailOptionsDiagnostics.SenderMatchesAuthenticatedAccount(options));
    }

    [Fact]
    public async Task ServiceFailureLog_ContainsSafeCategoryAndNoSecrets()
    {
        const string smtpPassword = "smtp-password-that-must-not-appear";
        const string resetUrl = "https://tenant.wasla.local/auth/reset-password?token=raw-token";
        using var dbFactory = new SingleTenantDbFactory();
        await dbFactory.SeedActiveUserAsync(TestContext.Current.CancellationToken);
        var logger = new CapturingLogger<TenantPasswordResetService>();
        var service = new TenantPasswordResetService(
            dbFactory,
            new ThrowingEmailSender(new TimeoutException("timeout with raw-token")),
            new PasswordResetEmailFactory(new EmailTemplateRenderer(), Options.Create(new EmailOptions { FromName = "Wasla" })),
            new DefaultPasswordPolicy(),
            Options.Create(new EmailOptions
            {
                Provider = "Smtp",
                FromEmail = "sender@example.test",
                FromName = "Wasla",
                SmtpHost = "smtp.example.test",
                SmtpPort = 587,
                SmtpUsername = "sender@example.test",
                SmtpPassword = smtpPassword
            }),
            TimeProvider.System,
            logger);

        var result = await service.RequestResetAsync(
            Guid.NewGuid(),
            "owner@example.test",
            _ => resetUrl,
            "en-US",
            TestContext.Current.CancellationToken);

        Assert.Equal(TenantPasswordResetRequestStatus.EmailDeliveryFailed, result.Status);
        var logs = string.Join(Environment.NewLine, logger.Messages);
        Assert.Contains("FailureCategory=Timeout", logs);
        Assert.Contains("ExceptionType=TimeoutException", logs);
        Assert.DoesNotContain(smtpPassword, logs);
        Assert.DoesNotContain("raw-token", logs);
        Assert.DoesNotContain(resetUrl, logs);
        Assert.DoesNotContain("owner@example.test", logs);
        Assert.DoesNotContain("PasswordHash", logs);
    }

    private sealed class ThrowingEmailSender : IEmailSender
    {
        private readonly Exception _exception;

        public ThrowingEmailSender(Exception exception)
        {
            _exception = exception;
        }

        public Task SendAsync(EmailMessage message, CancellationToken ct)
        {
            throw _exception;
        }
    }

    private sealed class SingleTenantDbFactory : ITenantDbContextFactory, IDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");

        public SingleTenantDbFactory()
        {
            _connection.Open();
            using var command = _connection.CreateCommand();
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
                    UpdatedAt TEXT NOT NULL
                );

                CREATE TABLE PasswordResetTokens (
                    Id TEXT NOT NULL CONSTRAINT PK_PasswordResetTokens PRIMARY KEY,
                    UserId TEXT NOT NULL,
                    TokenHash TEXT NOT NULL,
                    ExpiresAtUtc TEXT NOT NULL,
                    UsedAtUtc TEXT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    CONSTRAINT FK_PasswordResetTokens_AppUsers_UserId FOREIGN KEY (UserId) REFERENCES AppUsers (Id) ON DELETE CASCADE
                );
                """;
            command.ExecuteNonQuery();
        }

        public async Task SeedActiveUserAsync(CancellationToken ct)
        {
            await using var db = await CreateAsync(Guid.NewGuid(), ct);
            db.AppUsers.Add(new AppUser
            {
                Id = Guid.NewGuid(),
                Email = "owner@example.test",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("OldPassword123!"),
                FullName = "Owner",
                Role = UserRole.Owner,
                IsActive = true
            });
            await db.SaveChangesAsync(ct);
        }

        public Task<TenantDbContext> CreateAsync(Guid customerId, CancellationToken ct)
        {
            var options = new DbContextOptionsBuilder<TenantDbContext>()
                .UseSqlite(_connection)
                .Options;
            return Task.FromResult(new TenantDbContext(options));
        }

        public void Dispose()
        {
            _connection.Dispose();
        }
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
