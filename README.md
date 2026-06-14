# AirPlayer

> **当前版本 0.2.0（预发布）** — 0.x 阶段，核心投屏与音画同步已可用，部分功能仍在完善中。

Windows 上的 AirPlay / AirTunes 接收端，接收 iPhone / iPad 的**屏幕镜像投屏**，
全 GPU 硬件解码渲染视频（H.264），并解码播放音频（AAC-ELD），实现**音画同步**。

> ⚠️ **本项目仅供学习与个人研究使用。** 它通过逆向实现的方式兼容 Apple 的
> AirPlay 屏幕镜像协议，与 Apple Inc. 无任何隶属或背书关系。使用前请阅读文末
> [法律声明](#法律声明与免责)。

## 功能

- mDNS 广播 + RTSP 会话，兼容 iOS / iPadOS 的「屏幕镜像」。
- 视频：Media Foundation 硬件 H.264 解码 → D3D11 Video Processor（NV12→BGRA，BT.709，信箱/铺满缩放）
  → DXGI 翻转交换链呈现，低延迟、低 CPU。不支持硬解时自动回退软解。
- 音频：AAC-ELD 解码（fdk-aac）→ waveOut 低延迟播放，有界缓冲与视频对齐。
- 兼容 iOS 旋转引起的分辨率变化、解码器热重置与错误恢复。
- 「更多设置」可配置：分辨率（720p/1080p）、帧率（30/60fps）、音频输出设备、
  截图保存目录、HUD 监控参数、自定义快捷键等。
- 实用操作：全屏 / 铺满、画面旋转、屏幕截图、窗口置顶、实时 HUD（分辨率/帧率/解码/丢帧）。

## 环境要求

- Windows 10 1809（17763）及以上 / Windows 11，x64（部分视觉效果如 Mica 需 Windows 11）。
- .NET 8 SDK；Visual Studio 2022（含「Windows 应用 SDK / WinUI 3」工作负载）。
- **fdk-aac.dll（x64）** —— AAC-ELD 解码必需（见下）。

## 构建与运行

```bash
# 构建（x64）
dotnet build Airplayer.sln -c Debug -p:Platform=x64

# 运行
dotnet run --project AirPlayer.App
```

或在 Visual Studio 2022 打开 `Airplayer.sln` 直接 F5。

投屏方法：保持 iPhone/iPad 与电脑在**同一局域网**，在 iOS 控制中心点「屏幕镜像」，
选择列表中的本机设备名即可。

## 关于 fdk-aac.dll（音频必需）

投屏音频是 **AAC-ELD**。微软 Media Foundation 与 FFmpeg 原生解码器都无法解码 ELD，
唯一可行方案是 **fdk-aac**（RPiPlay / UxPlay 等开源 AirPlay 接收端同样采用）。

> 出于许可证与专利考虑，本仓库**不包含** `fdk-aac.dll`，需自行获取（任选其一）：

```powershell
# 方式 1：一键脚本（从 MSYS2 官方仓库获取）
powershell -ExecutionPolicy Bypass -File tools\get-fdk-aac.ps1

# 方式 2：vcpkg
vcpkg install fdk-aac:x64-windows
copy <vcpkg>\installed\x64-windows\bin\fdk-aac.dll AirPlayer.App\native\
```

把 `fdk-aac.dll` 放进 `AirPlayer.App\native\` 后，构建会自动拷到输出目录。
若缺失该文件，视频可正常投屏，但音频会静音。

## 项目结构与架构

两个项目，均面向 .NET 8：

- **AirPlayer.Protocol**（`net8.0`，平台无关）：AirPlay/AirTunes 协议栈、配对与加密、解码器。
  `AirPlayReceiver` 是对外入口，管理 mDNS 广播与 RTSP 会话，通过事件向 App 层暴露 H.264 帧、PCM 音频帧、投屏状态。
- **AirPlayer.App**（`net8.0-windows`，WinUI 3）：视频渲染管线、音频播放、UI，依赖 Protocol。

**握手流程**：iOS 经 mDNS 发现 → `pair-setup` → `pair-verify`（ED25519 + Curve25519）
→ `fp-setup`（FairPlay）→ `SETUP`（视频流 / 音频流）→ `RECORD`。

**视频管线（全 GPU）**：H.264 Annex-B → 硬件 H.264 解码（MFT）→ NV12 纹理
→ `ID3D11VideoProcessor`（色彩转换 + 缩放）→ DXGI 交换链呈现。

**音频管线**：UDP 接收 AAC-ELD → fdk-aac 解码为 PCM → waveOut 轮转缓冲播放。

## 数据存储位置

应用配置与身份持久化在 `%LocalAppData%\AirPlayer\`：

- `settings.json`：用户设置（分辨率、帧率、HUD、快捷键、窗口位置等）。
- `identity.dat`：设备身份（MAC + ED25519 种子，保证重启后设备标识不变）。

> 这两个文件位于用户目录、**不在仓库内**，无需提交。

## 发布打包

```powershell
powershell -ExecutionPolicy Bypass -File tools\build-release.ps1 -Version 0.2.0
```

产出 `publish\AirPlayer-0.2.0-win-x64\`（含 exe + 运行时 + fdk-aac.dll）及对应 zip。

## 诊断日志

诊断日志仅在 **Debug 构建**生效（`DiagLog` / `AudioDiagLog` 标记了 `[Conditional("DEBUG")]`），
Release 构建零开销、不产生日志文件。排查问题时用 Debug 构建运行，查看：

- `AirPlayer.Protocol\airplay-video.log`：解码器 / 视频管线。
- `AirPlayer.App\audio-debug.log`：音频播放器。

## 法律声明与免责

- 本项目为**个人学习与技术研究**用途的开源实现，**与 Apple Inc. 无任何关联，未获其授权或背书**。
- “AirPlay”“AirTunes”及相关名称是 Apple Inc. 的商标，此处仅用于客观描述兼容性，不作品牌用途。
- 本项目实现 AirPlay 屏幕镜像所需的协议握手（含 FairPlay 相关环节）系基于公开的逆向工程
  社区成果。不同司法辖区对协议逆向与 DRM 相关代码的法律认定不同，**是否分发、如何使用由你
  自行判断并承担相应风险**。
- 软件按“原样（AS IS）”提供，不附带任何明示或暗示的担保；作者不对使用本软件造成的任何后果负责。
- 请仅在你拥有或获得授权的设备之间使用本软件投屏。

## 第三方与致谢

- **Fraunhofer FDK AAC**：用于 AAC-ELD 音频解码，遵循其自身的许可证；AAC 受相关专利约束。
  本仓库不分发该库，由用户自行获取。
- **AirPlay/AirTunes 协议实现**参考并借鉴了开源 AirPlay 接收端社区（如 RPiPlay、UxPlay、shairport 等）的成果；
  相关组件保留其各自的原始许可证。
- WinUI 3 / Windows App SDK、Vortice（Direct3D 11 / DXGI / Media Foundation 托管封装）等开源库。

如对第三方组件的具体许可有疑问，请以各组件上游仓库的 LICENSE 为准。

## 许可证

本项目原创代码以 [MIT](LICENSE) 许可证发布。第三方与衍生组件保留其各自许可证（见上）。
