using System;
using System.IO;

namespace AirPlayer.Protocol.Utils
{
    /// <summary>
    /// 临时诊断日志：把视频管线关键节点写到项目根目录 airplay-video.log
    /// </summary>
    public static class DiagLog
    {
        // 日志文件路径：从 exe 目录向上查找 Airplayer.sln 所在目录
        private static readonly string LogPath = InitLogPath();

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

        // 写锁，避免多线程交叉写入
        private static readonly object Lock = new object();

        /// <summary>写一行带时间戳的诊断信息</summary>
        public static void Write(string message)
        {
            try
            {
                lock (Lock)
                {
                    File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}\n"); // 追加写入
                }
            }
            catch { /* 诊断日志失败不影响主流程 */ }
        }
    }
}
