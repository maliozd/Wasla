<#
.SYNOPSIS
Publishes Wasla.Web, Wasla.Api, Wasla.Worker and Wasla.Cli for Linux from one exact commit and packs them into the
archive that deploy/linux/bin/wasla-install-release installs (WAS-76).

.DESCRIPTION
Checks out the commit in a temporary detached worktree, so uncommitted local changes can never reach a release,
publishes framework-dependent linux-x64 builds in Release configuration, and writes
artifacts/release/wasla-<sha>.tar.gz with web/, api/, worker/, cli/, RELEASE, migrate-before-start.sh and
deploy/linux/. The servers run the shared ASP.NET Core 8 runtime, so the builds are not self-contained.

.EXAMPLE
pwsh deploy/linux/publish-release.ps1 -Commit 0123456789abcdef0123456789abcdef01234567
#>
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $Commit
)

# Native tools report through exit codes (checked below); cmdlets stop on their own errors. Windows PowerShell 5.1
# would otherwise turn git's progress output on stderr into a terminating error.
$ErrorActionPreference = 'Continue'
$PSDefaultParameterValues['*:ErrorAction'] = 'Stop'
$repo = (git rev-parse --show-toplevel).Trim()
$work = Join-Path ([IO.Path]::GetTempPath()) "wasla-release-$Commit"
$out = Join-Path $work 'out'
$artifacts = Join-Path $repo 'artifacts/release'
$archive = Join-Path $artifacts "wasla-$Commit.tar.gz"

git -C $repo cat-file -e "$Commit^{commit}"
if ($LASTEXITCODE -ne 0) { throw "Commit $Commit is not in this repository; fetch it first." }
if (Test-Path $work) { git -C $repo worktree remove --force $work; Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
git -C $repo worktree prune

# LF in the release's shell scripts regardless of this machine's core.autocrlf.
git -C $repo -c core.autocrlf=false -c core.eol=lf worktree add --detach $work $Commit
if ($LASTEXITCODE -ne 0) { throw 'git worktree add failed.' }
Push-Location $work
try {
    # The SDK line CI builds with (actions/setup-dotnet 8.0.x). Written into the throwaway worktree only; global.json is
    # resolved from the working directory.
    [IO.File]::WriteAllText((Join-Path $work 'global.json'), '{ "sdk": { "version": "8.0.100", "rollForward": "latestFeature", "allowPrerelease": false } }')
    $sdk = (dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $sdk.StartsWith('8.0.')) { throw "The .NET 8 SDK is required (found '$sdk')." }
    $projects = [ordered]@{
        web    = 'src/Wasla.Web/Wasla.Web.csproj'
        api    = 'src/Wasla.Api/Wasla.Api.csproj'
        worker = 'src/Wasla.Worker/Wasla.Worker.csproj'
        cli    = 'src/Wasla.Cli/Wasla.Cli.csproj'
    }
    foreach ($name in $projects.Keys) {
        dotnet publish (Join-Path $work $projects[$name]) --configuration Release --runtime linux-x64 --self-contained false `
            --output (Join-Path $out $name) -p:DebugType=None -p:DebugSymbols=false
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $name." }
    }
    if (-not (Test-Path (Join-Path $out 'web/wwwroot'))) { throw 'The Web publish output has no wwwroot.' }

    Copy-Item (Join-Path $work 'scripts/release/migrate-before-start.sh') $out
    # Releases from before deploy/linux existed still install; the host scripts come from the host setup.
    if (Test-Path (Join-Path $work 'deploy/linux')) {
        New-Item -ItemType Directory -Force (Join-Path $out 'deploy') | Out-Null
        Copy-Item -Recurse (Join-Path $work 'deploy/linux') (Join-Path $out 'deploy/linux')
    }
    $release = "commit=$Commit`npublished_utc=$((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'))`ndotnet_sdk=$sdk`n"
    [IO.File]::WriteAllText((Join-Path $out 'RELEASE'), $release)

    $crlf = Get-ChildItem (Join-Path $out 'migrate-before-start.sh'), (Join-Path $out 'deploy/linux/bin') -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object { [IO.File]::ReadAllText($_.FullName).Contains("`r") }
    if ($crlf) { throw "CRLF line endings in: $($crlf.Name -join ', ')" }

    New-Item -ItemType Directory -Force $artifacts | Out-Null
    tar -czf $archive -C $out .
    if ($LASTEXITCODE -ne 0) { throw 'tar failed.' }
    $hash = (Get-FileHash -Algorithm SHA256 $archive).Hash.ToLowerInvariant()
    Write-Host "Release archive: $archive"
    Write-Host "SHA-256: $hash"
}
finally {
    Pop-Location
    git -C $repo worktree remove --force $work
}
