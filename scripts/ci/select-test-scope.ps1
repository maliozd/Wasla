#Requires -Version 5.1
<#
.SYNOPSIS
    Chooses the CI test scope for the triggering event and the test filters that go with it.

.DESCRIPTION
    A pull request into dev runs the fast scope: every test except the groups that need a real headless browser
    (Wasla.UnitTests, Category=Browser) or a real WebView2 Runtime (Wasla.PrintBridge.Tests, Category=WebView2Runtime).
    Every other event runs the full scope, with no filter and no test left out: pushes to dev and master, pull
    requests into master, and manual runs. An event or base branch this script does not know also gets the full scope.

    A test without one of those Category traits is in the fast scope. On GitHub Actions the scope and filters are
    appended to GITHUB_ENV (TEST_SCOPE, UNIT_TESTS_FILTER, PRINT_BRIDGE_TESTS_FILTER) and to the job summary; an
    empty filter means the whole project runs.

.PARAMETER EventName
    The triggering event. Defaults to GITHUB_EVENT_NAME.

.PARAMETER BaseRef
    The pull request's base branch. Defaults to GITHUB_BASE_REF, which is empty for other events.

.EXAMPLE
    .\scripts\ci\select-test-scope.ps1 -EventName pull_request -BaseRef dev
#>
[CmdletBinding()]
param(
    [string] $EventName = $env:GITHUB_EVENT_NAME,
    [string] $BaseRef = $env:GITHUB_BASE_REF
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$fast = $EventName -eq 'pull_request' -and $BaseRef -eq 'dev'

if ($fast) {
    $scope = 'fast'
    $unitFilter = 'Category!=Browser'
    $printBridgeFilter = 'Category!=WebView2Runtime'
    $reason = 'pull request into dev; the headless-browser and WebView2 runtime tests run after the merge to dev, on pull requests into master and in manual runs'
}
else {
    $scope = 'full'
    $unitFilter = ''
    $printBridgeFilter = ''
    $trigger = if ($EventName) { $EventName } else { 'unknown event' }
    $reason = if ($BaseRef) { "$trigger into $BaseRef" } else { $trigger }
    $reason += '; every test runs'
}

function Format-Filter([string] $filter) {
    if ($filter) { return $filter }
    return 'none (whole project)'
}

Write-Host "Test scope: $scope ($reason)"
Write-Host "Wasla.UnitTests filter: $(Format-Filter $unitFilter)"
Write-Host "Wasla.PrintBridge.Tests filter: $(Format-Filter $printBridgeFilter)"

if ($env:GITHUB_ENV) {
    Add-Content -LiteralPath $env:GITHUB_ENV -Encoding UTF8 -Value @(
        "TEST_SCOPE=$scope",
        "UNIT_TESTS_FILTER=$unitFilter",
        "PRINT_BRIDGE_TESTS_FILTER=$printBridgeFilter"
    )
}

if ($env:GITHUB_STEP_SUMMARY) {
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding UTF8 -Value @(
        "**Test scope: $scope** ($reason)",
        '',
        "- Wasla.UnitTests filter: ``$(Format-Filter $unitFilter)``",
        "- Wasla.PrintBridge.Tests filter: ``$(Format-Filter $printBridgeFilter)``",
        ''
    )
}
