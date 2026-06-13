using System;
using System.Diagnostics;
using System.IO;

namespace AirPlayer.Protocol.Utils
{
    /// <summary>
    /// 诊断日志：把视频/解码管线关键节点写到 airplay-video.log。
    /// 仅 Debug 构建生效（Write 标记了 [Conditional("DEBUG")]，Release 构建中所有调用会被编译器移除，零开销）。
    /// </summary>
    public static class DiagLog
    {
        // 日志文件路径：从 exe 目录向上查找 Airplayer.sln 所在目录
        private static readonly string LogPath = InitLogPath();

        // 标记是否已执行启动时覆盖，确保只清空一次
        private static bool _sessionCleared;

        // 写锁，避免多线程交叉写入
        private static readonly object Lock = new object();

        static DiagLog()
        {
            try
            {
                lock (Lock)
                {
                    File.WriteAllText(LogPath, string.Empty);
                    _sessionCleared = true;
                }
            }
            catch
            {
                // 静态构造函数中的异常若不捕获会导致类型初始化失败
                // 如果清空失败，则留到首次 Write 时再次尝试
            }
        }

        private static string InitLogPath()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Airplayer.sln")))
                {
                    var protocolDir = Path.Combine(dir.FullName, "AirPlayer.Protocol");
                    if (Directory.Exists(protocolDir))
                        return Path.Combine(protocolDir, "airplay-video.log");
                    return Path.Combine(dir.FullName, "airplay-video.log");
                }
                dir = dir.Parent;
            }
            return Path.Combine(AppContext.BaseDirectory, "airplay-video.log");
        }

        /// <summary>写一行带时间戳的诊断信息（仅 Debug 构建生效）</summary>
        [Conditional("DEBUG")]
        public static void Write(string message)
        {
            try
            {
                lock (Lock)
                {
                    // 首次写入时覆盖旧日志，后续追加
                    if (!_sessionCleared)
                    {
                        File.WriteAllText(LogPath, string.Empty); // 清空文件
                        _sessionCleared = true;
                    }
                    File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {mes