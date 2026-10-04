namespace Wasla.UnitTests.Web;

public sealed class GitWorkingTreeTests : IDisposable
{
    private readonly string _directory = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "wasla-git-" + Guid.NewGuid().ToString("N"))).FullName;

    // The timeout only turns a regression into a failure instead of a stuck run; it is not the fix.
    [Fact(Timeout = 60_000)]
    public async Task OutsideAGitRepository_CompletesAndReportsNoChange()
    {
        File.WriteAllText(Path.Combine(_directory, "notes.md"), "exported copy");
        Assert.False(GitWorkingTree.IsInsideWorkTree(_directory), "precondition: the temp folder is not inside a repository");

        // git prints a long usage text to stderr here; an undrained stderr pipe used to block the test forever.
        var dirty = await Task.Run(() => GitWorkingTree.IsDirty(_directory, "notes.md"), TestContext.Current.CancellationToken);

        Assert.False(dirty);
    }

    [Fact(Timeout = 60_000)]
    public async Task InsideAGitRepository_ReportsOnlyAChangedTrackedFile()
    {
        await Task.Run(() =>
        {
            Git("init", "--quiet");
            File.WriteAllText(Path.Combine(_directory, "tracked.md"), "original");
            Git("add", "tracked.md");
            Git("-c", "user.name=Wasla Test", "-c", "user.email=test@example.test", "-c", "commit.gpgsign=false",
                "commit", "--quiet", "--no-verify", "-m", "initial");
        }, TestContext.Current.CancellationToken);

        Assert.False(GitWorkingTree.IsDirty(_directory, "tracked.md"));

        File.WriteAllText(Path.Combine(_directory, "tracked.md"), "changed");
        Assert.True(GitWorkingTree.IsDirty(_directory, "tracked.md"));
        Assert.False(GitWorkingTree.IsDirty(_directory, "missing.md"));
    }

    private void Git(params string[] arguments)
    {
        var result = GitWorkingTree.Run(_directory, arguments);
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {result.StandardError}");
    }

    public void Dispose()
    {
        if (!Directory.Exists(_directory))
            return;

        // Git marks object files read-only, which blocks a plain recursive delete on Windows.
        foreach (var file in Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_directory, recursive: true);
    }
}
