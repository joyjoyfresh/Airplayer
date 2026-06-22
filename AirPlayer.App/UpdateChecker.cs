using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using AirPlayer.Protocol.Utils;
using Microsoft.UI.Xaml;

namespace AirPlayer.App
{
    public record UpdateInfo(
        Version Version,
        string VersionString,
        string ReleaseNotes,
        string DownloadUrl,
        long AssetSize
    );

    public static class UpdateChecker
    {
        private static readonly HttpClient _httpClient;

        static UpdateChecker()
        {
            _httpClient = new HttpClient();
            // GitHub API 要求设置 User-Agent
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AirPlayer-Updater", "1.0"));
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
        }

        /// <summary>获取当前运行程序的版本号</summary>
        public static Version CurrentVersion
        {
            get
            {
                var ver = typeof(UpdateChecker).Assembly.GetName().Version;
                return ver ?? new Version(0, 2, 0);
            }
        }

        /// <summary>检查是否有更新</summary>
        /// <param name="repoOwner">仓库拥有者，例如 "joyjoyfresh"</param>
        /// <param name="repoName">仓库名称，例如 "Airplayer"</param>
        public static async Task<UpdateInfo?> CheckForUpdateAsync(string repoOwner, string repoName)
        {
            try
            {
                string url = $"https://api.github.com/repos/{repoOwner}/{repoName}/releases/latest";
                DiagLog.Write($"[UPDATE] 正在向 {url} 检查更新...");
                var response = await _httpClient.GetStringAsync(url);
                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;

                if (!root.TryGetProperty("tag_name", out var tagProp))
                {
                    DiagLog.Write("[UPDATE] GitHub 响应未包含 tag_name 字段");
                    return null;
                }

                string tag = tagProp.GetString() ?? "";
                string cleanTag = tag.TrimStart('v', 'V');
                if (!Version.TryParse(cleanTag, out var latestVersion))
                {
                    DiagLog.Write($"[UPDATE] 无法解析版本号: {tag}");
                    return null;
                }

                DiagLog.Write($"[UPDATE] 最新版本: {latestVersion}, 当前版本: {CurrentVersion}");
                if (latestVersion <= CurrentVersion)
                {
                    return null;
                }

                string body = root.TryGetProperty("body", out var bodyProp) ? (bodyProp.GetString() ?? "") : "";

                if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
                {
                    JsonElement bestAsset = default;
                    foreach (var asset in assetsProp.EnumerateArray())
                    {
                        string name = asset.GetProperty("name").GetString() ?? "";
                        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            // 优先匹配包含 win-x64 的压缩包，否则选择找到的第一个 zip
                            if (name.Contains("win-x64", StringComparison.OrdinalIgnoreCase))
                            {
                                bestAsset = asset;
                                break;
                            }
                            if (bestAsset.ValueKind == JsonValueKind.Undefined)
                            {
                                bestAsset = asset;
                            }
                        }
                    }

                    if (bestAsset.ValueKind != JsonValueKind.Undefined)
                    {
                        string downloadUrl = bestAsset.GetProperty("browser_download_url").GetString() ?? "";
                        long size = bestAsset.GetProperty("size").GetInt64();

                        return new UpdateInfo(
                            latestVersion,
                            tag,
                            body,
                            downloadUrl,
                            size
                        );
                    }
                    else
                    {
                        DiagLog.Write("[UPDATE] 未能在 Release 资产中找到 .zip 文件");
                    }
                }
            }
            catch (Exception ex)
            {
                DiagLog.Write($"[UPDATE] 检查更新失败: {ex.Message}");
                throw;
            }
            return null;
        }

        /// <summary>下载更新包，支持进度回报与取消</summary>
        public static async Task DownloadUpdateAsync(string downloadUrl, string destinationPath, IProgress<double> progress, CancellationToken cancellationToken)
        {
            DiagLog.Write($"[UPDATE] 开始下载更新包: {downloadUrl} -> {destinationPath}");
            using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            long totalBytes = response.Content.Headers.ContentLength ?? -1L;
            using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            long totalRead = 0L;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                totalRead += bytesRead;

                if (totalBytes > 0)
                {
                    double percent = (double)totalRead / totalBytes * 100.0;
                    progress.Report(percent);
                }
            }
            DiagLog.Write("[UPDATE] 更新包下载完成");
        }

        /// <summary>解压并调用外部 PowerShell 脚本替换文件后重启应用</summary>
        public static void ApplyUpdateAndRestart(string zipPath)
        {
            try
            {
                string appDir = AppDomain.CurrentDomain.BaseDirectory;
                string appExePath = Process.GetCurrentProcess().MainModule?.FileName ?? Path.Combine(appDir, "AirPlayer.App.exe");

                // 临时解压目录
                string tempDir = Path.Combine(Path.GetTempPath(), "AirPlayer_Update");
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
                Directory.CreateDirectory(tempDir);

                DiagLog.Write($"[UPDATE] 正在解压更新包 {zipPath} 到 {tempDir}...");
                ZipFile.ExtractToDirectory(zipPath, tempDir, true);

                // 转义特殊字符（针对 PowerShell 单引号）
                string escapedSource = Path.Combine(tempDir, "*").Replace("'", "''");
                string escapedDest = appDir.Replace("'", "''");
                string escapedExe = appExePath.Replace("'", "''");
                string escapedTempDir = tempDir.Replace("'", "''");

                // 编写 PowerShell 脚本：
                // 1. 等待当前进程退出
                // 2. 覆盖复制所有新文件到主程序目录
                // 3. 重新启动主程序
                // 4. 清理临时解压目录
                string psScript = 
                    $"$pid = {Process.GetCurrentProcess().Id}; " +
                    $"while (Get-Process -Id $pid -ErrorAction SilentlyContinue) {{ Start-Sleep -Milliseconds 200 }}; " +
                    $"Copy-Item -Path '{escapedSource}' -Destination '{escapedDest}' -Recurse -Force -ErrorAction SilentlyContinue; " +
                    $"Start-Process -FilePath '{escapedExe}'; " +
                    $"Remove-Item -Path '{escapedTempDir}' -Recurse -Force -ErrorAction SilentlyContinue";

                DiagLog.Write("[UPDATE] 正在启动外部更新脚本并退出主程序...");
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{psScript}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                Process.Start(psi);

                // 退出当前应用
                Application.Current.Exit();
            }
            catch (Exception ex)
            {
                DiagLog.Write($"[UPDATE] 应用更新失败: {ex.Message}");
                throw;
            }
        }
    }
}
