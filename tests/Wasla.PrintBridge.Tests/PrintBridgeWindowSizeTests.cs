using Wasla.PrintBridge.UI;



namespace Wasla.PrintBridge.Tests;



public sealed class PrintBridgeWindowSizeTests

{

    [Fact]

    public void Defaults_MatchStableRecommendedClientSize()

    {

        Assert.Equal(1220, PrintBridgeWindowSize.DefaultClientWidth);

        Assert.Equal(740, PrintBridgeWindowSize.DefaultClientHeight);

        Assert.Equal(1160, PrintBridgeWindowSize.MinimumClientWidth);

        Assert.Equal(680, PrintBridgeWindowSize.MinimumClientHeight);

        Assert.True(PrintBridgeWindowSize.DefaultClientWidth >= PrintBridgeWindowSize.MinimumClientWidth);

        Assert.True(PrintBridgeWindowSize.DefaultClientHeight >= PrintBridgeWindowSize.MinimumClientHeight);

        Assert.InRange(PrintBridgeWindowSize.DefaultClientWidth, 1180, 1240);

        Assert.InRange(PrintBridgeWindowSize.MinimumClientWidth, 1120, 1180);

    }



    [Fact]

    public void ResolveStartupOuterSize_UsesDefaultOuterWhenNothingSaved()

    {

        var (width, height) = PrintBridgeWindowSize.ResolveStartupOuterSize(null, null);



        Assert.Equal(PrintBridgeWindowSize.DefaultOuterWidth, width);

        Assert.Equal(PrintBridgeWindowSize.DefaultOuterHeight, height);

    }



    [Theory]

    [InlineData(800, 500)]

    [InlineData(1100, 700)]

    [InlineData(1, 1)]

    public void ResolveStartupOuterSize_ClampsTooSmallSavedBoundsToMinimumOuter(int savedWidth, int savedHeight)

    {

        var (width, height) = PrintBridgeWindowSize.ResolveStartupOuterSize(savedWidth, savedHeight);



        Assert.Equal(PrintBridgeWindowSize.MinimumOuterWidth, width);

        Assert.Equal(PrintBridgeWindowSize.MinimumOuterHeight, height);

    }



    [Fact]

    public void ResolveStartupOuterSize_PreservesValidLargeSavedBounds()

    {

        var (width, height) = PrintBridgeWindowSize.ResolveStartupOuterSize(1600, 900);



        Assert.Equal(1600, width);

        Assert.Equal(900, height);

    }



    [Fact]

    public void OuterSizeForClient_IncludesNonClientChrome()

    {

        Assert.Equal(

            PrintBridgeWindowSize.MinimumClientWidth + 16,

            PrintBridgeWindowSize.OuterWidthForClient(PrintBridgeWindowSize.MinimumClientWidth, 16));

        Assert.Equal(

            PrintBridgeWindowSize.MinimumClientHeight + 48,

            PrintBridgeWindowSize.OuterHeightForClient(PrintBridgeWindowSize.MinimumClientHeight, 48));

    }



    [Fact]

    public void MainForm_AppliesClientAwareWindowLayout_AndKeepsMaximizeUsable()

    {

        var root = FindRepositoryRoot();

        var mainForm = File.ReadAllText(Path.Combine(root, "src", "Wasla.PrintBridge", "UI", "MainForm.cs"));

        var windowLayout = File.ReadAllText(Path.Combine(root, "src", "Wasla.PrintBridge", "UI", "PrintBridgeWindowLayout.cs"));



        Assert.Contains("PrintBridgeWindowLayout.ApplyStartup", mainForm);

        Assert.Contains("form.ClientSize = new Size(DefaultClientWidth, DefaultClientHeight)", windowLayout);

        Assert.Contains("EnsureMinimumClientSize(form)", windowLayout);

        Assert.Contains("OuterWidthForClient(MinimumClientWidth, nonClientWidth)", windowLayout);

        Assert.DoesNotContain("MaximizeBox = false", mainForm);

        Assert.DoesNotContain("FormBorderStyle.Fixed", mainForm);

    }



    private static string FindRepositoryRoot()

    {

        var current = AppContext.BaseDirectory;

        while (!string.IsNullOrWhiteSpace(current))

        {

            if (Directory.Exists(Path.Combine(current, "src", "Wasla.PrintBridge", "UI")))

                return current;



            var parent = Directory.GetParent(current);

            if (parent is null)

                break;

            current = parent.FullName;

        }



        throw new InvalidOperationException("Repository root not found.");

    }

}
