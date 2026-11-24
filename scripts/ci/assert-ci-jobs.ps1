#Requires -Version 5.1
<#
.SYNOPSIS
    Proves that every CI job the plan selected ran and passed, and that every job it left out was skipped.

.DESCRIPTION
    select-test-scope.ps1 selects the jobs of a run (see docs/operations/testing.md, Job selection). A skipped job
    counts as passed on GitHub, so this check turns the plan into the run's verdict:

      - a planned job must have succeeded; a planned job that failed, was cancelled or was skipped fails the run;
      - a job the plan left out must have been skipped; a job that ran anyway fails the run, because nothing checked
        that it ran what the plan expected.

    On a push to dev every job runs, and the plan also records what the rules would have selected for a pull request
    with the push's changes (observation phase). For every job the rules would have skipped, this script reports the
    job's real result; a failure there is a failure the rules would have missed, reported as a warning.

    Prints a table and, on GitHub Actions, adds it to the job summary with error and warning annotations. Exits with 1
    when a job's result does not match the plan.

.PARAMETER Plan
    The plan's JSON, as select-test-scope.ps1 writes it to its "plan" output.

.PARAMETER Needs
    The JSON of the final job's needs context (toJSON(needs)): every job's result.

.EXAMPLE
    .\scripts\ci\assert-ci-jobs.ps1 -Plan $env:TEST_PLAN -Needs $env:JOB_RESULTS
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Plan,

    [Parameter(Mandatory = $true)]
    [string] $Needs
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$onGitHub = $env:GITHUB_ACTIONS -eq 'true'

function ConvertTo-CommandValue([string] $text) {
    return $text.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
}

function Get-Result($needs, [string] $job) {
    $entry = $needs.PSObject.Properties[$job]
    if ($null -eq $entry -or $null -eq $entry.Value) { return 'missing' }
    $result = $entry.Value.PSObject.Properties['result']
    if ($null -eq $result -or -not $result.Value) { return 'missing' }
    return [string]$result.Value
}

$planObject = $Plan | ConvertFrom-Json
$needsObject = $Needs | ConvertFrom-Json
$selection = $planObject.selection
$problems = New-Object System.Collections.Generic.List[string]
$warnings = New-Object System.Collections.Generic.List[string]

$mode = if ($selection.mode -eq 'focused') { 'focused' } else { 'every job' }
$summary = New-Object System.Collections.Generic.List[string]
$summary.Add("**Planned jobs: $mode** ($($selection.basis))")
$summary.Add('')
$summary.Add('| Job | Planned | Result | Why |')
$summary.Add('| --- | --- | --- | --- |')
foreach ($property in $selection.jobs.PSObject.Properties) {
    $job = $property.Name
    $planned = [bool]$property.Value.run
    $result = Get-Result $needsObject $job
    $expected = if ($planned) { 'success' } else { 'skipped' }
    $mark = ''
    if ($result -ne $expected) {
        $mark = ' (does not match the plan)'
        if ($planned) { $problems.Add("$job was planned to run and pass, but its result is $result.") }
        else { $problems.Add("$job was not planned, but its result is $result; only planned jobs may run.") }
    }
    $summary.Add("| ``$job`` | $(if ($planned) { 'runs' } else { 'skipped on purpose' }) | $result$mark | $($property.Value.why) |")
}
$summary.Add('')

$observation = $planObject.PSObject.Properties['observation']
if ($null -ne $observation -and $null -ne $observation.Value) {
    $shadow = $observation.Value
    $summary.Add("**Observation phase: the rules would select $(if ($shadow.mode -eq 'focused') { 'a focused set' } else { 'every job' })** ($($shadow.basis))")
    $summary.Add('')
    $skipped = @($shadow.jobs.PSObject.Properties | Where-Object { -not $_.Value.run })
    if ($skipped.Count -eq 0) {
        $summary.Add('The rules would have run every job for these changes; nothing to compare.')
    }
    else {
        $summary.Add('| Job the rules would skip | Real result | Missed failure |')
        $summary.Add('| --- | --- | --- |')
        foreach ($property in $skipped) {
            $result = Get-Result $needsObject $property.Name
            $missed = $result -eq 'failure'
            if ($missed) {
                $warnings.Add("The rules would have skipped $($property.Name) for these changes, and it failed: a failure the focused selection would have missed. Check whether the failure comes from the changes (then a rule is wrong) or is unrelated.")
            }
            $summary.Add("| ``$($property.Name)`` | $result | $(if ($missed) { '**yes**' } else { 'no' }) |")
        }
    }
    $summary.Add('')
}

$summary | ForEach-Object { Write-Host $_ }
if ($onGitHub -and $env:GITHUB_STEP_SUMMARY) {
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding UTF8 -Value ($summary -join "`n")
}

foreach ($warning in $warnings) {
    Write-Host "Warning: $warning"
    if ($onGitHub) { Write-Host "::warning title=Job selection would have missed a failure::$(ConvertTo-CommandValue $warning)" }
}

if ($problems.Count -gt 0) {
    foreach ($problem in $problems) {
        Write-Host $problem
        if ($onGitHub) { Write-Host "::error title=Planned jobs::$(ConvertTo-CommandValue $problem)" }
    }
    Write-Host "The jobs do not match the plan: $($problems.Count) problem(s)."
    exit 1
}

Write-Host 'Every planned job passed and every job the plan left out was skipped.'
