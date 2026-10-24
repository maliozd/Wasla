using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Wasla.Application.Onboarding;

/// <summary>
/// Physical database names for tenants (WAS-93). The server assigns them: a readable label from the slug and a random
/// suffix, so a business name or slug alone never decides which database a tenant gets. Every name that is about to be
/// created or written into CentralDb is checked against the SQL Server system databases and the CentralDb catalog.
/// </summary>
public static class TenantDatabaseNames
{
    public const int MaxLength = 128;
    private const int MaxLabelLength = 48;
    private const int RandomSuffixHexLength = 12;

    private static readonly Regex AllowedCharacters = new(@"^[A-Za-z0-9_]+$", RegexOptions.Compiled);
    private static readonly Regex GeneratedPattern = new(@"^Wasla_[A-Za-z][A-Za-z0-9]*_[0-9a-f]{12}$", RegexOptions.Compiled);

    // System databases, and the ones SQL Server features create with fixed names.
    private static readonly HashSet<string> SystemDatabaseNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "master",
        "model",
        "msdb",
        "tempdb",
        "mssqlsystemresource",
        "distribution",
        "SSISDB",
        "ReportServer",
        "ReportServerTempDB"
    };

    /// <summary>A new name such as <c>Wasla_SushiPlace_3f9a1c2b7d4e</c>. Uniqueness comes from the random suffix.</summary>
    public static string Generate(string slug)
    {
        var label = BuildLabel(slug);
        var suffix = RandomNumberGenerator.GetHexString(RandomSuffixHexLength, lowercase: true);
        return $"Wasla_{label}_{suffix}";
    }

    /// <summary>True for names produced by <see cref="Generate"/>.</summary>
    public static bool IsGeneratedFormat(string? databaseName) =>
        !string.IsNullOrEmpty(databaseName) && GeneratedPattern.IsMatch(databaseName);

    public static bool HasValidCharacters(string? databaseName) =>
        !string.IsNullOrEmpty(databaseName) && databaseName.Length <= MaxLength && AllowedCharacters.IsMatch(databaseName);

    public static bool IsSystemDatabaseName(string? databaseName) =>
        !string.IsNullOrWhiteSpace(databaseName) && SystemDatabaseNames.Contains(databaseName.Trim());

    /// <summary>
    /// Why <paramref name="databaseName"/> must not be used as a tenant database, or null when it may. Names are
    /// compared case-insensitively, as SQL Server compares them. An unknown CentralDb catalog is a refusal: without
    /// it the check cannot prove the name is not CentralDb.
    /// </summary>
    public static string? GetRejectionReason(string? databaseName, string? centralCatalog)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
            return "DatabaseName is required.";

        var name = databaseName.Trim();
        if (!HasValidCharacters(name))
            return $"DatabaseName '{name}' contains invalid characters.";

        if (IsSystemDatabaseName(name))
            return $"DatabaseName '{name}' is a SQL Server system database name.";

        if (string.IsNullOrWhiteSpace(centralCatalog))
            return "The CentralDb catalog name is not configured, so the tenant database name cannot be checked against it.";

        if (string.Equals(name, centralCatalog.Trim(), StringComparison.OrdinalIgnoreCase))
            return $"DatabaseName '{name}' is the CentralDb catalog.";

        return null;
    }

    private static string BuildLabel(string slug)
    {
        var builder = new StringBuilder();
        foreach (var part in (slug ?? string.Empty).Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries))
        {
            var letters = new string(part.Where(ch => ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9').ToArray());
            if (letters.Length == 0)
                continue;
            builder.Append(char.ToUpperInvariant(letters[0]));
            builder.Append(letters[1..].ToLowerInvariant());
        }

        var label = builder.Length == 0 ? "Tenant" : builder.ToString();
        if (!char.IsLetter(label[0]))
            label = "T" + label;
        return label.Length <= MaxLabelLength ? label : label[..MaxLabelLength];
    }
}
