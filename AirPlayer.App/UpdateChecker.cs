using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
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

    /// <summary>检查更新失败的分类原因，供 UI 给出针对性提示</summary>
    public enum UpdateCheckFailureReason
    {
        /// <summary>仓库尚无任何已发布的 Release（仅有 tag 不行，需在 GitHub 上创建 Release）</summary>
        NoRelease,
        /// <summary>GitHub API 速率限制（匿名 60 次/小时），需稍后重试或配置 Token</summary>
        RateLimited,
        /// <summary>网络错误 / 无法连接 GitHub</summary>
        Network,
        /// <summary>其它未知错误</summary>
        Unknown
    }

    /// <summary>检查更新异常，携带分类原因与人类可读信息</summary>
    public class UpdateCheckException : Exception
    {
        public UpdateCheckFailureReason Reason { get; }
        public UpdateCheckException(UpdateCheckFailureReason reason, string message, Exception? inner = null)
            : base(message, inner) { Reason = reason; }
    }

    public static class UpdateChecker
    {
        // 仓库信息：joyjoyfresh/Airplayer，硬编码避免到处传参
        public const string RepoOwner = "joyjoyfresh";
        public const string RepoName = "Airplayer";

        private static HttpClient? _httpClient;
        private static string? _lastToken;

        /// <summary>按当前 Token 构建或复用 HttpClient（Token 变化时重建，确保鉴权头同步）</summary>
        private static HttpClient GetHttpClient(string? token)
        {
            if (_httpClient != null && _lastToken == token)
                return _httpClient;

            _httpClient?.Dispose();
            var client = new HttpClient();
            // GitHub API 强制要求 User-Agent，否则 403
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AirPlayer-Updater", "1.0"));
            // 指定 API 版本与返回 JSON
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            // 配置了 Token 则带认证（匿名 60 次/小时 → 认证 5000 次/小时）
            if (!string.IsNullOrWhiteSpace(token))
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            client.Timeout = TimeSpan.FromSeconds(30);
            _httpClient = client;
            _lastToken = token;
            return client;
        }

        /// <summary>读取可选的 GitHub Token：优先环境变量，其次设置项（不强制要求配置）</summary>
        private static string? ResolveToken(string? settingsToken)
        {
            var env = Environment.GetEnvironmentVariable("AIRPLAYER_GITHUB_TOKEN");
            return !string.IsNullOrWhiteSpace(env) ? env : settingsToken;
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
        /// <param name="token">可选 GitHub Token（留空则匿名，匿名 60 次/小时易触发限频）</param>
        public static async Task<UpdateInfo?> CheckForUpdateAsync(string repoOwner, string repoName, string? token = null)
        {
            string url = $"https://api.github.com/repos/{repoOwner}/{repoName}/releases/latest";
            DiagLog.Write(token is null
                ? $"[UPDATE] 正在向 {url} 检查更新（匿名模式，60 次/小时）..."
                : $"[UPDATE] 正在向 {url} 检查更新（已配置 Token，5000 次/小时）...");

            var client = GetHttpClient(token);

            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            }
            catch (TaskCanceledException ex) when (!ex.CancellationToken.IsCancellationRequested)
            {
                // 超时（HttpClient.Timeout）会以 TaskCanceledException 抛出且未取消
                DiagLog.Write($"[UPDATE] 请求超时: {ex.Message}");
                throw new UpdateCheckException(UpdateCheckFailureReason.Network, "连接 GitHub 超时，请检查网络。", ex);
            }
            catch (HttpRequestException ex)
            {
                DiagLog.Write($"[UPDATE] 网络错误: {ex.Message}");
                throw new UpdateCheckException(UpdateCheckFailureReason.Network, "无法连接 GitHub，请检查网络后重试。", ex);
            }

            using (response)
            {
                // 404 = 仓库存在但无 latest release（未发布过 Release）
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    DiagLog.Write("[UPDATE] 仓库尚无已发布的 Release（404），需先在 GitHub 创建 Release");
                    throw new UpdateCheckException(UpdateCheckFailureReason.NoRelease,
                        "尚未发布任何版本。请先在 GitHub 仓库为标签创建 Release。");
                }

                // 403 多为速率限制（rate limit exceeded）；偶见 IP 被封，统一提示稍后重试
                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    var reset = response.Headers.Contains("X-RateLimit-Reset")
                        ? response.Headers.GetValues("X-RateLimit-Reset").FirstOrDefault()
                        : null;
                    DiagLog.Write($"[UPDATE] GitHub API 速率限制（403）{(reset != null ? $"，重置时间戳 {reset}" : "")}");
                    throw new UpdateCheckException(UpdateCheckFailureReason.RateLimited,
                        "GitHub 请求过于频繁已被限速，请稍后重试，或在设置中配置 GitHub Token 提升额度。");
                }

                // 其它非 2xx
                if (!response.IsSuccessStatusCode)
                {
                    DiagLog.Write($"[UPDATE] GitHub 返回非成功状态码: {(int)response.StatusCode} {response.StatusCode}");
                    throw new UpdateCheckException(UpdateCheckFailureReason.Unknown,
                        $"GitHub 返回错误 {(int)response.StatusCode}，请稍后重试。");
                }

                string json;
                try
                {
                    json = await response.Content.ReadAsStringAsync();
                }
                catch (Exception ex)
                {
                    DiagLog.Write($"[UPDATE] 读取响应失败: {ex.Message}");
                    throw new UpdateCheckException(UpdateCheckFailureReason.Network, "读取 GitHub 响应失败。", ex);
                }

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("tag_name", out var tagProp))
                {
                    DiagLog.Write("[UPDATE] GitHub 响应未包含 tag_name 字段");
                    throw new UpdateCheckException(UpdateCheckFailureReason.Unknown, "GitHub 响应格式异常，未包含版本字段。");
                }

                string tag = tagProp.GetString() ?? "";
                string cleanTag = tag.TrimStart('v', 'V');
                if (!Version.TryParse(cleanTag, out var latestVersion))
                {
                    DiagLog.Write($"[UPDATE] 无法解析版本号: {tag}");
                    throw new UpdateCheckException(UpdateCheckFailureReason.Unknown, $"无法解析版本号：{tag}。");
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
            return null;
        }

        /// <summary>下载更新包，支持进度回报与取消</summary>
        public static async Task DownloadUpdateAsync(string downloadUrl, string destinationPath, IProgress<double> progress, CancellationToken cancellationToken, string? token = null)
        {
            DiagLog.Write($"[UPDATE] 开始下载更新包: {downloadUrl} -> {destinationPath}");
            var client = GetHttpClient(token);
            using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
