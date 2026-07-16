using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.UI;

namespace Wasla.PrintBridge.Tests;

public sealed class PrintBridgeConnectionFieldSyncTests
{
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, true, true)]
    public void ShouldPreserveAgentTokenInput_RespectsEditingAndSaveState(
        bool isSavingConnection,
        bool isTokenFieldFocused,
        bool userEditedAgentToken,
        bool expected)
    {
        var actual = PrintBridgeConnectionFieldSync.ShouldPreserveAgentTokenInput(
            isSavingConnection,
            isTokenFieldFocused,
            userEditedAgentToken);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    public void ShouldTreatAgentTokenTextChangeAsUserEdit_IgnoresProgrammaticSync(
        bool isSyncingConnectionFields,
        bool isTokenFieldFocused,
        bool expected)
    {
        var actual = PrintBridgeConnectionFieldSync.ShouldTreatAgentTokenTextChangeAsUserEdit(
            isSyncingConnectionFields,
            isTokenFieldFocused);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    public void ShouldClearConnectionStatusOnFieldTextChange_IgnoresProgrammaticSync(
        bool isSyncingConnectionFields,
        bool isFieldFocused,
        bool expected)
    {
        var actual = PrintBridgeConnectionFieldSync.ShouldClearConnectionStatusOnFieldTextChange(
            isSyncingConnectionFields,
            isFieldFocused);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ProgrammaticSync_DoesNotMarkUserEdited_OrClearStatus()
    {
        Assert.False(PrintBridgeConnectionFieldSync.ShouldTreatAgentTokenTextChangeAsUserEdit(
            isSyncingConnectionFields: true,
            isTokenFieldFocused: true));
        Assert.False(PrintBridgeConnectionFieldSync.ShouldClearConnectionStatusOnFieldTextChange(
            isSyncingConnectionFields: true,
            isFieldFocused: true));
    }

    [Fact]
    public void FocusedUserPaste_MarksUserEdited_AndPreservesInput()
    {
        Assert.True(PrintBridgeConnectionFieldSync.ShouldTreatAgentTokenTextChangeAsUserEdit(
            isSyncingConnectionFields: false,
            isTokenFieldFocused: true));
        Assert.True(PrintBridgeConnectionFieldSync.ShouldPreserveAgentTokenInput(
            isSavingConnection: false,
            isTokenFieldFocused: true,
            userEditedAgentToken: true));
    }

    [Fact]
    public void AfterSuccessfulSaveOrTest_UserEditGuardResets()
    {
        Assert.False(PrintBridgeConnectionFieldSync.ShouldPreserveAgentTokenInput(
            isSavingConnection: false,
            isTokenFieldFocused: false,
            userEditedAgentToken: false));
    }

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
