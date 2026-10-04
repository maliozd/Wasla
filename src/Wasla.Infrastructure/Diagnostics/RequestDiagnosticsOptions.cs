namespace Wasla.Infrastructure.Diagnostics;

public sealed class RequestDiagnosticsOptions
{
    /// <summary>
    /// Web logs one completion line. API leaves completion logging to Serilog
    /// so the request is not recorded twice.
    /// </summary>
    public bool LogRequestCompletion { get; set; } = true;
}
