using System.Net.Sockets;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace Wasla.Infrastructure.Email;

public enum SmtpFailureCategory
{
    AuthenticationFailed = 0,
    ConnectionFailed = 1,
    TlsNegotiationFailed = 2,
    SenderRejected = 3,
    RecipientRejected = 4,
    Timeout = 5,
    ConfigurationInvalid = 6,
    Unknown = 7
}

public static class SmtpFailureClassifier
{
    public static SmtpFailureCategory Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is InvalidOperationException)
            return SmtpFailureCategory.ConfigurationInvalid;

        if (exception is TimeoutException or OperationCanceledException)
            return SmtpFailureCategory.Timeout;

        if (exception is AuthenticationException)
            return SmtpFailureCategory.AuthenticationFailed;

        if (exception is SmtpCommandException commandException)
            return ClassifySmtpCommand(commandException);

        if (exception is SmtpProtocolException)
            return SmtpFailureCategory.ConnectionFailed;

        if (ContainsExceptionType(exception, "SslHandshake"))
            return SmtpFailureCategory.TlsNegotiationFailed;

        if (ContainsException<SocketException>(exception) || exception is IOException)
            return SmtpFailureCategory.ConnectionFailed;

        return SmtpFailureCategory.Unknown;
    }

    private static SmtpFailureCategory ClassifySmtpCommand(SmtpCommandException exception)
    {
        var name = exception.ErrorCode.ToString();
        if (name.Contains("Sender", StringComparison.OrdinalIgnoreCase))
            return SmtpFailureCategory.SenderRejected;

        if (name.Contains("Recipient", StringComparison.OrdinalIgnoreCase))
            return SmtpFailureCategory.RecipientRejected;

        if (name.Contains("Authentication", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Credentials", StringComparison.OrdinalIgnoreCase))
            return SmtpFailureCategory.AuthenticationFailed;

        return SmtpFailureCategory.Unknown;
    }

    private static bool ContainsException<TException>(Exception exception)
        where TException : Exception
    {
        var current = exception;
        while (current is not null)
        {
            if (current is TException)
                return true;

            current = current.InnerException;
        }

        return false;
    }

    private static bool ContainsExceptionType(Exception exception, string typeName)
    {
        var current = exception;
        while (current is not null)
        {
            if (current.GetType().Name.Contains(typeName, StringComparison.OrdinalIgnoreCase))
                return true;

            current = current.InnerException;
        }

        return false;
    }
}
