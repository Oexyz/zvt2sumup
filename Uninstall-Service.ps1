#Requires -Version 5.1
#Requires -RunAsAdministrator

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$ToolsPath = (Join-Path $PSScriptRoot 'ZVT2SumUp.Tools.exe')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$serviceName = 'ZVT2SumUpGateway'
if (-not (Test-Path -LiteralPath $ToolsPath -PathType Leaf)) {
    throw "ZVT2SumUp.Tools.exe wurde nicht gefunden: $ToolsPath"
}
$toolsPath = (Resolve-Path -LiteralPath $ToolsPath).Path

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($null -eq $service) {
    Write-Host 'Der Dienst ist nicht installiert. Es wurden keine Daten verändert.' -ForegroundColor Yellow
    return
}

if (-not $PSCmdlet.ShouldProcess(
        "$serviceName (Konfiguration und Journal bleiben erhalten)",
        'Windows-Dienst stoppen und Registrierung entfernen')) {
    return
}

if ($service.Status -ne 'Stopped') {
    & $toolsPath stop
    if ($LASTEXITCODE -ne 0) {
        throw "Dienst konnte nicht sicher gestoppt werden (Exitcode $LASTEXITCODE)."
    }

    $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
}

& $toolsPath uninstall
if ($LASTEXITCODE -ne 0) {
    throw "Dienstregistrierung konnte nicht entfernt werden (Exitcode $LASTEXITCODE)."
}

Write-Host 'Dienstregistrierung entfernt.' -ForegroundColor Green
Write-Host "Daten unter '$env:ProgramData\ZVT2SumUp' wurden absichtlich nicht gelöscht."
