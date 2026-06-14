#Requires -Version 5.1
<#
.SYNOPSIS
    Builds a portable Windows zip package for OrderHub Print Bridge.

.DESCRIPTION
    Publishes OrderHub.PrintBridge (Release, win-x64, framework-dependent) and zips the output.
    Artifacts are written under artifacts/ (gitignored). Optionally copies the zip to the Web
    download folder for local tenant setup page testing.

.PARAMETER CopyToWebDownload
    Copy OrderHub.PrintBridge-win-x64.zip to src/OrderHub.Web/wwwroot/downloads/orderhub-print-bridge/

.PARAMETER SelfContained
    Publish self-contained (larger zip; no .NET 8 runtime required on target PC).

.EXAMPLE
    .\scripts\release\package-print-bridge.ps1

.EXAMPLE
    .\scripts\release\package-print-bridge.ps1 -CopyToWebDownload
#>
[CmdletBinding()]
param(
    [switch] $CopyToWebDownload,
    [switch] $SelfContained
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$projectPath = Join-Path $repoRoot 'src\OrderHub.PrintBridge\OrderHub.PrintBridge.csproj'
$publishDir = Join-Path $repoRoot 'artifacts\print-bridge\win-x64'
$zipPath = Join-Path $repoRoot 'artifacts\print-bridge\OrderHub.PrintBridge-win-x64.zip'
$webDownloadDir = Join-Path $repoRoot 'src\OrderHub.Web\wwwroot\downloads\orderhub-print-bridge'

$readmeText = @'
OrderHub Print Bridge — Portable Windows Package
================================================

This is a portable package (not a Windows installer). Extract the zip on the
Windows computer connected to your receipt printer, then run OrderHub.PrintBridge.exe.

Quick start:
1. Extract all files to a folder (for example C:\OrderHub\PrintBridge).
2. Run OrderHub.PrintBridge.exe.
3. Open the Settings tab in the app.
4. Paste the device token from OrderHub Web (Print Bridge > Devices).
5. Select your Windows receipt printer.
6. Turn off Test mode for real printing.
7. Click Save settings and verify the connection.

Configuration:
- Settings are saved under C:\ProgramData\OrderHub\PrintBridge\appsettings.json
- Do not share appsettings.json if it contains a device token.
- BaseUrl and device token are entered in the app Settings UI, not in this package.

Requirements:
- Windows 10 or later
- .NET 8 Desktop Runtime (unless you received a self-contained build)
- USB or network receipt printer installed in Windows

Support: configure devices and tokens in OrderHub Web > Print Bridge.
'@

Write-Host "Publishing OrderHub.PrintBridge..."
if (Test-Path $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}
New-Item -ItemType Directory -Path (Split-Path $publishDir -Parent) -Force | Out-Null

$publishArgs = @(
    'publish', $projectPath,
    '-c', 'Release',
    '-r', 'win-x64',
    '-o', $publishDir
)
if ($SelfContained) {
    $publishArgs += @('--self-contained', 'true')
} else {
    $publishArgs += @('--self-contained', 'false')
}

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

# Remove local override files from publish output if present.
Get-ChildItem -Path $publishDir -Filter 'appsettings.Local.json' -Recurse -ErrorAction SilentlyContinue |
    Remove-Item -Force

$readmePath = Join-Path $publishDir 'README.txt'
Set-Content -LiteralPath $readmePath -Value $readmeText -Encoding UTF8

Write-Host "Creating zip: $zipPath"
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
New-Item -ItemType Directory -Path (Split-Path $zipPath -Parent) -Force | Out-Null

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $publishDir,
    $zipPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)

if (-not (Test-Path -LiteralPath $zipPath)) {
    throw "Zip was not created: $zipPath"
}

$zipSizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
Write-Host "Package created ($zipSizeMb MB): $zipPath"

if ($CopyToWebDownload) {
    New-Item -ItemType Directory -Path $webDownloadDir -Force | Out-Null
    $webZip = Join-Path $webDownloadDir 'OrderHub.PrintBridge-win-x64.zip'
    Copy-Item -LiteralPath $zipPath -Destination $webZip -Force
    Write-Host "Copied to Web download folder: $webZip"
    Write-Host "Tenant setup page download will serve this file when present."
}

Write-Host 'Done. artifacts/ is gitignored — do not commit the zip.'
