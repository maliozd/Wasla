using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Integrations.DependencyInjection;
using Wasla.Integrations.Platform;

namespace Wasla.Integrations.ConsumerTests;

/// <summary>
/// The provider library stands on its own (WAS-233): it references no Orders and no POS project, and a consumer that
/// composes and uses it loads no Orders or POS assembly. Orders is every other Wasla project (Domain, Application,
/// Infrastructure, Contracts, the hosts and Print Bridge); POS is any <c>Wasla.Pos</c> project. The library may reference
/// no Wasla assembly at all, so a new Orders or POS project is covered without a change here.
/// </summary>
public sealed class IntegrationsIndependenceTests
{
    private static readonly Assembly Library = typeof(IFoodPlatformClient).Assembly;

    /// <summary>The only Wasla assemblies a consumer of the library loads: the library and the consumer itself.</summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "Wasla.Integrations",
        typeof(IntegrationsIndependenceTests).Assembly.GetName().Name!
    };

    [Fact]
    public void TheLibraryProjectReferencesNoOtherProject()
    {
        var project = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Wasla.Integrations", "Wasla.Integrations.csproj"));

        var references = Regex.Matches(project, @"<ProjectReference\s+Include=""([^""]+)""").Select(m => m.Groups[1].Value).ToList();

        Assert.True(references.Count == 0, "Wasla.Integrations references projects: " + string.Join(", ", references));
    }

    [Fact]
    public void TheCompiledLibraryReferencesNoWaslaAssembly()
    {
        var wasla = Library.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(IsWasla)
            .ToList();

        Assert.Equal("Wasla.Integrations", Library.GetName().Name);
        Assert.True(wasla.Count == 0, "Wasla.Integrations references: " + string.Join(", ", wasla));
    }

    [Fact]
    public void TheConsumersOutputHoldsNoOrdersOrPosAssembly()
    {
        var wasla = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => IsWasla(name!))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(Allowed.Order(StringComparer.Ordinal), wasla);
    }

    [Fact]
    public async Task ComposingAndUsingTheLibrary_LoadsNoOrdersOrPosAssembly()
    {
        foreach (var mode in new[] { "Mock", "Real" })
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Platforms:ProviderMode"] = mode })
                .Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddWaslaIntegrations(configuration);
            await using var provider = services.BuildServiceProvider();

            var clients = provider.GetServices<IFoodPlatformClient>().ToList();
            Assert.Equal(3, clients.Count);
            var getir = clients.Single(c => c.Platform == ProviderPlatform.GetirYemek);
            var connection = new ProviderConnectionContext(
                Guid.NewGuid(), ProviderPlatform.GetirYemek, "store-1", null, null, isActive: true, new FixedCredentials());
            await getir.AcceptOrderAsync(connection, "independence-1", 10, TestContext.Current.CancellationToken);
        }

        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name!)
            .Where(IsWasla)
            .Where(name => !Allowed.Contains(name))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(loaded.Count == 0, "Loaded Orders or POS assemblies: " + string.Join(", ", loaded));
    }

    private static bool IsWasla(string name) => name.StartsWith("Wasla.", StringComparison.Ordinal) || name == "Wasla";

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !directory.GetFiles("Wasla.sln").Any())
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate Wasla.sln.");
    }

    private sealed class FixedCredentials : IProviderCredentialSource
    {
        public Task<ProviderCredentials> GetCredentialsAsync(CancellationToken ct) =>
            Task.FromResult(new ProviderCredentials("key", "secret"));
    }
}
