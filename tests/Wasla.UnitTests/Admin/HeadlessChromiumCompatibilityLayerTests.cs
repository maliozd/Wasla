using System.Diagnostics;

namespace Wasla.UnitTests.Admin;

/// <summary>
/// The headless-browser driver started from a shell that sets a Windows application-compatibility layer (WAS-83). Edge
/// inheriting <c>__COMPAT_LAYER</c> relaunches itself and the started process exits with code 0 within about 40 ms, so
/// every browser test failed at startup and the relaunched browser outlived cleanup. These tests set the variable for
/// the whole test process, so the collection never runs in parallel with other tests.
/// </summary>
[Collection(nameof(CompatibilityLayerCollection))]
public sealed class HeadlessChromiumCompatibilityLayerTests
{
    private const string Layer = "DetectorsAppHealth";

    [Fact]
    public async Task Start_RemovesAnInheritedCompatibilityLayer_FromTheBrowserEnvironmentOnly()
    {
        using var inherited = new InheritedVariable(HeadlessChromium.CompatibilityLayerVariable, Layer);
        ProcessStartInfo? launched = null;

        await Assert.ThrowsAsync<InvalidOperationException>(() => HeadlessChromium.StartAsync(
            "browser.exe", HeadlessChromium.StartupTimeout,
            start =>
            {
                launched = start;
                return null;
            },
            TestContext.Current.CancellationToken));

        Assert.NotNull(launched);
        Assert.False(launched.Environment.ContainsKey(HeadlessChromium.CompatibilityLayerVariable));
        Assert.Contains("--remote-debugging-port=0", launched.ArgumentList);
        Assert.Equal(Layer, Environment.GetEnvironmentVariable(HeadlessChromium.CompatibilityLayerVariable));
    }

    [Fact]
    public async Task Start_WithAnInheritedCompatibilityLayer_ConnectsToTheStartedBrowser()
    {
        var executable = HeadlessChromium.FindExecutable();
        if (executable is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the real-browser startup check.");
        using var inherited = new InheritedVariable(HeadlessChromium.CompatibilityLayerVariable, Layer);
        var ct = TestContext.Current.CancellationToken;

        await using var browser = await HeadlessChromium.StartAsync(executable, ct);

        Assert.Equal("about:blank", await browser.EvaluateAsync<string>("location.href", ct));
    }

    /// <summary>Sets an environment variable of the test process and restores its previous value.</summary>
    private sealed class InheritedVariable : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public InheritedVariable(string name, string value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}

[CollectionDefinition(nameof(CompatibilityLayerCollection), DisableParallelization = true)]
public sealed class CompatibilityLayerCollection;
