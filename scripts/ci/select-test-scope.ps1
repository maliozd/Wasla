#Requires -Version 5.1
<#
.SYNOPSIS
    Chooses the CI test scope for the triggering event, the jobs that run it and the test shards of those jobs.

.DESCRIPTION
    Scope. A pull request into dev runs the fast scope: every test except the groups that need a real headless browser
    (Wasla.UnitTests, Category=Browser) or a real WebView2 Runtime (Wasla.PrintBridge.Tests, Category=WebView2Runtime).
    Every other event runs the full scope, with no test left out: pushes to dev and master, pull requests into master,
    and manual runs. An event or base branch this script does not know also gets the full scope.

    Jobs. The terminology guard and the Node tests run for every change. The Windows jobs (unit-tests: the
    Wasla.UnitTests shards; print-bridge: the installers and Wasla.PrintBridge.Tests) run when the change needs them:

      - A pull request into dev selects them from the files it changes, from the merge-base of its base and head to
        its head. Every changed file is matched against the rules in ci-job-rules.json; the first matching rule lists
        the jobs the file needs, or "all". The pull request runs the union. Every job runs when a file matches no rule,
        when a file is deleted, renamed, copied or changes type, when the diff is empty, and when it cannot be computed.
      - A push to dev runs every job (observation phase). The plan also records what the rules would have selected for
        the push's changes (before..after), so the summary and the final job can compare it with the real results.
      - Every other event runs every job: manual runs, pushes to master, pull requests into master and unknown events.
        Nothing an event carries (a label, an input) can narrow the selection.

    Shards. The tests of the scope are split into shards, each run by its own CI job on its own runner. A shard is a
    set of rules: traits a test must have, traits it must not have, name prefixes its fully qualified name (Namespace.
    Class.Method) must start with (any of them), and prefixes it must not start with. A namespace prefix ends with a
    dot, so Wasla.UnitTests.Auth. does not cover Wasla.UnitTests.Authorization. Every shard gets the dotnet test
    --filter made from its rules, and a limit on the test classes xUnit runs at once (0: xUnit's default, one per CPU).
    The rules of a project's shards partition its scope: every test of the scope is in exactly one shard, and no shard
    selects a test outside the scope. assert-test-shards.ps1 checks that partition against the built test assemblies
    and the results of every shard.

    Wasla.UnitTests shards (the prefixes were chosen from measured class durations so the shards of a group take about
    as long; see docs/operations/testing.md):

      nonbrowser-1  not Category=Browser, namespaces Auth, Routing, Printing, Contact      fast and full scope
      nonbrowser-2  not Category=Browser, every other namespace                             fast and full scope
      browser-1     Category=Browser, Web classes whose names start with L, O or P          full scope only
      browser-2     Category=Browser, the other Web classes                                 full scope only
      browser-3     Category=Browser, every other namespace                                 full scope only

    A browser shard runs at most two test classes at once. In the single job before the shards, at most two browser
    tests ran at once most of the time; four browsers at once on the four-CPU runner made timing-dependent browser tests
    fail (WAS-243), so a shard never runs them more densely than that job usually did.

    Wasla.PrintBridge.Tests has one shard, print-bridge, run by the job that also builds the installers.

    The plan lists only the .NET projects whose job runs; the final CI job checks the planned shards against it. On
    GitHub Actions the plan is written to GITHUB_OUTPUT (scope, plan, unit-shards, print-bridge-filter, unit-tests,
    print-bridge, dotnet-tests) and to the job summary. Without GitHub Actions the plan is printed.

.PARAMETER EventName
    The triggering event. Defaults to GITHUB_EVENT_NAME.

.PARAMETER BaseRef
    The pull request's base branch. Defaults to GITHUB_BASE_REF, which is empty for other events.

.PARAMETER RefName
    The pushed branch. Defaults to GITHUB_REF_NAME.

.PARAMETER EventPath
    The event's JSON payload: pull_request.base.sha and pull_request.head.sha for a pull request, before and after for
    a push. Defaults to GITHUB_EVENT_PATH.

.PARAMETER ChangeList
    The changed files instead of a git diff, one per item as git diff --name-status prints them (tab-separated status
    and paths). Used by the tests. An empty list is an empty diff.

.PARAMETER RepositoryRoot
    The git working tree to diff in. Defaults to the repository this script is in.

.PARAMETER RulesPath
    The job rules. Defaults to ci-job-rules.json next to this script.

.PARAMETER PlanOnly
    Writes only the plan's JSON to the pipeline: no log lines, no GitHub Actions output. Used by the tests.

.EXAMPLE
    .\scripts\ci\select-test-scope.ps1 -EventName pull_request -BaseRef dev -ChangeList "M`tdocs/README.md"
#>
[CmdletBinding()]
param(
    [string] $EventName = $env:GITHUB_EVENT_NAME,
    [string] $BaseRef = $env:GITHUB_BASE_REF,
    [string] $RefName = $env:GITHUB_REF_NAME,
    [string] $EventPath = $env:GITHUB_EVENT_PATH,
    [string[]] $ChangeList,
    [string] $RepositoryRoot = (Join-Path $PSScriptRoot '../..'),
    [string] $RulesPath = (Join-Path $PSScriptRoot 'ci-job-rules.json'),
    [switch] $PlanOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$changeListGiven = $PSBoundParameters.ContainsKey('ChangeList')
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

# ---- Job selection --------------------------------------------------------------------------------------------------

$maxListed = 20
$alwaysRun = [ordered]@{
    'terminology-guard' = 'runs for every change'
    'javascript-tests'  = 'runs for every change'
}
$rulesDocument = Get-Content -LiteralPath $RulesPath -Raw -Encoding UTF8 | ConvertFrom-Json
$skippableJobs = @($rulesDocument.skippableJobs)

# * stays within a folder, ** crosses folders, **/ at the start also matches the root; matching is case-sensitive.
function ConvertTo-PathRegex([string] $pattern) {
    $builder = New-Object System.Text.StringBuilder '^'
    $i = 0
    while ($i -lt $pattern.Length) {
        if ($pattern.Substring($i).StartsWith('**/')) { [void]$builder.Append('(?:.*/)?'); $i += 3; continue }
        if ($pattern.Substring($i).StartsWith('**')) { [void]$builder.Append('.*'); $i += 2; continue }
        $char = $pattern[$i]
        if ($char -eq '*') { [void]$builder.Append('[^/]*') }
        elseif ($char -eq '?') { [void]$builder.Append('[^/]') }
        else { [void]$builder.Append([regex]::Escape([string]$char)) }
        $i++
    }
    [void]$builder.Append('$')
    return New-Object System.Text.RegularExpressions.Regex ($builder.ToString(), [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
}

$rules = @(foreach ($rule in @($rulesDocument.rules)) {
        [pscustomobject]@{
            Id       = [string]$rule.id
            Jobs     = $rule.jobs
            Why      = [string]$rule.why
            Patterns = @(foreach ($path in @($rule.paths)) { ConvertTo-PathRegex $path })
        }
    })

function Find-Rule([string] $path) {
    foreach ($rule in $rules) {
        foreach ($pattern in $rule.Patterns) { if ($pattern.IsMatch($path)) { return $rule } }
    }
    return $null
}

function Test-AllJobs($rule) { return ($rule.Jobs -is [string]) -and $rule.Jobs -eq 'all' }

# Parses git diff --name-status lines (tab-separated) or -z fields into status, path and, for renames and copies, the
# previous path.
function ConvertFrom-NameStatus([string[]] $fields) {
    $changes = New-Object System.Collections.Generic.List[object]
    $i = 0
    while ($i -lt $fields.Count) {
        $status = $fields[$i]
        if (-not $status) { $i++; continue }
        $kind = $status.Substring(0, 1)
        if ($kind -eq 'R' -or $kind -eq 'C') {
            if ($i + 2 -ge $fields.Count) { throw "git diff printed an incomplete $status entry." }
            $changes.Add([pscustomobject]@{ Status = $kind; Path = $fields[$i + 2]; From = $fields[$i + 1] })
            $i += 3
        }
        else {
            if ($i + 1 -ge $fields.Count) { throw "git diff printed an incomplete $status entry." }
            $changes.Add([pscustomobject]@{ Status = $kind; Path = $fields[$i + 1]; From = $null })
            $i += 2
        }
    }
    return , $changes.ToArray()
}

# Runs git in the repository; returns its standard output (UTF-8) or throws with its error output.
function Invoke-Git([string[]] $arguments) {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = 'git'
    $start.Arguments = ($arguments | ForEach-Object { if ($_ -match '[\s"]') { '"' + $_.Replace('"', '\"') + '"' } else { $_ } }) -join ' '
    $start.WorkingDirectory = (Resolve-Path -LiteralPath $RepositoryRoot).Path
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $process = [System.Diagnostics.Process]::Start($start)
    $errorText = $process.StandardError.ReadToEndAsync()
    $output = $process.StandardOutput.ReadToEnd()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) {
        throw "git $($arguments -join ' ') failed ($($process.ExitCode)): $($errorText.Result.Trim())"
    }
    return $output
}

function Assert-Commit([string] $sha, [string] $what) {
    if (-not $sha -or $sha -match '^0+$') { throw "the $what commit is unknown" }
    if ($sha -notmatch '^[0-9a-fA-F]{7,64}$') { throw "the $what commit '$sha' is not a commit id" }
    try { [void](Invoke-Git @('rev-parse', '--verify', '--quiet', "$sha^{commit}")) }
    catch { throw "the $what commit $sha is not in the fetched history" }
}

# The changes as git diff reports them, with renames and copies detected. -MergeBase diffs from the merge-base of the
# two commits (a pull request's own changes); otherwise from the first commit (a push's before..after).
function Get-Diff([string] $from, [string] $to, [switch] $MergeBase) {
    Assert-Commit $from 'previous'
    Assert-Commit $to 'new'
    $base = $from
    if ($MergeBase) { $base = (Invoke-Git @('merge-base', $from, $to)).Trim() }
    $text = Invoke-Git @('-c', 'core.quotepath=false', 'diff', '--name-status', '-z', '-M', $base, $to)
    return [pscustomobject]@{
        From    = $base
        To      = $to
        Changes = ConvertFrom-NameStatus ($text.Split([char]0))
    }
}

function Format-Commit([string] $sha) { if ($sha.Length -gt 12) { return $sha.Substring(0, 12) } return $sha }

function Format-Change($change) {
    switch ($change.Status) {
        'D' { return "deleted $($change.Path)" }
        'R' { return "renamed $($change.From) to $($change.Path)" }
        'C' { return "copied $($change.From) to $($change.Path)" }
        'T' { return "changed the type of $($change.Path)" }
        default { return "$($change.Status) $($change.Path)" }
    }
}

# Matches the changes against the rules. $changes is $null when the diff could not be computed ($failure says why).
function Get-Selection($changes, [string] $basis, [string] $failure) {
    $forcedBy = New-Object System.Collections.Generic.List[string]
    $matched = [ordered]@{}
    $needed = @{}
    $fileCount = 0
    if ($failure) {
        $forcedBy.Add("the changes could not be computed: $failure")
    }
    elseif (@($changes).Count -eq 0) {
        $forcedBy.Add('the diff is empty')
    }
    else {
        $fileCount = @($changes).Count
        foreach ($change in $changes) {
            if ($change.Status -ne 'A' -and $change.Status -ne 'M') {
                $forcedBy.Add("a change other than an addition or modification: $(Format-Change $change)")
                continue
            }
            $rule = Find-Rule $change.Path
            if ($null -eq $rule) {
                $forcedBy.Add("no rule matches $($change.Path)")
                continue
            }
            if (-not $matched.Contains($rule.Id)) { $matched[$rule.Id] = New-Object System.Collections.Generic.List[string] }
            $matched[$rule.Id].Add($change.Path)
            if (Test-AllJobs $rule) {
                $forcedBy.Add("rule $($rule.Id) needs every job ($($change.Path))")
            }
            else {
                foreach ($job in @($rule.Jobs)) {
                    if (-not $needed.ContainsKey($job)) { $needed[$job] = New-Object System.Collections.Generic.List[string] }
                    if (-not $needed[$job].Contains($rule.Id)) { $needed[$job].Add($rule.Id) }
                }
            }
        }
    }

    $mode = if ($forcedBy.Count -gt 0) { 'all' } else { 'focused' }
    $jobs = [ordered]@{}
    foreach ($job in $alwaysRun.Keys) { $jobs[$job] = [ordered]@{ run = $true; why = $alwaysRun[$job] } }
    foreach ($job in $skippableJobs) {
        if ($mode -eq 'all') {
            $jobs[$job] = [ordered]@{ run = $true; why = "every job runs: $($forcedBy[0])" + $(if ($forcedBy.Count -gt 1) { " (and $($forcedBy.Count - 1) more)" } else { '' }) }
        }
        elseif ($needed.ContainsKey($job)) {
            $jobs[$job] = [ordered]@{ run = $true; why = "needed by rule $($needed[$job] -join ', ')" }
        }
        else {
            $jobs[$job] = [ordered]@{ run = $false; why = "no changed file needs it (rules: $(@($matched.Keys) -join ', '))" }
        }
    }

    $ruleSummary = @(foreach ($id in $matched.Keys) {
            $rule = $rules | Where-Object { $_.Id -eq $id } | Select-Object -First 1
            # Assigned, not taken from an if expression: that would turn a list of one job into a string.
            $ruleJobs = 'all'
            if (-not (Test-AllJobs $rule)) { $ruleJobs = [string[]]@($rule.Jobs) }
            [ordered]@{
                id    = $id
                jobs  = $ruleJobs
                files = $matched[$id].Count
                shown = @($matched[$id] | Select-Object -First $maxListed)
            }
        })
    return [ordered]@{
        mode     = $mode
        basis    = $basis
        files    = $fileCount
        forcedBy = @($forcedBy | Select-Object -First $maxListed)
        forced   = $forcedBy.Count
        rules    = $ruleSummary
        jobs     = $jobs
    }
}

# The changes of this event: a pull request's from the merge-base, a push's from before to after.
function Get-EventSelection([string] $kind) {
    $basis = ''
    try {
        if ($changeListGiven) {
            $changes = ConvertFrom-NameStatus @(foreach ($line in @($script:ChangeList)) { if ($line) { $line.Split("`t") } })
            return Get-Selection $changes "$kind (given change list)" $null
        }
        if (-not $EventPath -or -not (Test-Path -LiteralPath $EventPath -PathType Leaf)) { throw 'the event payload is not available' }
        $payload = Get-Content -LiteralPath $EventPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($kind -eq 'pull request') {
            $diff = Get-Diff ([string]$payload.pull_request.base.sha) ([string]$payload.pull_request.head.sha) -MergeBase
            $basis = "pull request into ${BaseRef}: merge-base $(Format-Commit $diff.From) to head $(Format-Commit $diff.To)"
        }
        else {
            $diff = Get-Diff ([string]$payload.before) ([string]$payload.after)
            $basis = "push to ${RefName}: $(Format-Commit $diff.From) to $(Format-Commit $diff.To)"
        }
        return Get-Selection $diff.Changes $basis $null
    }
    catch {
        return Get-Selection $null "$kind" $_.Exception.Message
    }
}

function Get-AllJobs([string] $why) {
    $jobs = [ordered]@{}
    foreach ($job in $alwaysRun.Keys) { $jobs[$job] = [ordered]@{ run = $true; why = $alwaysRun[$job] } }
    foreach ($job in $skippableJobs) { $jobs[$job] = [ordered]@{ run = $true; why = $why } }
    return [ordered]@{ mode = 'all'; basis = $why; files = 0; forcedBy = @(); forced = 0; rules = @(); jobs = $jobs }
}

$observation = $null
if ($fast) {
    $selection = Get-EventSelection 'pull request'
}
elseif ($EventName -eq 'push' -and $RefName -eq 'dev') {
    $selection = Get-AllJobs 'every job runs on a push to dev (observation phase)'
    $observation = Get-EventSelection 'push'
}
else {
    $trigger = if ($EventName) { $EventName } else { 'an unknown event' }
    $selection = Get-AllJobs "every job runs for $trigger$(if ($BaseRef) { " into $BaseRef" } elseif ($RefName -and $EventName -eq 'push') { " to $RefName" } else { '' })"
}

# ---- Shards ---------------------------------------------------------------------------------------------------------

function New-Shard {
    param(
        [string] $Id,
        [string] $Title,
        [string[]] $RequireTraits = @(),
        [string[]] $ExcludeTraits = @(),
        [string[]] $Prefixes = @(),
        [string[]] $ExceptPrefixes = @(),
        [int] $MaxParallelThreads = 0
    )

    # A $null or empty argument is no rule.
    $RequireTraits = @($RequireTraits | Where-Object { $_ })
    $ExcludeTraits = @($ExcludeTraits | Where-Object { $_ })
    $Prefixes = @($Prefixes | Where-Object { $_ })
    $ExceptPrefixes = @($ExceptPrefixes | Where-Object { $_ })

    # VSTest filter syntax: '~' is "contains" on the fully qualified test name, '!~' its negation. Every name starts
    # with its project's root namespace, so containing a prefix that starts with it means starting with that prefix.
    $conditions = New-Object System.Collections.Generic.List[string]
    foreach ($trait in $RequireTraits) { $conditions.Add($trait) }
    foreach ($trait in $ExcludeTraits) { $conditions.Add($trait.Replace('=', '!=')) }
    if ($Prefixes.Count -gt 0) {
        $conditions.Add('(' + (($Prefixes | ForEach-Object { "FullyQualifiedName~$_" }) -join '|') + ')')
    }
    foreach ($prefix in $ExceptPrefixes) { $conditions.Add("FullyQualifiedName!~$prefix") }

    [ordered]@{
        id                 = $Id
        title              = $Title
        requireTraits      = $RequireTraits
        excludeTraits      = $ExcludeTraits
        prefixes           = $Prefixes
        exceptPrefixes     = $ExceptPrefixes
        maxParallelThreads = $MaxParallelThreads
        filter             = $conditions -join '&'
    }
}

$browser = 'Category=Browser'
$webView2Runtime = 'Category=WebView2Runtime'
$firstNonBrowserNamespaces = @('Wasla.UnitTests.Auth.', 'Wasla.UnitTests.Routing.', 'Wasla.UnitTests.Printing.', 'Wasla.UnitTests.Contact.')
$web = 'Wasla.UnitTests.Web.'
$firstWebBrowserClasses = @("${web}L", "${web}O", "${web}P")
$browserThreads = 2

$unitShards = New-Object System.Collections.Generic.List[object]
$unitShards.Add((New-Shard -Id 'nonbrowser-1' -Title 'Auth, Routing, Printing, Contact' -ExcludeTraits $browser -Prefixes $firstNonBrowserNamespaces))
$unitShards.Add((New-Shard -Id 'nonbrowser-2' -Title 'every other namespace' -ExcludeTraits $browser -ExceptPrefixes $firstNonBrowserNamespaces))
if (-not $fast) {
    $unitShards.Add((New-Shard -Id 'browser-1' -Title 'browser tests, Web classes L, O, P' -RequireTraits $browser -Prefixes $firstWebBrowserClasses -MaxParallelThreads $browserThreads))
    $unitShards.Add((New-Shard -Id 'browser-2' -Title 'browser tests, other Web classes' -RequireTraits $browser -Prefixes $web -ExceptPrefixes $firstWebBrowserClasses -MaxParallelThreads $browserThreads))
    $unitShards.Add((New-Shard -Id 'browser-3' -Title 'browser tests, every other namespace' -RequireTraits $browser -ExceptPrefixes $web -MaxParallelThreads $browserThreads))
}

$printBridgeExclusions = @(if ($fast) { $webView2Runtime })
$printBridgeShard = New-Shard -Id 'print-bridge' -Title 'Print Bridge' -ExcludeTraits $printBridgeExclusions

$runUnitTests = [bool]$selection.jobs['unit-tests'].run
$runPrintBridge = [bool]$selection.jobs['print-bridge'].run

# Only the projects whose job runs: the final job checks every planned shard and rejects results of any other.
$projects = New-Object System.Collections.Generic.List[object]
if ($runUnitTests) {
    $projects.Add([ordered]@{
            project             = 'Wasla.UnitTests'
            scopeExcludesTraits = @(if ($fast) { $browser })
            # ToArray: Windows PowerShell 5.1 cannot put @(<generic list>) into an [ordered] literal.
            shards              = $unitShards.ToArray()
        })
}
if ($runPrintBridge) {
    $projects.Add([ordered]@{
            project             = 'Wasla.PrintBridge.Tests'
            scopeExcludesTraits = $printBridgeExclusions
            shards              = @($printBridgeShard)
        })
}

$plan = [ordered]@{
    scope       = $scope
    reason      = $reason
    selection   = $selection
    observation = $observation
    projects    = $projects.ToArray()
}

$planJson = ConvertTo-Json -InputObject $plan -Depth 8 -Compress
if ($PlanOnly) {
    $planJson
    return
}

# ---- Report ---------------------------------------------------------------------------------------------------------

function Format-Filter([string] $filter) {
    if ($filter) { return $filter }
    return 'none (whole project)'
}

function Format-Threads([int] $threads) {
    if ($threads -gt 0) { return "at most $threads test classes at once" }
    return "xUnit's default parallelism"
}

function Format-Jobs($jobs) {
    if ($jobs -is [string]) { return $jobs }
    $list = @($jobs)
    if ($list.Count -eq 0) { return 'no .NET job' }
    return $list -join ', '
}

function Format-Selection($title, $chosen) {
    $lines = New-Object System.Collections.Generic.List[string]
    $mode = if ($chosen.mode -eq 'focused') { 'focused' } else { 'every job' }
    $lines.Add("**${title}: $mode** ($($chosen.basis)$(if ($chosen.files -gt 0) { ", $($chosen.files) changed files" }))")
    $lines.Add('')
    $lines.Add('| Job | Runs | Why |')
    $lines.Add('| --- | --- | --- |')
    foreach ($job in $chosen.jobs.Keys) {
        $entry = $chosen.jobs[$job]
        $lines.Add("| ``$job`` | $(if ($entry.run) { 'yes' } else { '**skipped**' }) | $($entry.why) |")
    }
    if (@($chosen.rules).Count -gt 0) {
        $lines.Add('')
        $lines.Add('| Rule | Jobs it needs | Changed files |')
        $lines.Add('| --- | --- | --- |')
        foreach ($rule in $chosen.rules) {
            $files = (@($rule.shown) | ForEach-Object { "``$_``" }) -join ', '
            if ($rule.files -gt @($rule.shown).Count) { $files += " and $($rule.files - @($rule.shown).Count) more" }
            $lines.Add("| ``$($rule.id)`` | $(Format-Jobs $rule.jobs) | $files |")
        }
    }
    if ($chosen.forced -gt 0) {
        $lines.Add('')
        $lines.Add("Every job runs because of $($chosen.forced) finding(s):")
        foreach ($finding in $chosen.forcedBy) { $lines.Add("- $finding") }
        if ($chosen.forced -gt @($chosen.forcedBy).Count) { $lines.Add("- ... and $($chosen.forced - @($chosen.forcedBy).Count) more") }
    }
    $lines.Add('')
    return $lines
}

Write-Host "Test scope: $scope ($reason)"
Write-Host "Jobs: $($selection.mode) ($($selection.basis))"
foreach ($job in $selection.jobs.Keys) { Write-Host "  $job`: $(if ($selection.jobs[$job].run) { 'runs' } else { 'skipped' }) - $($selection.jobs[$job].why)" }
foreach ($finding in $selection.forcedBy) { Write-Host "  every job: $finding" }
if ($null -ne $observation) {
    Write-Host "Observation, what the rules would select for a pull request: $($observation.mode) ($($observation.basis))"
    foreach ($job in $observation.jobs.Keys) { Write-Host "  $job`: $(if ($observation.jobs[$job].run) { 'would run' } else { 'would be skipped' }) - $($observation.jobs[$job].why)" }
}
foreach ($project in $plan.projects) {
    foreach ($shard in $project.shards) {
        Write-Host "$($project.project) shard $($shard.id) ($($shard.title)): filter $(Format-Filter $shard.filter); $(Format-Threads $shard.maxParallelThreads)"
    }
}

# UTF-8 without a byte-order mark in both PowerShell editions: a mark would become part of the first output name.
$utf8 = New-Object System.Text.UTF8Encoding($false)

if ($env:GITHUB_OUTPUT) {
    # The matrix always lists the scope's shards: GitHub expands it before it skips the job.
    $matrix = ConvertTo-Json -InputObject @($unitShards | ForEach-Object { [ordered]@{ id = $_.id; title = $_.title; filter = $_.filter; maxParallelThreads = $_.maxParallelThreads } }) -Depth 3 -Compress
    $lines = @(
        "scope=$scope",
        "plan=$planJson",
        "unit-shards=$matrix",
        "print-bridge-filter=$($printBridgeShard.filter)",
        "unit-tests=$($runUnitTests.ToString().ToLowerInvariant())",
        "print-bridge=$($runPrintBridge.ToString().ToLowerInvariant())",
        "dotnet-tests=$(($projects.Count -gt 0).ToString().ToLowerInvariant())"
    )
    [System.IO.File]::AppendAllText($env:GITHUB_OUTPUT, (($lines -join "`n") + "`n"), $utf8)
}

if ($env:GITHUB_STEP_SUMMARY) {
    $summary = New-Object System.Collections.Generic.List[string]
    $summary.Add("**Test scope: $scope** ($reason)")
    $summary.Add('')
    foreach ($line in (Format-Selection 'Jobs' $selection)) { $summary.Add($line) }
    if ($null -ne $observation) {
        $summary.Add('Observation phase: every job runs on a push to dev. The rules would have selected the following for a pull request with these changes; the final job reports a failure in a job they would have skipped.')
        $summary.Add('')
        foreach ($line in (Format-Selection 'The rules would select' $observation)) { $summary.Add($line) }
    }
    if ($projects.Count -gt 0) {
        $summary.Add('| Project | Shard | Tests | Filter | Parallelism |')
        $summary.Add('| --- | --- | --- | --- | --- |')
        foreach ($project in $plan.projects) {
            foreach ($shard in $project.shards) {
                $summary.Add("| $($project.project) | $($shard.id) | $($shard.title) | ``$(Format-Filter $shard.filter)`` | $(Format-Threads $shard.maxParallelThreads) |")
            }
        }
    }
    else {
        $summary.Add('No .NET test job runs for this change.')
    }
    $summary.Add('')
    [System.IO.File]::AppendAllText($env:GITHUB_STEP_SUMMARY, (($summary -join "`n") + "`n"), $utf8)
}
