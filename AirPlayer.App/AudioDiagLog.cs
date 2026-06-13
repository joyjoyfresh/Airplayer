using System;
using System.Diagnostics;
using System.IO;

namespace AirPlayer.App
{
    /// <summary>
    /// 音频诊断日志：写入 audio-debug.log，用于排查音频管道问题。
    /// 仅 Debug 构建生效（Write 标记了 [Conditional("DEBUG")]，Release 构建零开销）。
    /// </summary>
    public static class AudioDiagLog
    {
        private static readonly string LogPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..",
            "AirPlayer.App", "audio-debug.log");

        private static readonly object _lock = new();
        private static StreamWriter? _writer;

        static AudioDiagLog()
        {
            try
            {
                _writer = new StreamWriter(LogPath, append: false) { AutoFlush = true };
                _writer.WriteLine($"=== AudioDiagLog started at {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ===");
                _writer.WriteLine($"Log path: {Path.GetFullPath(LogPath)}");
            }
            catch { }
        }

        [Conditional("DEBUG")]
        public static void Write(string message)
        {
            try
            {
                lock (_lock)
                {
                    _writer?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
                }
            }
            catch { }
        }

        [Conditional("DEBUG")]
        public static void Dispose()
        {
            try
            {
                lock (_lock)
                {
                    _writer?.WriteLine($"=== AudioDiagLog ended at {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ===");
         