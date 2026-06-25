using System;
using System.Collections.Generic;
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

        /// <summary>HUD 背景不透明度 (0.0 到 1.0)</summary>
        public double HudBgOpacity { get; set; } = 0.30;

        /// <summary>HUD 位置：0=左上, 1=右上, 2=左下, 3=右下</summary>
        public int HudCorner { get; set; } = 0;

        /// <summary>HUD 水平偏移（像素）</summary>
        public int HudOffsetX { get; set; } = 12;

        /// <summary>HUD 垂直偏移（像素）</summary>
        public int HudOffsetY { get; set; } = 12;

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

        /// <summary>录制保存目录，如果为空或无效则使用默认路径（视频\AirPlayer）</summary>
        public string? RecordingSavePath { get; set; }

        /// <summary>录制角标位置：0=左下, 1=右下, 2=左上, 3=右上</summary>
        public int RecBadgeCorner { get; set; } = 2;

        /// <summary>录制角标水平偏移（像素）</summary>
        public int RecBadgeOffsetX { get; set; } = 12;

        /// <summary>录制角标垂直偏移（像素）</summary>
        public int RecBadgeOffsetY { get; set; } = 12;

        /// <summary>录制角标背景不透明度 (0.0 到 1.0)</summary>
        public double RecBadgeBgOpacity { get; set; } = 0.30;

        /// <summary>录制角标字体大小</summary>
        public int RecBadgeFontSize { get; set; } = 13;

        /// <summary>首选音频输出设备名称（为 null 表示使用默认系统播放设备）</summary>
        public string? PreferredAudioDevice { get; set; }

        /// <summary>播放音量 (0–100)，100 = 原始音量</summary>
        public int AudioVolume { get; set; } = 100;

        /// <summary>视频缩放模式：true = 铺满屏幕（裁切），false = 显示完整（信箱/柱箱，默认）</summary>
        public bool FillScreen { get; set; } = false;

        /// <summary>首选视频分辨率：1080 = 1080p，720 = 720p</summary>
        public int PreferredResolution { get; set; } = 1080;

        /// <summary>首选视频播放帧率：30 = 30fps，60 = 60fps</summary>
        public int PreferredFps { get; set; } = 60;

        /// <summary>界面主题："System"=跟随系统，"Light"=浅色，"Dark"=深色</summary>
        public string Theme { get; set; } = "System";

        /// <summary>待机页面呼吸灯颜色Hex</summary>
        public string PulseGlowColor { get; set; } = "#7C4DFF";

        /// <summary>关闭窗口行为：true=最小化到系统托盘后台常驻，false=直接退出</summary>
        public bool CloseToTray { get; set; } = true;

        /// <summary>启动时自动检查更新</summary>
        public bool AutoCheckUpdate { get; set; } = false;

        /// <summary>GitHub 访问令牌（可选）：用于检查更新时鉴权，将匿名 60 次/小时提升到 5000 次/小时，避免被限速。明文保存于本地配置。</summary>
        public string? GitHubToken { get; set; }

        /// <summary>跳过的更新版本号</summary>
        public string? SkippedVersion { get; set; }

        /// <summary>
        /// 用户自定义快捷键：键=动作 id（如 "rotate"），值=组合键字符串（如 "Ctrl+Shift+S"、"R"，空串表示禁用）。
        /// 缺省项回退到代码内置默认值。详见 MainWindow 的 ShortcutDefs。
        /// </summary>
        public Dictionary<string, string> Shortcuts { get; set; } = new Dictionary<string, string>();

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
                        if (s.HudBgOpacity < 0 || s.HudBgOpacity > 1) s.HudBgOpacity = 0.30;
                        if (s.HudCorner < 0 || s.HudCorner > 3) s.HudCorner = 0;
                        if (s.HudOffsetX < 0) s.HudOffsetX = 12;
                        if (s.HudOffsetY < 0) s.HudOffsetY = 12;
                        if (s.Shortcuts == null) s.Shortcuts = new Dictionary<string, string>(); // 旧配置文件无此字段
                        if (string.IsNullOrEmpty(s.Theme)) s.Theme = "System"; // 旧配置文件无此字段
                        if (s.RecBadgeCorner < 0 || s.RecBadgeCorner > 3) s.RecBadgeCorner = 2;
                        if (s.RecBadgeOffsetX < 0) s.RecBadgeOffsetX = 12;
                        if (s.RecBadgeOffsetY < 0) s.RecBadgeOffsetY = 12;
                        if (s.RecBadgeBgOpacity < 0 || s.RecBadgeBgOpacity > 1) s.RecBadgeBgOpacity = 0.30;
                        if (s.RecBadgeFontSize <= 0) s.RecBadgeFontSize = 13;
                        if (s.AudioVolume < 0 || s.AudioVolume > 100) s.AudioVolume = 100;
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
