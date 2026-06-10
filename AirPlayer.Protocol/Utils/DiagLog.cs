using System;
using System.IO;

namespace AirPlayer.Protocol.Utils
{
    /// <summary>
    /// 临时诊断日志：把视频管线关键节点写到桌面 airplay-video.log，定位黑屏问题后可删除
    /// </summary>
    public static class DiagLog
    {
        // 日志文件路径（桌面）
        private static readonly string LogPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "airplay-video.log");

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
