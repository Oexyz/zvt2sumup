#Requires -Version 5.1
#Requires -RunAsAdministrator

[CmdletBinding()]
param(
    [string]$PackageDirectory = $PSScriptRoot,

    [switch]$Start
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $PackageDirectory -PathType Container)) {
    throw "Paketordner existiert nicht: $PackageDirectory"
}
$resolvedPackage = (Resolve-Path -LiteralPath $PackageDirectory).Path
$toolsPath = Join-Path $resolvedPackage 'ZVT2SumUp.Tools.exe'
$gatewayPath = Join-Path $resolvedPackage 'ZVT2SumUpGateway.exe'

foreach ($path in @($toolsPath, $gatewayPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Erwartete Datei fehlt: $path. Beide EXE-Dateien müssen im selben Ordner wie dieses Skript liegen."
    }
    if ([IO.Path]::GetExtension($path) -ne '.exe') {
        throw "Unerwarteter Dateityp: $path"
    }
}

Write-Host 'Zu installierende Dateien:' -ForegroundColor Cyan
foreach ($path in @($gatewayPath, $toolsPath)) {
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    Write-Host "  $([IO.Path]::GetFileName($path))"
    Write-Host "    SHA-256: $hash"
    Write-Host "    Signatur: $($signature.Status)"
}

Write-Warning 'Prüfen Sie Herkunft, Hashes und Zielpfad vor der Bestätigung. Der aktuelle Build kann unsigniert sein.'

& $toolsPath install $gatewayPath
if ($LASTEXITCODE -ne 0) {
    throw "Dienstinstallation fehlgeschlagen (Exitcode $LASTEXITCODE)."
}

if ($Start) {
    & $toolsPath start
    if ($LASTEXITCODE -ne 0) {
        throw "Der Dienst wurde installiert, konnte aber nicht gestartet werden (Exitcode $LASTEXITCODE)."
    }
}

& $toolsPath status
if ($LASTEXITCODE -ne 0) {
    throw "Dienststatus konnte nicht gelesen werden (Exitcode $LASTEXITCODE)."
}

Write-Host 'Dienstaktion erfolgreich. Es wurden keine Konfigurations- oder Zahlungsdaten gelöscht.' -ForegroundColor Green
