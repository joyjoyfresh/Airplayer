<#
  build-release.ps1 — 一键发布打包
  产出独立可运行目录 + zip（含 .NET 运行时、Windows App SDK、fdk-aac.dll）。

  用法（在仓库根目录）：
    powershell -ExecutionPolicy Bypass -File tools\build-release.ps1 -Version 1.0.0

  前置：已安装 .NET 8 SDK 与 Windows App SDK 工作负载（VS2022 安装即带）。
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

    Write-Host "[1/4] 清理旧产物 ..."
    if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }

    Write-Host "[2/4] dotnet publish (Release, $Runtime, 自包含) ..."
    dotnet publish "AirPlayer.App\AirPlayer.App.csproj" `
        -c Release -r $Runtime --self-contained `
        -p:Platform=x64 -p:WindowsPackageType=None -p:Version=$Version `
        -o $outDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（请确认已装 Windows App SDK 工作负载）" }

    Write-Host "[3/4] 确认 fdk-aac.dll ..."
    if (-not (Test-Path (Join-Path $outDir "fdk-aac.dll"))) {
        $nativeDll = Join-Path $root "AirPlayer.App\native\fdk-aac.dll"
        if (-not (Test-Path $nativeDll)) {
            Write-Host "  native\fdk-aac.dll 不存在，尝试用 get-fdk-aac.ps1 获取 ..."
            & (Join-Path $PSScriptRoot "get-fdk-aac.ps1")
        }
        if (Test-Path $nativeDll) {
            Copy-Item $nativeDll $outDir -Force
            Write-Host "  已加入 fdk-aac.dll"
        } else {
            Write-Warning "  未能获取 fdk-aac.dll —— 发布包内将缺少音频解码库（程序首次运行会尝试自动下载）。"
        }
    }

    Write-Host "[4/4] 打包 zip ..."
    $zip = Join-Path $root "publish\$outName.zip"
    if (Test-Path $zip) { Remove-Item -Force $zip }
    Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zip

    Write-Host ""
    Write-Host "完成！" -ForegroundColor Green
    Write-Host "  目录: $outDir"
    Write-Host "  压缩包: $zip"
}
finally {
    Pop-Location
}
