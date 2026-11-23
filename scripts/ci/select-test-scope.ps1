#Requires -Version 5.1
<#
.SYNOPSIS
    Chooses the CI test scope for the triggering event and plans the test shards that run it.

.DESCRIPTION
    A pull request into dev runs the fast scope: every test except the groups that need a real headless browser
    (Wasla.UnitTests, Category=Browser) or a real WebView2 Runtime (Wasla.PrintBridge.Tests, Category=WebView2Runtime).
    Every other event runs the full scope, with no test left out: pushes to dev and master, pull requests into master,
    and manual runs. An event or base branch this script does not know also gets the full scope.

    The tests of the scope are split into shards, each run by its own CI job on its own runner. A shard is a set of
    rules: traits a test must have, traits it must not have, namespaces it must be in and namespaces it must not be in.
    Every shard gets the dotnet test --filter made from its rules. The rules of a project's shards partition its scope:
    every test of the scope is in exactly one shard, and no shard selects a test outside the scope.
    assert-test-shards.ps1 checks that partition against the built test assemblies and the results of every shard.

    Wasla.UnitTests shards (the namespaces of the first shard were chosen from measured class durations so the two
    non-browser shards take about as long; see docs/operations/testing.md):

      nonbrowser-1  not Category=Browser, namespaces Auth, Routing, Printing, Contact   fast and full scope
      nonbrowser-2  not Category=Browser, every other namespace                          fast and full scope
      browser-1     Category=Browser, namespace Web                                      full scope only
      browser-2     Category=Browser, every other namespace                              full scope only

    Wasla.PrintBridge.Tests has one shard, print-bridge, run by the job that also builds the installers.

    On GitHub Actions the plan is written to GITHUB_OUTPUT (scope, plan, unit-shards, print-bridge-filter) and to the
    job summary. Without GitHub Actions the plan is printed.

.PARAMETER EventName
    The triggering event. Defaults to GITHUB_EVENT_NAME.

.PARAMETER BaseRef
    The pull request's base branch. Defaults to GITHUB_BASE_REF, which is empty for other events.

.PARAMETER PlanOnly
    Writes only the plan's JSON to the pipeline: no log lines, no GitHub Actions output. Used by the tests.

.EXAMPLE
    .\scripts\ci\select-test-scope.ps1 -EventName pull_request -BaseRef dev
#>
[CmdletBinding()]
param(
    [string] $EventName = $env:GITHUB_EVENT_NAME,
    [string] $BaseRef = $env:GITHUB_BASE_REF,
    [switch] $PlanOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$fast = $EventName -eq 'pull_request' -and $BaseRef -eq 'dev'

if ($fast) {
    $scope = 'fast'
    $reason = 'pull request into dev; the headless-browser and WebView2 runtime tests run after the merge to dev, on pull requests into master and in manual runs'
}
else {
    $scope = 'full'
    $trigger = if ($EventName) { $EventName } else { 'unknown event' }
    $reason = if ($BaseRef) { "$trigger into $BaseRef" } else { $trigger }
    $reason += '; every test runs'
}

function New-Shard {
    param(
        [string] $Id,
        [string] $Title,
        [string[]] $RequireTraits = @(),
        [string[]] $ExcludeTraits = @(),
        [string[]] $Namespaces = @(),
        [string[]] $ExceptNamespaces = @()
    )

    # A $null or empty argument is no rule.
    $RequireTraits = @($RequireTraits | Where-Object { $_ })
    $ExcludeTraits = @($ExcludeTraits | Where-Object { $_ })
    $Namespaces = @($Namespaces | Where-Object { $_ })
    $ExceptNamespaces = @($ExceptNamespaces | Where-Object { $_ })

    # VSTest filter syntax: '~' is "contains" on the fully qualified test name (Namespace.Class.Method), '!~' its
    # negation. The trailing dot keeps a namespace from matching a longer sibling name.
    $conditions = New-Object System.Collections.Generic.List[string]
    foreach ($trait in $RequireTraits) { $conditions.Add($trait) }
    foreach ($trait in $ExcludeTraits) { $conditions.Add($trait.Replace('=', '!=')) }
    if ($Namespaces.Count -gt 0) {
        $conditions.Add('(' + (($Namespaces | ForEach-Object { "FullyQualifiedName~$_." }) -join '|') + ')')
    }
    foreach ($namespace in $ExceptNamespaces) { $conditions.Add("FullyQualifiedName!~$namespace.") }

    [ordered]@{
        id               = $Id
        title            = $Title
        requireTraits    = $RequireTraits
        excludeTraits    = $ExcludeTraits
        namespaces       = $Namespaces
        exceptNamespaces = $ExceptNamespaces
        filter           = $conditions -join '&'
    }
}

$browser = 'Category=Browser'
$webView2Runtime = 'Category=WebView2Runtime'
$firstNonBrowserNamespaces = @('Wasla.UnitTests.Auth', 'Wasla.UnitTests.Routing', 'Wasla.UnitTests.Printing', 'Wasla.UnitTests.Contact')

$unitShards = New-Object System.Collections.Generic.List[object]
$unitShards.Add((New-Shard -Id 'nonbrowser-1' -Title 'Auth, Routing, Printing, Contact' -ExcludeTraits $browser -Namespaces $firstNonBrowserNamespaces))
$unitShards.Add((New-Shard -Id 'nonbrowser-2' -Title 'every other namespace' -ExcludeTraits $browser -ExceptNamespaces $firstNonBrowserNamespaces))
if (-not $fast) {
    $unitShards.Add((New-Shard -Id 'browser-1' -Title 'browser tests, Web' -RequireTraits $browser -Namespaces 'Wasla.UnitTests.Web'))
    $unitShards.Add((New-Shard -Id 'browser-2' -Title 'browser tests, every other namespace' -RequireTraits $browser -ExceptNamespaces 'Wasla.UnitTests.Web'))
}

$printBridgeExclusions = @(if ($fast) { $webView2Runtime })
$printBridgeShard = New-Shard -Id 'print-bridge' -Title 'Print Bridge' -ExcludeTraits $printBridgeExclusions

$plan = [ordered]@{
    scope    = $scope
    reason   = $reason
    projects = @(
        [ordered]@{
            project             = 'Wasla.UnitTests'
            scopeExcludesTraits = @(if ($fast) { $browser })
            # ToArray: Windows PowerShell 5.1 cannot put @(<generic list>) into an [ordered] literal.
            shards              = $unitShards.ToArray()
        },
        [ordered]@{
            project             = 'Wasla.PrintBridge.Tests'
            scopeExcludesTraits = $printBridgeExclusions
            shards              = @($printBridgeShard)
        }
    )
}

$planJson = ConvertTo-Json -InputObject $plan -Depth 6 -Compress
if ($PlanOnly) {
    $planJson
    return
}

function Format-Filter([string] $filter) {
    if ($filter) { return $filter }
    return 'none (whole project)'
}

Write-Host "Test scope: $scope ($reason)"
foreach ($project in $plan.projects) {
    foreach ($shard in $project.shards) {
        Write-Host "$($project.project) shard $($shard.id) ($($shard.title)): filter $(Format-Filter $shard.filter)"
    }
}

# UTF-8 without a byte-order mark in both PowerShell editions: a mark would become part of the first output name.
$utf8 = New-Object System.Text.UTF8Encoding($false)

if ($env:GITHUB_OUTPUT) {
    $matrix = ConvertTo-Json -InputObject @($unitShards | ForEach-Object { [ordered]@{ id = $_.id; title = $_.title; filter = $_.filter } }) -Depth 3 -Compress
    $lines = @(
        "scope=$scope",
        "plan=$planJson",
        "unit-shards=$matrix",
        "print-bridge-filter=$($printBridgeShard.filter)"
    )
    [System.IO.File]::AppendAllText($env:GITHUB_OUTPUT, (($lines -join "`n") + "`n"), $utf8)
}

if ($env:GITHUB_STEP_SUMMARY) {
    $summary = New-Object System.Collections.Generic.List[string]
    $summary.Add("**Test scope: $scope** ($reason)")
    $summary.Add('')
    $summary.Add('| Project | Shard | Tests | Filter |')
    $summary.Add('| --- | --- | --- | --- |')
    foreach ($project in $plan.projects) {
        foreach ($shard in $project.shards) {
            $summary.Add("| $($project.project) | $($shard.id) | $($shard.title) | ``$(Format-Filter $shard.filter)`` |")
        }
    }
    $summary.Add('')
    [System.IO.File]::AppendAllText($env:GITHUB_STEP_SUMMARY, (($summary -join "`n") + "`n"), $utf8)
}
