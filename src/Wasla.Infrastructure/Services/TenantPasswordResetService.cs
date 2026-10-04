using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Auth;
using Wasla.Application.Abstractions.Email;
using Wasla.Domain.Entities.Customer;
using Wasla.Infrastructure.Email;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.Infrastructure.Services;

public sealed class TenantPasswordResetService : ITenantPasswordResetService
{
    public const int RawTokenByteLength = 32;
    public const int TokenExpiryMinutes = 60;
    public const int Sha256Base64HashLength = 44;

    private readonly ITenantDbContextFactory _dbFactory;
    private readonly IEmailSender _emailSender;
    private readonly PasswordResetEmailFactory _emailFactory;
    private readonly IPasswordPolicy _passwordPolicy;
    private readonly EmailOptions _emailOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TenantPasswordResetService> _logger;

    public TenantPasswordResetService(
        ITenantDbContextFactory dbFactory,
        IEmailSender emailSender,
        PasswordResetEmailFactory emailFactory,
        IPasswordPolicy passwordPolicy,
        IOptions<EmailOptions> emailOptions,
        TimeProvider timeProvider,
        ILogger<TenantPasswordResetService> logger)
    {
        _dbFactory = dbFactory;
        _emailSender = emailSender;
        _emailFactory = emailFactory;
        _passwordPolicy = passwordPolicy;
        _emailOptions = emailOptions.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<TenantPasswordResetRequestResult> RequestResetAsync(
        Guid tenantId,
        string email,
        TenantPasswordResetUrlFactory resetUrlFactory,
        string? cultureName = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(resetUrlFactory);

        if (tenantId == Guid.Empty || string.IsNullOrWhiteSpace(email))
            return TenantPasswordResetRequestResult.UserNotFound();

        try
        {
            await using var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);
            var normalizedEmail = email.Trim();
            var user = await db.AppUsers
                .FirstOrDefaultAsync(u => u.Email == normalizedEmail, ct)
                .ConfigureAwait(false);

            if (user is null)
            {
                _logger.LogInformation(
                    "Password reset request user not found for tenant {TenantId}",
                    tenantId);
                return TenantPasswordResetRequestResult.UserNotFound();
            }

            if (!user.IsActive)
            {
                _logger.LogInformation(
                    "Password reset request rejected for inactive user. TenantId={TenantId} UserId={UserId}",
                    tenantId,
                    user.Id);
                return TenantPasswordResetRequestResult.UserInactive();
            }

            var now = UtcNow();
            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

            await InvalidateActiveTokensAsync(db, user.Id, now, exceptTokenId: null, ct).ConfigureAwait(false);

            var rawToken = GenerateRawToken();
            var token = new PasswordResetToken
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                TokenHash = HashToken(rawToken),
                ExpiresAtUtc = now.AddMinutes(TokenExpiryMinutes),
                CreatedAtUtc = now
            };

            db.PasswordResetTokens.Add(token);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            var resetUrl = resetUrlFactory(rawToken);
            var message = _emailFactory.BuildPasswordResetEmail(
                user.Email,
                user.FullName,
                resetUrl,
                cultureName);

            try
            {
                await _emailSender.SendAsync(message, ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "Password reset email sent. TenantId={TenantId} UserId={UserId} Provider={Provider} SenderMatchesAuthenticatedAccount={SenderMatchesAuthenticatedAccount}",
                    tenantId,
                    user.Id,
                    _emailOptions.Provider,
                    EmailOptionsDiagnostics.SenderMatchesAuthenticatedAccount(_emailOptions));
                return TenantPasswordResetRequestResult.EmailSent();
            }
            catch (Exception ex)
            {
                var failureCategory = SmtpFailureClassifier.Classify(ex);
                token.UsedAtUtc = now;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);

                _logger.LogWarning(
                    "Password reset email delivery failed. TenantId={TenantId} UserId={UserId} FailureCategory={FailureCategory} ExceptionType={ExceptionType} SmtpHostConfigured={SmtpHostConfigured} SmtpPort={SmtpPort} SslEnabled={SslEnabled} SenderMatchesAuthenticatedAccount={SenderMatchesAuthenticatedAccount}",
                    tenantId,
                    user.Id,
                    failureCategory,
                    ex.GetType().Name,
                    !string.IsNullOrWhiteSpace(_emailOptions.SmtpHost),
                    _emailOptions.SmtpPort,
                    _emailOptions.EnableSsl,
                    EmailOptionsDiagnostics.SenderMatchesAuthenticatedAccount(_emailOptions));
                return TenantPasswordResetRequestResult.EmailDeliveryFailed();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Password reset request could not be processed for tenant {TenantId}: {ExceptionType}",
                tenantId,
                ex.GetType().Name);
            return TenantPasswordResetRequestResult.EmailDeliveryFailed();
        }
    }

    public async Task<TenantPasswordResetResult> ResetPasswordAsync(
        Guid tenantId,
        string rawToken,
        string newPassword,
        CancellationToken ct = default)
    {
        var passwordValidation = _passwordPolicy.Validate(newPassword);
        if (!passwordValidation.IsValid)
            return TenantPasswordResetResult.InvalidPassword(passwordValidation.Errors);

        if (tenantId == Guid.Empty || !IsValidRawToken(rawToken))
            return TenantPasswordResetResult.InvalidToken();

        try
        {
            await using var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);
            var now = UtcNow();
            var tokenHash = HashToken(rawToken.Trim());

            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

            var token = await db.PasswordResetTokens
                .Include(t => t.User)
                .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct)
                .ConfigureAwait(false);

            if (token is null
                || token.UsedAtUtc is not null
                || token.ExpiresAtUtc <= now
                || token.User is null
                || !token.User.IsActive)
            {
                return TenantPasswordResetResult.InvalidToken();
            }

            token.User.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
            token.UsedAtUtc = now;

            await InvalidateActiveTokensAsync(db, token.UserId, now, token.Id, ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);

            return TenantPasswordResetResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Password reset could not be completed for tenant {TenantId}: {ExceptionType}",
                tenantId,
                ex.GetType().Name);
            return TenantPasswordResetResult.InvalidToken();
        }
    }

    public static string HashToken(string rawToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        var hash = Convert.ToBase64String(bytes);

        if (hash.Length != Sha256Base64HashLength)
            throw new InvalidOperationException("Unexpected password reset token hash length.");

        return hash;
    }

    private static string GenerateRawToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(RawTokenByteLength);
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool IsValidRawToken(string rawToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
            return false;

        try
        {
            return Base64UrlDecode(rawToken.Trim()).Length == RawTokenByteLength;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '=');
        return Convert.FromBase64String(base64);
    }

    private static async Task InvalidateActiveTokensAsync(
        TenantDbContext db,
        Guid userId,
        DateTime now,
        Guid? exceptTokenId,
        CancellationToken ct)
    {
        var activeTokens = await db.PasswordResetTokens
            .Where(t => t.UserId == userId
                        && t.UsedAtUtc == null
                        && t.ExpiresAtUtc > now
                        && (exceptTokenId == null || t.Id != exceptTokenId.Value))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var activeToken in activeTokens)
            activeToken.UsedAtUtc = now;
    }

    private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;
}
