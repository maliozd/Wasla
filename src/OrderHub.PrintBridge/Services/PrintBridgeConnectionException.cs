namespace OrderHub.PrintBridge.Services;

public sealed class PrintBridgeConnectionException : Exception
{
    public PrintBridgeConnectionException(
        string userMessage,
        string endpointPath,
        string baseUrl,
        int? statusCode = null,
        Exception? innerException = null)
        : base(userMessage, innerException)
    {
        UserMessage = userMessage;
        EndpointPath = endpointPath;
        BaseUrl = baseUrl;
        StatusCode = statusCode;
    }

    public string UserMessage { get; }

    public string EndpointPath { get; }

    public string BaseUrl { get; }

    public int? StatusCode { get; }

    public static PrintBridgeConnectionException FromResponse(
        string endpointPath,
        string baseUrl,
        int statusCode,
        string? responseBody)
    {
        var userMessage = statusCode switch
        {
            401 or 403 =>
                "Print Bridge token is invalid or unauthorized. Check the token in Settings.",
            404 =>
                "Print Bridge endpoint was not found. Check Base URL and server version.",
            >= 500 =>
                "OrderHub server is unavailable. Try again later.",
            _ => $"Print Bridge request failed (HTTP {statusCode})."
        };

        var ex = new PrintBridgeConnectionException(userMessage, endpointPath, baseUrl, statusCode);
        if (!string.IsNullOrWhiteSpace(responseBody))
            ex.Data["ResponseBody"] = responseBody;
        return ex;
    }

    public static PrintBridgeConnectionException SslError(string endpointPath, string baseUrl, Exception inner) =>
        new(
            "SSL connection failed. Ensure Windows trusts the local HTTPS certificate (see docs/local-https.md).",
            endpointPath,
            baseUrl,
            innerException: inner);

    public static PrintBridgeConnectionException ServerUnavailable(string endpointPath, string baseUrl, Exception inner) =>
        new(
            "Could not reach OrderHub. Check Base URL and that the server is running.",
            endpointPath,
            baseUrl,
            innerException: inner);
}
