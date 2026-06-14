<#
  build-release.ps1 - one-click release packaging
  Produces a self-contained run-anywhere folder + zip
  (includes .NET runtime, Windows App SDK, fdk-aac.dll).

  Usage (run from repo root):
    powershell -ExecutionPolicy Bypass -File tools\build-release.ps1 -Version 1.0.0

  Requires: .NET 8 SDK and the Windows App SDK workload (installed with VS2022).

  NOTE: keep this file ASCII-only. Windows PowerShell 5.1 reads .ps1 using the
  system ANSI code page, so non-ASCII text without a BOM becomes mojibake.
#>
param(
    [string]$Version = "1.0.0",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $outName = "AirPlayer-$Version-$Runtime"
    $outDir = Join-Path $root "publish\$outName"

    Write-Host "[1/4] Cleaning old output ..."
    if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }

    Write-Host "[2/4] dotnet publish (Release, $Runtime, self-contained) ..."
    dotnet publish "AirPlayer.App\AirPlayer.App.csproj" `
        -c Release -r $Runtime --self-contained `
        -p:Platform=x64 -p:WindowsPackageType=None -p:Version=$Version `
        -o $outDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (is the Windows App SDK workload installed?)" }

    Write-Host "[3/4] Checking fdk-aac.dll ..."
    if (-not (Test-Path (Join-Path $outDir "fdk-aac.dll"))) {
        $nativeDll = Join-Path $root "AirPlayer.App\native\fdk-aac.dll"
        if (-not (Test-Path $nativeDll)) {
            Write-Host "  native\fdk-aac.dll not found, trying get-fdk-aac.ps1 ..."
            & (Join-Path $PSScriptRoot "get-fdk-aac.ps1")
        }
        if (Test-Path $nativeDll) {
            Copy-Item $nativeDll $outDir -Force
            Write-Host "  fdk-aac.dll added"
        } else {
            Write-Warning "  Could not obtain fdk-aac.dll -- audio decoder missing (the app will try to auto-download on first run)."
        }
    }

    Write-Host "[4/4] Creating zip ..."
    $zip = Join-Path $root "publish\$outName.zip"
    if (Test-Path $zip) { Remove-Item -Force $zip }
    Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zip

    Write-Host ""
    Write-Host "Done!" -ForegroundColor Green
    Write-Host "  Folder: $outDir"
    Write-Host "  Zip:    $zip"
}
finally {
    Pop-Location
}
