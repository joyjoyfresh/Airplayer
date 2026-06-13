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

        // ===== 音频播放管线 =====
        private AudioSink? _audioSink;                // 音频播放器

        // ===== v0.3 体验：设置 / HUD / 控制条自动隐藏 =====
        private readonly AppSettings _settings = AppSettings.Load();   // 持久化设置
        private DispatcherTimer? _hudTimer;            // HUD 刷新定时器（1s）
        private DispatcherTimer? _controlHideTimer;    // 控制条自动隐藏定时器（3s）
        private DispatcherTimer? _toastTimer;          // 瞬时提示自动消失定时器
        private int _lastPresentedForFps;              // 上次呈现帧计数（算 FPS）
        private bool _suppressSettingsEvents;          // 初始化时抑制开关回调

        /// <summary>初始化主窗口并启动 AirPlay 接收服务</summary>
        public MainWindow()
        {
            this.InitializeComponent();

            // 获取原生窗口句柄并绑定 AppWindow
            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

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

        /// <summary>应用持久化设置到窗口与开关控件。</summary>
        private void ApplySettings()
        {
            _suppressSettingsEvents = true;
            try
            {
                if (_appWindow?.Presenter is OverlappedPresenter op)
                    op.IsAlwaysOnTop = _settings.AlwaysOnTop;
                AlwaysOnTopToggle.IsOn = _settings.AlwaysOnTop;
                HudToggle.IsOn = _settings.ShowHud;
                HudPanel.Visibility = _settings.ShowHud ? Visibility.Visible : Visibility.Collapsed;
                DeviceNameBox.Text = _settings.DeviceName ?? "";
            }
            finally { _suppressSettingsEvents = false; }
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
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "AirPlayer");
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                _presenter.RequestScreenshot(path);
                ShowToast("已保存截图到 图片\\AirPlayer");
            }
            catch (Exception ex)
            {
                ShowToast("截图失败");
                DiagLog.Write($"[UI] 截图请求失败: {ex.Message}");
            }
        }

        /// <summary>自定义设备名失焦时持久化（重启生效）。</summary>
        private void DeviceNameBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_suppressSettingsEvents) return;
            var name = DeviceNameBox.Text?.Trim();
            _settings.DeviceName = string.IsNullOrEmpty(name) ? null : name;
            _settings.Save();
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

        /// <summary>窗口置顶开关。</summary>
        private void AlwaysOnTopToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressSettingsEvents) return;
            _settings.AlwaysOnTop = AlwaysOnTopToggle.IsOn;
            if (_appWindow?.Presenter is OverlappedPresenter op)
                op.IsAlwaysOnTop = _settings.AlwaysOnTop;
            _settings.Save();
        }

        /// <summary>HUD 显示开关（设置浮窗内）。</summary>
        private void HudToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressSettingsEvents) return;
            SetHudVisible(HudToggle.IsOn);
        }

        /// <summary>HUD 按钮：切换显示。</summary>
        private void HudButton_Click(object sender, RoutedEventArgs e)
            => SetHudVisible(HudPanel.Visibility != Visibility.Visible);

        /// <summary>统一设置 HUD 可见性并持久化。</summary>
        private void SetHudVisible(bool on)
        {
            HudPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            _settings.ShowHud = on;
            if (HudToggle.IsOn != on)
            {
                _suppressSettingsEvents = true;
                HudToggle.IsOn = on;
                _suppressSettingsEvents = false;
            }
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

        /// <summary>鼠标移动：显示控制条并重置自动隐藏计时（仅投屏时会隐藏）。</summary>
        private void MainGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            ControlBar.Visibility = Visibility.Visible;
            _controlHideTimer?.Stop();
            if (_isMirroringActive) _controlHideTimer?.Start();
        }

        /// <summary>3 秒无操作：投屏中则隐藏控制条。</summary>
        private void ControlHideTimer_Tick(object? sender, object e)
        {
            _controlHideTimer?.Stop();
            if (_isMirroringActive) ControlBar.Visibility = Visibility.Collapsed;
        }

        /// <summary>获取本机第一个非回环 IPv4 地址。</summary>
        private static string GetLocalIPv4()
        {
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
                    _audioSink = new AudioSink();
                    _audioSink.Initialize();
                }
            });
        }

        /// <summary>镜像结束：停止管线，恢复引导页</summary>
        private void Receiver_OnMirroringStopped(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _isMirroringActive = false;
                StopVideoPipeline();

                // 释放音频播放器
                _audioSink?.Dispose();
                _audioSink = null;

                // 投屏结束：恢复控制条常显，重置 HUD 计数
                _controlHideTimer?.Stop();
                ControlBar.Visibility = Visibility.Visible;
                _lastPresentedForFps = 0;

                SwapPanel.Visibility  = Visibility.Collapsed;
                PromoGrid.Visibility  = Visibility.Visible;
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

                // 调整窗口大小并注册比例锁定
                _appWindow?.Resize(new SizeInt32(winW, winH));
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

        // ──────────────────────────────────────────────────────────────────
        // 全屏
        // ──────────────────────────────────────────────────────────────────

        /// <summary>全屏切换按钮</summary>
        private void FullScreenButton_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

        /// <summary>键盘快捷键：F11 切换全屏，Escape 退出全屏，R 旋转画面</summary>
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

        /// <summary>
        /// 旋转画面：在 0° 和 270°（逆时针 90°）之间切换。
        /// 通过 RotateTransform 实现视觉旋转，并调整窗口尺寸适应旋转后的画面。
        /// </summary>
        private void RotateVideo()
        {
            if (!_pipelineReady || _videoAspectRatio <= 0) return;

            _rotationDegrees = (_rotationDegrees == 0) ? 270 : 0;
            ApplyRotation();
            DiagLog.Write($"[UI] 画面旋转 → {_rotationDegrees}°");
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
                // 逆时针 90°：面板尺寸 = 窗口尺寸（交换），居中，视觉旋转 270°
                SwapPanel.HorizontalAlignment = HorizontalAlignment.Center;
                SwapPanel.VerticalAlignment = VerticalAlignment.Center;

                SwapPanel.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
                SwapPanel.RenderTransform = new RotateTransform { Angle = 270 };

                // 窗口按旋转后的比例调整（交换宽高）
                CalculateWindowSizeForVideo(_videoHeight, _videoWidth, out int winW, out int winH);
                _appWindow.Resize(new SizeInt32(winW, winH));

                // 面板尺寸由 MainGrid_SizeChanged 设置（跟随窗口）
            }
            else
            {
                // 恢复原始状态
                SwapPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
                SwapPanel.VerticalAlignment = VerticalAlignment.Stretch;
                SwapPanel.Width = double.NaN;
                SwapPanel.Height = double.NaN;
                SwapPanel.RenderTransform = null;

                // 窗口按原始视频比例调整
                CalculateWindowSizeForVideo(_videoWidth, _videoHeight, out int winW, out int winH);
                _appWindow.Resize(new SizeInt32(winW, winH));
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
                FullScreenButton.Content = "";

                // 退出全屏后恢复窗口尺寸（考虑旋转）
                if (_pipelineReady && _videoAspectRatio > 0)
                {
                    if (_rotationDegrees == 270)
                    {
                        CalculateWindowSizeForVideo(_videoHeight, _videoWidth, out int rw, out int rh);
                        _appWindow.Resize(new SizeInt32(rw, rh));
                    }
                    else
                    {
                        CalculateWindowSizeForVideo(_videoWidth, _videoHeight, out int rw, out int rh);
                        _appWindow.Resize(new SizeInt32(rw, rh));
                    }
                }
            }
            else
            {
                _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                _isFullScreen = true;
                FullScreenButton.Content = "";
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // 窗口关闭
        // ──────────────────────────────────────────────────────────────────

        /// <summary>窗口关闭：清理所有后台资源</summary>
        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            _cts?.Cancel();        // 停止 AirPlay 监听
            _receiver?.Dispose();  // 释放接收器
            StopVideoPipeline();   // 释放视频管线
            _audioSink?.Dispose(); // 释放音频播放器
        }
    }
}
