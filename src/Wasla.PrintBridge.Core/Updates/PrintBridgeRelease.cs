using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Wasla.PrintBridge.Updates;

/// <summary>
/// Print Bridge release versions (WAS-63): <c>major.minor.build</c>, each part within Windows Installer's limits
/// (255.255.65535), the form of <c>PrintBridgeVersion</c> (WAS-62) and of the setup's and MSI's versions.
/// </summary>
public static partial class PrintBridgeReleaseVersion
{
    [GeneratedRegex(@"^(\d{1,3})\.(\d{1,3})\.(\d{1,5})$", RegexOptions.CultureInvariant)]
    private static partial Regex ExactPattern();

    [GeneratedRegex(@"^\s*(\d{1,3})\.(\d{1,3})\.(\d{1,5})(?:\.\d{1,5})?(?:[^\d.].*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ResourcePattern();

    /// <summary>Exactly <c>a.b.c</c> within the limits; nothing else.</summary>
    public static bool TryParse(string? text, out Version version) => TryMatch(ExactPattern(), text, out version);

    /// <summary>
    /// A version resource or assembly version (<c>1.0.1</c>, <c>1.0.1.0</c>, or with a suffix such as <c>+commit</c>) as
    /// <c>a.b.c</c>; null when it is not one.
    /// </summary>
    public static Version? FromResource(string? text) => TryMatch(ResourcePattern(), text, out var version) ? version : null;

    public static string Format(Version version) =>
        string.Create(CultureInfo.InvariantCulture, $"{version.Major}.{version.Minor}.{version.Build}");

    /// <summary>The release version of a Print Bridge assembly (its assembly version, as <c>a.b.c</c>).</summary>
    public static Version Of(Assembly assembly)
    {
        var version = assembly.GetName().Version ?? new Version(0, 0, 0);
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
    }

    private static bool TryMatch(Regex pattern, string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (text is null)
            return false;

        var match = pattern.Match(text);
        if (!match.Success
            || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) || major > 255
            || !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor) || minor > 255
            || !int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var build) || build > 65535)
            return false;

        version = new Version(major, minor, build);
        return true;
    }
}

/// <summary>
/// The release the Wasla server describes (schema 1, <c>GET api/print-bridge/releases/latest</c>): its version and its setup's
/// file name, size and SHA-256. The description proves which file is meant, not who made it: the setup's own signature
/// decides that (<see cref="PrintBridgeSetupVerifier"/>).
/// </summary>
public sealed partial record PrintBridgeRelease(Version Version, string SetupFileName, long SetupBytes, string SetupSha256)
{
    public const int SchemaVersion = 1;

    /// <summary>A setup is about 60 MB; anything far larger is not one and is never downloaded.</summary>
    public const long MaxSetupBytes = 512L * 1024 * 1024;

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}\.exe$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();

    /// <summary>A described release with every field present and in range; otherwise null (the description is refused).</summary>
    public static PrintBridgeRelease? TryCreate(int schemaVersion, string? version, string? fileName, long bytes, string? sha256)
    {
        if (schemaVersion != SchemaVersion
            || !PrintBridgeReleaseVersion.TryParse(version, out var parsed)
            || fileName is null || !FileNamePattern().IsMatch(fileName)
            || bytes is <= 0 or > MaxSetupBytes
            || sha256 is null || !Sha256Pattern().IsMatch(sha256))
            return null;

        return new PrintBridgeRelease(parsed, fileName, bytes, sha256);
    }
}
