using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wasla.Web.PrintBridge;

/// <summary>A Print Bridge release this server offers to devices (WAS-63).</summary>
public sealed record PrintBridgeReleaseInfo(string Version, string FileName, long Bytes, string Sha256, string SetupPath);

/// <summary>
/// The release offered to Print Bridge devices (WAS-63, docs/integrations/print-bridge-updates.md): the setup the Web download
/// already serves (<see cref="PrintBridgePackagePaths"/>), described by the <c>Wasla.PrintBridge-package.json</c> the
/// packaging script writes next to it. Publishing a release stays "put the setup and its metadata where the download serves
/// them". Only a signed release is offered; anything incomplete or inconsistent is not offered at all (and logged), never
/// half described. The device verifies the setup's signature itself.
/// </summary>
public sealed partial class PrintBridgeReleaseCatalog
{
    public const string MetadataFileName = "Wasla.PrintBridge-package.json";
    public const int SchemaVersion = 1;

    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<PrintBridgeReleaseCatalog> _logger;

    public PrintBridgeReleaseCatalog(IConfiguration configuration, IWebHostEnvironment environment, ILogger<PrintBridgeReleaseCatalog> logger)
    {
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    [GeneratedRegex(@"^(\d{1,3})\.(\d{1,3})\.(\d{1,5})$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    /// <summary>The release to offer, or null when there is none to offer.</summary>
    public PrintBridgeReleaseInfo? GetCurrent()
    {
        var setupPath = PrintBridgePackagePaths.ResolvePackagePath(_configuration, _environment);
        if (setupPath is null || !File.Exists(setupPath))
            return null;

        var fileName = Path.GetFileName(setupPath);
        if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return null; // a development ZIP is downloadable from the setup page but is not a release

        var metadataPath = Path.Combine(Path.GetDirectoryName(setupPath)!, MetadataFileName);
        if (!File.Exists(metadataPath))
        {
            _logger.LogWarning("The Print Bridge setup has no {Metadata} next to it; no release is offered to devices.", MetadataFileName);
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            var root = document.RootElement;
            if (!TryInt(root, "schemaVersion", out var schema) || schema != SchemaVersion
                || !TryString(root, "version", out var version) || !IsVersion(version)
                || !root.TryGetProperty("signed", out var signed) || signed.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("The Print Bridge release metadata is not a signed release of schema {Schema}; no release is offered to devices.", SchemaVersion);
                return null;
            }

            foreach (var file in files.EnumerateArray())
            {
                if (!TryString(file, "role", out var role) || role != "setup"
                    || !TryString(file, "name", out var name) || !string.Equals(name, fileName, StringComparison.Ordinal))
                    continue;

                if (!file.TryGetProperty("bytes", out var bytesElement) || !bytesElement.TryGetInt64(out var bytes) || bytes <= 0
                    || !TryString(file, "sha256", out var sha256) || !Sha256Pattern().IsMatch(sha256))
                    break;

                if (new FileInfo(setupPath).Length != bytes)
                {
                    _logger.LogWarning("The Print Bridge setup does not match its release metadata (size); no release is offered to devices.");
                    return null;
                }

                return new PrintBridgeReleaseInfo(version, fileName, bytes, sha256, setupPath);
            }

            _logger.LogWarning("The Print Bridge release metadata does not describe the configured setup; no release is offered to devices.");
            return null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The Print Bridge release metadata could not be read; no release is offered to devices.");
            return null;
        }
    }

    private static bool IsVersion(string text)
    {
        var match = VersionPattern().Match(text);
        return match.Success
               && int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) <= 255
               && int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) <= 255
               && int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) <= 65535;
    }

    private static bool TryString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool TryInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value);
    }
}
