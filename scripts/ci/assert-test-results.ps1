#Requires -Version 5.1
<#
.SYNOPSIS
    Fails when a test result file is missing, empty, or reports a failed or skipped test.

.DESCRIPTION
    Reads .NET TRX files (dotnet test --logger trx) and JUnit XML files (node --test --test-reporter=junit).
    CI requires every test to run: the browser, WebView2 and printer tests skip themselves when their
    prerequisite is missing, and a skip must not pass as validation. Prints a summary per file and, on
    GitHub Actions, adds an error annotation per failed or skipped test and a table to the job summary.

.PARAMETER Path
    One or more TRX or JUnit XML files.

.EXAMPLE
    .\scripts\ci\assert-test-results.ps1 -Path artifacts/test-results/Wasla.UnitTests.trx
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]] $Path
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$maxAnnotations = 20
$onGitHub = $env:GITHUB_ACTIONS -eq 'true'

function Get-FirstLine([string] $text) {
    if ([string]::IsNullOrWhiteSpace($text)) { return '' }
    $line = ($text.Trim() -split "`r?`n")[0].Trim()
    if ($line.Length -gt 300) { $line = $line.Substring(0, 300) + '...' }
    return $line
}

function ConvertTo-CommandValue([string] $text) {
    return $text.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
}

function ConvertTo-CommandProperty([string] $text) {
    return (ConvertTo-CommandValue $text).Replace(':', '%3A').Replace(',', '%2C')
}

function Read-Trx([xml] $document) {
    $ns = New-Object System.Xml.XmlNamespaceManager($document.NameTable)
    $ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    foreach ($result in $document.SelectNodes('//t:Results//t:UnitTestResult', $ns)) {
        $message = $result.SelectSingleNode('t:Output/t:ErrorInfo/t:Message', $ns)
        if ($null -eq $message) { $message = $result.SelectSingleNode('t:Output/t:StdOut', $ns) }
        $status = switch ($result.GetAttribute('outcome')) {
            'Passed' { 'Passed' }
            'NotExecuted' { 'Skipped' }
            default { 'Failed' }
        }
        [pscustomobject]@{
            Name    = $result.GetAttribute('testName')
            Status  = $status
            Message = if ($null -ne $message) { Get-FirstLine $message.InnerText } else { '' }
        }
    }
}

function Read-JUnit([xml] $document) {
    foreach ($case in $document.SelectNodes('//testcase')) {
        $failure = $case.SelectSingleNode('failure|error')
        $skipped = $case.SelectSingleNode('skipped')
        $status = if ($null -ne $failure) { 'Failed' } elseif ($null -ne $skipped) { 'Skipped' } else { 'Passed' }
        $detail = if ($null -ne $failure) { $failure } else { $skipped }
        $message = ''
        if ($null -ne $detail) {
            $message = Get-FirstLine $detail.GetAttribute('message')
            if (-not $message) { $message = Get-FirstLine $detail.InnerText }
        }
        [pscustomobject]@{
            Name    = $case.GetAttribute('name')
            Status  = $status
            Message = $message
        }
    }
}

$problems = 0
$annotations = 0
$summary = New-Object System.Collections.Generic.List[string]
$summary.Add('| Results | Total | Passed | Failed | Skipped |')
$summary.Add('| --- | ---: | ---: | ---: | ---: |')

foreach ($file in $Path) {
    $label = [System.IO.Path]::GetFileName($file)
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        Write-Host "${label}: result file not found at $file"
        if ($onGitHub) { Write-Host "::error title=Missing test results::$(ConvertTo-CommandValue "No result file at $file; the test run did not complete.")" }
        $summary.Add("| $label | missing | | | |")
        $problems++
        continue
    }

    [xml] $document = Get-Content -LiteralPath $file -Raw -Encoding UTF8
    $tests = @(if ($document.DocumentElement.LocalName -eq 'TestRun') { Read-Trx $document } else { Read-JUnit $document })
    $failed = @($tests | Where-Object { $_.Status -eq 'Failed' })
    $skipped = @($tests | Where-Object { $_.Status -eq 'Skipped' })
    $passed = $tests.Count - $failed.Count - $skipped.Count

    Write-Host ("{0}: {1} total, {2} passed, {3} failed, {4} skipped" -f $label, $tests.Count, $passed, $failed.Count, $skipped.Count)
    $summary.Add("| $label | $($tests.Count) | $passed | $($failed.Count) | $($skipped.Count) |")

    if ($tests.Count -eq 0) {
        Write-Host "${label}: contains no test results."
        if ($onGitHub) { Write-Host "::error title=No tests ran::$(ConvertTo-CommandValue "$label contains no test results.")" }
        $problems++
    }

    foreach ($test in @($failed) + @($skipped)) {
        $problems++
        $kind = $test.Status
        Write-Host "  ${kind}: $($test.Name)"
        if ($test.Message) { Write-Host "    $($test.Message)" }
        if ($onGitHub -and $annotations -lt $maxAnnotations) {
            $annotations++
            Write-Host "::error title=$(ConvertTo-CommandProperty "$kind in $label")::$(ConvertTo-CommandValue "$($test.Name)`n$($test.Message)")"
        }
    }
}

if ($onGitHub -and $env:GITHUB_STEP_SUMMARY) {
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding UTF8 -Value ($summary -join "`n")
}

if ($problems -gt 0) {
    Write-Host "Test results are not clean: $problems missing, failed or skipped item(s). Every test must run and pass in CI; a skip means a prerequisite (browser, CDN, WebView2 Runtime, printer) was missing."
    exit 1
}

Write-Host 'All test results are clean: every test ran and passed.'
