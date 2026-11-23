#Requires -Version 5.1
<#
.SYNOPSIS
    Records which tests a CI shard job is about to run, for assert-test-shards.ps1.

.DESCRIPTION
    Writes two files into the shard's results folder, before its tests run:
      - <Project>.discovery.json: every test method of the built test assembly with its traits, as the xUnit v3
        runner lists them ("-list full/json"; no test runs);
      - test-shard.json: the project, shard, scope and filter of this job, the commit it checked out, and the names
        of the discovery listing and of the TRX file the job's dotnet test writes.

    Fails when the assembly cannot be listed or lists no test.

.PARAMETER Project
    Test project name, for example Wasla.UnitTests.

.PARAMETER Shard
    The shard id from the plan of select-test-scope.ps1.

.PARAMETER Scope
    fast or full.

.PARAMETER Filter
    The dotnet test --filter the job runs (empty for the whole project).

.PARAMETER TestExecutable
    The built xUnit v3 test executable, for example tests/Wasla.UnitTests/bin/Release/net8.0/Wasla.UnitTests.exe.

.PARAMETER ResultsDirectory
    The folder dotnet test writes its TRX file to; uploaded as the shard's artifact.

.PARAMETER ResultsFile
    The TRX file name given to dotnet test's trx logger.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Project,
    [Parameter(Mandatory = $true)] [string] $Shard,
    [Parameter(Mandatory = $true)] [ValidateSet('fast', 'full')] [string] $Scope,
    [AllowEmptyString()] [string] $Filter = '',
    [Parameter(Mandatory = $true)] [string] $TestExecutable,
    [Parameter(Mandatory = $true)] [string] $ResultsDirectory,
    [Parameter(Mandatory = $true)] [string] $ResultsFile
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Test-Path -LiteralPath $TestExecutable -PathType Leaf)) {
    throw "The test executable $TestExecutable does not exist; build the solution first."
}

$commit = (& git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { throw 'git rev-parse HEAD did not return a commit.' }

# The listing is read from the runner's console output: make both ends use UTF-8.
$utf8 = New-Object System.Text.UTF8Encoding($false)
$previousEncoding = [Console]::OutputEncoding
[Console]::OutputEncoding = $utf8
try {
    $listing = (& $TestExecutable -list full/json) -join "`n"
    $exitCode = $LASTEXITCODE
}
finally {
    [Console]::OutputEncoding = $previousEncoding
}
if ($exitCode -ne 0) { throw "$TestExecutable -list full/json exited with $exitCode." }

$start = $listing.IndexOf('[')
if ($start -lt 0) { throw "$TestExecutable -list full/json printed no JSON array." }
$listing = $listing.Substring($start)
# Counted, not parsed: Windows PowerShell 5.1's ConvertFrom-Json refuses text over 2 MB. assert-test-shards.ps1
# parses the listing.
$methodCount = [regex]::Matches($listing, '"Method"\s*:').Count
if ($methodCount -eq 0) { throw "$TestExecutable lists no test." }

New-Item -ItemType Directory -Force -Path $ResultsDirectory | Out-Null
$discovery = "$Project.discovery.json"
[System.IO.File]::WriteAllText((Join-Path $ResultsDirectory $discovery), $listing, $utf8)

$record = [ordered]@{
    project   = $Project
    shard     = $Shard
    scope     = $Scope
    filter    = $Filter
    commit    = $commit
    results   = $ResultsFile
    discovery = $discovery
}
[System.IO.File]::WriteAllText((Join-Path $ResultsDirectory 'test-shard.json'), (ConvertTo-Json -InputObject $record), $utf8)

Write-Host "$Project shard ${Shard} ($Scope scope) on $commit; the assembly lists $methodCount test methods."
Write-Host "Filter: $(if ($Filter) { $Filter } else { 'none (whole project)' })"
