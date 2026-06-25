using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Email;
using Wasla.Infrastructure.Email;

namespace Wasla.UnitTests.Email;

public sealed class LogEmailSenderTests
{
    [Fact]
    public async Task SendAsync_NormalEmail_LogsBodyPreview()
    {
        var logger = new CapturingLogger<LogEmailSender>();
        var sender = new LogEmailSender(logger);

        await sender.SendAsync(new EmailMessage
        {
            ToEmail = "owner@example.test",
            Subject = "Normal message",
            HtmlBody = "<p>Hello</p>",
            TextBody = "Hello normal preview"
        }, CancellationToken.None);

        var line = Assert.Single(logger.Messages);
        Assert.Contains("Preview=Hello normal preview", line);
    }

    [Fact]
    public async Task SendAsync_SensitiveEmail_SuppressesBodyPreview()
    {
        var logger = new CapturingLogger<LogEmailSender>();
        var sender = new LogEmailSender(logger);
        const string resetUrl = "https://tenant.wasla.local:5200/auth/reset-password?token=raw-token";

        await sender.SendAsync(new EmailMessage
        {
            ToEmail = "owner@example.test",
            Subject = "Password reset",
            HtmlBody = $"<a href=\"{resetUrl}\">Reset</a>",
            TextBody = $"Reset link: {resetUrl}",
            IsSensitive = true
        }, CancellationToken.None);

        var line = Assert.Single(logger.Messages);
        Assert.Contains("BodyPreviewSuppressed=true", line);
        Assert.DoesNotContain(resetUrl, line);
        Assert.DoesNotContain("raw-token", line);
        Assert.DoesNotContain("Reset link", line);
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
