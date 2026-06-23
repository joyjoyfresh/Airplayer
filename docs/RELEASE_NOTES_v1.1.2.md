Windows 上的 AirPlay 接收端：把 iPhone / iPad 的屏幕镜像投到电脑，H.264 全 GPU 硬件解码、AAC-ELD 音频，音画同步。

## ✨ 本版更新

- **在线更新流程修复**：修复在线更新下载完成后软件关闭但未实际完成替换与重启的问题（根因：WinUI3 非打包应用 `Application.Current.Exit()` 不终止进程，改用 `Environment.Exit(0)` 修复）。
- **按安装方式分流升级**：安装版自动下载 `setup.exe` 并启动安装程序完成升级；便携版自动下载 zip 解压覆盖后重启，各自保持原有发布格式。
- **安装版标记机制**：Inno Setup 安装完成后写入 `installed.marker` 文件，运行时据此自动识别安装版与便携版，无需用户手动配置。

## 💻 系统要求
- Windows 10 1809 及以上 / Windows 11，x64（部分效果如 Mica 需 Win11）
- 手机与电脑需在**同一局域网**

## 📥 下载与安装
- **安装版**：下载 `AirPlayer-1.1.2-setup.exe`，双击按向导安装。
- **便携版**：下载 `AirPlayer-1.1.2-win-x64.zip`，解压后双击 `AirPlayer.App.exe` 即用，免安装。
- 已内置所需运行时与音频解码库，无需另装 .NET 或其它依赖。

## 🚀 使用
1. 打开 AirPlayer，记下界面上显示的设备名。
2. iPhone 下拉控制中心 → 「屏幕镜像」→ 选择该设备名。

## ⚠️ 说明
- 本程序**未做代码签名**，首次运行可能弹 Windows SmartScreen「未知发布者」，点「更多信息 → 仍要运行」即可。
- 本项目仅供学习与个人研究，**与 Apple Inc. 无任何关联**；"AirPlay"为 Apple 商标，此处仅作兼容性描述。
- 详见仓库 README 的法律声明。

## 🙏 致谢
感谢开源 AirPlay 接收端社区（RPiPlay / UxPlay 等）与 Fraunhofer FDK AAC。
