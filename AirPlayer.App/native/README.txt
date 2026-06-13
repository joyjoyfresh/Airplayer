本目录用于放置 AAC-ELD 解码所需的原生库：fdk-aac.dll（x64）。

投屏（镜像）音频是 AAC-ELD。已确认：
  - 微软 Media Foundation 解码器不支持 ELD；
  - FFmpeg 原生解码器经实测也解不了 ELD（每帧返回 AVERROR_BUG）。
所以只能用 fdk-aac（RPiPlay/UxPlay 等也都用它）。

放好 fdk-aac.dll 后，构建会自动把它拷到输出目录（csproj 只匹配 *fdk*.dll）。
程序启动时也会在缺库时尝试自动从 MSYS2 下载（见日志 [FDK] 自动下载...）。

获取方式（任选）：
  1. 程序自动下载（需联网，无需你操作）。
  2. 运行：powershell -ExecutionPolicy Bypass -File tools\get-fdk-aac.ps1
  3. vcpkg： vcpkg install fdk-aac:x64-windows  然后复制 fdk-aac.dll 到这里。

注：本目录里若残留 avcodec-*/libx264/libx265 等 FFmpeg DLL，是早期尝试遗留的，
可以删除，不影响功能（构建已不再拷贝它们）。
