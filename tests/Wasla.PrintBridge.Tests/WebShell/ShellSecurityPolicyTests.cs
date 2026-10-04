using System.Reflection;
using System.Text.RegularExpressions;
using Wasla.PrintBridge.WebShell;
using static Wasla.PrintBridge.Tests.WebShell.WebShellTestSupport;

namespace Wasla.PrintBridge.Tests.WebShell;

public sealed partial class ShellSecurityPolicyTests
{
    [Theory]
    [InlineData("https://shell.printbridge.invalid/index.html")]
    [InlineData("https://SHELL.printbridge.invalid/index.html")]
    [InlineData("https://shell.printbridge.invalid:443/index.html")]
    [InlineData("https://shell.printbridge.invalid/index.html#status")]
    public void Navigation_IsAllowedOnlyToTheShellDocument(string uri) =>
        Assert.True(ShellNavigationPolicy.IsAllowedNavigation(uri));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("about:blank")]
    [InlineData("https://example.com/")]
    [InlineData("https://wasla.example/print-bridge")]
    [InlineData("http://shell.printbridge.invalid/index.html")]
    [InlineData("https://shell.printbridge.invalid:8443/index.html")]
    [InlineData("https://shell.printbridge.invalid/")]
    [InlineData("https://shell.printbridge.invalid/shell.js")]
    [InlineData("https://shell.printbridge.invalid/index.html?next=https://example.com")]
    [InlineData("https://user:pass@shell.printbridge.invalid/index.html")]
    [InlineData("https://shell.printbridge.invalid.example.com/index.html")]
    [InlineData("https://example.com/shell.printbridge.invalid/index.html")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("file:///C:/ProgramData/Wasla/PrintBridge/appsettings.json")]
    [InlineData("data:text/html,<p>x</p>")]
    [InlineData("javascript:alert(1)")]
    [InlineData("wasla-printbridge://setup?code=x")]
    [InlineData("ms-settings:privacy")]
    public void Navigation_IsDeniedEverywhereElse(string? uri) =>
        Assert.False(ShellNavigationPolicy.IsAllowedNavigation(uri));

    [Theory]
    [InlineData("https://shell.printbridge.invalid/shell.css", true)]
    [InlineData("https://shell.printbridge.invalid/shell-model.js", true)]
    [InlineData("https://shell.printbridge.invalid/favicon.ico", true)]
    [InlineData("https://example.com/pixel.png", false)]
    [InlineData("https://fonts.googleapis.com/css2?family=Inter", false)]
    [InlineData("http://shell.printbridge.invalid/shell.css", false)]
    [InlineData("http://127.0.0.1:5000/api/print-bridge/health", false)]
    [InlineData("file:///C:/Users/", false)]
    [InlineData("ws://127.0.0.1:9222/devtools", false)]
    public void SubResources_AreLimitedToTheShellOrigin(string uri, bool allowed) =>
        Assert.Equal(allowed, ShellNavigationPolicy.IsAllowedResourceRequest(uri));

    [Theory]
    [InlineData("https://shell.printbridge.invalid/index.html", true)]
    [InlineData("https://shell.printbridge.invalid/other.html", false)]
    [InlineData("https://example.com/index.html", false)]
    [InlineData(null, false)]
    public void WebMessages_AreTrustedOnlyFromTheShellDocument(string? source, bool trusted) =>
        Assert.Equal(trusted, ShellNavigationPolicy.IsTrustedMessageSource(source));

    [Fact]
    public void StartUri_IsTheShellDocumentOnTheSyntheticOrigin()
    {
        Assert.Equal("https://shell.printbridge.invalid/index.html", ShellNavigationPolicy.StartUri.AbsoluteUri);
        Assert.EndsWith(".invalid", ShellNavigationPolicy.HostName, StringComparison.Ordinal);
        Assert.True(ShellNavigationPolicy.IsAllowedNavigation(ShellNavigationPolicy.StartUri.AbsoluteUri));
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("https://shell.printbridge.invalid/index.html")]
    [InlineData(null)]
    public void Popups_Permissions_Downloads_Frames_And_ExternalSchemes_AreAlwaysDenied(string? uri)
    {
        Assert.False(ShellRequestPolicy.AllowNewWindow(uri));
        Assert.False(ShellRequestPolicy.AllowDownload(uri));
        Assert.False(ShellRequestPolicy.AllowFrameNavigation(uri));
        Assert.False(ShellRequestPolicy.AllowExternalUriScheme(uri));
        foreach (var kind in new[] { "Camera", "Microphone", "Geolocation", "Notifications", "ClipboardRead", "FileReadWrite", "LocalFonts", null })
            Assert.False(ShellRequestPolicy.AllowPermission(kind));
    }

    [Fact]
    public void ReleaseProfile_DisablesDevToolsAndEveryUnneededBrowserFeature()
    {
        var release = ShellSecurityProfile.Release;

        Assert.False(release.AreDevToolsEnabled);
        Assert.False(release.AreBrowserAcceleratorKeysEnabled);
        Assert.False(release.AreDefaultContextMenusEnabled);
        Assert.False(release.AreHostObjectsAllowed);
        Assert.False(release.IsPasswordAutosaveEnabled);
        Assert.False(release.IsGeneralAutofillEnabled);
        Assert.False(release.IsStatusBarEnabled);
        Assert.False(release.AreDefaultScriptDialogsEnabled);
        Assert.False(release.IsSwipeNavigationEnabled);
        Assert.False(release.AllowExternalDrop);
        Assert.True(release.IsWebMessageEnabled);
    }

    [Fact]
    public void DevelopmentProfile_OnlyAddsDevToolsAndBrowserKeys()
    {
        var expected = ShellSecurityProfile.Release with
        {
            AreDevToolsEnabled = true,
            AreBrowserAcceleratorKeysEnabled = true
        };

        Assert.Equal(expected, ShellSecurityProfile.Development);
    }

    [Fact]
    public void BuildProfile_MatchesTheBuildConfigurationOfTheShippedHost()
    {
        var hostIsDebug = typeof(ShellSecurityProfile).Assembly
            .GetCustomAttributes<System.Diagnostics.DebuggableAttribute>()
            .Any(a => a.IsJITOptimizerDisabled);

        Assert.Equal(hostIsDebug ? ShellSecurityProfile.Development : ShellSecurityProfile.Release, ShellSecurityProfile.ForCurrentBuild);
#if !DEBUG
        Assert.False(ShellSecurityProfile.ForCurrentBuild.AreDevToolsEnabled);
#endif
    }

    [Fact]
    public void Page_DeclaresARestrictiveContentSecurityPolicy()
    {
        var html = File.ReadAllText(Path.Combine(AssetsDirectory(), "index.html"));
        var csp = CspRegex().Match(html).Groups[1].Value;
        var directives = csp.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToDictionary(d => d.Split(' ')[0], d => d, StringComparer.Ordinal);

        Assert.Equal("default-src 'none'", directives["default-src"]);
        Assert.Equal("script-src 'self'", directives["script-src"]);
        Assert.Equal("style-src 'self'", directives["style-src"]);
        Assert.Equal("connect-src 'none'", directives["connect-src"]);
        Assert.Equal("object-src 'none'", directives["object-src"]);
        Assert.Equal("frame-src 'none'", directives["frame-src"]);
        Assert.Equal("base-uri 'none'", directives["base-uri"]);
        Assert.Equal("form-action 'none'", directives["form-action"]);
        Assert.Equal("require-trusted-types-for 'script'", directives["require-trusted-types-for"]);
        Assert.DoesNotContain("unsafe", csp, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", csp, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("*", csp, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_HasNoInlineScriptHandlersOrRemoteReferences()
    {
        var html = File.ReadAllText(Path.Combine(AssetsDirectory(), "index.html"));

        Assert.All(ScriptTagRegex().Matches(html), m => Assert.Matches("src=\"[a-z-]+\\.js\"", m.Value));
        Assert.Empty(ScriptTagWithBodyRegex().Matches(html));
        Assert.DoesNotMatch(@"\son[a-z]+\s*=", html);
        Assert.DoesNotMatch("(src|href)=\"(https?:)?//", html);
        Assert.DoesNotContain("<form", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scripts_UseNoDynamicCodeStorageOrNetworkApis()
    {
        foreach (var file in new[] { "shell.js", "shell-model.js" })
        {
            var script = StripComments(File.ReadAllText(Path.Combine(AssetsDirectory(), file)));

            foreach (var forbidden in new[]
                     {
                         "eval(", "new Function", "setTimeout('", "innerHTML", "outerHTML", "insertAdjacentHTML", "document.write",
                         "localStorage", "sessionStorage", "indexedDB", "document.cookie", "fetch(", "XMLHttpRequest",
                         "WebSocket", "EventSource", "sendBeacon", "import(", "hostObjects", "window.open", "location.href"
                     })
            {
                Assert.DoesNotContain(forbidden, script, StringComparison.Ordinal);
            }

            Assert.DoesNotMatch("https?://", script);
        }
    }

    [Fact]
    public void Assets_ContainNoCredentialMaterialOrTenantIdentifiers()
    {
        var assets = Directory.GetFiles(AssetsDirectory(), "*", SearchOption.AllDirectories);
        Assert.Equal(["index.html", "shell-model.js", "shell.css", "shell.js"], assets.Select(Path.GetFileName).Order(StringComparer.Ordinal));

        foreach (var path in assets)
        {
            var text = File.ReadAllText(path);
            foreach (var word in new[] { "token", "secret", "password", "apikey", "api_key", "bearer", "authorization", "X-PrintBridge", "ServerUrl", "InstallationId", "wasla.local", "localhost" })
                Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ShellForm_AppliesTheProfileAndWiresEveryGuard()
    {
        // The native WebView2 objects cannot be created without the runtime, so this source contract keeps
        // the wiring visible to CI; the real-runtime tests exercise the same paths where a runtime exists.
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Wasla.PrintBridge", "WebShell", "PrintBridgeShellForm.cs"));

        foreach (var property in typeof(ShellSecurityProfile).GetProperties().Where(p => p.PropertyType == typeof(bool)))
        {
            if (property.Name == nameof(ShellSecurityProfile.AllowExternalDrop))
                Assert.Contains("AllowExternalDrop = _profile.AllowExternalDrop", source, StringComparison.Ordinal);
            else
                Assert.Contains($"settings.{property.Name} = _profile.{property.Name};", source, StringComparison.Ordinal);
        }

        foreach (var guard in new[]
                 {
                     "core.NavigationStarting +=", "core.FrameNavigationStarting +=", "core.NewWindowRequested +=",
                     "core.PermissionRequested +=", "core.DownloadStarting +=", "core.LaunchingExternalUriScheme +=",
                     "core.BasicAuthenticationRequested +=", "core.WebResourceRequested +=", "core.ContextMenuRequested +=",
                     "core.WebMessageReceived +=", "CoreWebView2HostResourceAccessKind.Deny", "AddWebResourceRequestedFilter(\"*\"",
                     "CoreWebView2PermissionState.Deny", "ShellPaths.UserDataDirectory", "_bridge.HandleWebMessage(e.Source, raw)"
                 })
        {
            Assert.Contains(guard, source, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("AddHostObjectToScript", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecuteScriptAsync", source, StringComparison.Ordinal);
    }

    private static string StripComments(string script) =>
        BlockCommentRegex().Replace(LineCommentRegex().Replace(script, string.Empty), string.Empty);

    [GeneratedRegex("http-equiv=\"Content-Security-Policy\" content=\"([^\"]+)\"")]
    private static partial Regex CspRegex();

    [GeneratedRegex("<script[^>]*>")]
    private static partial Regex ScriptTagRegex();

    [GeneratedRegex(@"<script[^>]*>\s*[^<\s][^<]*</script>")]
    private static partial Regex ScriptTagWithBodyRegex();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockCommentRegex();

    [GeneratedRegex(@"(?m)^\s*//.*$")]
    private static partial Regex LineCommentRegex();
}
