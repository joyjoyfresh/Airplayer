Windows 上的 AirPlay 接收端：把 iPhone / iPad 的屏幕镜像投到电脑，H.264 全 GPU 硬件解码、AAC-ELD 音频，音画同步。

## ✨ 本版更新

### 新增
- **检查更新支持配置 GitHub 访问令牌**：在「更多设置」中填入个人 Token 后，更新检测速率上限从匿名的 60 次/小时提升至 5000 次/小时，避免被 API 限速。Token 明文保存于本地配置文件。

### 修复
- **图标未填满**：修复 Windows 任务栏与标题栏图标显示过小/未填满的问题；图标内容由占画布 76% 放大至 98%，同时保留圆角透明效果。

## 💻 系统要求
- Windows 10 1809 及以上 / Windows 11，x64（部分效果如 Mica 需 Win11）
- 手机与电脑需在**同一局域网**

## 📥 下载与安装
- **安装版**：下载 `AirPlayer-1.2.1-setup.exe`，双击按向导安装。
- **便携版**：下载 `AirPlayer-1.2.1-win-x64.zip`，解压后双击 `AirPlayer.App.exe` 即用，免安装。
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
