#Requires -Version 5.1

[CmdletBinding()]
param(
    [ValidateSet('Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$solution = Join-Path $repositoryRoot 'Zvt2SumUp.slnx'
$localDotNet = Join-Path $repositoryRoot '.dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotNet -PathType Leaf) {
    $localDotNet
} else {
    $command = Get-Command dotnet -ErrorAction Stop
    $command.Source
}

if (-not (Test-Path -LiteralPath $solution -PathType Leaf)) {
    throw "Solution fehlt: $solution"
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$releaseRoot = Join-Path $repositoryRoot "artifacts\publish-$stamp"
$gatewayStage = Join-Path $releaseRoot 'gateway'
$toolsStage = Join-Path $releaseRoot 'tools'
$package = Join-Path $releaseRoot 'ZVT2SumUp-win-x64'

foreach ($path in @($releaseRoot, $gatewayStage, $toolsStage, $package)) {
    $full = [IO.Path]::GetFullPath($path)
    $artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsicherer Ausgabepfad abgelehnt: $full"
    }
    New-Item -ItemType Directory -Path $full -Force | Out-Null
}

function Invoke-DotNet {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    & $dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') fehlgeschlagen (Exitcode $LASTEXITCODE)."
    }
}

function Invoke-CheckedExecutable {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $start = Start-Process -FilePath $Path -ArgumentList $Arguments -Wait -PassThru -NoNewWindow
    if ($start.ExitCode -ne 0) {
        throw "$([IO.Path]::GetFileName($Path)) $($Arguments -join ' ') fehlgeschlagen (Exitcode $($start.ExitCode))."
    }
}

function Invoke-CashSimulatorSmoke {
    param([Parameter(Mandatory = $true)][string]$Path)

    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $Path
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    try {
        if (-not $process.Start()) { throw 'Kassensimulator konnte nicht gestartet werden.' }
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.WriteLine('0')
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(15000)) {
            try { $process.Kill() } catch { }
            throw 'Kassensimulator-Smoke-Test lief in ein Timeout.'
        }
        $text = $output.GetAwaiter().GetResult() + $errorOutput.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0 -or -not $text.Contains('ZVT2SumUp KASSENSIMULATOR') -or -not $text.Contains('Zahlung senden')) {
            throw "Standardstart des Kassensimulators ist unvollständig (Exitcode $($process.ExitCode))."
        }
    } finally {
        $process.Dispose()
    }
}

Push-Location $repositoryRoot
try {
    & (Join-Path $repositoryRoot 'build\Generate-Icon.ps1') | Out-Null

    Invoke-DotNet @('restore', $solution, '--locked-mode')
    Invoke-DotNet @('build', $solution, '-c', $Configuration, '--no-restore')
    Invoke-DotNet @('test', (Join-Path $repositoryRoot 'tests\Zvt2SumUp.Tests\Zvt2SumUp.Tests.csproj'), '-c', $Configuration, '--no-build')

    Invoke-DotNet @(
        'publish', (Join-Path $repositoryRoot 'src\Zvt2SumUp.Desktop\Zvt2SumUp.Desktop.csproj'),
        '-c', $Configuration, '-r', $Runtime, '--self-contained', 'true', '--no-restore',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-o', $gatewayStage
    )
    Invoke-DotNet @(
        'publish', (Join-Path $repositoryRoot 'src\Zvt2SumUp.Tools\Zvt2SumUp.Tools.csproj'),
        '-c', $Configuration, '-r', $Runtime, '--self-contained', 'true', '--no-restore',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-o', $toolsStage
    )

    $gatewayExe = Join-Path $gatewayStage 'ZVT2SumUpGateway.exe'
    $toolsExe = Join-Path $toolsStage 'ZVT2SumUp.Tools.exe'
    foreach ($path in @($gatewayExe, $toolsExe)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Veröffentlichte EXE fehlt: $path"
        }
        if ((Get-Item -LiteralPath $path).Length -lt 1MB) {
            throw "Veröffentlichte EXE ist unerwartet klein: $path"
        }
    }

    Copy-Item -LiteralPath $gatewayExe -Destination $package
    Copy-Item -LiteralPath $toolsExe -Destination $package

    $expected = @('ZVT2SumUp.Tools.exe', 'ZVT2SumUpGateway.exe')
    $packagedFiles = @(Get-ChildItem -LiteralPath $package -File)
    $difference = Compare-Object ($packagedFiles.Name | Sort-Object) $expected
    if ($packagedFiles.Count -ne 2 -or $null -ne $difference) {
        throw 'Das Endpaket muss exakt ZVT2SumUpGateway.exe und ZVT2SumUp.Tools.exe enthalten.'
    }

    Invoke-CheckedExecutable (Join-Path $package 'ZVT2SumUpGateway.exe') @('--smoke-test')
    Invoke-CheckedExecutable (Join-Path $package 'ZVT2SumUpGateway.exe') @('--layout-smoke-test')
    Invoke-CheckedExecutable (Join-Path $package 'ZVT2SumUpGateway.exe') @('--service-smoke-test')
    Invoke-CheckedExecutable (Join-Path $package 'ZVT2SumUp.Tools.exe') @('help')
    Invoke-CashSimulatorSmoke (Join-Path $package 'ZVT2SumUp.Tools.exe')

    $archive = Join-Path $releaseRoot 'ZVT2SumUp-win-x64.zip'
    Compress-Archive -LiteralPath $packagedFiles.FullName -DestinationPath $archive -CompressionLevel Optimal
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $zipNames = @($zip.Entries | ForEach-Object FullName | Sort-Object)
        if ($zipNames.Count -ne 2 -or $null -ne (Compare-Object $zipNames $expected)) {
            throw 'Release-ZIP muss exakt die beiden freigegebenen EXE-Dateien im Stamm enthalten.'
        }
    } finally {
        $zip.Dispose()
    }

    $manifest = Join-Path $releaseRoot 'checksums.sha256'
    $archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifestLine = "$archiveHash  ZVT2SumUp-win-x64.zip"
    [IO.File]::WriteAllLines($manifest, @($manifestLine), [Text.UTF8Encoding]::new($false))

    Write-Host ''
    Write-Host 'Release-Kandidat erfolgreich erstellt:' -ForegroundColor Green
    Write-Host "  Zwei-EXE-Paket: $package"
    Write-Host "  GitHub-Release-ZIP: $archive"
    Write-Host "  SHA-256-Manifest: $manifest"
    Write-Host "  Dienstinstallation: .\Install-Service.ps1 -PackageDirectory '$package'"
    Write-Host "  $manifestLine"
    foreach ($file in $packagedFiles | Sort-Object Name) {
        Write-Host "  EXE $((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($file.Name)"
    }
} finally {
    Pop-Location
}
