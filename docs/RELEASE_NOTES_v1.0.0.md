Windows 上的 AirPlay 接收端：把 iPhone / iPad 的屏幕镜像投到电脑，H.264 全 GPU 硬件解码、AAC-ELD 音频，音画同步。

## ✨ 主要功能
- iOS/iPadOS「屏幕镜像」即插即用（mDNS 发现 + RTSP）
- 视频硬件解码 + 全 GPU 渲染，低延迟低 CPU；音频 AAC-ELD 解码
- 分辨率（720p/1080p）、帧率（30/60fps）限制
- 全屏 / 铺满、画面旋转、屏幕截图、窗口置顶、实时 HUD
- 自定义快捷键、明/暗/跟随系统主题、Mica 背景
- 系统托盘后台常驻：关窗口可最小化到托盘，有设备投屏自动弹出
- iOS 端音量与本机同步

## 💻 系统要求
- Windows 10 1809 及以上 / Windows 11，x64（部分效果如 Mica 需 Win11）
- 手机与电脑需在**同一局域网**

## 📥 下载与安装
- **安装版**：下载 `AirPlayer-1.0.0-setup.exe`，双击按向导安装。
- **便携版**：下载 `AirPlayer-1.0.0-win-x64.zip`，解压后双击 `AirPlayer.App.exe` 即用，免安装。
- 已内置所需运行时与音频解码库，无需另装 .NET 或其它依赖。

## 🚀 使用
1. 打开 AirPlayer，记下界面上显示的设备名。
2. iPhone 下拉控制中心 → 「屏幕镜像」→ 选择该设备名。

## ⚠️ 说明
- 本程序**未做代码签名**，首次运行可能弹 Windows SmartScreen「未知发布者」，点「更多信息 → 仍要运行」即可。
- 本项目仅供学习与个人研究，**与 Apple Inc. 无任何关联**；“AirPlay”为 Apple 商标，此处仅作兼容性描述。
- 详见仓库 README 的法律声明。

## 🙏 致谢
感谢开源 AirPlay 接收端社区（RPiPlay / UxPlay 等）与 Fraunhofer FDK AAC。
