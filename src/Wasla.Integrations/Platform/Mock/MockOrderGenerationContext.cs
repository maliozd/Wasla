namespace Wasla.Integrations.Platform.Mock;

/// <summary>
/// What the mock clients generate within one scope, set by the host around a fetch: the scenario families to draw
/// orders from and the language of their text. Outside a scope the mocks generate their default orders.
/// </summary>
public static class MockOrderGenerationContext
{
    private static readonly AsyncLocal<Scope?> Current = new();

    public static bool IsActive => Current.Value is not null;

    public static IReadOnlyList<string> Scenarios => Current.Value?.Scenarios ?? Array.Empty<string>();

    public static string Culture => Current.Value?.Culture ?? "tr";

    /// <summary>
    /// Starts a scope until the returned value is disposed.
    /// </summary>
    /// <param name="scenarios">
    /// The scenario families to draw from, by name: a restaurant type of the scenario files, such as <c>Pizza</c> or
    /// <c>WaffleCrepe</c>. A name without a family is skipped; with none left the generic family is used.
    /// </param>
    /// <param name="culture">
    /// The language of the generated text: <c>tr</c>, <c>en</c>, <c>ar</c> or <c>ru</c>, or one of their culture names.
    /// The mocks have no Russian text and write English for it; anything else is Turkish.
    /// </param>
    public static IDisposable Begin(IReadOnlyList<string> scenarios, string culture)
    {
        var previous = Current.Value;
        Current.Value = new Scope(scenarios, culture);
        return new Pop(previous);
    }

    private sealed class Scope(IReadOnlyList<string> scenarios, string culture)
    {
        public IReadOnlyList<string> Scenarios { get; } = scenarios;
        public string Culture { get; } = culture;
    }

    private sealed class Pop(Scope? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
