using System.Text.RegularExpressions;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// Reference direction of the provider library (WAS-233), read from every project file under <c>src/</c> as
/// docs/architecture/pos-application-boundary.md describes the guardrail for shared libraries (WAS-156). Wasla.Integrations
/// references neither product: no Orders project and no POS project, directly or through another project. Orders composes
/// it from Infrastructure only; Domain, Application and Print Bridge stay without it, so the Print Bridge payload carries no
/// provider code. A new project under <c>src/</c> fails here until it is placed in one of the groups.
/// </summary>
public sealed class IntegrationsReferenceDirectionTests
{
    private const string Library = "Wasla.Integrations";

    /// <summary>The Orders projects, as the POS boundary document lists them.</summary>
    private static readonly HashSet<string> Orders = new(StringComparer.Ordinal)
    {
        "Wasla.Domain", "Wasla.Application", "Wasla.Infrastructure", "Wasla.Contracts",
        "Wasla.Web", "Wasla.Api", "Wasla.Worker", "Wasla.Cli",
        "Wasla.PrintBridge", "Wasla.PrintBridge.Core", "Wasla.PrintBridge.Service"
    };

    private static bool IsPos(string project) => project.StartsWith("Wasla.Pos", StringComparison.Ordinal);

    [Fact]
    public void EverySourceProject_IsOrders_Pos_OrTheProviderLibrary()
    {
        var unplaced = Graph().Keys.Where(p => p != Library && !Orders.Contains(p) && !IsPos(p)).ToList();

        Assert.Contains(Library, Graph().Keys);
        Assert.True(unplaced.Count == 0, "Place these projects in IntegrationsReferenceDirectionTests: " + string.Join(", ", unplaced));
    }

    [Fact]
    public void TheProviderLibrary_ReferencesNeitherProduct()
    {
        var graph = Graph();
        var reached = Closure(graph, Library);

        Assert.DoesNotContain(reached, Orders.Contains);
        Assert.DoesNotContain(reached, IsPos);
        Assert.Empty(graph[Library]);
    }

    [Fact]
    public void OrdersComposesTheLibraryFromInfrastructure_AndPrintBridgeStaysWithoutIt()
    {
        var graph = Graph();

        Assert.Contains(Library, graph["Wasla.Infrastructure"]);
        foreach (var project in new[] { "Wasla.Domain", "Wasla.Application", "Wasla.Contracts", "Wasla.PrintBridge", "Wasla.PrintBridge.Core", "Wasla.PrintBridge.Service" })
            Assert.DoesNotContain(Library, Closure(graph, project));
    }

    /// <summary>Every project under <c>src/</c> with the projects it references directly, by project name.</summary>
    private static Dictionary<string, List<string>> Graph()
    {
        var source = Path.Combine(RepositoryRoot(), "src");
        var graph = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(source, "*.csproj", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (relative.Length != 2)
                continue;

            graph[Path.GetFileNameWithoutExtension(file)] = Regex.Matches(File.ReadAllText(file), @"<ProjectReference\s+Include=""([^""]+)""")
                .Select(m => Path.GetFileNameWithoutExtension(m.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar)))
                .ToList();
        }

        return graph;
    }

    private static HashSet<string> Closure(Dictionary<string, List<string>> graph, string project)
    {
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(graph[project]);
        while (pending.TryPop(out var next))
        {
            if (reached.Add(next) && graph.TryGetValue(next, out var references))
                foreach (var reference in references)
                    pending.Push(reference);
        }

        return reached;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !directory.GetFiles("Wasla.sln").Any())
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate Wasla.sln.");
    }
}
