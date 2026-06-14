<#
  get-fdk-aac.ps1
  Fetches the native fdk-aac.dll (x64) needed for AAC-ELD decoding, places it in
  AirPlayer.App\native\, and copies it into any existing bin output folders.

  Usage (run from repo root):
    powershell -ExecutionPolicy Bypass -File tools\get-fdk-aac.ps1

  How it works: downloads the mingw-w64-x86_64-fdk-aac package from the official
  MSYS2 repo and extracts libfdk-aac-2.dll. Needs the tar.exe bundled with
  Windows 10/11 (with .zst support). If it fails, see the fallbacks at the end.

  NOTE: keep this file ASCII-only. Windows PowerShell 5.1 reads .ps1 using the
  system ANSI code page, so non-ASCII text without a BOM becomes mojibake.
#>

$ErrorActionPreference = 'Stop'

# Repo root (this script lives in tools\)
$root = Split-Path -Parent $PSScriptRoot
$nativeDir = Join-Path $root 'AirPlayer.App\native'
New-Item -ItemType Directory -Force -Path $nativeDir | Out-Null
$target = Join-Path $nativeDir 'fdk-aac.dll'

function Copy-ToBin($src) {
    $binRoot = Join-Path $root 'AirPlayer.App\bin'
    if (Test-Path $binRoot) {
        Get-ChildItem -Path $binRoot -Recurse -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like 'net8.0-windows*' } |
            ForEach-Object {
                Copy-Item $src (Join-Path $_.FullName 'fdk-aac.dll') -Force
                Write-Host "  -> copied to $($_.FullName)"
            }
    }
}

try {
    $base = 'https://repo.msys2.org/mingw/mingw64/'
    Write-Host "[1/4] Fetching MSYS2 package list ..."
    $html = (Invoke-WebRequest -UseBasicParsing -Uri $base).Content

    # NOTE: wrap in @(...) so a single match is still an array.
    # Otherwise $names becomes a string and $names[-1] returns its LAST CHAR (e.g. 't').
    $names = @([regex]::Matches($html, 'mingw-w64-x86_64-fdk-aac-[0-9][^"<> ]*?-any\.pkg\.tar\.zst') |
             ForEach-Object { $_.Value } | Sort-Object -Unique)
    if ($names.Count -eq 0) { throw "fdk-aac not found in the package list" }
    $pkg = $names[-1]
    Write-Host "  found $($names.Count) package(s), using: $pkg"
    $pkgUrl = $base + $pkg
    Write-Host "[2/4] Downloading $pkg ..."
    $tmpPkg = Join-Path $env:TEMP $pkg
    Invoke-WebRequest -UseBasicParsing -Uri $pkgUrl -OutFile $tmpPkg

    Write-Host "[3/4] Extracting ..."
    $extract = Join-Path $env:TEMP 'fdkaac_extract'
    Remove-Item -Recurse -Force $extract -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $extract | Out-Null
    & tar.exe -xf $tmpPkg -C $extract
    if ($LASTEXITCODE -ne 0) { throw "tar extraction failed (your Windows tar may not support .zst; use a fallback below)" }

    $dll = Get-ChildItem -Path $extract -Recurse -Filter 'libfdk-aac-2.dll' | Select-Object -First 1
    if (-not $dll) { throw "libfdk-aac-2.dll not found in the package" }

    Write-Host "[4/4] Installing into project ..."
    Copy-Item $dll.FullName $target -Force
    Write-Host "  -> $target"
    Copy-ToBin $target

    Write-Host ""
    Write-Host "Done! Rebuild and run; the log should show [FDK] AAC-ELD decoder ready." -ForegroundColor Green
}
catch {
    Write-Host ""
    Write-Host "Auto-fetch failed: $($_.Exception.Message)" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Fallback A (vcpkg):" -ForegroundColor Cyan
    Write-Host "  git clone https://github.com/microsoft/vcpkg"
    Write-Host "  .\vcpkg\bootstrap-vcpkg.bat"
    Write-Host "  .\vcpkg\vcpkg install fdk-aac:x64-windows"
    Write-Host "  copy .\vcpkg\installed\x64-windows\bin\fdk-aac.dll `"$nativeDir`""
    Write-Host ""
    Write-Host "Fallback B (MSYS2 already installed):" -ForegroundColor Cyan
    Write-Host "  pacman -S mingw-w64-x86_64-fdk-aac"
    Write-Host "  copy C:\msys64\mingw64\bin\libfdk-aac-2.dll `"$nativeDir\fdk-aac.dll`""
    exit 1
}
