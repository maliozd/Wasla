namespace Wasla.UnitTests.Admin;

/// <summary>
/// The throw-away profile directory of one browser start. Cleanup deletes only this exact directory, and only while it
/// is still what <see cref="Create"/> made: a direct child of the temporary folder of that moment, named
/// <see cref="Prefix"/> plus a random suffix, and not a link. Profiles left by earlier runs are never looked at.
/// </summary>
internal sealed class BrowserProfile
{
    public const string Prefix = "wasla-headless-";

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Tests use this to show that cleanup refuses a directory it did not create.</summary>
    internal BrowserProfile(string path, string root)
    {
        Path = path;
        Root = root;
    }

    public string Path { get; }

    /// <summary>The temporary folder the profile was created in, as it was then.</summary>
    public string Root { get; }

    public static BrowserProfile Create()
    {
        var root = System.IO.Path.GetTempPath();
        return new BrowserProfile(Directory.CreateTempSubdirectory(Prefix).FullName, root);
    }

    /// <summary>Null when <see cref="Path"/> may be deleted; otherwise why not.</summary>
    public string? WhyNotDeletable() => WhyNotDeletable(Path, Root);

    internal static string? WhyNotDeletable(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path))
            return "it is not an absolute path";
        if (string.IsNullOrWhiteSpace(root) || !System.IO.Path.IsPathFullyQualified(root))
            return "the temporary folder it was created in is unknown";

        var full = System.IO.Path.GetFullPath(path);
        if (!string.Equals(full, path, PathComparison))
            return "it is not a normalized path";
        var parent = System.IO.Path.GetDirectoryName(full);
        var temp = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
        if (parent is null || !string.Equals(System.IO.Path.TrimEndingDirectorySeparator(parent), temp, PathComparison))
            return $"it is not directly inside the temporary folder {temp}";
        var name = System.IO.Path.GetFileName(full);
        if (!name.StartsWith(Prefix, StringComparison.Ordinal) || name.Length == Prefix.Length)
            return $"its name does not start with {Prefix} followed by a random suffix";
        if (Directory.Exists(full) && File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint))
            return "it is a link to another location";
        return null;
    }
}
