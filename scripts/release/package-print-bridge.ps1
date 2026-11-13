#Requires -Version 5.1
<#
.SYNOPSIS
    Builds Wasla Print Bridge for Windows: the setup EXE restaurants run, the MSI inside it, a support ZIP, and their
    version and SHA-256 metadata.

.DESCRIPTION
    WAS-62. One self-contained Release win-x64 publish of the app (Wasla.PrintBridge.exe) and the service
    (Wasla.PrintBridge.Service.exe) into one folder, then:

      Wasla.PrintBridge-Setup-x64.exe       The restaurant-facing installer (WiX Burn): installs the Microsoft Edge
                                            WebView2 Runtime when it is missing, then the MSI. The Web download serves it.
      Wasla.PrintBridge-x64.msi             The MSI the setup EXE carries (for administrators who deploy MSIs).
      Wasla.PrintBridge-win-x64-support.zip The same files unpacked, for development and support recovery only. It is not
                                            an installation: the service must be registered by hand (README-SUPPORT.txt).
      Wasla.PrintBridge-package.json        Version, signing state, commit, size and SHA-256 of each file.
      SHA256SUMS.txt                        SHA-256 of each file, in sha256sum format.

    Everything is written under artifacts\ (gitignored). Nothing built here is committed, published or deployed.

    Signing: with -SigningCertificateThumbprint and -TimestampUrl (or WASLA_SIGNING_CERT_THUMBPRINT and
    WASLA_SIGNING_TIMESTAMP_URL), Wasla's own executables and libraries, the MSI, the setup EXE's engine and the setup EXE
    are signed with that certificate from the build machine's certificate store (signtool from the Windows SDK). Without
    them the build is an unsigned development build: its product name says "(unsigned development build)" in the setup
    window and in Installed Apps, and the metadata says signed: false. Such a build is not for restaurants.

    The WebView2 Runtime bootstrapper is Microsoft's (https://go.microsoft.com/fwlink/p/?LinkId=2124703). It is downloaded
    for every build unless -WebView2BootstrapperPath names a copy, and the build stops unless its Authenticode signature is
    valid and Microsoft's.

    WiX Toolset v7 builds the MSI and the setup EXE; its Open Source Maintenance Fee EULA is accepted in
    installer\print-bridge\Directory.Build.props. See docs\integrations\print-bridge-installer.md.

.PARAMETER Version
    major.minor.build, each within Windows Installer's limits (255.255.65535). Default: PrintBridgeVersion in
    src\Wasla.PrintBridge.Version.props.

.PARAMETER OutputDirectory
    Where the files are written. Default: artifacts\print-bridge.

.PARAMETER WebView2BootstrapperPath
    A copy of MicrosoftEdgeWebview2Setup.exe to use instead of downloading it. Its signature is checked the same way.

.PARAMETER SkipSupportZip
    Do not write the support ZIP.

.PARAMETER CopyToWebDownload
    Copy the setup EXE to src\Wasla.Web\wwwroot\downloads\wasla-print-bridge\ for testing the Web download locally.

.EXAMPLE
    .\scripts\release\package-print-bridge.ps1

.EXAMPLE
    .\scripts\release\package-print-bridge.ps1 -SigningCertificateThumbprint 0123...CDEF -TimestampUrl http://timestamp.example.com
#>
[CmdletBinding()]
param(
    [string] $Version,
    [string] $OutputDirectory,
    [string] $WebView2BootstrapperPath,
    [string] $SigningCertificateThumbprint = $env:WASLA_SIGNING_CERT_THUMBPRINT,
    [string] $TimestampUrl = $env:WASLA_SIGNING_TIMESTAMP_URL,
    [switch] $SkipSupportZip,
    [switch] $CopyToWebDownload
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$appProject = Join-Path $repoRoot 'src\Wasla.PrintBridge\Wasla.PrintBridge.csproj'
$serviceProject = Join-Path $repoRoot 'src\Wasla.PrintBridge.Service\Wasla.PrintBridge.Service.csproj'
$packageProject = Join-Path $repoRoot 'installer\print-bridge\Package\Wasla.PrintBridge.Package.wixproj'
$bundleProject = Join-Path $repoRoot 'installer\print-bridge\Bundle\Wasla.PrintBridge.Bundle.wixproj'
$versionProps = Join-Path $repoRoot 'src\Wasla.PrintBridge.Version.props'
$webDownloadDir = Join-Path $repoRoot 'src\Wasla.Web\wwwroot\downloads\wasla-print-bridge'
$webView2Url = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703'

$setupName = 'Wasla.PrintBridge-Setup-x64.exe'
$msiName = 'Wasla.PrintBridge-x64.msi'
$supportZipName = 'Wasla.PrintBridge-win-x64-support.zip'
$metadataName = 'Wasla.PrintBridge-package.json'

if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'artifacts\print-bridge' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$work = Join-Path $OutputDirectory 'build'
$publishDir = Join-Path $work 'publish\win-x64'

function Invoke-Checked([string] $what, [string] $file, [string[]] $arguments) {
    Write-Host "> $what"
    & $file @arguments
    if ($LASTEXITCODE -ne 0) { throw "$what failed with exit code $LASTEXITCODE." }
}

function Get-Sha256([string] $path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# ---- Version ------------------------------------------------------------------------------------------------------
if (-not $Version) {
    $match = [regex]::Match((Get-Content -LiteralPath $versionProps -Raw), '<PrintBridgeVersion[^>]*>([^<]+)</PrintBridgeVersion>')
    if (-not $match.Success) { throw "PrintBridgeVersion was not found in $versionProps." }
    $Version = $match.Groups[1].Value.Trim()
}
$versionMatch = [regex]::Match($Version, '^(\d{1,3})\.(\d{1,3})\.(\d{1,5})$')
if (-not $versionMatch.Success -or [int]$versionMatch.Groups[1].Value -gt 255 -or [int]$versionMatch.Groups[2].Value -gt 255 -or [int]$versionMatch.Groups[3].Value -gt 65535) {
    throw "Version '$Version' is not major.minor.build within 255.255.65535 (Windows Installer compares these three parts)."
}

# ---- Signing ------------------------------------------------------------------------------------------------------
$signed = [bool]($SigningCertificateThumbprint -and $TimestampUrl)
if (($SigningCertificateThumbprint -or $TimestampUrl) -and -not $signed) {
    throw 'Signing needs both a certificate thumbprint and a timestamp URL.'
}
$signTool = ''
if ($signed) {
    if ($SigningCertificateThumbprint -notmatch '^[0-9A-Fa-f]{40}$') { throw 'The signing certificate thumbprint must be 40 hexadecimal digits (SHA-1).' }
    $signTool = Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $signTool) { throw 'signtool.exe (Windows SDK) was not found; it is needed to sign.' }
}
$distribution = if ($signed) { 'Signed release build.' } else { 'Unsigned development build: not for restaurants.' }

Write-Host "Wasla Print Bridge $Version ($distribution)"
# A provided bootstrapper is read before the output folder is replaced: it may be the copy an earlier build left there.
$providedBootstrapper = $null
if ($WebView2BootstrapperPath) {
    if (-not (Test-Path -LiteralPath $WebView2BootstrapperPath -PathType Leaf)) { throw "WebView2BootstrapperPath '$WebView2BootstrapperPath' does not exist." }
    $providedBootstrapper = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $WebView2BootstrapperPath).Path)
}
if (Test-Path -LiteralPath $OutputDirectory) { Remove-Item -LiteralPath $OutputDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $work -Force | Out-Null

# ---- 1. Self-contained publish of the app and the service into one folder -------------------------------------------
$appPublish = Join-Path $work 'publish\app'
$servicePublish = Join-Path $work 'publish\service'
foreach ($item in @(@($appProject, $appPublish), @($serviceProject, $servicePublish))) {
    Invoke-Checked "dotnet publish $([IO.Path]::GetFileName($item[0]))" 'dotnet' @(
        'publish', $item[0], '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        "-p:PrintBridgeVersion=$Version", '-o', $item[1], '--nologo')
}

# Both publishes carry the same .NET runtime; a file with the same name must be the same file, or one executable would
# run with the other's copy. Any difference stops the build.
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
$conflicts = New-Object System.Collections.Generic.List[string]
foreach ($source in @($appPublish, $servicePublish)) {
    foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File) {
        $relative = $file.FullName.Substring($source.Length).TrimStart('\')
        $target = Join-Path $publishDir $relative
        if (Test-Path -LiteralPath $target) {
            if ((Get-Sha256 $target) -ne (Get-Sha256 $file.FullName)) { $conflicts.Add($relative) }
            continue
        }
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}
if ($conflicts.Count -gt 0) {
    throw "The app and the service publish different files under the same name: $($conflicts -join ', '). Align their package versions."
}
# A developer's local overrides never ship.
Get-ChildItem -LiteralPath $publishDir -Filter 'appsettings.Local.json' -Recurse | Remove-Item -Force
foreach ($required in @('Wasla.PrintBridge.exe', 'Wasla.PrintBridge.Service.exe', 'shell-ui\index.html')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDir $required))) { throw "The publish folder has no $required." }
}

if ($signed) {
    $own = @(Get-ChildItem -LiteralPath $publishDir -File | Where-Object { $_.Name -like 'Wasla.*' -and $_.Extension -in '.exe', '.dll' } |
        Select-Object -ExpandProperty FullName)
    Invoke-Checked 'sign Wasla executables and libraries' $signTool (@('sign', '/sha1', $SigningCertificateThumbprint, '/fd', 'SHA256',
        '/tr', $TimestampUrl, '/td', 'SHA256', '/d', 'Wasla Print Bridge') + $own)
}

# ---- 2. Microsoft's WebView2 Runtime bootstrapper ----------------------------------------------------------------------
$bootstrapper = Join-Path $work 'MicrosoftEdgeWebview2Setup.exe'
if ($providedBootstrapper) {
    [IO.File]::WriteAllBytes($bootstrapper, $providedBootstrapper)
}
else {
    Write-Host "> download the WebView2 Runtime bootstrapper from $webView2Url"
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $webView2Url -OutFile $bootstrapper -UseBasicParsing
}
$signature = Get-AuthenticodeSignature -LiteralPath $bootstrapper
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)') {
    throw "The WebView2 Runtime bootstrapper is not validly signed by Microsoft (status $($signature.Status))."
}

# ---- 3. The MSI and the setup EXE (WiX) ------------------------------------------------------------------------------
$wixProperties = @("-p:PrintBridgeVersion=$Version", '-c', 'Release', '--nologo')
if ($signed) {
    $wixProperties += @("-p:WaslaSigningCertificateThumbprint=$SigningCertificateThumbprint", "-p:WaslaSigningTimestampUrl=$TimestampUrl",
        "-p:WaslaSignToolPath=$signTool")
}
$msiOut = Join-Path $work 'msi'
Invoke-Checked 'build the MSI' 'dotnet' (@('build', $packageProject, "-p:PrintBridgePublishDir=$publishDir", "-p:OutputPath=$msiOut") + $wixProperties)
$builtMsi = Join-Path $msiOut $msiName
if (-not (Test-Path -LiteralPath $builtMsi)) { throw "The MSI was not built: $builtMsi" }

$bundleOut = Join-Path $work 'bundle'
Invoke-Checked 'build the setup EXE' 'dotnet' (@('build', $bundleProject, "-p:PrintBridgeMsiPath=$builtMsi",
    "-p:WebView2BootstrapperPath=$bootstrapper", "-p:OutputPath=$bundleOut") + $wixProperties)
$builtSetup = Join-Path $bundleOut $setupName
if (-not (Test-Path -LiteralPath $builtSetup)) { throw "The setup EXE was not built: $builtSetup" }

Copy-Item -LiteralPath $builtSetup -Destination (Join-Path $OutputDirectory $setupName)
Copy-Item -LiteralPath $builtMsi -Destination (Join-Path $OutputDirectory $msiName)

# ---- 4. Support ZIP (development and support recovery only) ------------------------------------------------------------
if (-not $SkipSupportZip) {
    $readme = @"
Wasla Print Bridge $Version - support files (not an installation)
=================================================================

Restaurants install Print Bridge with $setupName. This ZIP holds the same program
files for development and support recovery only.

Since WAS-61 the app (Wasla.PrintBridge.exe) does not print by itself: printing is
done by the Windows Service Wasla.PrintBridge.Service.exe, which only an administrator
can register. Running setup again (or Repair in Settings > Apps > Installed apps) is
the supported way to repair an installation. The manual service commands are in
docs/integrations/print-bridge-service.md (Installer handoff) in the Wasla repository.

Pairing, settings and history stay in C:\ProgramData\Wasla\PrintBridge. Never share
appsettings.json from there: it holds the device token.

$distribution
"@
    $readmePath = Join-Path $publishDir 'README-SUPPORT.txt'
    Set-Content -LiteralPath $readmePath -Value $readme -Encoding UTF8
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($publishDir, (Join-Path $OutputDirectory $supportZipName),
        [IO.Compression.CompressionLevel]::Optimal, $false)
    Remove-Item -LiteralPath $readmePath
}

# ---- 5. Version and SHA-256 metadata -----------------------------------------------------------------------------------
$roles = [ordered]@{ $setupName = 'setup'; $msiName = 'msi'; $supportZipName = 'support-zip' }
$files = @()
$sums = @()
foreach ($name in $roles.Keys) {
    $path = Join-Path $OutputDirectory $name
    if (-not (Test-Path -LiteralPath $path)) { continue }
    $hash = Get-Sha256 $path
    $files += [ordered]@{ name = $name; role = $roles[$name]; bytes = (Get-Item -LiteralPath $path).Length; sha256 = $hash }
    $sums += "$hash  $name"
}
$commit = & git -C $repoRoot rev-parse HEAD 2>$null
$localChanges = [bool](& git -C $repoRoot status --porcelain 2>$null)
$metadata = [ordered]@{
    product = 'Wasla Print Bridge'
    version = $Version
    signed = $signed
    distribution = $distribution
    commit = $(if ($commit) { "$commit".Trim() } else { $null })
    commitHasLocalChanges = $localChanges
    builtAtUtc = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    files = $files
    webView2Bootstrapper = [ordered]@{
        source = $(if ($WebView2BootstrapperPath) { 'provided' } else { $webView2Url })
        sha256 = (Get-Sha256 $bootstrapper)
        signer = $signature.SignerCertificate.Subject
    }
}
$metadata | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory $metadataName) -Encoding UTF8
$sums | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ASCII

foreach ($file in $files) {
    Write-Host ("{0,-40} {1,12:N0} bytes  sha256 {2}" -f $file.name, $file.bytes, $file.sha256)
}

if ($CopyToWebDownload) {
    New-Item -ItemType Directory -Path $webDownloadDir -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $OutputDirectory $setupName) -Destination (Join-Path $webDownloadDir $setupName) -Force
    Write-Host "Copied $setupName to $webDownloadDir; the Web setup page serves it from there."
    if (-not $signed) { Write-Warning 'This setup is an unsigned development build. Use it for local testing only.' }
}

Write-Host "Done: $OutputDirectory (gitignored; do not commit these files)."
