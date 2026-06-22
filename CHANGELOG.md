# 更新日志

本项目的所有重要变更都会记录在本文件。
格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [1.0.0] - 2026-06-14

首个正式版。Windows 上的 AirPlay 接收端，接收 iPhone/iPad 的屏幕镜像投屏，
H.264 全 GPU 硬件解码、AAC-ELD 音频，音画同步。

### 新增
- iOS/iPadOS「屏幕镜像」接收：mDNS 发现 + RTSP 会话，配对（ED25519 / Curve25519）与 FairPlay 握手。
- 全 GPU 视频管线：Media Foundation 硬件 H.264 解码 → D3D11 Video Processor（BT.709、信箱/铺满缩放）→ DXGI 交换链呈现，不支持硬解时自动回退软解。
- 音频：AAC-ELD 解码（fdk-aac）→ waveOut 低延迟播放，有界缓冲与视频对齐。
- 视频设置：分辨率（720p/1080p）、帧率（30/60fps）限制。
- 操作：全屏 / 铺满、画面旋转、屏幕截图、窗口置顶、实时 HUD（分辨率/帧率/解码/丢帧）。
- 用户自定义快捷键（支持组合键、冲突处理、恢复默认）。
- 外观：浅色 / 深色 / 跟随系统主题，Mica 背景。
- 系统托盘后台常驻：可设置关闭窗口时最小化到托盘；后台有设备投屏自动弹出窗口；托盘右键退出。
- iOS 端音量与本机播放音量同步。
- 待机页信息卡：显示设备名、本机 IP、当前网络名，便于确认同一局域网。
- 一键发布脚本（自包含可运行目录 + zip）与 Inno Setup 安装包脚本。

### 优化
- 渲染端缓存 VideoProcessor 输入视图，避免每帧重建，降低 CPU 开销。
- 启用解码器多线程（CODECAPI_AVDecNumWorkerThreads）与低延迟模式（MF_LOW_LATENCY）。
- 视频帧通道改为无界 + GOP 对齐处理，减少因丢帧导致的花屏。

### 修复
- 丢帧计数仅统计真实解码失败，排除解码器流水线 NEED_MORE_INPUT 的误报。
- iOS 端调节音量在 Windows 播放端无效的问题。
- 打包脚本编码问题（改为纯 ASCII，避免无 BOM UTF-8 在 Windows PowerShell 下乱码）。

[1.0.0]: https://github.com/<your-account>/Airplayer/releases/tag/v1.0.0
