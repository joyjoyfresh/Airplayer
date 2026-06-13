using System;
using System.IO;
using System.Text.Json;

namespace AirPlayer.App
{
    /// <summary>
    /// 应用设置，持久化到 %LocalAppData%\AirPlayer\settings.json。
    /// 读写失败一律安全降级到默认值，不影响主流程。
    /// </summary>
    public sealed class AppSettings
    {
        /// <summary>窗口置顶</summary>
        public bool AlwaysOnTop { get; set; }

        /// <summary>显示实时 HUD（FPS/分辨率等）</summary>
        public bool ShowHud { get; set; }

        /// <summary>自定义 AirPlay 设备名（为空则用计算机名）</summary>
        public string? DeviceName { get; set; }

        /// <summary>HUD 字体大小</summary>
        public int HudFontSize { get; set; } = 13;

        /// <summary>HUD 文本颜色Hex</summary>
        public string HudTextColor { get; set; } = "#00E676";

        /// <summary>HUD 背景透明度 (0.0 到 1.0)</summary>
        public double HudBgOpacity { get; set; } = 0.56;

        /// <summary>窗口位置 X（像素），null 表示首次启动默认居中</summary>
        public int? WindowX { get; set; }

        /// <summary>窗口位置 Y（像素），null 表示首次启动默认居中</summary>
        public int? WindowY { get; set; }

        /// <summary>窗口宽度（像素），null 表示使用默认大小</summary>
        public int? WindowWidth { get; set; }

        /// <summary>窗口高度（像素），null 表示使用默认大小</summary>
        public int? WindowHeight { get; set; }

        /// <summary>截图保存目录，如果为空或无效则使用默认路径（图片\AirPlayer）</summary>
        public string? ScreenshotSavePath { get; set; }

        /// <summary>首选音频输出设备 ID（为 null 表示使用默认系统播放设备）</summary>
        public string? PreferredAudioDevice { get; set; }

        /// <summary>录像保存目录，如果为空或无效则使用默认路径（视频\AirPlayer）</summary>
        public string? VideoSavePath { get; set; }

        private static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirPlayer");

        private static string FilePath => Path.Combine(Dir, "settings.json");

        /// <summary>从磁盘加载设置（不存在或出错则返回默认值）。</summary>
        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var s = JsonSerializer.Deserialize<AppSettings>(json);
                    if (s != null)
                    {
                        if (s.HudFontSize <= 0) s.HudFontSize = 13;
                        if (string.IsNullOrEmpty(s.HudTextColor)) s.HudTextColor = "#00E676";
                        if (s.HudBgOpacity < 0 || s.HudBgOpacity > 1) s.HudBgOpacity = 0.56;
                        return s;
                    }
                }
            }
            catch { /* 忽略，使用默认值 */ }
            return new AppSettings();
        }

        /// <summary>保存设置到磁盘。</summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FilePath, json);
            }
            catch { /* 忽略 */ }
        }
    }
}
