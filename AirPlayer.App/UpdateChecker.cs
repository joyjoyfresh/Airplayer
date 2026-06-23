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
    /// <summary>更新信息。同时携带安装包与绿色包两类资产 URL（可能为空），
    /// 由调用方按当前安装方式（安装版/便携版）选用对应资产，保证升级后形态不变。</summary>
    public record UpdateInfo(
        Version Version,
        string VersionString,
        string ReleaseNotes,
        string SetupUrl,
        string ZipUrl,
        long AssetSize
    );

    /// <summary>检查更新失败的分类原因，供 UI 给出针对性提示</summary>
    public enum UpdateCheckFailureReason
    {
        /// <summary>仓库尚无任何已发布的 Release（仅有 tag 不行，需在 GitHub 上创建 Release）</summary>
        NoRelease,
        /// <summary>GitHub API 速率限制（匿名 60 次/小时），需稍后重试</summary>
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

        // 匿名访问 GitHub API：未鉴权，限额 60 次/小时（单人偶尔检查够用；超限会触发 403 并提示稍后重试）
        private static readonly HttpClient _httpClient;

        static UpdateChecker()
        {
            _httpClient = new HttpClient();
            // GitHub API 强制要求 User-Agent，否则 403
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AirPlayer-Updater", "1.0"));
            // 指定 API 版本与返回 JSON
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
        }

        /// <summary>当前运行程序的版本号（用于与 GitHub Release 的 tag 比较大小）</summary>
        public static Version CurrentVersion
        {
            get
            {
                var ver = typeof(UpdateChecker).Assembly.GetName().Version;
                return ver ?? new Version(1, 0, 0);
            }
        }

        /// <summary>用于界面显示的版本字符串：只取前三段（Major.Minor.Build），与 git tag「v1.0.0」三段对齐。
        /// 程序集版本固定四段，第四段 Revision 始终为 0，显示时去掉避免出现「1.0.0.0」。</summary>
        public static string CurrentVersionDisplay => $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";

        /// <summary>标记文件名：仅安装器安装后写入 app 目录，便携版(zip 解压)无此文件。</summary>
        private const string InstallMarkerFileName = "installed.marker";

        /// <summary>当前是否为安装版（由 Inno Setup 安装器写入 installed.marker 标记区分）。
        /// 决定在线更新时下载 setup.exe(安装版) 还是 win-x64.zip(便携版)，保证升级后形态不变。</summary>
        public static bool IsInstalledVersion
        {
            get
            {
                try
                {
                    string marker = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, InstallMarkerFileName);
                    return File.Exists(marker);
                }
                catch { return false; }
            }
        }

        /// <summary>检查是否有更新</summary>
        /// <param name="repoOwner">仓库拥有者，例如 "joyjoyfresh"</param>
        /// <param name="repoName">仓库名称，例如 "Airplayer"</param>
        public static async Task<UpdateInfo?> CheckForUpdateAsync(string repoOwner, string repoName)
        {
            string url = $"https://api.github.com/repos/{repoOwner}/{repoName}/releases/latest";
            DiagLog.Write($"[UPDATE] 正在向 {url} 检查更新...");

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
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
                        "GitHub 请求过于频繁已被限速，请稍后重试。");
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

                DiagLog.Write($"[UPDATE] 最新版本: {latestVersion}, 当前版本: {CurrentVersionDisplay}");
                if (latestVersion <= CurrentVersion)
                {
                    return null;
                }

                string body = root.TryGetProperty("body", out var bodyProp) ? (bodyProp.GetString() ?? "") : "";

                if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
                {
                    // 同时收集安装包(setup.exe)与绿色包(win-x64.zip)两类资产 URL，
                    // 交由调用方按当前安装方式选用：安装版用 setup，便携版用 zip，升级后形态保持不变。
                    string setupUrl = "";
                    string zipUrl = "";
                    long size = 0;
                    foreach (var asset in assetsProp.EnumerateArray())
                    {
                        string name = asset.GetProperty("name").GetString() ?? "";
                        string assetUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                        long assetSize = asset.GetProperty("size").GetInt64();

                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                            name.Contains("setup", StringComparison.OrdinalIgnoreCase))
                        {
                            setupUrl = assetUrl;
                            size = assetSize;
                        }
                        else if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                                 name.Contains("win-x64", StringComparison.OrdinalIgnoreCase) &&
                                 string.IsNullOrEmpty(zipUrl))
                        {
                            zipUrl = assetUrl;
                            if (size == 0) size = assetSize;
                        }
                    }

                    if (!string.IsNullOrEmpty(setupUrl) || !string.IsNullOrEmpty(zipUrl))
                    {
                        return new UpdateInfo(
                            latestVersion,
                            tag,
                            body,
                            setupUrl,
                            zipUrl,
                            size
                        );
                    }
                    else
                    {
                        DiagLog.Write("[UPDATE] 未能在 Release 资产中找到 setup.exe 或 win-x64.zip");
                    }
                }
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

        /// <summary>应用更新：安装包(.exe)→启动安装器原地升级；绿色包(.zip)→解压覆盖当前目录。</summary>
        /// <remarks>安装版与便携版各自升级后形态不变：
        /// - 安装包走 Inno Setup（自带 UAC 提权/文件覆盖/安装后重启），覆盖到原安装目录；
        /// - 绿色包解压覆盖便携版目录（便携版目录用户可写，无需提权）。</remarks>
        public static void ApplyUpdateAndRestart(string downloadedPath)
        {
            try
            {
                bool isInstaller = downloadedPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                if (isInstaller)
                {
                    LaunchInstaller(downloadedPath);
                }
                else
                {
                    ExtractPortable(downloadedPath);
                }

                // 强制终止当前进程：unpackaged WinUI3 下 Application.Current.Exit() 只关窗口未必终止进程
                // （音频/RTSP 后台线程可能存活），会让外部脚本的「等待主进程退出」循环卡死。Environment.Exit 保证退出。
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                DiagLog.Write($"[UPDATE] 应用更新失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>安装包路径：写脚本等待主进程退出后启动 Inno Setup 安装器（触发 UAC，安装后由 [Run] 重启应用）。</summary>
        private static void LaunchInstaller(string setupPath)
        {
            int appPid = Process.GetCurrentProcess().Id;
            string scriptPath = Path.Combine(Path.GetTempPath(), "AirPlayer_run_setup.ps1");
            string logPath = Path.Combine(Path.GetTempPath(), "AirPlayer_update.log");

            string Esc(string? s) => (s ?? "").Replace("'", "''");

            const string PsTemplate = @"$log = '__LOG__'
$procId = __PID__
$setup = '__SETUP__'

'=== AirPlayer update (installer) started ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') | Out-File -FilePath $log -Encoding UTF8

# 1. 等待主进程退出，确保其 exe 不被占用
try {
  while (Get-Process -Id $procId -ErrorAction SilentlyContinue) {
    Start-Sleep -Milliseconds 200
  }
  'main process exited' | Out-File -FilePath $log -Append -Encoding UTF8
} catch {
  'wait pid error: ' + $_.Exception.Message | Out-File -FilePath $log -Append -Encoding UTF8
}

# 2. 启动安装器（PrivilegesRequired=admin 触发 UAC 提权，向导式安装，安装后 [Run] 重启应用）
try {
  Start-Process -FilePath $setup
  'setup launched' | Out-File -FilePath $log -Append -Encoding UTF8
} catch {
  'launch error: ' + $_.Exception.Message | Out-File -FilePath $log -Append -Encoding UTF8
}
";
            string psScript = PsTemplate
                .Replace("__LOG__", Esc(logPath))
                .Replace("__PID__", appPid.ToString())
                .Replace("__SETUP__", Esc(setupPath));
            File.WriteAllText(scriptPath, psScript, System.Text.Encoding.UTF8);

            DiagLog.Write($"[UPDATE] 安装包升级脚本已写入 {scriptPath}，日志 {logPath}");
            DiagLog.Write($"[UPDATE] 即将退出主程序并启动安装器 {setupPath}");
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }

        /// <summary>绿色包路径：解压到临时目录，写脚本等待主进程退出后覆盖复制到当前目录并重启。</summary>
        private static void ExtractPortable(string zipPath)
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string appExePath = Process.GetCurrentProcess().MainModule?.FileName ?? Path.Combine(appDir, "AirPlayer.App.exe");
            int appPid = Process.GetCurrentProcess().Id;

            // 临时解压目录
            string tempDir = Path.Combine(Path.GetTempPath(), "AirPlayer_Update");
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            Directory.CreateDirectory(tempDir);

            DiagLog.Write($"[UPDATE] 正在解压绿色更新包 {zipPath} 到 {tempDir}...");
            ZipFile.ExtractToDirectory(zipPath, tempDir, true);

            string scriptPath = Path.Combine(Path.GetTempPath(), "AirPlayer_apply_portable.ps1");
            string logPath = Path.Combine(Path.GetTempPath(), "AirPlayer_update.log");

            string Esc(string? s) => (s ?? "").Replace("'", "''");

            const string PsTemplate = @"$log = '__LOG__'
$procId = __PID__
$source = '__SOURCE__'
$dest = '__DEST__'
$exe = '__EXE__'
$tempDir = '__TEMPDIR__'

'=== AirPlayer update (portable) started ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') | Out-File -FilePath $log -Encoding UTF8

# 1. 等待主进程退出，确保 exe 不被占用
try {
  while (Get-Process -Id $procId -ErrorAction SilentlyContinue) {
    Start-Sleep -Milliseconds 200
  }
  'main process exited' | Out-File -FilePath $log -Append -Encoding UTF8
} catch {
  'wait pid error: ' + $_.Exception.Message | Out-File -FilePath $log -Append -Encoding UTF8
}

# 2. 覆盖复制新文件到便携版目录
try {
  Copy-Item -Path $source -Destination $dest -Recurse -Force
  'files copied' | Out-File -FilePath $log -Append -Encoding UTF8
} catch {
  'copy error: ' + $_.Exception.Message | Out-File -FilePath $log -Append -Encoding UTF8
}

# 3. 重启主程序
try {
  Start-Process -FilePath $exe
  'restarted' | Out-File -FilePath $log -Append -Encoding UTF8
} catch {
  'restart error: ' + $_.Exception.Message | Out-File -FilePath $log -Append -Encoding UTF8
}

# 4. 清理临时解压目录
Start-Sleep -Seconds 1
try {
  Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
} catch {}
";
            string psScript = PsTemplate
                .Replace("__LOG__", Esc(logPath))
                .Replace("__PID__", appPid.ToString())
                .Replace("__SOURCE__", Esc(Path.Combine(tempDir, "*")))
                .Replace("__DEST__", Esc(appDir))
                .Replace("__EXE__", Esc(appExePath))
                .Replace("__TEMPDIR__", Esc(tempDir));
            File.WriteAllText(scriptPath, psScript, System.Text.Encoding.UTF8);

            DiagLog.Write($"[UPDATE] 绿色包升级脚本已写入 {scriptPath}，日志 {logPath}");
            DiagLog.Write($"[UPDATE] 即将退出主程序并覆盖解压到 {appDir}");
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
    }
}
