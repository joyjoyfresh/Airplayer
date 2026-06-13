# AirPlayer

> **当前版本 0.2.0（预发布）** — 0.x 阶段，核心投屏与音画同步已可用，部分功能仍在完善中。

Windows 上的 AirPlay / AirTunes 接收端，接收 iPhone / iPad 的**屏幕镜像投屏**，
全 GPU 硬件解码渲染视频（H.264），并解码播放音频（AAC-ELD），实现**音画同步**。

## 功能

- mDNS 广播 + RTSP 会话，兼容 iOS/iPadOS 屏幕镜像。
- 视频：Media Foundation 硬件 H.264 解码 → D3D11 Video Processor（NV12→BGRA，BT.709，信箱缩放）
  → DXGI 翻转交换链呈现，低延迟、低 CPU。
- 音频：AAC-ELD 解码（fdk-aac）→ waveOut 低延迟播放，有界缓冲与视频对齐。
- 支持 iOS 旋转导致的分辨率变化、解码器热重置、错误恢复。

## 环境要求

- Windows 10 (1903+) / 11，x64
- .NET 8 SDK、Visual Studio 2022（含 Windows App SDK / WinUI3 工作负载）
- **fdk-aac.dll（x64）** —— AAC-ELD 解码必需（见下）

## 构建与运行

```bash
# 构建
dotnet build Airplayer.sln -c Debug

# 运行
dotnet run --project AirPlayer.App
```

或在 Visual Studio 2022 打开 `Airplayer.sln` 直接 F5。

## 关于 fdk-aac.dll（音频必需）

投屏音频是 **AAC-ELD**。微软 Media Foundation 与 FFmpeg 原生解码器都无法解码 ELD，
唯一可行方案是 **fdk-aac**（RPiPlay / UxPlay 等 AirPlay 接收端同样采用）。

程序启动时若检测到缺少 `fdk-aac.dll`，会**自动从 MSYS2 官方仓库下载**并放到输出目录。
若所在网络无法下载，可手动获取（任选其一）：

```powershell
# 方式 1：一键脚本
powershell -ExecutionPolicy Bypass -File tools\get-fdk-aac.ps1

# 方式 2：vcpkg
vcpkg install fdk-aac:x64-windows
copy <vcpkg>\installed\x64-windows\bin\fdk-aac.dll AirPlayer.App\native\
```

把 `fdk-aac.dll` 放进 `AirPlayer.App\native\` 后，构建会自动拷到输出目录。

## 项目结构

- **AirPlayer.Protocol**（net8.0，平台无关）：AirPlay/AirTunes 协议栈、加密、解码器。
- **AirPlayer.App**（WinUI3，net8.0-windows）：视频渲染管线、音频播放、UI。

详见 `AirPlayer.App\.claude\CLAUDE.md` 的架构说明。

## 发布打包

```powershell
powershell -ExecutionPolicy Bypass -File tools\build-release.ps1 -Version 0.2.0
```

产出 `publish\AirPlayer-0.2.0-win-x64\`（含 exe + 运行时 + fdk-aac.dll）及对应 zip。

## 诊断日志

诊断日志仅在 **Debug 构建**生效（`DiagLog` / `AudioDiagLog` 标记了 `[Conditional("DEBUG")]`），
Release 构建零开销、不产生日志文件。排查问题时用 Debug 构建运行，查看：

- `AirPlayer.Protocol\airplay-video.log`：解码器 / 视频管线（`[FDK]` `[DEC]` `[PRS]`）
- `AirPlayer.App\audio-debug.log`：音频播放器（`[PLAY]` `[ENQ]` 等）
