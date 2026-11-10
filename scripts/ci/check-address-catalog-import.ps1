#Requires -Version 7
<#
.SYNOPSIS
    Checks a real-source import of the neighbourhood and street catalog on a disposable CentralDb (WAS-214).

.DESCRIPTION
    Run by .github/workflows/address-reference-import.yml after a dry run, an import that was stopped and resumed, and a
    repeated import of the pinned turkey-geo-api snapshot. It reads the three JSON reports of
    `seed-address-reference-data --report` and the database named by ConnectionStrings__CentralDb, and fails when:

    - the dry run wrote a row, or its counts do not add up to the declared source totals;
    - the stopped-and-resumed import and the dry run disagree on what was imported;
    - the repeated import inserted or changed anything;
    - CentralDb holds a different number of catalog rows, or two rows with one source id;
    - the neighbourhood created before the import lost its id or name;
    - a neighbourhood or street of Istanbul/Kadıköy or Sakarya/Akyazı is missing, misnamed, has another street type
      or hangs under another neighbourhood than in the source files.

    It writes a summary of the reports and the two districts to the job summary when GITHUB_STEP_SUMMARY is set.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Source,
    [Parameter(Mandatory)] [string] $DryRunReport,
    [Parameter(Mandatory)] [string] $ImportReport,
    [Parameter(Mandatory)] [string] $RepeatReport,
    [Parameter(Mandatory)] [int] $PreservedNeighborhoodId,
    [Parameter(Mandatory)] [int] $RowsBeforeImport
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$failures = [System.Collections.Generic.List[string]]::new()
function Fail([string] $message) { $failures.Add($message); Write-Host "::error::$message" }

function Invoke-Query([string] $sql) {
    $connection = [System.Data.SqlClient.SqlConnection]::new($env:ConnectionStrings__CentralDb)
    $connection.Open()
    try {
        $command = $connection.CreateCommand()
        $command.CommandText = $sql
        $command.CommandTimeout = 600
        $table = [System.Data.DataTable]::new()
        $table.Load($command.ExecuteReader())
        return , $table
    }
    finally { $connection.Dispose() }
}

function Scalar([string] $sql) { (Invoke-Query $sql).Rows[0][0] }

$dry = Get-Content -LiteralPath $DryRunReport -Raw -Encoding utf8 | ConvertFrom-Json
$import = Get-Content -LiteralPath $ImportReport -Raw -Encoding utf8 | ConvertFrom-Json
$repeat = Get-Content -LiteralPath $RepeatReport -Raw -Encoding utf8 | ConvertFrom-Json

# --- The dry run accounts for every source record and wrote nothing.
foreach ($level in 'Neighborhoods', 'Streets') {
    $c = $dry.$level
    $sum = $c.Insert + $c.Fill + $c.Unchanged + $c.Quarantined + $c.Rejected + $c.Conflict + $c.ParentNotImported
    if ($sum -ne $c.Source) { Fail "dry run: $level outcomes add up to $sum, not the $($c.Source) source records" }
}
if ($dry.Neighborhoods.Source -ne $dry.DeclaredCounts.Neighborhoods) { Fail "dry run read $($dry.Neighborhoods.Source) neighbourhoods; the source declares $($dry.DeclaredCounts.Neighborhoods)" }
if ($dry.Streets.Source -ne $dry.DeclaredCounts.Streets) { Fail "dry run read $($dry.Streets.Source) streets; the source declares $($dry.DeclaredCounts.Streets)" }
if ($dry.MatchedProvinces -ne $dry.DeclaredCounts.Provinces -or $dry.MatchedDistricts -ne $dry.DeclaredCounts.Districts) {
    Fail "dry run matched $($dry.MatchedProvinces) provinces and $($dry.MatchedDistricts) districts to CentralDb"
}
if ($RowsBeforeImport -ne 1) { Fail "the dry run left $RowsBeforeImport catalog rows; only the neighbourhood created before it should exist" }

# --- The resumed import imported exactly what the dry run planned; the repeat changed nothing.
foreach ($level in 'Neighborhoods', 'Streets') {
    $planned = $dry.$level.Insert + $dry.$level.Fill
    $done = $import.$level.Insert + $import.$level.Fill + $import.$level.Unchanged
    if ($done -ne $planned) { Fail "$level`: the resumed import ended with $done rows from the source; the dry run planned $planned" }
    if ($import.$level.Unchanged -eq 0) { Fail "$level`: the resumed import found no row of the stopped one" }
    if ($repeat.$level.Insert -ne 0 -or $repeat.$level.Fill -ne 0) { Fail "$level`: the repeated import wrote $($repeat.$level.Insert) inserts and $($repeat.$level.Fill) fills" }
    if ($repeat.$level.Unchanged -ne $planned) { Fail "$level`: the repeated import found $($repeat.$level.Unchanged) of $planned rows unchanged" }
}

# --- CentralDb rows.
$neighborhoodRows = Scalar 'SELECT COUNT(*) FROM dbo.Neighborhoods'
$streetRows = Scalar 'SELECT COUNT(*) FROM dbo.Streets'
$expectedNeighborhoods = $dry.Neighborhoods.Insert + $dry.Neighborhoods.Fill + $dry.Neighborhoods.Unchanged
$expectedStreets = $dry.Streets.Insert + $dry.Streets.Fill + $dry.Streets.Unchanged
if ($neighborhoodRows -ne $expectedNeighborhoods) { Fail "CentralDb has $neighborhoodRows neighbourhoods, expected $expectedNeighborhoods" }
if ($streetRows -ne $expectedStreets) { Fail "CentralDb has $streetRows streets, expected $expectedStreets" }
foreach ($table in 'Neighborhoods', 'Streets') {
    $shared = Scalar "SELECT COUNT(*) FROM (SELECT ExternalCode FROM dbo.$table WHERE ExternalCode IS NOT NULL GROUP BY ExternalCode HAVING COUNT(*) > 1) AS d"
    if ($shared -ne 0) { Fail "$shared source ids are on more than one row of $table" }
}

$preserved = (Invoke-Query "SELECT Id, Name, ExternalCode FROM dbo.Neighborhoods WHERE Id = $PreservedNeighborhoodId").Rows
if ($preserved.Count -ne 1 -or $preserved[0].Name -cne 'Caferağa' -or [string]::IsNullOrEmpty([string]$preserved[0].ExternalCode)) {
    Fail "the neighbourhood created before the import (id $PreservedNeighborhoodId, 'Caferağa') was not kept with its name and given its source id"
}

# --- Istanbul/Kadıköy and Sakarya/Akyazı against the source files, record by record.
$labels = @{ 0 = 'Köy Sokağı'; 1 = 'Meydan'; 2 = 'Bulvar'; 3 = 'Cadde'; 4 = 'Sokak'; 5 = 'Küme Evler' }
function Normalize([string] $value) { if ($null -eq $value) { '' } else { ($value.Normalize() -replace '\s+', ' ').Trim() } }

$sampleRows = [System.Collections.Generic.List[object]]::new()
foreach ($sample in @(
        @{ Plate = 34; District = 1421; WaslaDistrict = 'Kadıköy' },
        @{ Plate = 54; District = 1123; WaslaDistrict = 'Akyazı' })) {
    $folder = Join-Path $Source "province-$($sample.Plate)"
    $marker = '"district_id":' + $sample.District + ','
    $neighborhoods = @(Select-String -LiteralPath (Join-Path $folder 'neighborhoods.jsonl') -SimpleMatch $marker -Encoding utf8 | ForEach-Object { $_.Line | ConvertFrom-Json })
    $streets = @(Select-String -LiteralPath (Join-Path $folder 'streets.jsonl') -SimpleMatch $marker -Encoding utf8 | ForEach-Object { $_.Line | ConvertFrom-Json })

    $plate = '{0:00}' -f $sample.Plate
    $db = Invoke-Query @"
SELECT n.Id, n.Name, n.ExternalCode FROM dbo.Neighborhoods n
JOIN dbo.Districts d ON d.Id = n.DistrictId JOIN dbo.Cities c ON c.Id = d.CityId
WHERE c.PlateCode = '$plate' AND d.Name = N'$($sample.WaslaDistrict)'
"@
    $dbNeighborhoods = @{}; $codeOf = @{}
    foreach ($row in $db.Rows) { $dbNeighborhoods[[string]$row.ExternalCode] = $row; $codeOf[[int]$row.Id] = [string]$row.ExternalCode }
    $dbStreets = @{}
    foreach ($row in (Invoke-Query @"
SELECT s.Name, s.StreetType, s.ExternalCode, s.NeighborhoodId FROM dbo.Streets s
JOIN dbo.Neighborhoods n ON n.Id = s.NeighborhoodId JOIN dbo.Districts d ON d.Id = n.DistrictId JOIN dbo.Cities c ON c.Id = d.CityId
WHERE c.PlateCode = '$plate' AND d.Name = N'$($sample.WaslaDistrict)'
"@).Rows) { $dbStreets[[string]$row.ExternalCode] = $row }

    $bad = 0
    foreach ($n in $neighborhoods) {
        $row = $dbNeighborhoods[[string]$n.id]
        if ($null -eq $row) { $bad++; Fail "$($sample.WaslaDistrict): neighbourhood $($n.id) $($n.name) is not in CentralDb"; continue }
        if ($row.Id -eq $PreservedNeighborhoodId) { continue }
        if ($row.Name -cne (Normalize $n.name)) { $bad++; Fail "$($sample.WaslaDistrict): neighbourhood $($n.id) is '$($row.Name)', the source says '$($n.name)'" }
    }
    foreach ($s in $streets) {
        $row = $dbStreets[[string]$s.id]
        if ($null -eq $row) { $bad++; Fail "$($sample.WaslaDistrict): street $($s.id) $($s.name) is not in CentralDb"; continue }
        if ($row.Name -cne (Normalize $s.name) -or $row.StreetType -cne $labels[[int]$s.type_code] -or $codeOf[[int]$row.NeighborhoodId] -ne [string]$s.neighborhood_id) {
            $bad++; Fail "$($sample.WaslaDistrict): street $($s.id) is '$($row.Name)' ($($row.StreetType)) under $($codeOf[[int]$row.NeighborhoodId]); the source says '$($s.name)' ($($labels[[int]$s.type_code])) under $($s.neighborhood_id)"
        }
    }

    $entry = $dry.Districts | Where-Object { $_.SourceDistrictId -eq $sample.District }
    $sampleRows.Add([pscustomobject]@{
            District = "$($entry.Province) / $($entry.District)"
            SourceNeighborhoods = $neighborhoods.Count
            DbNeighborhoods = $dbNeighborhoods.Count
            SourceStreets = $streets.Count
            DbStreets = $dbStreets.Count
            Mismatches = $bad
            DryRun = $entry
        })
    Write-Host ("{0}: source {1} neighbourhoods / {2} streets; CentralDb {3} / {4}; mismatches {5}" -f $sample.WaslaDistrict, $neighborhoods.Count, $streets.Count, $dbNeighborhoods.Count, $dbStreets.Count, $bad)
}

# --- Summary.
$summary = [System.Text.StringBuilder]::new()
[void]$summary.AppendLine("## Address catalog import (real source, disposable CentralDb)")
[void]$summary.AppendLine("")
[void]$summary.AppendLine("Source: $($dry.Repository) at ``$($dry.Commit)``, $($dry.FilesVerified) files verified by SHA-256. CentralDb collation: ``$($dry.DatabaseCollation)``.")
[void]$summary.AppendLine("")
[void]$summary.AppendLine("| Report | Level | Source | Insert | Fill | Unchanged | Quarantined | Rejected | Conflict | Parent not imported |")
[void]$summary.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |")
foreach ($pair in @(@('dry run', $dry), @('import (resumed)', $import), @('repeat', $repeat))) {
    foreach ($level in 'Neighborhoods', 'Streets') {
        $c = $pair[1].$level
        [void]$summary.AppendLine("| $($pair[0]) | $level | $($c.Source) | $($c.Insert) | $($c.Fill) | $($c.Unchanged) | $($c.Quarantined) | $($c.Rejected) | $($c.Conflict) | $($c.ParentNotImported) |")
    }
}
[void]$summary.AppendLine("")
[void]$summary.AppendLine("Neighbourhood names: $($dry.NeighborhoodNames.Name) source names, $($dry.NeighborhoodNames.OfficialNameForMissingName) official names (no name in source), $($dry.NeighborhoodNames.OfficialNameForSharedName) official names (shared name). Collision groups: $($dry.Collisions.Count).")
[void]$summary.AppendLine("")
[void]$summary.AppendLine("| Sample district | Source neighbourhoods | In CentralDb | Source streets | In CentralDb | Mismatches |")
[void]$summary.AppendLine("| --- | --- | --- | --- | --- | --- |")
foreach ($row in $sampleRows) {
    [void]$summary.AppendLine("| $($row.District) | $($row.SourceNeighborhoods) | $($row.DbNeighborhoods) | $($row.SourceStreets) | $($row.DbStreets) | $($row.Mismatches) |")
}
[void]$summary.AppendLine("")
[void]$summary.AppendLine("Checks failed: $($failures.Count)")
Write-Host $summary.ToString()
if ($env:GITHUB_STEP_SUMMARY) { $summary.ToString() | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8 }

if ($failures.Count -gt 0) { exit 1 }
Write-Host 'Every address catalog import check passed.'
