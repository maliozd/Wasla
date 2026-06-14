namespace Wasla.PrintBridge.Services;

public sealed class LocalizedApplicationException : Exception
{
    public LocalizedApplicationException(string resourceKey, params object[] args)
        : base(resourceKey)
    {
        ResourceKey = resourceKey;
        Args = args;
    }

    public string ResourceKey { get; }

    public object[] Args { get; }
}
