# 更新日志

> 当前处于 0.x 预发布阶段，功能仍在持续完善中。

## v0.2.0（预发布）

在 v0.1.0（视频投屏）基础上**新增音频**：投屏视频与音频均可用，音画同步。

### 音频（本次重点）
- 新增 AAC-ELD 解码（fdk-aac）。投屏音频为 AAC-ELD，微软 MFT 与 FFmpeg 原生解码器
  均无法解码（FFmpeg 每帧返回 AVERROR_BUG），改用 fdk-aac。
- 缺少 `fdk-aac.dll` 时自动从 MSYS2 下载；并提供 `tools\get-fdk-aac.ps1` 与 vcpkg 两种手动方式。
- 重写 `AudioSink`：
  - 修正 waveOut 完成消息常量（`WOM_DONE` 0x3BD，原误用 0x3C00，导致首块缓冲后停播）。
  - 改用 `CALLBACK_EVENT` + 专用工作线程，避免在回调内调用 waveOut 造成的死锁。
  - 用 `dwBufferLength` 正确推进音频时钟。
  - 有界、丢旧的低延迟队列（~150ms），与"始终呈现最新帧"的视频对齐，实现音画同步。
- 修正解码帧长度：按实际解码字节数投递 PCM（原固定 4096，导致噪音/不同步）。

### 性能与工程
- 诊断日志改为 `[Conditional("DEBUG")]`，Release 构建零日志开销、不产生日志文件。
- 解码热点路径复用数组，减少每帧分配。
- 清理死代码（废弃的 FFmpeg 解码尝试、未用字段/using），简化原生库解析器。
- 加入版本信息、`.gitignore`、README、发布打包脚本 `tools\build-release.ps1`。

## v0.1.0

AirPlay 接收器初始版本：全 GPU 视频管线（H.264 硬解 → D3D11 处理 → DXGI 呈现）、画面旋转、全屏。仅视频，无音频。
