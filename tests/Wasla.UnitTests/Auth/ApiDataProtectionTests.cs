using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wasla.Api.Security;

namespace Wasla.UnitTests.Auth;

/// <summary>
/// WAS-141: the Api keeps its Data Protection keys in <c>C:\Wasla-keys</c> on Windows unless
/// <c>DataProtection:KeyPath</c> names another folder; on any other OS an unset path keeps the framework's default
/// key storage. The registration tests use a temporary folder, so no test touches a real key directory.
/// </summary>
public sealed class ApiDataProtectionTests : IDisposable
{
    private readonly string _keyDirectory = Path.Combine(Path.GetTempPath(), "wasla-dp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_keyDirectory))
            Directory.Delete(_keyDirectory, recursive: true);
    }

    [Fact]
    public void DefaultWindowsKeyPath_IsWaslaKeys()
    {
        Assert.Equal(@"C:\Wasla-keys", ApiDataProtection.DefaultWindowsKeyPath);
    }

    [Fact]
    public void UnsetKeyPath_OnWindows_ResolvesToWaslaKeys()
    {
        Assert.Equal(@"C:\Wasla-keys", ApiDataProtection.ResolveKeyPath(Configuration(keyPath: null), isWindows: true));
        Assert.Equal(@"C:\Wasla-keys", ApiDataProtection.ResolveKeyPath(Configuration(keyPath: "  "), isWindows: true));
    }

    [Fact]
    public void UnsetKeyPath_OffWindows_KeepsTheFrameworkDefaultKeyStorage()
    {
        Assert.Null(ApiDataProtection.ResolveKeyPath(Configuration(keyPath: null), isWindows: false));

        using var services = Services(Configuration(keyPath: null), isWindows: false);

        Assert.Null(services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository);
        Assert.Equal("Wasla", services.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConfiguredKeyPath_IsTheKeyRepositoryDirectory(bool isWindows)
    {
        using var services = Services(Configuration(_keyDirectory), isWindows);

        var repository = Assert.IsType<FileSystemXmlRepository>(
            services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository);
        Assert.Equal(Path.GetFullPath(_keyDirectory), repository.Directory.FullName);
        Assert.True(Directory.Exists(_keyDirectory));
        Assert.Equal("Wasla", services.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Development")]
    [InlineData("Production")]
    public void CommittedApiConfiguration_ResolvesToWaslaKeysOnWindowsOnly(string? environment)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(Path.Combine(RepositoryRoot(), "src", "Wasla.Api"))
            .AddJsonFile("appsettings.json", optional: false);
        if (environment is not null)
            builder.AddJsonFile($"appsettings.{environment}.json", optional: false);
        var configuration = builder.Build();

        Assert.Equal(@"C:\Wasla-keys", ApiDataProtection.ResolveKeyPath(configuration, isWindows: true));
        Assert.Null(ApiDataProtection.ResolveKeyPath(configuration, isWindows: false));
    }

    [Fact]
    public void ApiProgram_RegistersDataProtectionThroughTheHelperForTheCurrentOs()
    {
        var program = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Wasla.Api", "Program.cs"));

        Assert.Contains(
            "builder.Services.AddWaslaApiDataProtection(builder.Configuration, OperatingSystem.IsWindows());",
            program,
            StringComparison.Ordinal);
        Assert.DoesNotContain("PersistKeysToFileSystem", program, StringComparison.Ordinal);
    }

    private static IConfiguration Configuration(string? keyPath) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [ApiDataProtection.KeyPathConfigurationKey] = keyPath })
            .Build();

    private static ServiceProvider Services(IConfiguration configuration, bool isWindows)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWaslaApiDataProtection(configuration, isWindows);
        return services.BuildServiceProvider();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !directory.GetFiles("Wasla.sln").Any())
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate Wasla.sln.");
    }
}
