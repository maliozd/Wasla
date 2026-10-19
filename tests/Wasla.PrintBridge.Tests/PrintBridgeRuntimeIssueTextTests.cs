using Wasla.PrintBridge.Models;

namespace Wasla.PrintBridge.Tests;

/// <summary>The texts the engine's connection issues resolve to.</summary>
public sealed class PrintBridgeRuntimeIssueTextTests
{
    [Fact]
    public void ReconnectRequiredIssue_UsesLifecycleTitleAndGenericConnectionSummary()
    {
        var issue = new PrintBridgeRuntimeIssue(PrintBridgeRuntimeIssueCode.ReconnectRequired);
        var root = PrintBridgeRuntimeLifecycleTestsHelpers.FindRepositoryRoot();

        Assert.Equal("RuntimeIssue.ReconnectRequired.Title", issue.EffectiveTitleResourceKey);
        Assert.Equal("ConnectionSummary.NotConnected", issue.EffectiveConnectionSummaryResourceKey);
        Assert.Equal("RuntimeIssue.ReconnectRequired.Detail", issue.EffectiveDetailResourceKey);

        var title = PrintBridgeRuntimeLifecycleTestsHelpers.ReadResourceValue(
            root,
            "PrintBridgeResources.tr-TR.resx",
            issue.EffectiveTitleResourceKey);
        var summary = PrintBridgeRuntimeLifecycleTestsHelpers.ReadResourceValue(
            root,
            "PrintBridgeResources.tr-TR.resx",
            issue.EffectiveConnectionSummaryResourceKey);
        var detail = PrintBridgeRuntimeLifecycleTestsHelpers.ReadResourceValue(
            root,
            "PrintBridgeResources.tr-TR.resx",
            issue.EffectiveDetailResourceKey);

        Assert.Equal("Yeniden bağlantı gerekli", title);
        Assert.Equal("Bağlı değil", summary);
        Assert.Contains("Geçersiz token", detail, StringComparison.Ordinal);
        Assert.NotEqual(title, summary);
        Assert.DoesNotContain("Bu Print Bridge", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledByAdminIssue_UsesDisabledSummary_NotLifecycleDetail()
    {
        var issue = new PrintBridgeRuntimeIssue(PrintBridgeRuntimeIssueCode.DisabledByAdmin);
        var root = PrintBridgeRuntimeLifecycleTestsHelpers.FindRepositoryRoot();

        Assert.Equal("ConnectionSummary.Disabled", issue.EffectiveConnectionSummaryResourceKey);

        var summary = PrintBridgeRuntimeLifecycleTestsHelpers.ReadResourceValue(
            root,
            "PrintBridgeResources.tr-TR.resx",
            issue.EffectiveConnectionSummaryResourceKey);
        var detail = PrintBridgeRuntimeLifecycleTestsHelpers.ReadResourceValue(
            root,
            "PrintBridgeResources.tr-TR.resx",
            issue.EffectiveDetailResourceKey);

        Assert.Equal("Devre dışı", summary);
        Assert.Contains("Wasla Web", detail, StringComparison.Ordinal);
        Assert.True(detail.Length > summary.Length);
    }

    [Fact]
    public void ResetFollowUpCopy_PointsToWebSetupPage_NotDesktopSetupCodeEntry()
    {
        var root = PrintBridgeRuntimeLifecycleTestsHelpers.FindRepositoryRoot();
        var resourcesDir = Path.Combine(root, "src", "Wasla.PrintBridge", "Resources");

        var tr = PrintBridgeRuntimeLifecycleTestsHelpers.ReadResourceValue(
            root,
            "PrintBridgeResources.tr-TR.resx",
            "RuntimeIssue.ManualReset.Detail");
        var en = PrintBridgeRuntimeLifecycleTestsHelpers.ReadResourceValue(
            root,
            "PrintBridgeResources.en-US.resx",
            "RuntimeIssue.ManualReset.Detail");

        Assert.Contains("Wasla Web", tr, StringComparison.Ordinal);
        Assert.Contains("kurulum sayfas", tr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("buraya gir", tr, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("Wasla Web panel", en, StringComparison.Ordinal);
        Assert.Contains("Print Bridge setup page", en, StringComparison.Ordinal);
        Assert.DoesNotContain("enter it here", en, StringComparison.OrdinalIgnoreCase);

        foreach (var culture in new[]
                 {
                     "PrintBridgeResources.resx",
                     "PrintBridgeResources.tr-TR.resx",
                     "PrintBridgeResources.en-US.resx",
                     "PrintBridgeResources.ar-SA.resx",
                     "PrintBridgeResources.ru-RU.resx"
                 })
        {
            var content = File.ReadAllText(Path.Combine(resourcesDir, culture));
            Assert.Contains("RuntimeIssue.ManualReset.Detail", content);
        }
    }

    [Fact]
    public void TokenRevokedDetail_PointsToWebSetupPage()
    {
        var root = PrintBridgeRuntimeLifecycleTestsHelpers.FindRepositoryRoot();
        var detail = PrintBridgeRuntimeLifecycleTestsHelpers.ReadResourceValue(
            root,
            "PrintBridgeResources.tr-TR.resx",
            "RuntimeIssue.ReconnectRequired.TokenRevoked.Detail");

        Assert.Contains("Wasla Web", detail, StringComparison.Ordinal);
        Assert.Contains("kurulum sayfas", detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("buraya gir", detail, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class PrintBridgeRuntimeLifecycleTestsHelpers
{
    public static string FindRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (Directory.Exists(Path.Combine(current, "src", "Wasla.PrintBridge", "Resources")))
                return current;

            var parent = Directory.GetParent(current);
            if (parent is null)
                break;
            current = parent.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    public static string ReadResourceValue(string root, string fileName, string key)
    {
        var path = Path.Combine(root, "src", "Wasla.PrintBridge", "Resources", fileName);
        var doc = System.Xml.Linq.XDocument.Load(path);
        return doc.Root?
            .Elements("data")
            .FirstOrDefault(e => string.Equals((string?)e.Attribute("name"), key, StringComparison.Ordinal))
            ?.Element("value")
            ?.Value ?? string.Empty;
    }
}
