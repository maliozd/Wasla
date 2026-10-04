using Wasla.Application.Abstractions.Printing;

namespace Wasla.Web.PrintBridge;

public static class PrintBridgeContext
{
    public const string HttpContextItemKey = "PrintBridgeAuth";

    public static PrintBridgeAuthContext? Get(HttpContext httpContext)
    {
        if (httpContext.Items.TryGetValue(HttpContextItemKey, out var value) && value is PrintBridgeAuthContext ctx)
            return ctx;
        return null;
    }

    public static void Set(HttpContext httpContext, PrintBridgeAuthContext context)
    {
        httpContext.Items[HttpContextItemKey] = context;
    }
}
