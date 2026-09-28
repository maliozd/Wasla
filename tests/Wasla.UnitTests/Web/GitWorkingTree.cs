using System.Diagnostics;

namespace Wasla.UnitTests.Web;

/// <summary>
/// Runs git for source-contract tests. Both redirected streams are read to the end while git
/// runs, so git can never block on a full pipe (for example its usage text outside a repository).
/// </summary>
internal static class GitWorkingTree
{
    /// <summary>
    /// True when git reports an unstaged change to <paramref name="relativePath"/>.
    /// Outside a Git work tree (an exported copy without <c>.git</c>) there is no working-tree
    /// change to report, so the result is false. Any other git failure throws.
    /// </summary>
    public static bool IsDirty(string repositoryRoot, string relativePath)
    {
        var diff = Run(repositoryRoot, "diff", "--name-only", "--", relativePath.Replace('\\', '/'));
        if (diff.ExitCode == 0)
            return !string.IsNullOrWhiteSpace(diff.StandardOutput);

        if (!IsInsideWorkTree(repositoryRoot))
            return false;

        throw new InvalidOperationException(
            $"git diff failed with exit code {diff.ExitCode}: {diff.StandardError.Trim()}");
    }

    public static bool IsInsideWorkTree(string directory)
    {
        var result = Run(directory, "rev-parse", "--is-inside-work-tree");
        return result.ExitCode == 0 && result.StandardOutput.Trim() == "true";
    }

    public static GitResult Run(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("git could not be started.");

        // Drain both pipes concurrently; waiting first (or reading only stdout) can deadlock.
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        return new GitResult(
            process.ExitCode,
            standardOutput.GetAwaiter().GetResult(),
            standardError.GetAwaiter().GetResult());
    }

    public sealed record GitResult(int ExitCode, string StandardOutput, string StandardError);
}
