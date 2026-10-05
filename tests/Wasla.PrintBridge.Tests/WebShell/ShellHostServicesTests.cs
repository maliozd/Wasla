using System.Reflection;
using System.Text.Json.Nodes;
using Wasla.PrintBridge.Configuration;
using Wasla.PrintBridge.Services;
using Wasla.PrintBridge.WebShell;

namespace Wasla.PrintBridge.Tests.WebShell;

/// <summary>Printer persistence, printer discovery and the log folder, against an isolated temporary data root.</summary>
[Collection(PrintBridgeDataRootCollection.Name)]
public sealed class ShellHostServicesTests : IDisposable
{
    private const string FakeToken = "test-token-not-a-real-credential";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "wasla-pb-shell-host-tests", Guid.NewGuid().ToString("N"));
    private readonly IDisposable _rootScope;

    public ShellHostServicesTests()
    {
        Directory.CreateDirectory(_root);
        _rootScope = PrintBridgePaths.UseRootForTests(_root);
    }

    public void Dispose()
    {
        _rootScope.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void SavingAPrinter_PersistsOnlyThePrinterAndKeepsEveryOtherSetting()
    {
        var (store, holder) = Configured();
        var before = holder.Bridge;

        Assert.True(new ShellPrinterSettings(store, holder).TrySavePrinter(" Microsoft Print to PDF ", out var errorKey));

        Assert.Null(errorKey);
        var saved = store.Load();
        Assert.Equal("Microsoft Print to PDF", saved.PrintBridge.PrinterName);
        Assert.Equal("WindowsPrinter", saved.PrintBridge.PrinterMode);
        Assert.Equal(FakeToken, saved.OrderHub.AgentToken);
        Assert.Equal("http://print-bridge.test", saved.OrderHub.ServerUrl);
        Assert.True(saved.PrintBridge.DryRun);
        Assert.Equal(7, saved.PrintBridge.IdlePollIntervalSeconds);
        Assert.Equal(2, saved.PrintBridge.BusyPollIntervalSeconds);
        Assert.Equal(20, saved.PrintBridge.ErrorPollIntervalSeconds);
        Assert.Equal(before.InstallationId, saved.PrintBridge.InstallationId);
        Assert.Equal("Kasa 1", saved.PrintBridge.DisplayName);
        Assert.Equal("ar-SA", saved.Ui.Language);

        // The running engine sees the new printer through a new options object; a job that already captured the
        // previous options keeps printing to the printer it was claimed for.
        Assert.Equal("Microsoft Print to PDF", holder.Bridge.PrinterName);
        Assert.NotSame(before, holder.Bridge);
        Assert.Equal("POS-58", before.PrinterName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankPrinter_IsRejectedAndNothingIsWritten(string name)
    {
        var (store, holder) = Configured();
        var fileBefore = File.ReadAllText(PrintBridgePaths.ProgramDataConfigPath);

        Assert.False(new ShellPrinterSettings(store, holder).TrySavePrinter(name, out var errorKey));

        Assert.Equal("Validation.PrinterRequired", errorKey);
        Assert.Equal(fileBefore, File.ReadAllText(PrintBridgePaths.ProgramDataConfigPath));
        Assert.Equal("POS-58", holder.Bridge.PrinterName);
    }

    [Fact]
    public void InvalidExistingAdvancedSettings_BlockTheSaveInsteadOfBeingRewritten()
    {
        var (store, holder) = Configured(idlePoll: 0);

        Assert.False(new ShellPrinterSettings(store, holder).TrySavePrinter("Microsoft Print to PDF", out var errorKey));

        Assert.Equal("Validation.InvalidPollingIntervals", errorKey);
        Assert.Equal("POS-58", store.Load().PrintBridge.PrinterName);
    }

    [Fact]
    public void SettingsFileFromAnEarlierVersion_StillLoadsAndKeepsItsValuesWhenThePrinterChanges()
    {
        // The shape written before the WebView2 app existed: no new sections or fields are required.
        PrintBridgePaths.EnsureProgramDataDirectories();
        File.WriteAllText(PrintBridgePaths.ProgramDataConfigPath, """
            {
              "OrderHub": { "ServerUrl": "http://print-bridge.test/", "AgentToken": "test-token-not-a-real-credential" },
              "PrintBridge": { "PrinterName": "POS-58", "DryRun": true, "BridgeName": "Kasa 1" },
              "Ui": { "Language": "ru-RU" }
            }
            """);
        var store = new PrintBridgeSettingsStore();
        var document = store.Load();
        var holder = new PrintBridgeSettingsHolder();
        holder.Replace(document.OrderHub, document.PrintBridge, document.Ui);

        Assert.True(new ShellPrinterSettings(store, holder).TrySavePrinter("Microsoft Print to PDF", out _));

        var root = JsonNode.Parse(File.ReadAllText(PrintBridgePaths.ProgramDataConfigPath))!.AsObject();
        Assert.Equal(["OrderHub", "PrintBridge", "Ui"], root.Select(p => p.Key));
        var saved = store.Load();
        Assert.Equal("Microsoft Print to PDF", saved.PrintBridge.PrinterName);
        Assert.Equal(FakeToken, saved.OrderHub.AgentToken);
        Assert.Equal("http://print-bridge.test", saved.OrderHub.ServerUrl);
        Assert.Equal("Kasa 1", saved.PrintBridge.DisplayName);
        Assert.True(saved.PrintBridge.DryRun);
        Assert.Equal("ru-RU", saved.Ui.Language);
    }

    [Fact]
    public async Task PrinterCatalog_ListsWindowsPrintersOnceEach_InAStableOrder()
    {
        var calls = 0;
        var catalog = new WindowsPrinterCatalog(() =>
        {
            Interlocked.Increment(ref calls);
            return ["POS-58", "", "  ", "Microsoft Print to PDF", "pos-58", "Kitchen"];
        });

        Assert.False(catalog.HasDiscovered);
        Assert.Empty(catalog.Installed);
        Assert.False(catalog.IsInstalled("POS-58"));

        var listed = await catalog.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.True(catalog.HasDiscovered);
        Assert.Equal(1, calls);
        Assert.Equal(3, listed.Count);
        Assert.Equal(listed, catalog.Installed);
        Assert.Equal(listed.Order(StringComparer.CurrentCultureIgnoreCase), listed);
        Assert.True(catalog.IsInstalled("pos-58"));
        Assert.False(catalog.IsInstalled("POS-5"));
        Assert.False(catalog.IsInstalled(@"\\print-server\POS-58"));
    }

    [Fact]
    public void LogFolder_IsTheKnownProgramDataFolder_AndTakesNoPath()
    {
        Assert.Equal(PrintBridgePaths.ProgramDataLogDirectory, ShellLogFolder.Path);
        Assert.StartsWith(_root, ShellLogFolder.Path, StringComparison.OrdinalIgnoreCase);

        var methods = typeof(ShellLogFolder).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        Assert.All(methods.Where(m => !m.IsSpecialName), m => Assert.Empty(m.GetParameters()));
        Assert.Null(typeof(ShellLogFolder).GetProperty(nameof(ShellLogFolder.Path))!.SetMethod);
    }

    [Fact]
    public void NativeActions_TakeNothingFromThePage()
    {
        // No path, URL, token or file name can travel from the renderer into a privileged action, and the
        // connection dialog returns only a result with a localized message.
        Assert.All(typeof(IShellNativeActions).GetMethods(), m => Assert.Empty(m.GetParameters()));
        Assert.Equal(
            ["ConfirmConnectionResetAsync", "ConfirmEnableTestModeAsync", "OpenClassicWindow", "OpenLogFolder", "RunConnectionSetupAsync"],
            typeof(IShellNativeActions).GetMethods().Select(m => m.Name).Order());
        Assert.Equal(
            ["Field", "IsConnected", "Message", "Outcome"],
            typeof(ShellConnectionSetupResult).GetProperties().Select(p => p.Name).Where(n => n != "EqualityContract").Order());
    }

    [Fact]
    public void SavingOperationalSettings_ChangesOnlyTestModeAndThePollIntervals()
    {
        var (store, holder) = Configured();
        var before = holder.Bridge;

        Assert.True(new ShellOperationalSettings(store, holder).TrySave(new ShellOperationalSettingsChange(false, 30, 3, 60), out var errorKey));

        Assert.Null(errorKey);
        var saved = store.Load();
        Assert.False(saved.PrintBridge.DryRun);
        Assert.Equal(30, saved.PrintBridge.IdlePollIntervalSeconds);
        Assert.Equal(3, saved.PrintBridge.BusyPollIntervalSeconds);
        Assert.Equal(60, saved.PrintBridge.ErrorPollIntervalSeconds);
        Assert.Equal(3, saved.PrintBridge.MaxJobsPerPoll);
        Assert.Equal("POS-58", saved.PrintBridge.PrinterName);
        Assert.Equal(FakeToken, saved.OrderHub.AgentToken);
        Assert.Equal("http://print-bridge.test", saved.OrderHub.ServerUrl);
        Assert.Equal(before.InstallationId, saved.PrintBridge.InstallationId);
        Assert.Equal("Kasa 1", saved.PrintBridge.DisplayName);
        Assert.Equal("ar-SA", saved.Ui.Language);

        // The engine reads these for every poll and job: the in-memory settings are the saved ones at once.
        Assert.Equal(30, holder.Bridge.IdlePollIntervalSeconds);
        Assert.False(holder.Bridge.DryRun);
        Assert.NotSame(before, holder.Bridge);
    }

    [Theory]
    [InlineData(0, 1, 15)]
    [InlineData(301, 1, 15)]
    [InlineData(5, 0, 15)]
    [InlineData(5, 61, 15)]
    [InlineData(5, 1, 0)]
    [InlineData(5, 1, 301)]
    public void OutOfRangeOperationalSettings_AreRejected_AndNothingIsWritten(int idle, int busy, int error)
    {
        var (store, holder) = Configured();
        var fileBefore = File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath);
        var bridgeBefore = holder.Bridge;

        Assert.False(new ShellOperationalSettings(store, holder).TrySave(new ShellOperationalSettingsChange(false, idle, busy, error), out var errorKey));

        Assert.Equal("Validation.InvalidPollingIntervals", errorKey);
        Assert.Equal(fileBefore, File.ReadAllBytes(PrintBridgePaths.ProgramDataConfigPath));
        Assert.Same(bridgeBefore, holder.Bridge);
    }

    [Fact]
    public void OperationalSettings_ThatCannotBeWritten_LeaveTheEngineOnTheOldValues()
    {
        var (store, holder) = Configured();
        var bridgeBefore = holder.Bridge;
        File.SetAttributes(PrintBridgePaths.ProgramDataConfigPath, FileAttributes.ReadOnly);
        try
        {
            Assert.ThrowsAny<Exception>(() =>
                new ShellOperationalSettings(store, holder).TrySave(new ShellOperationalSettingsChange(false, 30, 3, 60), out _));
        }
        finally
        {
            File.SetAttributes(PrintBridgePaths.ProgramDataConfigPath, FileAttributes.Normal);
        }

        Assert.Same(bridgeBefore, holder.Bridge);
        Assert.Equal(7, store.Load().PrintBridge.IdlePollIntervalSeconds);
    }

    private static (PrintBridgeSettingsStore Store, PrintBridgeSettingsHolder Holder) Configured(int idlePoll = 7)
    {
        var store = new PrintBridgeSettingsStore();
        var document = store.Load();
        document.OrderHub.ServerUrl = "http://print-bridge.test";
        document.OrderHub.AgentToken = FakeToken;
        document.PrintBridge.PrinterName = "POS-58";
        document.PrintBridge.DisplayName = "Kasa 1";
        document.PrintBridge.DryRun = true;
        document.PrintBridge.IdlePollIntervalSeconds = idlePoll;
        document.PrintBridge.BusyPollIntervalSeconds = 2;
        document.PrintBridge.ErrorPollIntervalSeconds = 20;
        document.Ui.Language = "ar-SA";
        store.Save(document);

        var holder = new PrintBridgeSettingsHolder();
        holder.Replace(document.OrderHub, document.PrintBridge, document.Ui);
        return (store, holder);
    }
}
