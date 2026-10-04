namespace Wasla.Infrastructure.Diagnostics;

public static class WaslaLogOutput
{
    public const string Template =
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} TraceId={TraceId} TenantId={TenantId}{NewLine}{Exception}";
}
