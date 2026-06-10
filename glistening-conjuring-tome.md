# Airplayer —— Windows 当 AirPlay 接收器（WinUI3）开发计划

## Context（为什么做、要解决什么）

目标：把 Windows PC 变成一台“电视”，接收同一局域网内 iPhone 的 AirPlay 投屏，在一个 WinUI3 窗口里**内嵌**显示 iPhone 的屏幕画面（v1 仅视频，音频留到 v2）。

关键现实约束（决定了整个方案）：
- iPhone 投屏用的是 Apple 私有的 **AirPlay 2 镜像协议**，包含 mDNS 广播发现、RTSP 控制、FairPlay 加密配对、H.264 视频打包、AAC-ELD 音频。**从零实现不现实**（尤其加密配对），必须复用成熟开源实现。
- 已确认最佳复用来源：**[YimingZhanshen/Airplay2OnWindows](https://github.com/YimingZhanshen/Airplay2OnWindows)**（C#/.NET 8，MIT，Windows 原生），其底层是 **[SteeBono/airplayreceiver](https://github.com/SteeBono/airplayreceiver)**。两者的协议部分是纯 C# 网络代码，可搬进类库被 WinUI3 引用。
- 它们现状用“命名管道 → 外部 ffplay 窗口”显示视频，**不是内嵌**。本项目的核心增量 = 把视频换成 **WinUI3 原生 `MediaPlayerElement` 内嵌渲染**。

最终形态（已与用户确认的架构 A）：
```
iPhone ──AirPlay2──▶ [C# 协议核心(复用)]  mDNS/RTSP/配对/H264解包
                          │ H264 access units (+SPS/PPS)
                          ▼
                  MediaStreamSource ──▶ MediaPlayerElement (WinUI3 窗口内画面)
                          │ AAC-ELD 音频包  →  v1 直接丢弃（v2 再做）
```

> 说明：本项目复用的协议实现均标注“仅供学习用途（educational purposes only）”，属逆向工程。仅用于个人学习/局域网内自有设备，勿用于商业分发。

> 编码约定（项目 CLAUDE.md 强制）：所有代码注释用**简体中文**，原则上尽量逐行注释；函数定义行写用途注释，参数/返回值放 docstring。

---

## 解决方案总览（分阶段，新手友好，小步见效）

技术栈：**.NET 8 + Windows App SDK (WinUI3) + C#**。
解决方案 `Airplayer.sln` 拆两个工程：

| 工程 | 类型 | 职责 |
|------|------|------|
| `AirPlayer.Protocol` | 类库 (.NET 8) | 搬入并改造 SteeBono/Airplay2OnWindows 的协议核心：mDNS 广播、RTSP 服务、AirPlay2 配对/解密、H264 解包。对外暴露事件：`VideoConfigReceived(sps, pps)`、`VideoFrameReceived(byte[] nal, ulong ptsTicks)`。音频路径 v1 留空/丢弃。 |
| `AirPlayer.App` | WinUI3 打包应用 (.NET 8) | UI：一个全屏 `MediaPlayerElement` + 状态栏。订阅 `AirPlayer.Protocol` 事件，把 H264 帧喂给 `MediaStreamSource` 显示。 |

---

## 阶段拆解

### Phase 0 —— 环境与 WinUI3 基础（确认工具链能跑）
- Visual Studio 安装工作负载：**“.NET 桌面开发”** + **“Windows 应用 SDK / WinUI”**（含 Windows App SDK）；确认 **.NET 8 SDK**。
- 用模板新建一个空白 **Blank App, Packaged (WinUI 3 in Desktop)** 工程，按 F5 跑起来看到空窗口。
- 熟悉 `App.xaml` / `MainWindow.xaml` / `MainWindow.xaml.cs` 结构。
- **里程碑**：空窗口能启动 = 工具链 OK。

### Phase 1 —— 先用现成引擎验证“能不能投上来”（最便宜的排雷）
- **不写代码**，先下载 Airplay2OnWindows 的 release 直接运行（或任一 UxPlay 封装版）。
- 用 iPhone 控制中心 → 屏幕镜像，确认能看到这台 PC 并投屏成功（画面出现在它的 ffplay 窗口）。
- 借此排查：iPhone 与 PC 是否同一 Wi-Fi/局域网、是否需要装 **Bonjour 服务**、**Windows 防火墙**放行（注意 AirPlay 需放行 3 个 UDP 端口，少放一个会丢音频）、iOS 版本兼容。
- **里程碑**：现成引擎能投屏成功 = 网络/发现/防火墙这条链路没问题，后面的失败一定是我们自己的代码问题，定位范围大幅缩小。

### Phase 2 —— 让 iPhone 能“发现”我们自己的 App（设备发现）
- 建 `Airplayer.sln`，加入 `AirPlayer.Protocol`（类库）与 `AirPlayer.App`（WinUI3）。
- 把 SteeBono/Airplay2OnWindows 的 `AirPlay/` 协议源码搬入 `AirPlayer.Protocol`，先只让 **mDNS 广播**跑起来，使 PC 出现在 iPhone 的“屏幕镜像”列表里（此时点进去连不上没关系）。
- **主要风险点 = mDNS 广播怎么实现**：先查清参考工程用的是哪种 —— 若依赖 Apple **Bonjour**（随 iTunes 安装），就装 Bonjour；若想纯托管无外部依赖，可换 **Makaretu.Dns**（纯 C# 可广播）。在本阶段定下来。
- **里程碑**：iPhone 控制中心能看到名为 “Airplayer” 的设备。

### Phase 3 —— 接收并内嵌显示视频（**v1 的核心目标**）
1. **完成握手**：复用协议核心跑通 RTSP SETUP + FairPlay 配对。视频、音频两条流都要接受 SETUP 应答，但**音频包收到即丢弃**（v1 不解码音频，因此**无需编译 FDK-AAC/ALAC 原生库**，大幅省事）。
2. **取出 H264**：从解包器拿到 SPS/PPS 与每个 H264 access unit，转抛成 `AirPlayer.Protocol` 的事件。
3. **WinUI3 视频接收器**（核心新代码，建议放 `AirPlayer.App/VideoSink.cs`）：
   - 建 `VideoEncodingProperties.CreateH264()` → `VideoStreamDescriptor` → `MediaStreamSource`。
   - 订阅 `MediaStreamSource.SampleRequested`：用 deferral 从一个线程安全队列取帧，`MediaStreamSample.CreateFromBuffer(buffer, timestamp)` 返回。
   - `MediaSource.CreateFromMediaStreamSource(mss)` 赋给 `MediaPlayerElement.Source`，调 `Play()`。硬解由 Media Foundation/DXVA 自动完成。
   - **关键坑（务必处理）—— NAL 格式**：AirPlay 发的是 AVCC 长度前缀格式；多数情况下需转 **Annex-B**（把 4 字节长度替换为起始码 `00 00 00 01`），并在首帧前注入 SPS/PPS。可参考 **[FFmpegInteropX](https://github.com/ffmpeginteropx/FFmpegInteropX)** 的喂流写法。
   - **线程**：协议在 socket 线程产帧，UI 在 UI 线程；用 `ConcurrentQueue` + `SampleRequested` deferral 解耦，避免跨线程直接操作。
4. **里程碑（v1 完成）**：iPhone 点屏幕镜像 → 手机画面实时显示在 **WinUI3 窗口内**。

### Phase 4 —— 打磨（仍属 v1）
- 全屏切换（“电视”观感）、显示已连接设备名、断开/重连处理干净、SmartScreen/未签名提示说明。

### v2 及以后（不在本次范围，先记下）
- 音频：FDK-AAC 经 P/Invoke 解 AAC-ELD → PCM → `AudioGraph`/WASAPI 播放；纯音乐用 ALAC 流。
- 音画同步、设置页、开机自启、多设备。

---

## 需要新建/改造的关键文件
- `Airplayer.sln`
- `AirPlayer.Protocol/`：搬入 SteeBono/Airplay2OnWindows 的 `AirPlay/` 源码（mDNS、RTSP、配对、H264 解包），改造对外事件，去掉/桩掉音频解码与 ffplay 命名管道部分。
- `AirPlayer.App/MainWindow.xaml`：放置 `MediaPlayerElement` + 状态栏。
- `AirPlayer.App/VideoSink.cs`：**核心新代码**，H264 → `MediaStreamSource` → `MediaPlayerElement` 的喂流与 NAL 转换。
- `AirPlayer.App/App.xaml.cs`：启动时拉起 `AirPlayer.Protocol`，订阅事件接到 `VideoSink`。

## 复用清单（不要重写）
- 协议/配对/解包：`SteeBono/airplayreceiver`、`YimingZhanshen/Airplay2OnWindows`（直接搬 C# 源码）。
- 视频喂流写法参考：`FFmpegInteropX`。
- 协议细节文档：SteeBono 仓库 Wiki “AirPlay2-Protocol”，及 `FDH2/UxPlay`（参考实现）。

---

## 验证方式（端到端，逐里程碑测）
- **前置**：iPhone 与 PC 同一 Wi-Fi；Windows 防火墙放行本应用（含所需 UDP 端口）；如参考工程依赖 Bonjour，先确认 Bonjour 服务在运行。
- **Phase 1**：跑现成 release，iPhone 投屏出现在其窗口 → 链路通。
- **Phase 2**：F5 跑 `AirPlayer.App`，iPhone 控制中心“屏幕镜像”列表里出现 “Airplayer”。
- **Phase 3（v1 验收）**：iPhone 点 “Airplayer” → 手机画面出现在 WinUI3 窗口内，滑动/播放视频画面跟手、不崩。
- 调试技巧：先把收到的 H264 帧 dump 成 `.h264` 文件用 ffplay/VLC 验证“收的数据本身没问题”，再排查 `MediaStreamSource` 渲染端，能快速区分“收流问题”还是“显示问题”。

## 风险与对策
- **mDNS 广播依赖**（Bonjour vs 纯托管 Makaretu.Dns）：Phase 2 定型。
- **NAL 格式不匹配导致黑屏/不解码**：按 Annex-B + 注入 SPS/PPS 处理，用 dump 文件法定位。
- **AirPlay2 配对**：新版 iOS 强制 AirPlay2 配对加密，务必用参考实现已写好的配对代码，勿自行简化。
- **跨线程**：socket 线程产帧 → UI 线程渲染，用队列 + deferral 解耦。
- **新手难度**：这是中高难度项目；严格按阶段走，每阶段都有可见里程碑，避免一次性全做。
