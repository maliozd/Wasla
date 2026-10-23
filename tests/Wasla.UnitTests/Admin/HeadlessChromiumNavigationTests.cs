using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Wasla.UnitTests.Admin;

/// <summary>
/// A navigation that does not finish loading names what the page still waits for. Browser tests that time out on a CI
/// runner are diagnosed from the exception message alone, because the runner and its browser are gone afterwards.
/// </summary>
[Trait("Category", "Browser")]
public sealed class HeadlessChromiumNavigationTests
{
    [Fact]
    public async Task Navigate_ThatDoesNotFinishLoading_SaysWhereThePageIsAndWhatItWaitsFor()
    {
        var executable = HeadlessChromium.FindExecutable();
        if (executable is null)
            Assert.Skip($"No Chromium browser found; set {HeadlessChromium.BrowserVariable} to run the real-browser navigation check.");
        var ct = TestContext.Current.CancellationToken;

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.MapGet("/", () => Results.Content("<!doctype html><title>Stalled</title><p>Text</p><img src=\"/never.png\" alt=\"\">", "text/html"));
        app.MapGet("/never.png", (HttpContext http) => Task.Delay(Timeout.Infinite, http.RequestAborted));
        app.MapGet("/fails", () => Results.StatusCode(StatusCodes.Status500InternalServerError));
        await app.StartAsync(ct);
        var origin = new Uri(app.Urls.First());
        var page = new Uri(origin, "/");
        var failing = new Uri(origin, "/fails");

        await using (var browser = await HeadlessChromium.StartAsync(executable, ct))
        {
            var error = await Assert.ThrowsAsync<TimeoutException>(() => browser.NavigateAsync(page, TimeSpan.FromSeconds(2), ct));

            Assert.True(error.Message.StartsWith($"{page} did not finish loading: at /, readyState interactive", StringComparison.Ordinal), error.Message);
            Assert.True(error.Message.Contains("pending: img /never.png", StringComparison.Ordinal), error.Message);

            // An HTTP error without a body: the browser shows its own error page, which never becomes the requested URL.
            error = await Assert.ThrowsAsync<TimeoutException>(() => browser.NavigateAsync(failing, TimeSpan.FromSeconds(2), ct));

            Assert.True(error.Message.StartsWith($"{failing} did not finish loading: at chrome-error://chromewebdata/, readyState complete", StringComparison.Ordinal), error.Message);
        }

        await app.StopAsync(ct);
    }
}
