using System.Reflection;
using Wasla.PrintBridge.Hosting;
using Wasla.PrintBridge.Localization;
using Wasla.PrintBridge.Models;
using Wasla.PrintBridge.Printing;
using Wasla.PrintBridge.ServiceClient;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.Setup;
using Wasla.PrintBridge.Tests.WebShell;
using Wasla.PrintBridge.UI;
using Wasla.PrintBridge.WebShell;

namespace Wasla.PrintBridge.Tests.Service;

/// <summary>
/// WAS-61: the app is a client. Nothing in it can poll, claim, print, report, hold the device token or write the
/// service's data folder, and what it shows while the service cannot be reached is localized and offers no action.
/// </summary>
[Collection(ProcessCultureCollection.Name)]
public sealed class PrintBridgeUiCompositionTests : IDisposable
{
    private static readonly Type[] EngineTypes =
    [
        typeof(PrintBridgeRuntime),
        typeof(PrintBridgeEngineHost),
        typeof(WaslaPrintBridgeClient),
        typeof(PrintBridgeSettingsStore),
        typeof(LocalPrintJobHistoryStore),
        typeof(PrintBridgeJobJournal),
        typeof(IReceiptPrinter),
        typeof(PrintBridgeAutoSetupCoordinator),
        typeof(IPrintBridgeConnectionGuard)
    ];

    private readonly CultureScope _cultureScope = new();

    public void Dispose() => _cultureScope.Dispose();

    [Fact]
    public void NoTypeOfTheApp_HoldsOrCreatesAnEnginePart()
    {
        var app = typeof(TrayApplicationContext).Assembly;
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        foreach (var type in app.GetTypes())
        {
            foreach (var field in type.GetFields(all))
                Assert.False(IsEnginePart(field.FieldType), $"{type.FullName}.{field.Name} holds {field.FieldType.Name}");
            foreach (var constructor in type.GetConstructors(all))
            {
                foreach (var parameter in constructor.GetParameters())
                    Assert.False(IsEnginePart(parameter.ParameterType), $"{type.FullName}({parameter.Name}) takes {parameter.ParameterType.Name}");
            }
        }

        Assert.DoesNotContain(app.GetReferencedAssemblies(), a => a.Name == "Microsoft.Extensions.Http");
        Assert.DoesNotContain(app.GetReferencedAssemblies(), a => a.Name == "Wasla.PrintBridge.Service");
    }

    [Fact]
    public void TheInstalledApp_TalksToTheInstalledService_AndVerifiesItThroughTheServiceControlManager()
    {
        var options = PrintBridgeServiceClientOptions.ForCurrentBuild();

        Assert.Equal("WaslaPrintBridge.Service", options.PipeName);
        var verifier = Assert.IsType<ServiceControlManagerVerifier>(options.Verifier);
        Assert.Equal("WaslaPrintBridge", verifier.ServiceName);
        Assert.False(verifier.RequiresCurrentUserOnly);
    }

    [Fact]
    public async Task UntilTheServiceAnswers_TheAppShowsItUnavailable_AndKnowsNoSettings()
    {
        // A pipe nobody serves: the app keeps trying and shows why it cannot print.
        await using var client = new PrintBridgeServiceClient(
            new PrintBridgeServiceClientOptions("WaslaPbTest.Nobody." + Guid.NewGuid().ToString("N"), new CurrentUserServerVerifier()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        client.Start();

        var status = client.GetStatus();
        Assert.Equal(PrintBridgeRuntimeIssueCode.ServiceUnavailable, status.LastIssue?.Code);
        Assert.False(status.IsRunning);
        Assert.Equal(TrayIconState.ConnectionLost, status.TrayIconState);
        Assert.False(client.Settings.HasSavedToken);
        Assert.Equal(string.Empty, client.Settings.ServerUrl);
        var refused = await Assert.ThrowsAsync<LocalizedApplicationException>(() => client.TestPrinterAsync(TestContext.Current.CancellationToken));
        Assert.Equal("RuntimeIssue.ServiceUnavailable.Detail", refused.ResourceKey);
    }

    [Theory]
    [InlineData(PrintBridgeRuntimeIssueCode.ServiceUnavailable)]
    [InlineData(PrintBridgeRuntimeIssueCode.ServiceIncompatible)]
    [InlineData(PrintBridgeRuntimeIssueCode.EngineHeldElsewhere)]
    [InlineData(PrintBridgeRuntimeIssueCode.DataFolderNotSecured)]
    public void EveryServiceState_IsExplainedInEveryLanguage_AndOffersNoAction(PrintBridgeRuntimeIssueCode code)
    {
        var issue = new PrintBridgeRuntimeIssue(code);
        var texts = new Dictionary<string, (string Title, string Detail, string Summary)>();
        foreach (var culture in SupportedCultures.All)
        {
            var service = WebShellTestSupport.Culture(culture);
            var localizer = new PrintBridgeLocalizer(service);
            var title = localizer.GetRuntimeIssueTitle(issue);
            var detail = localizer.GetRuntimeIssueDetail(issue);
            var summary = localizer.GetRuntimeIssueConnectionSummary(issue);
            Assert.False(string.IsNullOrWhiteSpace(title));
            Assert.DoesNotContain("RuntimeIssue.", title, StringComparison.Ordinal);
            Assert.DoesNotContain("RuntimeIssue.", detail, StringComparison.Ordinal);
            Assert.DoesNotContain("ConnectionSummary.", summary, StringComparison.Ordinal);
            texts[culture] = (title, detail, summary);

            var snapshot = WebShellTestSupport.Factory(service).Create(WebShellTestSupport.Status(
                BridgeServerConnectionStatus.Error,
                issue,
                isRunning: false));
            Assert.Equal(ShellConnectionStates.Error, snapshot.Connection.State);
            Assert.Equal(title, snapshot.Connection.Label);
            Assert.Equal(detail, snapshot.Connection.Detail);
            Assert.Equal(new ShellActionsView(false, false, false, false, false, false, false), snapshot.Actions);
            Assert.Equal(culture == SupportedCultures.Arabic ? "rtl" : "ltr", snapshot.Direction);
        }

        foreach (var culture in SupportedCultures.All.Where(c => c != SupportedCultures.Turkish))
            Assert.NotEqual(texts[SupportedCultures.Turkish].Detail, texts[culture].Detail);
    }

    [Fact]
    public void APrinterOnlyThisUserHas_IsExplained_WhenTheServiceCannotSeeIt()
    {
        // The service lists only machine-wide printers. When it reports the saved printer missing but this user's Windows
        // lists it (a connection added in the user's own profile), the app says what to do instead of "not found".
        foreach (var culture in SupportedCultures.All)
        {
            var service = WebShellTestSupport.Culture(culture);
            var localizer = new PrintBridgeLocalizer(service);
            var factory = new ShellSnapshotFactory(localizer, service, installedForThisUser: name => name == "POS-58");

            var userOnly = factory.Create(WebShellTestSupport.Status(printerName: "POS-58", printerHealth: PrinterHealthStatus.NotFound));
            var missing = factory.Create(WebShellTestSupport.Status(printerName: "Kitchen", printerHealth: PrinterHealthStatus.NotFound));
            var ready = factory.Create(WebShellTestSupport.Status(printerName: "POS-58", printerHealth: PrinterHealthStatus.Ready));

            Assert.Equal(localizer["Shell.Printer.UserOnly"], userOnly.Printer.Label);
            Assert.Equal("notFound", userOnly.Printer.State);
            Assert.Equal(localizer.GetPrinterHealthStatus(PrinterHealthStatus.NotFound), missing.Printer.Label);
            Assert.Equal(localizer.GetPrinterHealthStatus(PrinterHealthStatus.Ready), ready.Printer.Label);
            Assert.DoesNotContain("Shell.Printer.", userOnly.Printer.Label, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(userOnly.Strings["Shell.Printer.ServiceScope"]));
        }
    }

    [Fact]
    public void ReceiptsWaitingForTheUsersCheck_AreCountedOnTheOverview()
    {
        var service = WebShellTestSupport.Culture(SupportedCultures.Arabic);
        var status = new PrintBridgeRuntimeStatus
        {
            IsRunning = true,
            ServerConnectionStatus = BridgeServerConnectionStatus.Connected,
            PrinterHealthStatus = PrinterHealthStatus.Ready,
            UncertainJobsCount = 2
        };

        var snapshot = WebShellTestSupport.Factory(service).Create(status);

        Assert.Equal(2, snapshot.Activity.NeedsCheck);
        Assert.Equal("rtl", snapshot.Direction);
        Assert.NotEqual("Shell.Activity.NeedsCheck", snapshot.Strings["Shell.Activity.NeedsCheck"]);
    }

    [Fact]
    public void TheTraysCloseItem_SaysThatPrintingContinues_InEveryLanguage()
    {
        foreach (var culture in SupportedCultures.All)
        {
            var text = new PrintBridgeLocalizer(WebShellTestSupport.Culture(culture))["Tray.Exit"];
            Assert.Contains("(", text, StringComparison.Ordinal);
            Assert.NotEqual("Tray.Exit", text);
        }
    }

    private static bool IsEnginePart(Type type)
    {
        var element = type.IsArray ? type.GetElementType()! : type;
        if (element.IsGenericType)
            return element.GetGenericArguments().Any(IsEnginePart);
        return EngineTypes.Any(engine => engine.IsAssignableFrom(element));
    }
}
