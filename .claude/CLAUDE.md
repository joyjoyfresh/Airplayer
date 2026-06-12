# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

**重要提醒**：请无论如何使用简体中文回答。

## 构建与运行

```bash
# 构建（从仓库根目录）
dotnet build Airplayer.sln

# 运行应用
dotnet run --project AirPlayer.App

# 仅构建协议库
dotnet build AirPlayer.Protocol
```

- 解决方案：`Airplayer.sln`（Visual Studio 2022）
- 目标框架：.NET 8.0
- 当前无测试项目，无 CI/CD 配置

## 项目架构

两个项目，单向依赖：`AirPlayer.App` → `AirPlayer.Protocol`

### AirPlayer.Protocol（协议库，net8.0，平台无关）

实现完整的 AirPlay/AirTunes 接收端协议栈，核心类：

- **AirPlayReceiver** — 顶层入口：mDNS 广播 + RTSP 会话编排，通过 `IAirPlayReceiver` 向上层暴露事件
- **AirTunesListener** — 主 RTSP TCP 控制通道（端口 7020），处理 pair-setup/verify、FairPlay fp-setup、SETUP/TEARDOWN 等
- **MirroringListener** — 原始 TCP，接收加密 H.264 帧，AES-CTR 解密，SPS/PPS 解析，AVCC→AnnexB 转换
- **AudioListener** — 双 UDP（端口 7022/7023），接收加密音频，AES-CBC 解密，AAC 解码，RAOP 缓冲管理
- **SessionManager** — 单例，`ConcurrentDictionary<string, Session>` 管理每个客户端的会话状态

加密流程：ED25519 pair-verify → FairPlay fp-setup（预计算回复）→ `OmgHax.DecryptAesKey()` 派生真实 AES 密钥 → 视频用 AES-CTR，音频用 AES-CBC

### AirPlayer.App（WinUI3 桌面应用，net8.0-windows10.0.19041.0）

- **MainWindow** — 订阅 AirPlayReceiver 事件，协调视频/音频管线
- **VideoPresenter** — 专用渲染线程（MTA），全 GPU 管线编排：
  `H.264 帧` → `HardwareH264Decoder`（MF MFT + D3D11 硬件加速）→ `Nv12VideoProcessor`（D3D11 Video Processor Blt，NV12→BGRA8，BT.709，letterbox）→ `DxgiSwapChainHost`（DXGI flip swap chain 绑定 SwapChainPanel）→ Present
- **AudioSink** — waveOut API PCM 回放，44100Hz 2ch 16-bit，4 缓冲队列 + PTS 时钟

关键设计：帧队列使用 `Channel<H264Data>`（BoundedChannel，DropOldest 策略）吸收突发流量；渲染线程批量解码但只 Present 最新的帧；分辨率变化（iOS 旋转）触发解码器重置 + VideoProcessor 重建

## 关键约定

- 事件驱动架构：协议层通过事件（`OnH264DataReceived`、`OnPcmDataReceived` 等）向上传递数据
- ED25519 身份持久化于 `%LocalAppData%\AirPlayer\identity.dat`
- FairPlay 解密表嵌入为 `table_s1.bin` ~ `table_s10.bin` 资源
- 诊断日志：`DiagLog`（协议层）和 `AudioDiagLog`（音频管线）写时间戳条目
- 端口不可用时自动回退到动态端口
