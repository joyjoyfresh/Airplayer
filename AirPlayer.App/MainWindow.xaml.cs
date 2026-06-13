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


        // ===== v0.3 体验：设置 / HUD / 控制条自动隐藏 =====
        private readonly AppSettings _settings = AppSettings.Load();   // 持久化设置
        private DispatcherTimer? _hudTimer;            // HUD 刷新定时器（1s）
        private DispatcherTimer? _controlHideTimer;    // 控制条自动隐藏定时器（3s）
        private DispatcherTimer? _toastTimer;          // 瞬时提示自动消失定时器
        private int _lastPresentedForFps;              // 上次呈现帧计数（算 FPS）
        private bool _menuOpen;                         // 菜单是否打开（打开时不自动隐藏按钮）

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

            this.Title = "AirPlayer Receiver";
            this.Closed += MainWindow_Closed;

            // 注册全局键盘快捷键（F11/Escape/R/H）
            this.Content.KeyDown += MainWindow_KeyDown;

            // 监听主网格尺寸变化，用于旋转时同步面板尺寸
            MainGrid.SizeChanged += MainGrid_SizeChanged;

            // 设备名：优先用设置里的自定义名，否则用计算机名
            string hostName = string.IsNullOrWhiteSpace(_settings.DeviceName)
                ? Environment.MachineName : _settings.DeviceName!;
            DeviceNameText.Text = hostName;

            // 待机页显示本机局域网 IP
            string ip = GetLocalIPv4();
            IpText.Text = string.IsNullOrEmpty(ip) ? "" : $"本机 IP：{ip}";

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
            
            HudPanel.Visibility = _settings.ShowHud ? Visibility.Visible : Visibility.Collapsed;

            // 应用 HUD 自定义尺寸、颜色和背景透明度参数
            HudText.FontSize = _settings.HudFontSize;
            HudText.Foreground = new SolidColorBrush(GetColorFromHex(_settings.HudTextColor));
            HudPanel.Background = new SolidColorBrush(Microsoft.UI.Colors.Black) { Opacity = _settings.HudBgOpacity };
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
            var stackPanel = new StackPanel { Spacing = 16, Width = 300 };

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
                Margin = new Thickness(0, 4, 0, 0)
            };

            // 3. 截图保存路径配置
            var screenshotHeader = new TextBlock 
            { 
                Text = "截图保存目录", 
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, 
                Margin = new Thickness(0, 0, 0, 4) 
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

            stackPanel.Children.Add(nameContainer);     // 设备名称
            stackPanel.Children.Add(hudSectionHeader);  // HUD 区块标题
            stackPanel.Children.Add(hudContainer);      // HUD 参数控件
            stackPanel.Children.Add(screenshotContainer); // 截图保存路径
            stackPanel.Children.Add(audioContainer);    // 音频设备

            var scrollViewer = new Microsoft.UI.Xaml.Controls.ScrollViewer
            {
                Content = stackPanel,
                VerticalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Disabled,
                MaxHeight = 450
            };

            var dlg = new ContentDialog
            {
                Title = "更多设置",
                Content = scrollViewer,
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            };

            var result = await dlg.ShowAsync();
            if (result == ContentDialogResult.Primary)
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
                if (_settings.DeviceName != oldName)
                {
                    ShowToast("设置已保存，设备名重启生效");
                }
                else
                {
                    ShowToast("设置已保存");
                }
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
            ScreenshotMenuItem.Visibility = visibility;
            RotateMenuItem.Visibility = visibility;
            FullScreenMenuItem.Visibility = visibility;
            ExitMirroringMenuItem.Visibility = visibility;
            ActiveCastingSeparator.Visibility = visibility;

            // 铺满屏幕仅在「投屏中 + 全屏」时才有意义，独立分栏显示
            var fillVisibility = (_isMirroringActive && _isFullScreen) ? Visibility.Visible : Visibility.Collapsed;
            FillScreenSeparatorTop.Visibility    = fillVisibility;
            FillScreenMenuItem.Visibility        = fillVisibility;
            FillScreenMenuItem.IsChecked         = _settings.FillScreen;
            FillScreenSeparatorBottom.Visibility = fillVisibility;
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
            HudText.Text = $"{s.Width}x{s.Height}   {fps} fps\n解码 {s.Decoded}  丢帧 {s.Skipped}";
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

            _receiver = new AirPlayReceiver(deviceName);
            _receiver.OnMirroringStartedReceived += Receiver_OnMirroringStarted;
            _receiver.OnMirroringStoppedReceived += Receiver_OnMirroringStopped;
            _receiver.OnH264DataReceived         += Receiver_OnH264DataReceived;
            _receiver.OnPcmDataReceived          += Receiver_OnPcmDataReceived;
            _receiver.OnAudioFlushReceived       += Receiver_OnAudioFlushReceived;

            Task.Run(async () =>
            {
                try
                {
                    await _receiver.StartListeners(_cts.Token);
                    await _receiver.StartMdnsAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"AirPlay 接收器启动异常: {ex}");
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
                }
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
                _presenter.Initialize(SwapPanel, videoWidth, videoHeight, panelPixelW, panelPixelH);

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

        /// <summary>键盘快捷键：F11 切换全屏，Escape 退出全屏，R 旋转画面，H 切换 HUD，S 截图，T 窗口置顶，Q 退出投屏</summary>
        private void MainWindow_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.F11)
            {
                ToggleFullScreen();
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Escape && _isFullScreen)
            {
                ToggleFullScreen();
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.R && !IsTextInputFocused())
            {
                RotateVideo();
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.H && !IsTextInputFocused())
            {
                SetHudVisible(HudPanel.Visibility != Visibility.Visible);
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.T && !IsTextInputFocused())
            {
                ToggleAlwaysOnTop();
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Q && _isMirroringActive && !IsTextInputFocused())
            {
                _receiver?.StopActiveMirroring();
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.S && !IsTextInputFocused())
            {
                TakeScreenshot();
                e.Handled = true;
            }

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

        }
    }
}
