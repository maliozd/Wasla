#Requires -Version 5.1
<#
.SYNOPSIS
    Generates a trusted local HTTPS certificate for Wasla tenant domains using mkcert.

.DESCRIPTION
    Run from the repository root:
        .\scripts\dev\setup-local-https.ps1

    Creates .certs/wasla-local.pem, wasla-local-key.pem, and wasla-local.pfx
    for *.wasla.local, wasla.local, localhost, and 127.0.0.1.
    Generated files are gitignored and must not be committed.

    Legacy: older setups may still have .certs/orderhub-local.* from the OrderHub era.
    Those files are not used by current Kestrel config; regenerate with this script.
#>
param(
    [string] $PfxPassword = "wasla-dev"
)

$ErrorActionPreference = "Stop"

function Write-Step([string] $Message) {
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Note([string] $Message) {
    Write-Host $Message -ForegroundColor Yellow
}

function Write-Ok([string] $Message) {
    Write-Host $Message -ForegroundColor Green
}

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
Set-Location $RepoRoot

Write-Step "Wasla local HTTPS setup"
Write-Host "Repository root: $RepoRoot"

$CertsDir = Join-Path $RepoRoot ".certs"
if (-not (Test-Path $CertsDir)) {
    New-Item -ItemType Directory -Path $CertsDir | Out-Null
    Write-Ok "Created .certs directory."
}

$pemFile = Join-Path $CertsDir "wasla-local.pem"
$keyFile = Join-Path $CertsDir "wasla-local-key.pem"
$pfxFile = Join-Path $CertsDir "wasla-local.pfx"

$mkcert = Get-Command mkcert -ErrorAction SilentlyContinue
if (-not $mkcert) {
    Write-Note "mkcert was not found in PATH."
    Write-Host ""
    Write-Host "Install mkcert using one of these options:"
    Write-Host "  winget install FiloSottile.mkcert"
    Write-Host ""
    Write-Host "Or download mkcert.exe manually from:"
    Write-Host "  https://github.com/FiloSottile/mkcert/releases"
    Write-Host ""
    Write-Host "After installation, open a new terminal and run this script again."
    exit 1
}

Write-Ok "Found mkcert at: $($mkcert.Source)"

Write-Step "Installing local mkcert root CA (if not already trusted)"
try {
    & mkcert -install
    Write-Ok "mkcert root CA is installed/trusted."
}
catch {
    Write-Note "mkcert -install failed. You may need to run PowerShell as Administrator."
    Write-Host $_.Exception.Message
    exit 1
}

Write-Step "Generating certificate for Wasla local domains"
& mkcert `
    -cert-file $pemFile `
    -key-file $keyFile `
    "*.wasla.local" `
    "wasla.local" `
    "localhost" `
    "127.0.0.1"

Write-Ok "Created:"
Write-Host "  $pemFile"
Write-Host "  $keyFile"

function Get-OpenSslPath {
    $fromPath = Get-Command openssl -ErrorAction SilentlyContinue
    if ($fromPath) {
        return $fromPath.Source
    }

    $gitOpenSsl = "${env:ProgramFiles}\Git\usr\bin\openssl.exe"
    if (Test-Path $gitOpenSsl) {
        return $gitOpenSsl
    }

    return $null
}

$openssl = Get-OpenSslPath
if (-not $openssl) {
    Write-Note "OpenSSL was not found in PATH or Git for Windows."
    Write-Host ""
    Write-Host "Install one of the following, then run this script again:"
    Write-Host "  winget install Git.Git"
    Write-Host "  https://git-scm.com/download/win"
    Write-Host ""
    Write-Host "Or install OpenSSL and ensure openssl.exe is available in PATH."
    Write-Host ""
    Write-Host "PEM files were created successfully. Convert to PFX manually with:"
    Write-Host "  openssl pkcs12 -export -out .certs/wasla-local.pfx -inkey .certs/wasla-local-key.pem -in .certs/wasla-local.pem -password pass:wasla-dev"
    exit 1
}

Write-Ok "Found OpenSSL at: $openssl"

Write-Step "Converting PEM to PFX"
& $openssl pkcs12 -export `
    -out $pfxFile `
    -inkey $keyFile `
    -in $pemFile `
    -password "pass:$PfxPassword"

Write-Ok "Created: $pfxFile"

Write-Step "Next steps"
Write-Host @"
1. Add hosts entries on this PC (run Notepad as Administrator, edit):
     C:\Windows\System32\drivers\etc\hosts

   Example:
     127.0.0.1 bahce-tadi.wasla.local
     127.0.0.1 pilavcirahim.wasla.local
     127.0.0.1 sushim.wasla.local
     127.0.0.1 wasla.local

2. Ensure src/OrderHub.Web/appsettings.Development.json contains Kestrel certificate config
   pointing to ../../.certs/wasla-local.pfx (relative to the Web project directory).

3. Run Web:
     dotnet run --project src\OrderHub.Web\OrderHub.Web.csproj

4. Test in browser:
     https://wasla.local:7200
     https://bahce-tadi.wasla.local:7200/auth/login

5. Configure Print Bridge BaseUrl:
     https://bahce-tadi.wasla.local:7200

See docs/local-https.md for full details and LAN testing notes.
"@
