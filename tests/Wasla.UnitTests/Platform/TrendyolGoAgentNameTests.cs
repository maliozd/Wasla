using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Wasla.Infrastructure.Platform.TrendyolGo;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// WAS-141: Wasla identifies itself to Trendyol GO as <c>Wasla</c>, in <c>x-agentname</c> and in the
/// <c>User-Agent</c> suffix. The value comes from <see cref="TrendyolGoOptions.AgentName"/>; the committed
/// configuration either sets that same value or leaves the default in place. The headers the Real-mode client sends
/// are checked in <see cref="ProviderHttpClientRegistrationTests"/>.
/// </summary>
public sealed class TrendyolGoAgentNameTests
{
    private const string WaslaAgentName = "Wasla";

    [Fact]
    public void Options_DefaultAgentName_IsWasla()
    {
        Assert.Equal(WaslaAgentName, new TrendyolGoOptions().AgentName);
    }

    [Theory]
    [InlineData("Wasla.Api", null)]
    [InlineData("Wasla.Api", "Development")]
    [InlineData("Wasla.Api", "Production")]
    [InlineData("Wasla.Web", null)]
    [InlineData("Wasla.Web", "Development")]
    [InlineData("Wasla.Web", "Production")]
    [InlineData("Wasla.Worker", null)]
    [InlineData("Wasla.Worker", "Development")]
    [InlineData("Wasla.Worker", "Production")]
    public void CommittedHostConfiguration_BindsTheWaslaAgentName(string project, string? environment)
    {
        var directory = Path.Combine(RepositoryRoot(), "src", project);
        var builder = new ConfigurationBuilder()
            .SetBasePath(directory)
            .AddJsonFile("appsettings.json", optional: false);
        if (environment is not null)
            builder.AddJsonFile($"appsettings.{environment}.json", optional: false);

        var options = new TrendyolGoOptions();
        builder.Build().GetSection(TrendyolGoOptions.SectionName).Bind(options);

        Assert.Equal(WaslaAgentName, options.AgentName);
    }

    [Fact]
    public void EveryCommittedAppSettingsFile_SetsTheWaslaAgentNameOrNothing()
    {
        var src = Path.Combine(RepositoryRoot(), "src");
        var files = Directory.GetFiles(src, "appsettings*.json", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(Path.GetRelativePath(src, path)))
            .ToArray();
        Assert.NotEmpty(files);

        var configured = new List<string>();
        foreach (var file in files)
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(file),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!TryGet(document.RootElement, out var agentName, "Platform", "TrendyolGo", "AgentName"))
                continue;

            Assert.True(
                agentName.ValueKind == JsonValueKind.String && agentName.GetString() == WaslaAgentName,
                $"{Path.GetRelativePath(src, file)} sets Platform:TrendyolGo:AgentName to {agentName.GetRawText()}.");
            configured.Add(Path.GetRelativePath(src, file));
        }

        Assert.NotEmpty(configured);
    }

    private static bool TryGet(JsonElement element, out JsonElement value, params string[] path)
    {
        value = element;
        foreach (var name in path)
        {
            if (value.ValueKind != JsonValueKind.Object)
                return false;

            var match = value.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match.Value.ValueKind == JsonValueKind.Undefined)
                return false;

            value = match.Value;
        }

        return true;
    }

    private static bool IsBuildOutput(string relativePath)
    {
        var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment => segment is "bin" or "obj");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !directory.GetFiles("Wasla.sln").Any())
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate Wasla.sln.");
    }
}
