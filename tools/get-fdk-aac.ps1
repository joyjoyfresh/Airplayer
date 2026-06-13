<#
  get-fdk-aac.ps1
  自动获取 AAC-ELD 解码所需的原生库 fdk-aac.dll（x64），放到 AirPlayer.App\native\，
  并同步拷贝到已存在的 bin 输出目录。

  用法（在仓库根目录）：
    powershell -ExecutionPolicy Bypass -File tools\get-fdk-aac.ps1

  原理：从 MSYS2 官方仓库下载 mingw-w64-x86_64-fdk-aac 包，解出其中的 libfdk-aac-2.dll。
  需要 Windows 10/11 自带的 tar.exe（支持 .zst）。若失败，见文末的备用方案。
#>

$ErrorActionPreference = 'Stop'

# 仓库根目录（本脚本位于 tools\ 下）
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
                Write-Host "  → 已拷到 $($_.FullName)"
            }
    }
}

try {
    $base = 'https://repo.msys2.org/mingw/mingw64/'
    Write-Host "[1/4] 获取 MSYS2 包列表 ..."
    $html = (Invoke-WebRequest -UseBasicParsing -Uri $base).Content

    $names = [regex]::Matches($html, 'mingw-w64-x86_64-fdk-aac-[0-9][^"<> ]*?-any\.pkg\.tar\.zst') |
             ForEach-Object { $_.Value } | Sort-Object -Unique
    if (-not $names) { throw "包列表中未找到 fdk-aac" }
    $pkg = $names[-1]
    $pkgUrl = $base + $pkg
    Write-Host "[2/4] 下载 $pkg ..."
    $tmpPkg = Join-Path $env:TEMP $pkg
    Invoke-WebRequest -UseBasicParsing -Uri $pkgUrl -OutFile $tmpPkg

    Write-Host "[3/4] 解包 ..."
    $extract = Join-Path $env:TEMP 'fdkaac_extract'
    Remove-Item -Recurse -Force $extract -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $extract | Out-Null
    & tar.exe -xf $tmpPkg -C $extract
    if ($LASTEXITCODE -ne 0) { throw "tar 解包失败（你的 Windows tar 可能不支持 .zst，请用文末备用方案）" }

    $dll = Get-ChildItem -Path $extract -Recurse -Filter 'libfdk-aac-2.dll' | Select-Object -First 1
    if (-not $dll) { throw "包内未找到 libfdk-aac-2.dll" }

    Write-Host "[4/4] 安装到工程 ..."
    Copy-Item $dll.FullName $target -Force
    Write-Host "  → $target"
    Copy-ToBin $target

    Write-Host ""
    Write-Host "完成！现在重新编译运行；日志应出现 [FDK] AAC-ELD 解码器就绪。" -ForegroundColor Green
}
catch {
    Write-Host ""
    Write-Host "自动获取失败：$($_.Exception.Message)" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "备用方案 A（vcpkg）：" -ForegroundColor Cyan
    Write-Host "  git clone https://github.com/microsoft/vcpkg"
    Write-Host "  .\vcpkg\bootstrap-vcpkg.bat"
    Write-Host "  .\vcpkg\vcpkg install fdk-aac:x64-windows"
    Write-Host "  copy .\vcpkg\installed\x64-windows\bin\fdk-aac.dll `"$nativeDir`""
    Write-Host ""
    Write-Host "备用方案 B（MSYS2 已安装）：" -ForegroundColor Cyan
    Write-Host "  pacman -S mingw-w64-x86_64-fdk-aac"
    Write-Host "  copy C:\msys64\mingw64\bin\libfdk-aac-2.dll `"$nativeDir\fdk-aac.dll`""
    exit 1
}
