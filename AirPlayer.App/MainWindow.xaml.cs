using System;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Windowing;
using Microsoft.UI;
using Windows.Graphics;
using AirPlayer.Protocol;
using AirPlayer.Protocol.Models;
using AirPlayer.Protocol.Models.Audio;
using AirPlayer.Protocol.Utils;
using AirPlayer.App.Rendering;
using System.Threading;

namespace AirPlayer.App
{
    /// <summary>
    /// 主窗口后置代码类：管理 AirPlay 服务、事件监听，以及全 GPU 视频管线。
    /// 视频管线由 VideoPresenter 全权负责，MainWindow 仅负责生命周期协调。
    /// </summary>
    public partial class MainWindow : Window
    {
        // AirPlay 接收核心实例
        private AirPlayReceiver? _receiver;

        // 接收监听器取消句柄
        private CancellationTokenSource? _cts;

        // 应用窗口抽象，用于控制全屏和 Resize
        private AppWindow? _appWindow;

        // 全屏状态标志
        private bool _isFullScreen;

        // 镜像连接是否活动中
        private volatile bool _isMirroringActive;

        // ===== 全 GPU 视频管线 =====
        private VideoPresenter? _presenter;           // 全 GPU 视频呈现器
        private bool _pipelineStarting;               // 管线是否正在创建
        private volatile bool _pipelineReady;         // 管线是否就绪
        private bool _gotKeyframe;                    // 是否已收到首个关键帧
        private bool _firstFrameLogged;               // 诊断：首帧是否已记录
        private int _videoWidth;
        private int _videoHeight;

        private double _videoAspectRatio;
        private int _rotationDegrees; // 0 or 270 (toggle only)
        private H264Data? _pendingFirstFrame;         // 首帧 IDR 暂存，管线就绪后补投

        // ===== 投屏态窗口位置记忆（0° 和 270° 各一套，投屏结束后重置）=====
        private PointInt32? _castingPos0;              // 0° 时的窗口位置
        private SizeInt32? _castingSize0;              // 0° 时的窗口大小
        private PointInt32? _castingPos270;            // 270° 时的窗口位置
        private SizeInt32? _castingSize270;            // 270° 时的窗口大小

        // ===== 音频播放管线 =====
        private AudioSink? _audioSink;                // 音频播放器
        private double _lastAirplayVolume = 0.0;      // iOS 端最近设置的音量(dB)，0=满音量；新建播放器时复用


        // ===== v0.3 体验：设置 / HUD / 控制条自动隐藏 =====
        private readonly AppSettings _settings = AppSettings.Load();   // 持久化设置
        private DispatcherTimer? _hudTimer;            // HUD 刷新定时器（1s）
        private DispatcherTimer? _controlHideTimer;    // 控制条自动隐藏定时器（3s）
        private DispatcherTimer? _toastTimer;          // 瞬时提示自动消失定时器
        private int _lastPresentedForFps;              // 上次呈现帧计数（算 FPS）
        private bool _menuOpen;                         // 菜单是否打开（打开时不自动隐藏按钮）
        private bool _titleThemeHooked;                 // 是否已订阅 ActualThemeChanged（避免重复订阅）
        private bool _micaActive;                        // 是否启用了 Mica 背景（影响根网格是否透明）
        private TrayIcon? _tray;                          // 系统托盘图标
        private bool _forceExit;                          // true 时关闭窗口直接退出（不再隐藏到托盘）
        private bool _hiddenToTray;                       // 当前是否已隐藏到托盘
        private bool _isDialogShowing;                    // 当前是否有 ContentDialog 正在显示（WinUI 同一时刻只允许一个，并发会崩溃）

        /// <summary>初始化主窗口并启动 AirPlay 接收服务</summary>
        public MainWindow()
        {
            this.InitializeComponent();

            // 获取原生窗口句柄并绑定 AppWindow
            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            // 设置应用窗口的图标
            try
            {
                string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "AppIcon.ico");
                if (System.IO.File.Exists(iconPath))
                {
                    _appWindow.SetIcon(iconPath);
                }
            }
            catch (Exception ex)
            {
                DiagLog.Write($"[UI] 设置窗口图标失败: {ex.Message}");
            }

            // Mica 背景：Win11 支持时启用，让待机页呈现 Fluent 磨砂质感（随主题明暗自动适配）。
            // 启用后把根网格背景透明化以透出 Mica；旧系统不支持则保留主题纯色背景。
            try
            {
                if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
                {
                    this.SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
                    _micaActive = true;
                    // 根网格保留半透明主题背景（AppBgBrush），透出少量 Mica 又保证深色够暗
                }
            }
            catch (Exception ex)
            {
                DiagLog.Write($"[UI] 启用 Mica 失败（非致命）: {ex.Message}");
            }

            // 系统托盘图标：左键还原，右键菜单（显示窗口 / 退出）；并拦截关闭按钮
            try
            {
                string trayIconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "AppIcon.ico");
                _tray = new TrayIcon(hWnd, trayIconPath, "AirPlayer");
                _tray.LeftClick     += ShowFromTray;
                _tray.ShowRequested += ShowFromTray;
                _tray.ExitRequested += ExitApp;
                _appWindow.Closing  += AppWindow_Closing;
            }
            catch (Exception ex)
            {
                DiagLog.Write($"[UI] 初始化托盘失败（非致命）: {ex.Message}");
            }

            this.Title = "AirPlayer";
            this.Closed += MainWindow_Closed;

            // 注册全局键盘快捷键（F11/Escape/R/H）
            this.Content.KeyDown += MainWindow_KeyDown;

            // 监听主网格尺寸变化，用于旋转时同步面板尺寸
            MainGrid.SizeChanged += MainGrid_SizeChanged;

            // 设备名：优先用设置里的自定义名，否则用计算机名
            string hostName = string.IsNullOrWhiteSpace(_settings.DeviceName)
                ? Environment.MachineName : _settings.DeviceName!;
            DeviceNameText.Text = hostName;

            // 待机页信息卡：本机 IP + 当前网络名
            UpdateStandbyInfo();

            // 应用持久化设置（置顶 / HUD），并同步设置浮窗开关状态
            ApplySettings();

            // 恢复上次窗口位置/大小（首次启动默认居中）
            RestoreWindowPosition();

            // HUD 刷新定时器（始终运行，仅在 HUD 可见时更新文本）
            _hudTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _hudTimer.Tick += HudTimer_Tick;
            _hudTimer.Start();

            // 控制条自动隐藏：鼠标移动显示并重置 3s 隐藏定时器（仅投屏时隐藏）
            _controlHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _controlHideTimer.Tick += ControlHideTimer_Tick;
            MainGrid.PointerMoved += MainGrid_PointerMoved;

            // 启动等待页脉动动画
            if (MainGrid.Resources.TryGetValue("PulseStoryboard", out object sbObj) && sbObj is Storyboard sb)
                sb.Begin();

            StartReceiver(hostName);

            // 启动时自动检查更新
            if (_settings.AutoCheckUpdate)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(3000);
                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        await RunUpdateCheckAsync(manual: false);
                    });
                });
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // v0.3：设置 / HUD / 控制条
        // ──────────────────────────────────────────────────────────────────

        /// <summary>解析十六进制颜色值为 Windows.UI.Color</summary>
        private static Windows.UI.Color GetColorFromHex(string hex)
        {
            try
            {
                hex = hex.Replace("#", string.Empty);
                byte r = byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
                byte g = byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
                byte b = byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
                return Windows.UI.Color.FromArgb(255, r, g, b);
            }
            catch
            {
                return Windows.UI.Color.FromArgb(255, 0, 230, 118); // 默认绿色
            }
        }

        /// <summary>应用持久化设置到窗口（菜单项的勾选状态在菜单打开时同步）。</summary>
        private void ApplySettings()
        {
            if (_appWindow?.Presenter is OverlappedPresenter op)
                op.IsAlwaysOnTop = _settings.AlwaysOnTop;

            // 应用界面主题：跟随系统 / 浅色 / 深色（设到根元素，立即生效）
            if (this.Content is FrameworkElement root)
            {
                root.RequestedTheme = CurrentElementTheme();
                // 跟随系统时，系统主题变化需同步标题栏颜色 → 订阅一次 ActualThemeChanged
                if (!_titleThemeHooked)
                {
                    root.ActualThemeChanged += (s, e) => ApplyTitleBarTheme(s.ActualTheme);
                    _titleThemeHooked = true;
                }
                // 用解析后的实际主题（System 会解析成 Light/Dark）给标题栏上色
                ApplyTitleBarTheme(root.ActualTheme);
            }
            
            HudPanel.Visibility = _settings.ShowHud ? Visibility.Visible : Visibility.Collapsed;

            // 应用 HUD 自定义尺寸、颜色和背景透明度参数
            HudText.FontSize = _settings.HudFontSize;
            HudText.Foreground = new SolidColorBrush(GetColorFromHex(_settings.HudTextColor));
            HudPanel.Background = new SolidColorBrush(Microsoft.UI.Colors.Black) { Opacity = _settings.HudBgOpacity };
        }

        /// <summary>把设置中的主题字符串映射为 ElementTheme（Default=跟随系统）。</summary>
        private Microsoft.UI.Xaml.ElementTheme CurrentElementTheme() => _settings.Theme switch
        {
            "Light" => Microsoft.UI.Xaml.ElementTheme.Light,
            "Dark"  => Microsoft.UI.Xaml.ElementTheme.Dark,
            _       => Microsoft.UI.Xaml.ElementTheme.Default,
        };

        /// <summary>按实际主题给系统标题栏（背景/标题/三个按钮）上色，使窗口整体跟随主题。</summary>
        private void ApplyTitleBarTheme(Microsoft.UI.Xaml.ElementTheme actual)
        {
            try
            {
                if (!Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported()) return; // 旧系统不支持则跟随系统
                var tb = _appWindow?.TitleBar;
                if (tb == null) return;

                bool dark = actual == Microsoft.UI.Xaml.ElementTheme.Dark;
                Windows.UI.Color C(byte a, byte r, byte g, byte b) => Windows.UI.Color.FromArgb(a, r, g, b);

                // 背景与根网格 AppBgBrush 一致：85% 主题色叠在 Mica 上，深色够暗又透出磨砂
                var bg       = dark ? C(0xD9, 0x09, 0x08, 0x11) : C(0xD9, 0xF2, 0xF3, 0xF7);
                var fg       = dark ? C(255, 0xFF, 0xFF, 0xFF) : C(255, 0x15, 0x15, 0x2B); // 标题/按钮前景
                var fgInact  = dark ? C(255, 0x96, 0x96, 0xAA) : C(255, 0x82, 0x82, 0x96); // 失焦前景
                var hoverBg  = dark ? C(40, 0xFF, 0xFF, 0xFF) : C(40, 0x00, 0x00, 0x00);   // 悬停背景
                var pressBg  = dark ? C(70, 0xFF, 0xFF, 0xFF) : C(70, 0x00, 0x00, 0x00);   // 按下背景

                tb.BackgroundColor = bg;
                tb.InactiveBackgroundColor = bg;
                tb.ForegroundColor = fg;
                tb.InactiveForegroundColor = fgInact;

                tb.ButtonBackgroundColor = bg;
                tb.ButtonInactiveBackgroundColor = bg;
                tb.ButtonForegroundColor = fg;
                tb.ButtonInactiveForegroundColor = fgInact;
                tb.ButtonHoverForegroundColor = fg;
                tb.ButtonHoverBackgroundColor = hoverBg;
                tb.ButtonPressedForegroundColor = fg;
                tb.ButtonPressedBackgroundColor = pressBg;
            }
            catch (Exception ex)
            {
                DiagLog.Write($"[UI] 标题栏主题上色失败（非致命）: {ex.Message}");
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // 系统托盘 / 后台常驻
        // ──────────────────────────────────────────────────────────────────

        /// <summary>关闭按钮：按设置最小化到托盘后台常驻，或直接退出。</summary>
        private void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
        {
            if (_settings.CloseToTray && !_forceExit)
            {
                args.Cancel = true; // 取消关闭，改为隐藏到托盘
                HideToTray();
            }
        }

        /// <summary>隐藏窗口到托盘（应用继续后台运行，接收投屏）。隐藏前先断开当前投屏。</summary>
        private void HideToTray()
        {
            try
            {
                // 先断开当前投屏，再隐藏到托盘（下次有设备投屏会自动弹窗）
                if (_isMirroringActive)
                    _receiver?.StopActiveMirroring();

                _appWindow?.Hide();
                _hiddenToTray = true;
            }
            catch (Exception ex) { DiagLog.Write($"[UI] 隐藏到托盘失败: {ex.Message}"); }
        }

        /// <summary>从托盘还原并激活窗口。</summary>
        private void ShowFromTray()
        {
            try
            {
                _appWindow?.Show();
                _hiddenToTray = false;
                _appWindow?.MoveInZOrderAtTop();
                this.Activate();
            }
            catch (Exception ex) { DiagLog.Write($"[UI] 从托盘还原失败: {ex.Message}"); }
        }

        /// <summary>彻底退出应用（托盘右键「退出」）。</summary>
        private void ExitApp()
        {
            _forceExit = true;       // 让 Closing 不再拦截
            _tray?.Dispose();
            _tray = null;
            this.Close();            // 关闭主窗口 → 应用退出
        }

        /// <summary>截图按钮：把当前帧保存为 PNG 到「图片\AirPlayer」。</summary>
        private void ScreenshotButton_Click(object sender, RoutedEventArgs e) => TakeScreenshot();

        private void TakeScreenshot()
        {
            if (_presenter == null || !_pipelineReady)
            {
                ShowToast("请先投屏再截图");
                return;
            }
            try
            {
                string? dir = _settings.ScreenshotSavePath;
                bool isDefault = false;
                if (string.IsNullOrWhiteSpace(dir))
                {
                    dir = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "AirPlayer");
                    isDefault = true;
                }

                try
                {
                    System.IO.Directory.CreateDirectory(dir);
                }
                catch (Exception ex)
                {
                    DiagLog.Write($"[UI] 自定义截图路径创建失败，回退到默认路径: {ex.Message}");
                    dir = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "AirPlayer");
                    System.IO.Directory.CreateDirectory(dir);
                    isDefault = true;
                }

                string path = System.IO.Path.Combine(dir, $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                _presenter.RequestScreenshot(path);

                if (isDefault)
                {
                    ShowToast("已保存截图到 图片\\AirPlayer");
                }
                else
                {
                    ShowToast($"已保存截图到 {dir}");
                }
            }
            catch (Exception ex)
            {
                ShowToast("截图失败");
                DiagLog.Write($"[UI] 截图请求失败: {ex.Message}");
            }
        }

        /// <summary>菜单「更多设置…」：弹窗进行本机名称、HUD相关参数（大小、颜色、透明度等）等综合设置。</summary>
        private async void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            // 1. 设备名称配置
            var nameHeader = new TextBlock 
            { 
                Text = "设备名称", 
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, 
                Margin = new Thickness(0, 0, 0, 4) 
            };
            var nameBox = new TextBox
            {
                Text = _settings.DeviceName ?? "",
                PlaceholderText = "使用系统默认计算机名",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var nameTip = new TextBlock 
            { 
                Text = "* 修改设备名称后需要重启应用生效", 
                FontSize = 11, 
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray), 
                Margin = new Thickness(0, 2, 0, 0) 
            };
            var nameContainer = new StackPanel();
            nameContainer.Children.Add(nameHeader);
            nameContainer.Children.Add(nameBox);
            nameContainer.Children.Add(nameTip);

            // 2. HUD 设置
            // HUD 字体大小 Slider
            var hudSizeSlider = new Slider
            {
                Header = "HUD 字体大小",
                Minimum = 10,
                Maximum = 24,
                Value = _settings.HudFontSize,
                StepFrequency = 1,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            // HUD 文本颜色 ComboBox
            var colors = new[]
            {
                (Name: "绿色", Hex: "#00E676"),
                (Name: "青色", Hex: "#00B0FF"),
                (Name: "白色", Hex: "#FFFFFF"),
                (Name: "黄色", Hex: "#FFD600"),
                (Name: "橙色", Hex: "#FF3D00"),
                (Name: "粉色", Hex: "#FF4081")
            };
            var colorCombo = new ComboBox
            {
                Header = "HUD 文本颜色",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            foreach (var c in colors)
            {
                colorCombo.Items.Add(c.Name);
            }
            int selectedIndex = 0;
            for (int i = 0; i < colors.Length; i++)
            {
                if (colors[i].Hex.Equals(_settings.HudTextColor, StringComparison.OrdinalIgnoreCase))
                {
                    selectedIndex = i;
                    break;
                }
            }
            colorCombo.SelectedIndex = selectedIndex;

            // HUD 背景透明度 Slider
            var hudOpacitySlider = new Slider
            {
                Header = "HUD 背景不透明度 (%)",
                Minimum = 0,
                Maximum = 100,
                Value = _settings.HudBgOpacity * 100,
                StepFrequency = 5,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var hudContainer = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 0, 4) };
            hudContainer.Children.Add(hudSizeSlider);
            hudContainer.Children.Add(colorCombo);
            hudContainer.Children.Add(hudOpacitySlider);

            // HUD 区块标题，直接展示（不使用折叠 Expander）
            var hudSectionHeader = new TextBlock
            {
                Text = "HUD 监控参数设置",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 0)
            };

            // 3. 截图保存路径配置
            var screenshotHeader = new TextBlock 
            { 
                Text = "截图保存目录", 
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, 
                Margin = new Thickness(0, 8, 0, 4) 
            };

            var defaultPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "AirPlayer");

            var pathGrid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var pathBox = new TextBox
            {
                Text = _settings.ScreenshotSavePath ?? defaultPath,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                PlaceholderText = defaultPath
            };

            var browseBtn = new Button
            {
                Content = "浏览...",
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Bottom
            };

            Grid.SetColumn(pathBox, 0);
            Grid.SetColumn(browseBtn, 1);
            pathGrid.Children.Add(pathBox);
            pathGrid.Children.Add(browseBtn);

            browseBtn.Click += async (s, args) =>
            {
                try
                {
                    var folderPicker = new Windows.Storage.Pickers.FolderPicker();
                    IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                    WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, hWnd);
                    folderPicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
                    folderPicker.FileTypeFilter.Add("*");

                    var folder = await folderPicker.PickSingleFolderAsync();
                    if (folder != null)
                    {
                        pathBox.Text = folder.Path;
                    }
                }
                catch (Exception ex)
                {
                    DiagLog.Write($"[UI] 启动文件夹选择器失败: {ex.Message}");
                    ShowToast("打开文件夹选择失败");
                }
            };

            var screenshotContainer = new StackPanel { Spacing = 6 };
            screenshotContainer.Children.Add(screenshotHeader);
            screenshotContainer.Children.Add(pathGrid);

            // 截图路径区块标题已在 screenshotContainer 内部（screenshotHeader），直接展示

            // 4. 音频输出设备配置
            var audioHeader = new TextBlock 
            { 
                Text = "音频设置", 
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, 
                Margin = new Thickness(0, 8, 0, 0) 
            };

            var audioDeviceCombo = new ComboBox
            {
                Header = "音频输出设备",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            // 添加系统默认设备选项
            audioDeviceCombo.Items.Add("系统默认设备");
            var deviceIds = new System.Collections.Generic.List<string?>();
            deviceIds.Add(null); // 系统默认的 ID 对应 null

            // 获取音频输出设备
            try
            {
                var devices = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(
                    Windows.Media.Devices.MediaDevice.GetAudioRenderSelector());
                int selectIdx = 0;
                int idx = 1;
                foreach (var device in devices)
                {
                    audioDeviceCombo.Items.Add(device.Name);
                    deviceIds.Add(device.Name); // 存设备名（waveOut 按名称匹配索引）
                    if (device.Name == _settings.PreferredAudioDevice)
                    {
                        selectIdx = idx;
                    }
                    idx++;
                }
                audioDeviceCombo.SelectedIndex = selectIdx;
            }
            catch (Exception ex)
            {
                DiagLog.Write($"[UI] 枚举音频设备失败: {ex.Message}");
                audioDeviceCombo.SelectedIndex = 0; // 默认第一项（系统默认）
            }

            var audioContainer = new StackPanel { Spacing = 6 };
            audioContainer.Children.Add(audioHeader);
            audioContainer.Children.Add(audioDeviceCombo);

            // 5. 视频分辨率配置
            var resolutionHeader = new TextBlock 
            { 
                Text = "视频设置", 
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, 
                Margin = new Thickness(0, 8, 0, 0) 
            };
            var resolutionCombo = new ComboBox
            {
                Header = "分辨率限制 (修改后重启生效)",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            resolutionCombo.Items.Add("1080p (1920x1080)");
            resolutionCombo.Items.Add("720p (1280x720)");
            resolutionCombo.SelectedIndex = _settings.PreferredResolution == 720 ? 1 : 0;

            var resolutionContainer = new StackPanel { Spacing = 6 };
            resolutionContainer.Children.Add(resolutionHeader);
            resolutionContainer.Children.Add(resolutionCombo);

            var fpsCombo = new ComboBox
            {
                Header = "帧率限制 (修改后重启生效)",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            fpsCombo.Items.Add("60 fps (推荐)");
            fpsCombo.Items.Add("30 fps");
            fpsCombo.SelectedIndex = _settings.PreferredFps == 30 ? 1 : 0;

            var fpsContainer = new StackPanel { Spacing = 6 };
            fpsContainer.Children.Add(fpsCombo);

            // 后台运行：关闭窗口行为
            var closeBehaviorCombo = new ComboBox
            {
                Header = "关闭窗口时",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            closeBehaviorCombo.Items.Add("最小化到托盘（后台常驻）");
            closeBehaviorCombo.Items.Add("直接退出");
            closeBehaviorCombo.SelectedIndex = _settings.CloseToTray ? 0 : 1;
            var closeBehaviorHeader = new TextBlock
            {
                Text = "后台运行",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 0)
            };
            var closeBehaviorTip = new TextBlock
            {
                Text = "最小化到托盘后，有 iOS 设备投屏会自动弹出窗口；托盘图标右键可彻底退出。",
                FontSize = 11,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            };
            var closeBehaviorContainer = new StackPanel { Spacing = 6 };
            closeBehaviorContainer.Children.Add(closeBehaviorHeader);
            closeBehaviorContainer.Children.Add(closeBehaviorCombo);
            closeBehaviorContainer.Children.Add(closeBehaviorTip);

            // 6.5 外观主题
            var themeCombo = new ComboBox
            {
                Header = "外观主题",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            themeCombo.Items.Add("跟随系统");
            themeCombo.Items.Add("浅色");
            themeCombo.Items.Add("深色");
            themeCombo.SelectedIndex = _settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
            var themeHeader = new TextBlock
            {
                Text = "外观",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 0)
            };
            var themeContainer = new StackPanel { Spacing = 6 };
            themeContainer.Children.Add(themeHeader);
            themeContainer.Children.Add(themeCombo);

            // 7. 快捷键设置（按键捕获，支持组合键，退出全屏固定为 Esc）
            var shortcutHeader = new TextBlock
            {
                Text = "快捷键设置",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 0)
            };
            var shortcutTip = new TextBlock
            {
                Text = "点击右侧按钮后，按下想要的按键（可配合 Ctrl/Shift/Alt 组合）。捕获时按 Esc 取消；退出全屏固定为 Esc。",
                FontSize = 11,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            };

            // 工作副本：在弹窗内编辑，确认后才写回设置
            var pendingShortcuts = new System.Collections.Generic.Dictionary<string, string>();
            foreach (var d in ShortcutDefs) pendingShortcuts[d.Id] = GetEffectiveShortcut(d.Id);
            var rowButtons = new System.Collections.Generic.Dictionary<string, Button>();
            string? capturingId = null; // 当前正在捕获的动作 id（同一时刻仅一个）

            static string DisplayCombo(string c) => string.IsNullOrEmpty(c) ? "未设置" : c;

            var shortcutContainer = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 4) };
            shortcutContainer.Children.Add(shortcutHeader);
            shortcutContainer.Children.Add(shortcutTip);

            foreach (var d in ShortcutDefs)
            {
                string captureId = d.Id; // 供闭包捕获

                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var label = new TextBlock { Text = d.Name, VerticalAlignment = VerticalAlignment.Center };
                var btn = new Button
                {
                    Content = DisplayCombo(pendingShortcuts[captureId]),
                    MinWidth = 130,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                Grid.SetColumn(label, 0);
                Grid.SetColumn(btn, 1);
                row.Children.Add(label);
                row.Children.Add(btn);
                rowButtons[captureId] = btn;

                // 点击进入捕获状态
                btn.Click += (s, args) =>
                {
                    capturingId = captureId;
                    btn.Content = "按下快捷键…(Esc 取消)";
                    btn.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
                };

                // 捕获按键
                btn.KeyDown += (s, args) =>
                {
                    if (capturingId != captureId) return;
                    if (IsModifierKey(args.Key)) { args.Handled = true; return; } // 等待非修饰主键
                    args.Handled = true;

                    if (args.Key == Windows.System.VirtualKey.Escape)
                    {
                        btn.Content = DisplayCombo(pendingShortcuts[captureId]); // 取消，恢复原显示
                        capturingId = null;
                        return;
                    }

                    string combo = FormatCombo(args.Key);
                    // 冲突处理：清除占用同一组合键的其它动作
                    foreach (var other in ShortcutDefs)
                    {
                        if (other.Id != captureId &&
                            string.Equals(pendingShortcuts[other.Id], combo, StringComparison.OrdinalIgnoreCase))
                        {
                            pendingShortcuts[other.Id] = "";
                            rowButtons[other.Id].Content = DisplayCombo("");
                        }
                    }
                    pendingShortcuts[captureId] = combo;
                    btn.Content = DisplayCombo(combo);
                    capturingId = null;
                };

                // 捕获中点到别处：取消并恢复显示
                btn.LostFocus += (s, args) =>
                {
                    if (capturingId == captureId)
                    {
                        btn.Content = DisplayCombo(pendingShortcuts[captureId]);
                        capturingId = null;
                    }
                };

                shortcutContainer.Children.Add(row);
            }

            // 恢复默认快捷键
            var resetShortcutsBtn = new Button { Content = "恢复默认快捷键", Margin = new Thickness(0, 4, 0, 0) };
            resetShortcutsBtn.Click += (s, args) =>
            {
                capturingId = null;
                foreach (var d in ShortcutDefs)
                {
                    pendingShortcuts[d.Id] = d.Default;
                    rowButtons[d.Id].Content = DisplayCombo(d.Default);
                }
            };
            shortcutContainer.Children.Add(resetShortcutsBtn);

            // 分类标签页：把设置按 通用/视频/音频/外观/快捷键 分组，避免一长条滚动
            ScrollViewer Scroll(StackPanel sp) => new Microsoft.UI.Xaml.Controls.ScrollViewer
            {
                Content = sp,
                VerticalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Disabled
            };
            PivotItem Tab(string header, params UIElement[] items)
            {
                var sp = new StackPanel { Spacing = 16, Margin = new Thickness(2, 8, 14, 8) };
                foreach (var it in items) sp.Children.Add(it);
                return new PivotItem { Header = header, Content = Scroll(sp) };
            }

            // 6.6 软件更新配置
            var updateHeader = new TextBlock
            {
                Text = "软件更新",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 0)
            };
            var autoUpdateSwitch = new ToggleSwitch
            {
                Header = "启动时自动检查更新",
                IsOn = _settings.AutoCheckUpdate,
                Margin = new Thickness(0, 4, 0, 4)
            };
            var versionText = new TextBlock
            {
                Text = $"当前版本: v{UpdateChecker.CurrentVersionDisplay}",
                FontSize = 12,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                VerticalAlignment = VerticalAlignment.Center
            };
            var checkUpdateBtn = new Button
            {
                Content = "检查更新",
                Margin = new Thickness(12, 0, 0, 0)
            };
            var checkUpdatePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 4, 0, 0)
            };
            checkUpdatePanel.Children.Add(versionText);
            checkUpdatePanel.Children.Add(checkUpdateBtn);

            var updateContainer = new StackPanel { Spacing = 6 };
            updateContainer.Children.Add(updateHeader);
            updateContainer.Children.Add(autoUpdateSwitch);
            updateContainer.Children.Add(checkUpdatePanel);

            ContentDialog dlg = null!;

            void SaveAllSettings()
            {
                // 1. 保存并更新设备名称
                var name = nameBox.Text?.Trim();
                var oldName = _settings.DeviceName;
                _settings.DeviceName = string.IsNullOrEmpty(name) ? null : name;

                // 2. 更新 HUD 参数型设置
                _settings.HudFontSize = (int)hudSizeSlider.Value;
                _settings.HudTextColor = colors[colorCombo.SelectedIndex].Hex;
                _settings.HudBgOpacity = hudOpacitySlider.Value / 100.0;

                // 3. 保存截图路径
                var pathInput = pathBox.Text?.Trim();
                if (string.IsNullOrEmpty(pathInput) || pathInput.Equals(defaultPath, StringComparison.OrdinalIgnoreCase))
                {
                    _settings.ScreenshotSavePath = null;
                }
                else
                {
                    try
                    {
                        var fullPath = System.IO.Path.GetFullPath(pathInput);
                        _settings.ScreenshotSavePath = fullPath;
                    }
                    catch (Exception ex)
                    {
                        ShowToast("截图路径格式不合法，未保存该项");
                        DiagLog.Write($"[UI] 用户输入的截图保存路径不合法: {pathInput}, {ex.Message}");
                    }
                }

                // 4. 保存音频输出设备设置
                string? oldAudioDevice = _settings.PreferredAudioDevice;
                int audioIdx = audioDeviceCombo.SelectedIndex;
                string? newAudioDevice = null;
                if (audioIdx >= 0 && audioIdx < deviceIds.Count)
                {
                    newAudioDevice = deviceIds[audioIdx];
                }
                _settings.PreferredAudioDevice = newAudioDevice;

                // 5. 保存视频分辨率设置
                var oldRes = _settings.PreferredResolution;
                _settings.PreferredResolution = resolutionCombo.SelectedIndex == 1 ? 720 : 1080;
                bool resChanged = _settings.PreferredResolution != oldRes;

                // 6. 保存视频帧率设置
                var oldFps = _settings.PreferredFps;
                _settings.PreferredFps = fpsCombo.SelectedIndex == 1 ? 30 : 60;
                bool fpsChanged = _settings.PreferredFps != oldFps;

                // 7. 保存自定义快捷键（即时生效，无需重启）
                _settings.Shortcuts = new System.Collections.Generic.Dictionary<string, string>(pendingShortcuts);

                // 8. 保存外观主题（ApplySettings 会立即应用）
                _settings.Theme = themeCombo.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "System" };

                // 9. 保存关闭窗口行为（即时生效，下次关闭按此处理）
                _settings.CloseToTray = closeBehaviorCombo.SelectedIndex == 0;

                // 9.5 保存自动更新设置
                _settings.AutoCheckUpdate = autoUpdateSwitch.IsOn;

                // 应用所有 HUD 设置并保存
                ApplySettings();
                _settings.Save();

                // 如果音频设备改变，且当前正在投屏，实时重启 AudioSink
                if (oldAudioDevice != _settings.PreferredAudioDevice)
                {
                    if (_audioSink != null && _isMirroringActive)
                    {
                        try
                        {
                            _audioSink.Dispose();
                            _audioSink = new AudioSink(_settings.PreferredAudioDevice);
                            _audioSink.Initialize();
                            DiagLog.Write("[UI] 音频输出设备发生变化，已实时重启 AudioSink");
                            ShowToast("音频设备已切换");
                        }
                        catch (Exception ex)
                        {
                            DiagLog.Write($"[UI] 实时切换音频设备异常: {ex.Message}");
                        }
                    }
                }

                // 提示反馈
                if (_settings.DeviceName != oldName || resChanged || fpsChanged)
                {
                    ShowToast("设置已保存，修改内容重启生效");
                }
                else
                {
                    ShowToast("设置已保存");
                }
            }

            checkUpdateBtn.Click += async (s, args) =>
            {
                SaveAllSettings();
                dlg.Hide();
                await RunUpdateCheckAsync(manual: true);
            };

            var pivot = new Pivot { Width = 380, Height = 460 };
            pivot.Items.Add(Tab("通用", nameContainer, closeBehaviorContainer, screenshotContainer, updateContainer));
            // 音视频合并为一个选项卡，内部以「视频设置」「音频设置」分组标题区分
            pivot.Items.Add(Tab("音视频", resolutionContainer, fpsContainer, audioContainer));
            pivot.Items.Add(Tab("外观", themeContainer, hudSectionHeader, hudContainer));
            pivot.Items.Add(Tab("快捷键", shortcutContainer));

            dlg = new ContentDialog
            {
                Title = "更多设置",
                Content = pivot,
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
                RequestedTheme = CurrentElementTheme() // 浮层不在主网格子树内，需单独跟随主题
            };

            var result = await ShowDialogAsync(dlg);
            if (result == ContentDialogResult.Primary)
            {
                SaveAllSettings();
            }
        }

        /// <summary>底部瞬时提示（约 1.8 秒后自动消失）。</summary>
        private void ShowToast(string message)
        {
            ToastText.Text = message;
            ToastPanel.Visibility = Visibility.Visible;
            if (_toastTimer == null)
            {
                _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
                _toastTimer.Tick += (s, e) => { _toastTimer?.Stop(); ToastPanel.Visibility = Visibility.Collapsed; };
            }
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        /// <summary>菜单打开前：把开关项勾选状态同步到当前实际状态。</summary>
        private void MainMenuFlyout_Opening(object? sender, object e)
        {
            HudMenuItem.IsChecked = HudPanel.Visibility == Visibility.Visible;
            AlwaysOnTopMenuItem.IsChecked =
                (_appWindow?.Presenter as OverlappedPresenter)?.IsAlwaysOnTop ?? _settings.AlwaysOnTop;

            var visibility = _isMirroringActive ? Visibility.Visible : Visibility.Collapsed;
            FullScreenMenuItem.Visibility    = visibility;
            RotateMenuItem.Visibility        = visibility;
            ScreenshotMenuItem.Visibility    = visibility;
            ExitMirroringMenuItem.Visibility = visibility;
            ActiveCastingSeparator.Visibility = visibility;

            // 铺满屏幕仅在「投屏中 + 全屏」时才有意义
            var fillVisibility = (_isMirroringActive && _isFullScreen) ? Visibility.Visible : Visibility.Collapsed;
            FillScreenMenuItem.Visibility = fillVisibility;
            FillScreenMenuItem.IsChecked  = _settings.FillScreen;

            // 检查更新仅主界面（未投屏）显示，投屏播放时隐藏，避免打断投屏体验
            CheckUpdateMenuItem.Visibility = _isMirroringActive ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>菜单「退出投屏」按钮点击事件。</summary>
        private void ExitMirroringMenuItem_Click(object sender, RoutedEventArgs e)
        {
            _receiver?.StopActiveMirroring();
        }

        /// <summary>菜单打开/关闭：维护标志，避免菜单开着时控制按钮被自动隐藏。</summary>
        private void MainMenuFlyout_Opened(object? sender, object e) => _menuOpen = true;

        private void MainMenuFlyout_Closed(object? sender, object e)
        {
            _menuOpen = false;
            // 关闭后若在投屏，重新开始自动隐藏倒计时
            _controlHideTimer?.Stop();
            if (_isMirroringActive) _controlHideTimer?.Start();
        }

        /// <summary>切换窗口置顶状态并同步 UI 和配置。</summary>
        private void ToggleAlwaysOnTop()
        {
            bool on = !((_appWindow?.Presenter as OverlappedPresenter)?.IsAlwaysOnTop ?? _settings.AlwaysOnTop);
            SetAlwaysOnTop(on);
            ShowToast(on ? "窗口已置顶" : "窗口已取消置顶");
        }

        private void SetAlwaysOnTop(bool on)
        {
            _settings.AlwaysOnTop = on;
            if (_appWindow?.Presenter is OverlappedPresenter op)
                op.IsAlwaysOnTop = on;
            _settings.Save();
            AlwaysOnTopMenuItem.IsChecked = on;
        }

        /// <summary>菜单「窗口置顶」开关。</summary>
        private void AlwaysOnTopMenuItem_Click(object sender, RoutedEventArgs e)
        {
            bool on = AlwaysOnTopMenuItem.IsChecked;
            SetAlwaysOnTop(on);
            ShowToast(on ? "窗口已置顶" : "窗口已取消置顶");
        }

        /// <summary>菜单「显示 HUD」开关。</summary>
        private void HudMenuItem_Click(object sender, RoutedEventArgs e)
            => SetHudVisible(HudMenuItem.IsChecked);

        /// <summary>统一设置 HUD 可见性并持久化。</summary>
        private void SetHudVisible(bool on)
        {
            HudPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            _settings.ShowHud = on;
            _settings.Save();
        }

        /// <summary>HUD 定时刷新：分辨率 / FPS / 解码 / 丢帧。</summary>
        private void HudTimer_Tick(object? sender, object e)
        {
            if (HudPanel.Visibility != Visibility.Visible) return;
            if (_presenter == null || !_pipelineReady)
            {
                HudText.Text = "等待投屏…";
                _lastPresentedForFps = 0;
                return;
            }
            var s = _presenter.GetStats();
            int fps = s.Presented - _lastPresentedForFps;
            if (fps < 0) fps = 0;
            _lastPresentedForFps = s.Presented;
            // 显示「目标/实际」帧率：目标为设置中的 PreferredFps，实际为上一秒真实呈现帧数
            HudText.Text = $"{s.Width}x{s.Height}   {_settings.PreferredFps}/{fps} fps\n解码 {s.Decoded}  丢帧 {s.Skipped}";
        }

        /// <summary>鼠标移动：显示菜单按钮并重置自动隐藏计时（仅投屏时会隐藏）。</summary>
        private void MainGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            MenuButton.Visibility = Visibility.Visible;
            _controlHideTimer?.Stop();
            if (_isMirroringActive) _controlHideTimer?.Start();
        }

        /// <summary>3 秒无操作：投屏中且菜单未打开时隐藏菜单按钮。</summary>
        private void ControlHideTimer_Tick(object? sender, object e)
        {
            _controlHideTimer?.Stop();
            if (_menuOpen) return; // 菜单打开时不隐藏
            if (_isMirroringActive) MenuButton.Visibility = Visibility.Collapsed;
        }

        /// <summary>刷新待机页信息卡：本机 IP 与当前网络名。</summary>
        private void UpdateStandbyInfo()
        {
            string ip = GetLocalIPv4();
            IpText.Text = string.IsNullOrEmpty(ip) ? "未连接网络" : ip;
            WifiText.Text = GetNetworkName();
        }

        /// <summary>获取当前已联网的网络名称（Wi-Fi 为 SSID，有线为适配器名）。</summary>
        private static string GetNetworkName()
        {
            try
            {
                var profile = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
                string? name = profile?.ProfileName;
                return string.IsNullOrEmpty(name) ? "—" : name;
            }
            catch
            {
                return "—";
            }
        }

        /// <summary>获取本机第一个非回环 IPv4 地址。</summary>
        private static string GetLocalIPv4()
        {
            try
            {
                // 用 UDP 连接一个外部地址，借此让操作系统根据路由表选择出口 IP（无需真正发送数据）
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                {
                    socket.Connect("8.8.8.8", 65530);
                    if (socket.LocalEndPoint is System.Net.IPEndPoint endPoint)
                    {
                        return endPoint.Address.ToString();
                    }
                }
            }
            catch
            {
                // 如果没有公网路由或断网状态，回退到遍历网卡寻找有网关的物理网卡 IP
                try
                {
                    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (ni.OperationalStatus != OperationalStatus.Up) continue;
                        if (ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet && 
                            ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;

                        // 排除常见虚拟网卡关键字
                        string desc = ni.Description.ToLower();
                        if (desc.Contains("virtual") || desc.Contains("wsl") || desc.Contains("hyper-v") ||
                            desc.Contains("vmware") || desc.Contains("virtualbox") || desc.Contains("vbox") ||
                            desc.Contains("vpn") || desc.Contains("tap") || desc.Contains("loopback"))
                            continue;

                        var props = ni.GetIPProperties();
                        if (props.GatewayAddresses.Count == 0) continue; // 必须有网关

                        foreach (var ua in props.UnicastAddresses)
                        {
                            if (ua.Address.AddressFamily == AddressFamily.InterNetwork && 
                                !System.Net.IPAddress.IsLoopback(ua.Address))
                            {
                                return ua.Address.ToString();
                            }
                        }
                    }
                }
                catch { }
            }

            // 最后的兜底：普通遍历第一个非回环 IPv4
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork
                            && !System.Net.IPAddress.IsLoopback(ua.Address))
                            return ua.Address.ToString();
                    }
                }
            }
            catch { }
            return "";
        }

        // ──────────────────────────────────────────────────────────────────
        // AirPlay 服务启动
        // ──────────────────────────────────────────────────────────────────

        /// <summary>后台启动 AirPlay 接收服务</summary>
        private void StartReceiver(string deviceName)
        {
            _cts = new CancellationTokenSource();

            // 必须在任何监听器启动前重定向控制台：把各监听器/库的 Console.WriteLine 接进诊断日志，
            // 否则 WinUI 桌面应用无控制台窗口，握手与连接期的关键诊断全部丢失，无法排查"连不上"
            DiagLog.RedirectConsole();

            _receiver = new AirPlayReceiver(
                deviceName,
                preferredWidth: _settings.PreferredResolution == 720 ? 1280 : 1920,
                preferredHeight: _settings.PreferredResolution == 720 ? 720 : 1080,
                preferredFps: _settings.PreferredFps
            );
            _receiver.OnMirroringStartedReceived += Receiver_OnMirroringStarted;
            _receiver.OnMirroringStoppedReceived += Receiver_OnMirroringStopped;
            _receiver.OnH264DataReceived         += Receiver_OnH264DataReceived;
            _receiver.OnPcmDataReceived          += Receiver_OnPcmDataReceived;
            _receiver.OnAudioFlushReceived       += Receiver_OnAudioFlushReceived;
            _receiver.OnSetVolumeReceived        += Receiver_OnSetVolume; // iOS 端调整投屏音量

            Task.Run(async () =>
            {
                try
                {
                    await _receiver.StartListeners(_cts.Token);
                    await _receiver.StartMdnsAsync();
                }
                catch (Exception ex)
                {
                    // 启动异常接进诊断日志（原 Debug.WriteLine 在无调试器时不可见，会导致"连不上却无任何线索"）
                    DiagLog.Write($"[RECV] AirPlay 接收器启动异常: {ex}");
                }
            });
        }

        // ──────────────────────────────────────────────────────────────────
        // 镜像事件处理
        // ──────────────────────────────────────────────────────────────────

        /// <summary>镜像开始：重置状态，等待首帧触发管线创建</summary>
        private void Receiver_OnMirroringStarted(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                // 后台（托盘隐藏）状态下有设备投屏：自动弹出播放窗口
                if (_hiddenToTray) ShowFromTray();

                _isMirroringActive = true;
                _gotKeyframe       = false;
                _firstFrameLogged  = false;

                // 投屏开始：启动控制条自动隐藏倒计时
                _controlHideTimer?.Start();

                // 初始化音频播放器
                if (_audioSink == null)
                {
                    _audioSink = new AudioSink(_settings.PreferredAudioDevice);
                    _audioSink.Initialize();
                    _audioSink.SetVolume(_lastAirplayVolume); // 应用 iOS 端当前音量（可能在建播放器前已下发）
                }

                ShowToast("已连接，正在投屏…"); // 连接反馈
            });
        }

        /// <summary>镜像结束：停止管线，恢复引导页，恢复待机态窗口</summary>
        private void Receiver_OnMirroringStopped(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _isMirroringActive = false;
                StopVideoPipeline();

                // 释放音频播放器
                _audioSink?.Dispose();
                _audioSink = null;

                // 投屏结束：恢复菜单按钮常显，重置 HUD 计数
                _controlHideTimer?.Stop();
                MenuButton.Visibility = Visibility.Visible;
                _lastPresentedForFps = 0;

                SwapPanel.Visibility  = Visibility.Collapsed;
                PromoGrid.Visibility  = Visibility.Visible;

                // 刷新待机信息（IP/网络可能已变化）并提示
                UpdateStandbyInfo();
                ShowToast("投屏已结束");

                // 恢复投屏前保存的待机态窗口位置和大小
                RestoreWindowPosition();
            });
        }

        /// <summary>收到 PCM 音频帧时：投递到音频播放器</summary>
        private void Receiver_OnPcmDataReceived(object? sender, PcmData pcmData)
        {
            if (!_isMirroringActive) return;
            if (_pcmFrameCount <= 5 || _pcmFrameCount % 1000 == 0)
                AudioDiagLog.Write($"[UI-PCM] #{_pcmFrameCount}: len={pcmData.Length} pts={pcmData.Pts} sink={(_audioSink != null ? "ok" : "null")}");
            _pcmFrameCount++;
            _audioSink?.EnqueueFrame(pcmData);


        }

        private long _pcmFrameCount;

        /// <summary>音频刷新事件：清空播放队列</summary>
        private void Receiver_OnAudioFlushReceived(object? sender, EventArgs e)
        {
            _audioSink?.Flush();
        }

        /// <summary>iOS 端调整投屏音量：记录并实时应用到音频播放器（在网络线程触发，SetVolume 内部线程安全）</summary>
        private void Receiver_OnSetVolume(object? sender, decimal volume)
        {
            _lastAirplayVolume = (double)volume; // 记录最近音量，新建播放器时复用
            _audioSink?.SetVolume(_lastAirplayVolume);
        }

        /// <summary>每收到一帧 H264 时：过滤后入队，首帧触发管线创建</summary>
        private void Receiver_OnH264DataReceived(object? sender, H264Data data)
        {
            if (!_isMirroringActive) return;



            // 诊断：记录首帧到达
            if (!_firstFrameLogged)
            {
                _firstFrameLogged = true;
                DiagLog.Write($"[UI] 首帧到达 {data.Width}x{data.Height} type={data.FrameType}");
            }

            // 等待首个关键帧(IDR)：之前的 P 帧必须丢弃，否则解码器无参考帧
            if (!_gotKeyframe)
            {
                if (data.FrameType != 5) return; // 非 IDR，丢弃
                _gotKeyframe = true;
                DiagLog.Write("[UI] 收到首个 IDR，开始投递帧");
            }

            // 管线就绪后直接投递
            if (_pipelineReady)
            {
                _presenter?.EnqueueFrame(data);
                return;
            }

            // 管线尚未就绪：首帧（IDR）触发唯一一次管线创建，并暂存该帧
            if (!_pipelineStarting)
            {
                _pipelineStarting = true;
                _pendingFirstFrame = data; // 暂存首帧 IDR，管线就绪后补投
                int w = data.Width, h = data.Height;
                DispatcherQueue.TryEnqueue(() => CreateVideoPipeline(w, h));
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // 管线创建（在 UI 线程）
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// 在 UI 线程创建全 GPU 视频管线。
        /// 完成后 VideoPresenter 接管解码和呈现。
        /// </summary>
        private void CreateVideoPipeline(int videoWidth, int videoHeight)
        {
            if (!_isMirroringActive) return;

            // 投屏开始前，保存当前待机态窗口位置/大小，以便投屏结束后恢复
            // 注意：此时 _isMirroringActive 已为 true，不能用 SaveWindowPosition()（会被跳过）
            if (_appWindow != null && !_isFullScreen)
            {
                _settings.WindowX = _appWindow.Position.X;
                _settings.WindowY = _appWindow.Position.Y;
                _settings.WindowWidth = _appWindow.Size.Width;
                _settings.WindowHeight = _appWindow.Size.Height;
                _settings.Save();
            }

            _videoWidth = videoWidth;
            _videoHeight = videoHeight;
            _videoAspectRatio = (double)videoWidth / videoHeight;

            // 计算初始窗口大小（保持比例，不超过屏幕 85%）
            CalculateWindowSizeForVideo(videoWidth, videoHeight, out int winW, out int winH);

            try
            {
                // 显示视频面板，隐藏引导页
                SwapPanel.Visibility  = Visibility.Visible;
                PromoGrid.Visibility  = Visibility.Collapsed;

                // 调整窗口大小以适配视频，然后居中
                _appWindow?.Resize(new SizeInt32(winW, winH));
                CenterWindowOnScreen();
                _appWindow!.Changed += AppWindow_Changed;

                DiagLog.Write($"[UI] 窗口调整 {winW}x{winH} (视频 {videoWidth}x{videoHeight})");

                // 获取面板物理像素尺寸（VisualSize 是逻辑尺寸，需乘 DPI 缩放）
                double dpiScale  = GetDpiScale();
                int panelPixelW  = Math.Max(1, (int)(SwapPanel.ActualWidth  * dpiScale));
                int panelPixelH  = Math.Max(1, (int)(SwapPanel.ActualHeight * dpiScale));

                // 若 ActualSize 还未测量，回落到窗口尺寸
                if (panelPixelW < 8 || panelPixelH < 8)
                {
                    panelPixelW = winW;
                    panelPixelH = winH;
                }

                // 创建并初始化 VideoPresenter
                _presenter = new VideoPresenter();
                // 传入目标帧率：超过该帧率的多余帧在接收端只解码不呈现（主动丢帧），使实际呈现帧率符合设置
                _presenter.Initialize(SwapPanel, videoWidth, videoHeight, panelPixelW, panelPixelH, _settings.PreferredFps);

                // 注册面板尺寸变化事件（用于 Resize）
                SwapPanel.SizeChanged += SwapPanel_SizeChanged;
            }
            catch (Exception ex)
            {
                DiagLog.Write($"[UI] 管线创建失败: {ex}");
                StopVideoPipeline();
                _pipelineStarting = false;
                return;
            }

            _pipelineReady    = true;
            _pipelineStarting = false;
            DiagLog.Write("[UI] 全 GPU 管线就绪");

            // 应用持久化的缩放模式设置
            if (_settings.FillScreen)
                _presenter?.SetFillMode(true);

            // 补投暂存的首帧 IDR，避免首个关键帧丢失导致黑屏
            if (_pendingFirstFrame.HasValue)
            {
                _presenter?.EnqueueFrame(_pendingFirstFrame.Value);
                DiagLog.Write("[UI] 已补投首帧 IDR");
                _pendingFirstFrame = null;
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // 面板 Resize
        // ──────────────────────────────────────────────────────────────────

        /// <summary>面板视觉尺寸变化时通知 VideoPresenter 更新输出目标</summary>
        private void SwapPanel_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_pipelineReady || _presenter == null) return;

            double dpiScale = GetDpiScale();
            int pixelW = Math.Max(1, (int)(e.NewSize.Width  * dpiScale));
            int pixelH = Math.Max(1, (int)(e.NewSize.Height * dpiScale));

            _presenter.NotifyPanelSizeChanged(pixelW, pixelH);
            DiagLog.Write($"[UI] SwapPanel SizeChanged → {pixelW}x{pixelH}");
        }

        /// <summary>
        /// 主网格尺寸变化时：若处于旋转状态，同步更新 SwapChainPanel 的显式尺寸
        /// 使面板在窗口中居中且旋转后视觉填满窗口。
        /// </summary>
        private void MainGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_rotationDegrees != 270) return;

            // 面板宽度 = 窗口高度，面板高度 = 窗口宽度（交换以适配旋转）
            SwapPanel.Width = e.NewSize.Height;
            SwapPanel.Height = e.NewSize.Width;
        }

        /// <summary>
        /// 窗口尺寸变化时不做比例修正，由 VideoProcessor 信箱缩放铺满任意形状窗口。
        /// </summary>
        private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
        {
            // 不再锁定窗口比例——用户可自由缩放窗口，画面自动信箱/邮筒铺满
        }

        // ──────────────────────────────────────────────────────────────────
        // 管线停止 / 释放
        // ──────────────────────────────────────────────────────────────────

        /// <summary>停止并释放视频管线，重置所有状态</summary>
        private void StopVideoPipeline()
        {
            SwapPanel.SizeChanged -= SwapPanel_SizeChanged;

            if (_appWindow != null)
                _appWindow.Changed -= AppWindow_Changed;

            // 重置旋转状态
            SwapPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            SwapPanel.VerticalAlignment = VerticalAlignment.Stretch;
            SwapPanel.Width = double.NaN;
            SwapPanel.Height = double.NaN;
            SwapPanel.RenderTransform = null;
            _rotationDegrees = 0;

            // 清除投屏态窗口位置记忆
            _castingPos0 = null;
            _castingSize0 = null;
            _castingPos270 = null;
            _castingSize270 = null;

            _presenter?.Dispose();
            _presenter = null;

            _pipelineReady    = false;
            _pipelineStarting = false;
            _gotKeyframe      = false;
            _firstFrameLogged = false;
            _pendingFirstFrame = null;

        }

        // ──────────────────────────────────────────────────────────────────
        // 工具方法
        // ──────────────────────────────────────────────────────────────────

        /// <summary>计算窗口目标尺寸（最大不超过屏幕 85%，保持视频比例）</summary>
        private void CalculateWindowSizeForVideo(int videoWidth, int videoHeight,
            out int winW, out int winH)
        {
            winW = videoWidth;
            winH = videoHeight;
            if (_appWindow == null || videoWidth <= 0 || videoHeight <= 0) return;

            var displayArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest);
            if (displayArea == null) return;

            double maxW = displayArea.WorkArea.Width  * 0.85;
            double maxH = displayArea.WorkArea.Height * 0.85;
            double aspect = (double)videoWidth / videoHeight;

            if (maxW / maxH > aspect)
            {
                winH = (int)maxH;
                winW = (int)(maxH * aspect);
            }
            else
            {
                winW = (int)maxW;
                winH = (int)(maxW / aspect);
            }

            if (winW < 320) winW = 320;
            if (winH < 240) winH = 240;
        }

        /// <summary>获取当前窗口的 DPI 缩放比例（物理像素 / 逻辑像素）</summary>
        private double GetDpiScale()
        {
            try
            {
                // XamlRoot.RasterizationScale 给出 DPI 缩放（1.0 = 96 DPI）
                if (Content?.XamlRoot != null)
                    return Content.XamlRoot.RasterizationScale;
            }
            catch { }
            return 1.0; // 回落到 1x（96 DPI）
        }

        /// <summary>
        /// 恢复上次保存的窗口位置和大小。
        /// 首次启动（设置为 null）时将窗口居中到主屏幕。
        /// 恢复时会校验位置是否在可见屏幕范围内，防止窗口在不可见区域。
        /// </summary>
        private void RestoreWindowPosition()
        {
            if (_appWindow == null) return;

            var displayArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest);
            if (displayArea == null) return;

            var workArea = displayArea.WorkArea;

            if (_settings.WindowWidth.HasValue && _settings.WindowHeight.HasValue
                && _settings.WindowWidth.Value >= 320 && _settings.WindowHeight.Value >= 240)
            {
                // 恢复保存的窗口大小
                _appWindow.Resize(new SizeInt32(_settings.WindowWidth.Value, _settings.WindowHeight.Value));
            }

            if (_settings.WindowX.HasValue && _settings.WindowY.HasValue)
            {
                int x = _settings.WindowX.Value;
                int y = _settings.WindowY.Value;
                int w = _appWindow.Size.Width;
                int h = _appWindow.Size.Height;

                // 校验窗口是否在任意可见屏幕范围内（至少 100px 可见）
                bool visible = (x + w > workArea.X + 100) && (x < workArea.X + workArea.Width - 100)
                            && (y > workArea.Y - 10) && (y < workArea.Y + workArea.Height - 100);

                if (visible)
                {
                    _appWindow.Move(new PointInt32(x, y));
                }
                else
                {
                    // 保存的位置不可见（如副屏已断开），回退到居中
                    CenterWindow(workArea);
                }
            }
            else
            {
                // 首次启动：居中显示
                CenterWindow(workArea);
            }
        }

        /// <summary>将窗口居中到指定工作区域。</summary>
        private void CenterWindow(RectInt32 workArea)
        {
            if (_appWindow == null) return;
            int x = workArea.X + (workArea.Width - _appWindow.Size.Width) / 2;
            int y = workArea.Y + (workArea.Height - _appWindow.Size.Height) / 2;
            _appWindow.Move(new PointInt32(x, y));
        }

        /// <summary>将窗口居中到当前所在屏幕。</summary>
        private void CenterWindowOnScreen()
        {
            if (_appWindow == null) return;
            var displayArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest);
            if (displayArea == null) return;
            CenterWindow(displayArea.WorkArea);
        }

        /// <summary>
        /// 保存当前窗口位置和大小到持久化设置（待机态窗口）。
        /// 仅在非全屏、非投屏时保存，投屏态窗口大小不应覆盖用户的待机窗口。
        /// </summary>
        private void SaveWindowPosition()
        {
            if (_appWindow == null || _isFullScreen || _isMirroringActive) return;

            _settings.WindowX = _appWindow.Position.X;
            _settings.WindowY = _appWindow.Position.Y;
            _settings.WindowWidth = _appWindow.Size.Width;
            _settings.WindowHeight = _appWindow.Size.Height;
            _settings.Save();
        }

        // ──────────────────────────────────────────────────────────────────
        // 全屏
        // ──────────────────────────────────────────────────────────────────

        /// <summary>全屏切换按钮</summary>
        private void FullScreenButton_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

        // ──────────────────────────────────────────────────────────────────
        // 可自定义快捷键
        // ──────────────────────────────────────────────────────────────────

        /// <summary>一个可自定义快捷键动作的元数据。</summary>
        private sealed class ShortcutDef
        {
            public string Id = "";       // 动作 id（持久化键）
            public string Name = "";     // 中文显示名
            public string Default = "";  // 默认组合键字符串
        }

        /// <summary>所有可自定义动作（退出全屏固定为 Esc，不在此列）。</summary>
        private static readonly ShortcutDef[] ShortcutDefs = new[]
        {
            new ShortcutDef { Id = "fullscreen", Name = "切换全屏",  Default = "F11" },
            new ShortcutDef { Id = "rotate",     Name = "旋转画面",  Default = "R" },
            new ShortcutDef { Id = "hud",        Name = "切换 HUD",  Default = "H" },
            new ShortcutDef { Id = "ontop",      Name = "窗口置顶",  Default = "T" },
            new ShortcutDef { Id = "stop",       Name = "退出投屏",  Default = "Q" },
            new ShortcutDef { Id = "screenshot", Name = "屏幕截图",  Default = "S" },
            new ShortcutDef { Id = "fill",       Name = "铺满屏幕",  Default = "F" },
        };

        /// <summary>取某动作当前生效的快捷键：优先用户自定义，缺省回退内置默认。</summary>
        private string GetEffectiveShortcut(string id)
        {
            if (_settings.Shortcuts != null &&
                _settings.Shortcuts.TryGetValue(id, out var v) && v != null)
                return v;
            foreach (var d in ShortcutDefs)
                if (d.Id == id) return d.Default;
            return "";
        }

        /// <summary>判断某虚拟键是否为修饰键（Ctrl/Shift/Alt/Win）。</summary>
        private static bool IsModifierKey(Windows.System.VirtualKey k) =>
            k == Windows.System.VirtualKey.Control || k == Windows.System.VirtualKey.LeftControl || k == Windows.System.VirtualKey.RightControl ||
            k == Windows.System.VirtualKey.Shift   || k == Windows.System.VirtualKey.LeftShift   || k == Windows.System.VirtualKey.RightShift   ||
            k == Windows.System.VirtualKey.Menu    || k == Windows.System.VirtualKey.LeftMenu     || k == Windows.System.VirtualKey.RightMenu     ||
            k == Windows.System.VirtualKey.LeftWindows || k == Windows.System.VirtualKey.RightWindows;

        /// <summary>查询某键当前是否被按下（WinUI3 桌面读取修饰键状态的标准方式）。</summary>
        private static bool IsKeyDown(Windows.System.VirtualKey k) =>
            (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(k)
                & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

        /// <summary>把当前修饰键状态 + 主键格式化为统一的组合键字符串（如 "Ctrl+Shift+S"）。</summary>
        private static string FormatCombo(Windows.System.VirtualKey key)
        {
            var sb = new System.Text.StringBuilder();
            if (IsKeyDown(Windows.System.VirtualKey.Control)) sb.Append("Ctrl+");
            if (IsKeyDown(Windows.System.VirtualKey.Menu))    sb.Append("Alt+");
            if (IsKeyDown(Windows.System.VirtualKey.Shift))   sb.Append("Shift+");
            sb.Append(key.ToString()); // 如 F、F11、S；与默认值书写一致
            return sb.ToString();
        }

        /// <summary>从按键事件得到组合键字符串；若仅按下修饰键则返回 null。</summary>
        private static string? ComboFromEvent(KeyRoutedEventArgs e)
        {
            if (IsModifierKey(e.Key)) return null;
            return FormatCombo(e.Key);
        }

        /// <summary>键盘快捷键：Esc 退出全屏（固定），其余动作按「更多设置」中的自定义配置匹配触发。</summary>
        private void MainWindow_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            // 退出全屏固定为 Esc，最先处理
            if (e.Key == Windows.System.VirtualKey.Escape && _isFullScreen)
            {
                ToggleFullScreen();
                e.Handled = true;
                return;
            }

            string? combo = ComboFromEvent(e);
            if (combo == null) return; // 仅按下修饰键

            // 纯输入键（不含 Ctrl/Alt）时，若焦点在文本框则不触发，避免打断输入
            bool hasCtrlAlt = combo.StartsWith("Ctrl+") || combo.Contains("Alt+");
            if (!hasCtrlAlt && IsTextInputFocused()) return;

            foreach (var def in ShortcutDefs)
            {
                if (string.Equals(GetEffectiveShortcut(def.Id), combo, StringComparison.OrdinalIgnoreCase))
                {
                    if (RunShortcut(def.Id))
                        e.Handled = true;
                    break;
                }
            }
        }

        /// <summary>执行某快捷键动作（含状态前提判断）。返回是否实际触发。</summary>
        private bool RunShortcut(string id)
        {
            switch (id)
            {
                case "fullscreen":
                    ToggleFullScreen();
                    return true;
                case "rotate":
                    RotateVideo();
                    return true;
                case "hud":
                    SetHudVisible(HudPanel.Visibility != Visibility.Visible);
                    return true;
                case "ontop":
                    ToggleAlwaysOnTop();
                    return true;
                case "stop":
                    if (_isMirroringActive) { _receiver?.StopActiveMirroring(); return true; }
                    return false;
                case "screenshot":
                    TakeScreenshot();
                    return true;
                case "fill":
                    if (_isMirroringActive && _isFullScreen)
                    {
                        _settings.FillScreen = !_settings.FillScreen;
                        _settings.Save();
                        _presenter?.SetFillMode(_settings.FillScreen);
                        ShowToast(_settings.FillScreen ? "铺满屏幕" : "显示完整");
                        return true;
                    }
                    return false;
            }
            return false;
        }

        /// <summary>当前焦点是否在文本输入控件上（避免单字母快捷键打断输入）。</summary>
        private bool IsTextInputFocused()
        {
            try
            {
                if (Content?.XamlRoot == null) return false;
                var fe = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Content.XamlRoot);
                return fe is TextBox;
            }
            catch { return false; }
        }

        /// <summary>旋转按钮</summary>
        private void RotateButton_Click(object sender, RoutedEventArgs e) => RotateVideo();

        /// <summary>铺满屏幕菜单项：切换铺满/信箱缩放模式并持久化。</summary>
        private void FillScreenMenuItem_Click(object sender, RoutedEventArgs e)
        {
            _settings.FillScreen = !_settings.FillScreen;
            _settings.Save();
            _presenter?.SetFillMode(_settings.FillScreen);
            ShowToast(_settings.FillScreen ? "铺满屏幕" : "显示完整");
        }

        /// <summary>
        /// 旋转画面：在 0° 和 270°（逆时针 90°）之间切换。
        /// 通过 RotateTransform 实现视觉旋转，并调整窗口尺寸适应旋转后的画面。
        /// </summary>
        private void RotateVideo()
        {
            if (!_pipelineReady || _videoAspectRatio <= 0) return;

            // 切换前保存当前旋转状态的窗口位置/大小
            SaveCastingWindowState();

            _rotationDegrees = (_rotationDegrees == 0) ? 270 : 0;
            ApplyRotation();
            DiagLog.Write($"[UI] 画面旋转 → {_rotationDegrees}°");
        }

        /// <summary>保存当前旋转状态下的投屏态窗口位置和大小。</summary>
        private void SaveCastingWindowState()
        {
            if (_appWindow == null || _isFullScreen) return;

            if (_rotationDegrees == 0)
            {
                _castingPos0 = _appWindow.Position;
                _castingSize0 = _appWindow.Size;
            }
            else
            {
                _castingPos270 = _appWindow.Position;
                _castingSize270 = _appWindow.Size;
            }
        }

        /// <summary>
        /// 应用当前旋转状态：设置 SwapChainPanel 的视觉旋转变换，
        /// 并调整窗口大小以适应旋转后的画面比例。
        /// </summary>
        private void ApplyRotation()
        {
            if (_appWindow == null) return;

            if (_rotationDegrees == 270)
            {
                // 逆时针 90°：居中 + 视觉旋转 270°
                SwapPanel.HorizontalAlignment = HorizontalAlignment.Center;
                SwapPanel.VerticalAlignment = VerticalAlignment.Center;
                SwapPanel.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
                SwapPanel.RenderTransform = new RotateTransform { Angle = 270 };

                if (_isFullScreen)
                {
                    // 全屏下不能改窗口大小，直接按可视区域交换面板宽高
                    SwapPanel.Width = MainGrid.ActualHeight;
                    SwapPanel.Height = MainGrid.ActualWidth;
                }
                else
                {
                    // 窗口模式：有记忆则恢复，否则计算默认值并居中
                    if (_castingSize270.HasValue)
                    {
                        _appWindow.Resize(_castingSize270.Value);
                        if (_castingPos270.HasValue)
                            _appWindow.Move(_castingPos270.Value);
                    }
                    else
                    {
                        CalculateWindowSizeForVideo(_videoHeight, _videoWidth, out int winW, out int winH);
                        _appWindow.Resize(new SizeInt32(winW, winH));
                        CenterWindowOnScreen();
                    }
                    // 面板尺寸由 MainGrid_SizeChanged 跟随设置
                }
            }
            else
            {
                // 恢复原始状态
                SwapPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
                SwapPanel.VerticalAlignment = VerticalAlignment.Stretch;
                SwapPanel.Width = double.NaN;
                SwapPanel.Height = double.NaN;
                SwapPanel.RenderTransform = null;

                // 窗口模式下：有记忆则恢复，否则计算默认值并居中
                if (!_isFullScreen)
                {
                    if (_castingSize0.HasValue)
                    {
                        _appWindow.Resize(_castingSize0.Value);
                        if (_castingPos0.HasValue)
                            _appWindow.Move(_castingPos0.Value);
                    }
                    else
                    {
                        CalculateWindowSizeForVideo(_videoWidth, _videoHeight, out int winW, out int winH);
                        _appWindow.Resize(new SizeInt32(winW, winH));
                        CenterWindowOnScreen();
                    }
                }
            }
        }

        /// <summary>切换全屏 / 窗口模式</summary>
        private void ToggleFullScreen()
        {
            if (_appWindow == null) return;

            if (_isFullScreen)
            {
                _appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                _isFullScreen = false;

                // 退出全屏后：窗口会自动恢复到进入全屏前的尺寸和位置（系统行为），
                // 不再强制 Resize，保留用户调整的窗口大小。
                // 恢复置顶设置（全屏切换可能重置 Presenter 属性）
                if (_appWindow.Presenter is OverlappedPresenter op)
                    op.IsAlwaysOnTop = _settings.AlwaysOnTop;
            }
            else
            {
                _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                _isFullScreen = true;

                // 进入全屏时若处于旋转状态，重新按全屏可视区域设置面板尺寸
                if (_pipelineReady && _rotationDegrees == 270)
                {
                    SwapPanel.Width = MainGrid.ActualHeight;
                    SwapPanel.Height = MainGrid.ActualWidth;
                }
            }
        }

        /// <summary>安全显示 ContentDialog：同一时刻仅允许一个对话框，若已有对话框在显示则放弃本次（避免 COMException 0x80000019 闪退）。</summary>
        private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
        {
            if (_isDialogShowing)
            {
                // 已有对话框打开：直接关闭新对话框并返回 None，绝不并发 Show
                try { dialog.Hide(); } catch { }
                return ContentDialogResult.None;
            }
            _isDialogShowing = true;
            try
            {
                return await dialog.ShowAsync();
            }
            finally
            {
                _isDialogShowing = false;
            }
        }

        /// <summary>菜单「检查更新…」点击事件</summary>
        private async void CheckUpdateMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await RunUpdateCheckAsync(manual: true);
        }

        /// <summary>运行更新检查并展示 UI</summary>
        private async Task RunUpdateCheckAsync(bool manual)
        {
            ContentDialog? loadingDlg = null;
            Task<ContentDialogResult>? loadingTask = null;
            if (manual)
            {
                var pr = new ProgressRing { IsActive = true, HorizontalAlignment = HorizontalAlignment.Center };
                loadingDlg = new ContentDialog
                {
                    Title = "正在检查更新",
                    Content = new StackPanel
                    {
                        Spacing = 15,
                        Children = { new TextBlock { Text = "正在连接 GitHub 获取最新版本信息..." }, pr }
                    },
                    XamlRoot = Content.XamlRoot,
                    RequestedTheme = CurrentElementTheme()
                };
                // loading 用普通 ShowAsync（不进互斥包装），它独占显示并在检查完成后由 Hide() 关闭；
                // loadingTask 用于等待其真正关闭后再弹结果对话框，避免两个 ContentDialog 并发闪退或提示被吞
                loadingTask = loadingDlg.ShowAsync().AsTask();
            }

            UpdateInfo? updateInfo = null;
            bool success = false;
            UpdateCheckFailureReason failureReason = UpdateCheckFailureReason.Unknown;
            try
            {
                updateInfo = await UpdateChecker.CheckForUpdateAsync(
                    UpdateChecker.RepoOwner, UpdateChecker.RepoName);
                success = true;
            }
            catch (UpdateCheckException ex)
            {
                DiagLog.Write($"[UI] 检查更新失败({ex.Reason}): {ex.Message}");
                failureReason = ex.Reason;
            }
            catch (Exception ex)
            {
                DiagLog.Write($"[UI] 检查更新异常: {ex.Message}");
                failureReason = UpdateCheckFailureReason.Unknown;
            }
            finally
            {
                if (loadingDlg != null)
                {
                    loadingDlg.Hide(); // 请求关闭 loading
                    if (loadingTask != null)
                    {
                        try { await loadingTask; } catch { } // 等待 loading 真正关闭，之后才能安全弹下一个对话框
                    }
                }
            }

            if (!success)
            {
                if (manual)
                {
                    // 按失败原因给出针对性提示，而非笼统的"检查失败"
                    string failText = failureReason switch
                    {
                        UpdateCheckFailureReason.NoRelease => "尚未发布任何版本。请先在 GitHub 仓库为标签创建 Release 后再检查更新。",
                        UpdateCheckFailureReason.RateLimited => "GitHub 请求过于频繁已被限速，请稍后重试。",
                        UpdateCheckFailureReason.Network => "无法连接 GitHub，请检查网络连接后重试。",
                        _ => "检查更新失败，请稍后重试。"
                    };
                    var failDlg = new ContentDialog
                    {
                        Title = "检查更新",
                        Content = new TextBlock { Text = failText, TextWrapping = TextWrapping.Wrap },
                        CloseButtonText = "确定",
                        XamlRoot = Content.XamlRoot,
                        RequestedTheme = CurrentElementTheme()
                    };
                    await ShowDialogAsync(failDlg);
                }
                return;
            }

            if (updateInfo == null)
            {
                if (manual)
                {
                    var latestDlg = new ContentDialog
                    {
                        Title = "检查更新",
                        Content = new TextBlock { Text = $"当前已是最新版本 (v{UpdateChecker.CurrentVersionDisplay})。" },
                        CloseButtonText = "确定",
                        XamlRoot = Content.XamlRoot,
                        RequestedTheme = CurrentElementTheme()
                    };
                    await ShowDialogAsync(latestDlg);
                }
                return;
            }

            // 如果是自动检查，并且该版本已被跳过，则不提示
            if (!manual && _settings.SkippedVersion == updateInfo.VersionString)
            {
                DiagLog.Write($"[UI] 自动检查发现新版本 {updateInfo.VersionString}，但用户已选择跳过该版本");
                return;
            }

            // 弹出发现更新对话框
            var contentPanel = new StackPanel { Spacing = 10, Width = 320 };

            var notesHeader = new TextBlock { Text = "更新日志:", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            var notesScroll = new ScrollViewer
            {
                Height = 150,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock
                {
                    Text = updateInfo.ReleaseNotes,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12
                }
            };

            var progressPanel = new StackPanel { Spacing = 6, Visibility = Visibility.Collapsed };
            var progressBar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
            var progressText = new TextBlock { Text = "正在准备下载...", FontSize = 11, Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray) };
            progressPanel.Children.Add(progressText);
            progressPanel.Children.Add(progressBar);

            contentPanel.Children.Add(notesHeader);
            contentPanel.Children.Add(notesScroll);
            contentPanel.Children.Add(progressPanel);

            var updateDlg = new ContentDialog
            {
                Title = $"发现新版本: {updateInfo.VersionString}",
                Content = contentPanel,
                PrimaryButtonText = "立即更新",
                SecondaryButtonText = "跳过此版本",
                CloseButtonText = "稍后提醒",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
                RequestedTheme = CurrentElementTheme()
            };

            CancellationTokenSource? downloadCts = null;

            updateDlg.PrimaryButtonClick += async (sender, args) =>
            {
                args.Cancel = true;
                updateDlg.IsPrimaryButtonEnabled = false;
                updateDlg.IsSecondaryButtonEnabled = false;
                updateDlg.CloseButtonText = "取消";
                progressPanel.Visibility = Visibility.Visible;

                downloadCts = new CancellationTokenSource();
                string tempZip = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"AirPlayer_{updateInfo.VersionString}.zip");

                try
                {
                    var progressHandler = new Progress<double>(value =>
                    {
                        progressBar.Value = value;
                        progressText.Text = $"正在下载... {value:F1}%";
                    });

                    await UpdateChecker.DownloadUpdateAsync(updateInfo.DownloadUrl, tempZip, progressHandler, downloadCts.Token);

                    progressText.Text = "下载完成，正在进行替换覆盖并重启...";
                    await Task.Delay(1000);

                    UpdateChecker.ApplyUpdateAndRestart(tempZip);
                }
                catch (OperationCanceledException)
                {
                    DiagLog.Write("[UI] 用户取消了更新下载");
                    progressPanel.Visibility = Visibility.Collapsed;
                    updateDlg.IsPrimaryButtonEnabled = true;
                    updateDlg.IsSecondaryButtonEnabled = true;
                    updateDlg.CloseButtonText = "稍后提醒";
                    if (System.IO.File.Exists(tempZip))
                    {
                        try { System.IO.File.Delete(tempZip); } catch { }
                    }
                    ShowToast("下载已取消");
                }
                catch (Exception ex)
                {
                    DiagLog.Write($"[UI] 下载更新失败: {ex.Message}");
                    progressText.Text = "下载失败: " + ex.Message;
                    updateDlg.IsPrimaryButtonEnabled = true;
                    updateDlg.IsSecondaryButtonEnabled = true;
                    updateDlg.CloseButtonText = "稍后提醒";
                    if (System.IO.File.Exists(tempZip))
                    {
                        try { System.IO.File.Delete(tempZip); } catch { }
                    }
                    ShowToast("更新下载失败");
                }
            };

            updateDlg.SecondaryButtonClick += (sender, args) =>
            {
                _settings.SkippedVersion = updateInfo.VersionString;
                _settings.Save();
                ShowToast($"已跳过 v{updateInfo.VersionString} 版本");
            };

            updateDlg.CloseButtonClick += (sender, args) =>
            {
                if (downloadCts != null && !downloadCts.IsCancellationRequested)
                {
                    downloadCts.Cancel();
                }
            };

            await ShowDialogAsync(updateDlg);
        }

        // ──────────────────────────────────────────────────────────────────
        // 窗口关闭
        // ──────────────────────────────────────────────────────────────────

        /// <summary>窗口关闭：保存窗口位置并清理所有后台资源</summary>
        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            // 非全屏时保存窗口位置和大小
            SaveWindowPosition();

            _cts?.Cancel();        // 停止 AirPlay 监听
            _receiver?.Dispose();  // 释放接收器
            StopVideoPipeline();   // 释放视频管线
            _audioSink?.Dispose(); // 释放音频播放器
            _tray?.Dispose();      // 移除托盘图标
        }
    }
}
