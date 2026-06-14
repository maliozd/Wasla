namespace Wasla.PrintBridge.Services;

public sealed class PrintBridgeConnectionException : Exception
{
    public PrintBridgeConnectionException(
        string userMessageKey,
        string endpointPath,
        string baseUrl,
        object[]? formatArgs = null,
        int? statusCode = null,
        Exception? innerException = null)
        : base(userMessageKey, innerException)
    {
        UserMessageKey = userMessageKey;
        FormatArgs = formatArgs ?? [];
        EndpointPath = endpointPath;
        BaseUrl = baseUrl;
        StatusCode = statusCode;
    }

    public string UserMessageKey { get; }

    public object[] FormatArgs { get; }

    public string EndpointPath { get; }

    public string BaseUrl { get; }

    public int? StatusCode { get; }

    public bool IsTokenAuthFailure => StatusCode is 401 or 403;

    public static PrintBridgeConnectionException FromResponse(
        string endpointPath,
        string baseUrl,
        int statusCode,
        string? responseBody)
    {
        var (key, args) = statusCode switch
        {
            401 or 403 => ("Connection.TokenInvalid", Array.Empty<object>()),
            404 => ("Connection.EndpointNotFound", Array.Empty<object>()),
            >= 500 => ("Connection.ServerUnavailable", Array.Empty<object>()),
            _ => ("Connection.RequestFailed", new object[] { statusCode })
        };

        var ex = new PrintBridgeConnectionException(key, endpointPath, baseUrl, args, statusCode);
        if (!string.IsNullOrWhiteSpace(responseBody))
            ex.Data["ResponseBody"] = responseBody;
        return ex;
    }

    public static PrintBridgeConnectionException SslError(string endpointPath, string baseUrl, Exception inner) =>
        new("Connection.SslError", endpointPath, baseUrl, innerException: inner);

    public static PrintBridgeConnectionException ServerUnavailable(string endpointPath, string baseUrl, Exception inner) =>
        new("Connection.ServerUnreachable", endpointPath, baseUrl, innerException: inner);
}
