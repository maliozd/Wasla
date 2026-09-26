namespace Wasla.Infrastructure.Platform.Mock;

internal static class MockOrderGenerationContext
{
    private static readonly AsyncLocal<Scope?> Current = new();

    public static bool IsActive => Current.Value is not null;

    public static IReadOnlyList<string> Codes => Current.Value?.Codes ?? Array.Empty<string>();

    public static string Culture => Current.Value?.Culture ?? "tr";

    public static IDisposable Begin(IReadOnlyList<string> codes, string culture)
    {
        var previous = Current.Value;
        Current.Value = new Scope(codes, culture);
        return new Pop(previous);
    }

    private sealed class Scope(IReadOnlyList<string> codes, string culture)
    {
        public IReadOnlyList<string> Codes { get; } = codes;
        public string Culture { get; } = culture;
    }

    private sealed class Pop(Scope? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
