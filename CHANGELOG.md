# 更新日志

本项目的所有重要变更都会记录在本文件。
格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [1.2.0] - 2026-06-24

### 新增
- 窗口模式下投屏窗口锁定视频比例：缩放窗口时实时维持视频宽高比，窗口恰好框住视频，无黑边无裁切（基于 Win32 拦截 `WM_SIZING`，旋转 270° 时比例自动互换）。
- 窗口模式下菜单也显示「铺满屏幕」按钮，投屏中即可在窗口/全屏下切换铺满与信箱缩放模式。

### 修复
- 修复全屏下退出投屏后主界面窗口尺寸异常（变成视频/手机尺寸）且画面冻在最后一帧的问题（切换窗口前先用不透明覆盖层遮挡 swapchain 残留帧，避免显式解绑交换链导致的崩溃）。
- 修复信箱模式未裁掉 H.264 解码纹理对齐 padding，与铺满模式比例基准不一致，切换时画面横向拉伸的问题。
- 修复窗口最大化时点旋转导致窗口尺寸与比例乱跳的问题（最大化态下不再主动 Resize 并跳过窗口位置记忆）。

## [1.1.2] - 2026-06-23

### 修复
- 修复在线更新下载完成后软件关闭但未执行替换与重启的问题（由 WinUI3 非打包应用 `Application.Current.Exit()` 不终止进程导致，改用 `Environment.Exit(0)` 修复）。

### 变更
- 在线更新按安装方式分流：安装版下载 `setup.exe` 并启动安装程序静默升级；便携版下载 zip 解压覆盖后重启，各自升级到对应格式的新版本。
- Inno Setup 安装后写入 `installed.marker` 标记文件，运行时以此区分安装版与便携版。

## [1.1.1] - 2026-06-23

### 新增
- 全屏投屏时鼠标静止 3 秒自动隐藏指针，移动鼠标或打开菜单/对话框时自动恢复。

### 变更
- 全屏默认快捷键由 F11 改为单键 G。

### 修复
- 修复全屏中旋转画面后退出全屏，窗口未同步为当前旋转方向（仍停留在进入全屏前的方向）的问题。

## [1.1.0] - 2026-06-23

### 新增
- 手动检查更新：最新版、失败（网络/限频/无 Release）均弹出明确提示，不再静默吞掉结果。

### 优化
- 检查更新菜单在投屏界面自动隐藏，仅在待机主界面显示，避免操作误触。
- 更多设置 > 视频 + 音频选项卡合并为「音视频」，布局更简洁。
- 检查更新改用纯匿名 GitHub API，移除 GitHub Token 配置入口。

### 修复
- 修复手动检查更新时多个 ContentDialog 并发导致的程序闪退（COMException 0x80000019）。
- 修复版本号显示多余第四段（1.0.0.0 → 1.0.0）。

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

[1.2.0]: https://github.com/joyjoyfresh/Airplayer/releases/tag/v1.2.0
[1.1.2]: https://github.com/joyjoyfresh/Airplayer/releases/tag/v1.1.2
[1.1.1]: https://github.com/joyjoyfresh/Airplayer/releases/tag/v1.1.1
[1.1.0]: https://github.com/joyjoyfresh/Airplayer/releases/tag/v1.1.0
[1.0.0]: https://github.com/joyjoyfresh/Airplayer/releases/tag/v1.0.0
