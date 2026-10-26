using System.Diagnostics;
using System.Text.Json;
using Wasla.UnitTests.Admin;

namespace Wasla.UnitTests.Deployment;

/// <summary>
/// deploy/linux/publish-release.ps1 writes the release archive with the Windows tar.exe even when the PowerShell that
/// runs it was started from Git Bash, whose PATH puts Git's GNU tar first (GNU tar reads the archive's "C:\..." path as a
/// remote host:path and fails). The script's own Resolve-ArchiveTool function is loaded from the file and run in Windows
/// PowerShell 5.1 with a failing tar placed first on PATH; the tool it returns must write and list an archive at an
/// absolute Windows path. Windows only, like the release workstation.
/// </summary>
public sealed class PublishReleaseArchiveToolTests
{
    private const string Probe = """
        param([string] $Publish, [string] $Work)
        $ErrorActionPreference = 'Stop'
        $tokens = $null; $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($Publish, [ref] $tokens, [ref] $errors)
        $fn = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Resolve-ArchiveTool' }, $true)
        if (-not $fn) { throw 'Resolve-ArchiveTool is not defined in publish-release.ps1.' }
        . ([scriptblock]::Create($fn.Extent.Text))

        # What Git Bash hands to PowerShell: another tar ahead of System32 on PATH. This one always fails.
        $shadow = Join-Path $Work 'shadow'
        New-Item -ItemType Directory -Force $shadow | Out-Null
        Set-Content -LiteralPath (Join-Path $shadow 'tar.cmd') -Value '@exit /b 7' -Encoding ascii
        $env:PATH = "$shadow;$env:PATH"
        $first = (Get-Command tar | Select-Object -First 1).Source

        $content = Join-Path $Work 'out'
        New-Item -ItemType Directory -Force (Join-Path $content 'web') | Out-Null
        Set-Content -LiteralPath (Join-Path $content 'RELEASE') -Value 'commit=probe' -Encoding ascii
        Set-Content -LiteralPath (Join-Path $content 'web/index.txt') -Value 'web' -Encoding ascii
        $archive = Join-Path $Work 'release.tar.gz'

        $tool = Resolve-ArchiveTool
        & $tool -czf $archive -C $content .
        $writeExit = $LASTEXITCODE
        $entries = @(& $tool -tzf $archive)
        [pscustomobject]@{ first = $first; tool = $tool; writeExit = $writeExit; listExit = $LASTEXITCODE; entries = $entries } | ConvertTo-Json -Compress
        """;

    [Fact]
    public async Task FromAGitBashPath_TheArchiveIsWrittenWithTheWindowsTar_AtAnAbsolutePath()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("The release archive is published from a Windows workstation.");

        var ct = TestContext.Current.CancellationToken;
        var work = Path.Combine(Path.GetTempPath(), "wasla-archive-tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var probe = Path.Combine(work, "probe.ps1");
            await File.WriteAllTextAsync(probe, Probe, ct);
            var publish = TenantOperationsRulesTests.RepoFile("deploy", "linux", "publish-release.ps1");

            var start = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", probe, "-Publish", publish, "-Work", work })
                start.ArgumentList.Add(argument);

            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            Assert.True(process.ExitCode == 0, $"The probe failed: {await error}");

            using var result = JsonDocument.Parse((await output).Trim());
            var root = result.RootElement;
            var systemTar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "tar.exe");

            // The precondition: plain "tar" would be the shadowing one, as from Git Bash.
            Assert.EndsWith(Path.Combine("shadow", "tar.cmd"), root.GetProperty("first").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(systemTar, root.GetProperty("tool").GetString(), StringComparer.OrdinalIgnoreCase);
            Assert.Equal(0, root.GetProperty("writeExit").GetInt32());
            Assert.Equal(0, root.GetProperty("listExit").GetInt32());
            var entries = root.GetProperty("entries").EnumerateArray().Select(entry => entry.GetString()!.TrimEnd('/')).ToList();
            Assert.Contains("./RELEASE", entries);
            Assert.Contains("./web/index.txt", entries);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }
}
