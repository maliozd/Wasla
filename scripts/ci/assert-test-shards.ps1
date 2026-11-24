#Requires -Version 5.1
<#
.SYNOPSIS
    Proves that every test of the CI scope ran exactly once, in the shard the plan put it in.

.DESCRIPTION
    select-test-scope.ps1 plans the scope's shards; each shard runs in its own job, which records in its result folder
    a test-shard.json (project, shard, scope, filter, parallelism, commit and the names of its two files below), the discovery
    listing of its test assembly (xUnit v3 "-list full/json": every test method with its traits) and its TRX file.

    For every project of the plan this script checks:
      - every planned shard has exactly one record, made on the expected commit, in the planned scope, with the
        planned filter and the planned limit on classes run at once, and no record belongs to a shard the plan does
        not have;
      - every shard of a project listed the same test methods;
      - the plan's rules partition the scope: each listed method outside the scope's excluded traits is in exactly
        one shard, and no shard selects a method outside the scope;
      - each shard has planned methods and a TRX file with results;
      - each shard ran exactly its planned methods: none omitted, none unexpected, none run by two shards.

    Methods are compared by class and method name (a theory's data rows are one method). Whether each test passed and
    nothing was skipped is checked by assert-test-results.ps1 over the same TRX files.

    Prints a table per shard and, on GitHub Actions, adds error annotations (the first 20) and the table to the job
    summary. Exits with 1 when anything is wrong.

.PARAMETER Plan
    The plan's JSON, as select-test-scope.ps1 writes it to its "plan" output.

.PARAMETER ResultsRoot
    Folder that holds every shard's result folder (for example the downloaded artifacts, one folder each).

.PARAMETER Commit
    The commit every shard must have tested.

.EXAMPLE
    .\scripts\ci\assert-test-shards.ps1 -Plan $env:TEST_PLAN -ResultsRoot artifacts/shards -Commit (git rev-parse HEAD)
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Plan,

    [Parameter(Mandatory = $true)]
    [string] $ResultsRoot,

    [Parameter(Mandatory = $true)]
    [string] $Commit
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$maxAnnotations = 20
$maxListed = 25
$onGitHub = $env:GITHUB_ACTIONS -eq 'true'
$problems = New-Object System.Collections.Generic.List[string]

function Add-Problem([string] $message) {
    $script:problems.Add($message)
}

function ConvertTo-CommandValue([string] $text) {
    return $text.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
}

function Get-Field($object, [string] $name) {
    if ($null -eq $object) { return $null }
    if ($object -is [System.Collections.IDictionary]) {
        # Keys, not Contains: a generic Dictionary implements IDictionary.Contains only explicitly.
        if (@($object.Keys) -contains $name) { return $object[$name] }
        return $null
    }
    $property = $object.PSObject.Properties[$name]
    if ($null -ne $property) { return $property.Value }
    return $null
}

# Windows PowerShell 5.1's ConvertFrom-Json refuses text over 2 MB; a discovery listing can grow past that.
function ConvertFrom-LargeJson([string] $text) {
    if ($PSVersionTable.PSVersion.Major -ge 6) {
        return , @($text | ConvertFrom-Json)
    }
    Add-Type -AssemblyName System.Web.Extensions
    $serializer = New-Object System.Web.Script.Serialization.JavaScriptSerializer
    $serializer.MaxJsonLength = [int]::MaxValue
    return , @($serializer.DeserializeObject($text))
}

function Read-Discovery([string] $file) {
    $entries = ConvertFrom-LargeJson ([System.IO.File]::ReadAllText($file))
    $methods = @{}
    foreach ($entry in $entries) {
        $class = [string](Get-Field $entry 'Class')
        $method = [string](Get-Field $entry 'Method')
        if (-not $class -or -not $method) { throw "$file has a test without a class or method name." }
        $traits = @{}
        $rawTraits = Get-Field $entry 'Traits'
        if ($null -ne $rawTraits) {
            $names = if ($rawTraits -is [System.Collections.IDictionary]) { @($rawTraits.Keys) } else { @($rawTraits.PSObject.Properties | ForEach-Object { $_.Name }) }
            foreach ($name in $names) { $traits[$name] = @(Get-Field $rawTraits $name | ForEach-Object { [string]$_ }) }
        }
        $methods["$class.$method"] = [pscustomobject]@{ Class = $class; Traits = $traits }
    }
    return $methods
}

function Read-TrxMethods([string] $file) {
    [xml] $document = Get-Content -LiteralPath $file -Raw -Encoding UTF8
    $ns = New-Object System.Xml.XmlNamespaceManager($document.NameTable)
    $ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    $methodById = @{}
    foreach ($test in $document.SelectNodes('//t:TestDefinitions/t:UnitTest', $ns)) {
        $method = $test.SelectSingleNode('t:TestMethod', $ns)
        $methodById[$test.GetAttribute('id')] = "$($method.GetAttribute('className')).$($method.GetAttribute('name'))"
    }
    $methods = New-Object 'System.Collections.Generic.HashSet[string]'
    $outcomes = @{ Passed = 0; Failed = 0; Skipped = 0 }
    $results = 0
    foreach ($result in $document.SelectNodes('//t:Results//t:UnitTestResult', $ns)) {
        $results++
        $key = $methodById[$result.GetAttribute('testId')]
        if ($key) { [void]$methods.Add($key) }
        switch ($result.GetAttribute('outcome')) {
            'Passed' { $outcomes.Passed++ }
            'NotExecuted' { $outcomes.Skipped++ }
            default { $outcomes.Failed++ }
        }
    }
    return [pscustomobject]@{ Methods = $methods; Results = $results; Outcomes = $outcomes }
}

function Test-Trait($traits, [string] $rule) {
    $name, $value = $rule.Split('=', 2)
    if (-not $traits.ContainsKey($name)) { return $false }
    return @($traits[$name]) -contains $value
}

# The shard's filter tests "contains" (VSTest '~'); every fully qualified name starts with its project's root namespace,
# and so does every planned prefix. A difference between the two shows up as an omitted or unexpected test.
function Test-Prefix([string] $name, [string] $prefix) {
    return $name.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-ShardRules($shard, [string] $name, $test) {
    foreach ($rule in @($shard.requireTraits)) { if (-not (Test-Trait $test.Traits $rule)) { return $false } }
    foreach ($rule in @($shard.excludeTraits)) { if (Test-Trait $test.Traits $rule) { return $false } }
    $prefixes = @($shard.prefixes)
    if ($prefixes.Count -gt 0) {
        $any = $false
        foreach ($prefix in $prefixes) { if (Test-Prefix $name $prefix) { $any = $true; break } }
        if (-not $any) { return $false }
    }
    foreach ($prefix in @($shard.exceptPrefixes)) { if (Test-Prefix $name $prefix) { return $false } }
    return $true
}

function Format-List([string] $what, $items) {
    $sorted = @($items | Sort-Object)
    $text = "${what}: $($sorted.Count)"
    $shown = @($sorted | Select-Object -First $maxListed)
    if ($shown.Count -gt 0) { $text += "`n  " + ($shown -join "`n  ") }
    if ($sorted.Count -gt $shown.Count) { $text += "`n  ... and $($sorted.Count - $shown.Count) more" }
    return $text
}

$planObject = $Plan | ConvertFrom-Json
if (-not (Test-Path -LiteralPath $ResultsRoot -PathType Container)) {
    Add-Problem "The results folder $ResultsRoot does not exist; no shard result was found."
}

$records = @()
if (Test-Path -LiteralPath $ResultsRoot -PathType Container) {
    $records = @(Get-ChildItem -LiteralPath $ResultsRoot -Recurse -File -Filter 'test-shard.json' | ForEach-Object {
            $record = Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
            [pscustomobject]@{ Folder = $_.DirectoryName; Record = $record }
        })
}

$summary = New-Object System.Collections.Generic.List[string]
$summary.Add("**Test accounting: $($planObject.scope) scope on $Commit**")
$summary.Add('')
$summary.Add('| Project | Shard | Planned methods | Methods run | Results | Passed | Failed | Skipped |')
$summary.Add('| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |')

$plannedShards = @{}
foreach ($project in @($planObject.projects)) {
    foreach ($shard in @($project.shards)) { $plannedShards["$($project.project)/$($shard.id)"] = $true }
}
foreach ($entry in $records) {
    $key = "$($entry.Record.project)/$($entry.Record.shard)"
    if (-not $plannedShards.ContainsKey($key)) { Add-Problem "A result in $($entry.Folder) belongs to $key, which this run's plan does not have." }
}

foreach ($project in @($planObject.projects)) {
    $projectName = [string]$project.project
    $shards = @($project.shards)
    $scopeExclusions = @($project.scopeExcludesTraits)
    $discoveries = @{}
    $ran = @{}
    $counts = @{}

    foreach ($shard in $shards) {
        $label = "$projectName shard $($shard.id)"
        $matching = @($records | Where-Object { $_.Record.project -eq $projectName -and $_.Record.shard -eq $shard.id })
        if ($matching.Count -eq 0) {
            Add-Problem "${label}: no result was found; the shard did not run or did not upload its results."
            continue
        }
        if ($matching.Count -gt 1) {
            Add-Problem "${label}: $($matching.Count) results were found; each shard runs once."
            continue
        }
        $folder = $matching[0].Folder
        $record = $matching[0].Record
        if ($record.commit -ne $Commit) { Add-Problem "${label}: tested commit $($record.commit), not $Commit." }
        if ($record.scope -ne $planObject.scope) { Add-Problem "${label}: ran the $($record.scope) scope, not $($planObject.scope)." }
        if ([string]$record.filter -ne [string]$shard.filter) { Add-Problem "${label}: ran filter '$($record.filter)', not the planned '$($shard.filter)'." }
        $recordedThreads = Get-Field $record 'maxParallelThreads'
        if ([string]$recordedThreads -ne [string]$shard.maxParallelThreads) {
            Add-Problem "${label}: ran with a limit of '$recordedThreads' test classes at once, not the planned $($shard.maxParallelThreads) (0: xUnit's default)."
        }

        $discoveryFile = Join-Path $folder ([string]$record.discovery)
        if (-not $record.discovery -or -not (Test-Path -LiteralPath $discoveryFile -PathType Leaf)) {
            Add-Problem "${label}: the discovery listing $($record.discovery) is missing."
        }
        else {
            $discoveries[$shard.id] = Read-Discovery $discoveryFile
        }

        $trxFile = Join-Path $folder ([string]$record.results)
        if (-not $record.results -or -not (Test-Path -LiteralPath $trxFile -PathType Leaf)) {
            Add-Problem "${label}: the result file $($record.results) is missing; the test run did not complete."
        }
        else {
            $trx = Read-TrxMethods $trxFile
            $ran[$shard.id] = $trx.Methods
            $counts[$shard.id] = $trx
            if ($trx.Results -eq 0) { Add-Problem "${label}: $($record.results) contains no test results." }
        }
    }

    if ($discoveries.Count -eq 0) {
        foreach ($shard in $shards) { $summary.Add("| $projectName | $($shard.id) | ? | ? | | | | |") }
        continue
    }

    # Every shard of a project built the same commit, so each must list the same methods.
    $reference = $null
    foreach ($id in @($discoveries.Keys | Sort-Object)) {
        if ($null -eq $reference) { $reference = $id; continue }
        $a = $discoveries[$reference]; $b = $discoveries[$id]
        $missing = @($a.Keys | Where-Object { -not $b.ContainsKey($_) })
        $extra = @($b.Keys | Where-Object { -not $a.ContainsKey($_) })
        if ($missing.Count -gt 0 -or $extra.Count -gt 0) {
            Add-Problem "$projectName shards $reference and $id listed different tests ($($missing.Count) only in $reference, $($extra.Count) only in $id)."
        }
    }
    $tests = $discoveries[$reference]

    $planned = @{}
    foreach ($shard in $shards) { $planned[$shard.id] = New-Object 'System.Collections.Generic.HashSet[string]' }
    $notCovered = New-Object System.Collections.Generic.List[string]
    $overlapping = New-Object System.Collections.Generic.List[string]
    $outOfScope = New-Object System.Collections.Generic.List[string]
    foreach ($name in $tests.Keys) {
        $test = $tests[$name]
        $inScope = -not @($scopeExclusions | Where-Object { Test-Trait $test.Traits $_ }).Count
        $owners = @(foreach ($shard in $shards) { if (Test-ShardRules $shard $name $test) { $shard } })
        if ($inScope -and $owners.Count -eq 0) { $notCovered.Add($name) }
        elseif ($inScope -and $owners.Count -gt 1) { $overlapping.Add("$name ($(@($owners | ForEach-Object { $_.id }) -join ', '))") }
        elseif (-not $inScope -and $owners.Count -gt 0) { $outOfScope.Add("$name ($(@($owners | ForEach-Object { $_.id }) -join ', '))") }
        if ($inScope -and $owners.Count -ge 1) { foreach ($owner in $owners) { [void]$planned[$owner.id].Add($name) } }
    }
    if ($notCovered.Count -gt 0) { Add-Problem (Format-List "$projectName methods of the scope that no shard selects" $notCovered) }
    if ($overlapping.Count -gt 0) { Add-Problem (Format-List "$projectName methods that more than one shard selects" $overlapping) }
    if ($outOfScope.Count -gt 0) { Add-Problem (Format-List "$projectName methods outside the scope that a shard selects" $outOfScope) }

    $runBy = @{}
    foreach ($shard in $shards) {
        $label = "$projectName shard $($shard.id)"
        $plannedMethods = $planned[$shard.id]
        if ($plannedMethods.Count -eq 0) { Add-Problem "${label}: the plan selects no test of the scope; an empty shard proves nothing." }
        # Assigned, not taken from an if expression: that would unroll the set (a set of one becomes a string).
        $ranMethods = $null
        if ($ran.ContainsKey($shard.id)) { $ranMethods = $ran[$shard.id] }
        if ($null -ne $ranMethods) {
            $omitted = @($plannedMethods | Where-Object { -not $ranMethods.Contains($_) })
            $unexpected = @($ranMethods | Where-Object { -not $plannedMethods.Contains($_) })
            if ($omitted.Count -gt 0) { Add-Problem (Format-List "${label}: planned methods that did not run" $omitted) }
            if ($unexpected.Count -gt 0) { Add-Problem (Format-List "${label}: methods that ran but were not planned for this shard" $unexpected) }
            foreach ($name in $ranMethods) {
                if (-not $runBy.ContainsKey($name)) { $runBy[$name] = New-Object System.Collections.Generic.List[string] }
                $runBy[$name].Add($shard.id)
            }
        }

        $trx = if ($counts.ContainsKey($shard.id)) { $counts[$shard.id] } else { $null }
        if ($null -ne $trx) {
            $summary.Add("| $projectName | $($shard.id) | $($plannedMethods.Count) | $($ranMethods.Count) | $($trx.Results) | $($trx.Outcomes.Passed) | $($trx.Outcomes.Failed) | $($trx.Outcomes.Skipped) |")
        }
        else {
            $summary.Add("| $projectName | $($shard.id) | $($plannedMethods.Count) | missing | | | | |")
        }
    }
    $twice = @($runBy.Keys | Where-Object { $runBy[$_].Count -gt 1 } | ForEach-Object { "$_ ($($runBy[$_] -join ', '))" })
    if ($twice.Count -gt 0) { Add-Problem (Format-List "$projectName methods that ran in more than one shard" $twice) }
}

$summary | ForEach-Object { Write-Host $_ }
if ($onGitHub -and $env:GITHUB_STEP_SUMMARY) {
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding UTF8 -Value ($summary -join "`n")
}

if ($problems.Count -gt 0) {
    $annotations = 0
    foreach ($problem in $problems) {
        Write-Host $problem
        if ($onGitHub -and $annotations -lt $maxAnnotations) {
            $annotations++
            Write-Host "::error title=Test accounting::$(ConvertTo-CommandValue $problem)"
        }
    }
    Write-Host "Test accounting failed: $($problems.Count) problem(s). Every test of the scope must run exactly once, in the shard the plan put it in."
    exit 1
}

Write-Host 'Test accounting is clean: every test of the scope ran exactly once, in its planned shard, on the expected commit.'
