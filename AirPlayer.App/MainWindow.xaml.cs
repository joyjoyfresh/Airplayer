using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Windowing;
using Microsoft.UI;
using AirPlayer.Protocol;
using AirPlayer.Protocol.Models;
using AirPlayer.Protocol.Utils;
using AirPlayer.App.Rendering;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;

namespace AirPlayer.App
{
    /// <summary>
    /// 主窗口后置代码类：管理 AirPlay 服务、事件监听，以及「解码即呈现」的视频管线。
    /// </summary>
    public partial class MainWindow : Window
    {
        // AirPlay 接收核心实例
        private AirPlayReceiver? _receiver;

        // 接收监听器取消句柄
        private CancellationTokenSource? _cts;

        // 应用窗口抽象，用于控制全屏
        private AppWindow? _appWindow;

        // 全屏状态标志
        private bool _isFullScreen;

        // 镜像连接是否活动中
        private volatile bool _isMirroringActive;

        // ===== 解码渲染管线相关 =====
        private H264Decoder? _decoder;                       // MF H264 解码器
        private CanvasSwapChain? _swapChain;                 // Win2D 交换链（呈现目标）
        private Thread? _renderThread;                       // 渲染线程
        private volatile bool _renderRunning;                // 渲染线程运行标志
        private readonly object _queueLock = new object();   // 帧队列锁
        private readonly Queue<H264Data> _frameQueue = new();// 待解码帧队列
        private readonly SemaphoreSlim _frameSignal = new SemaphoreSlim(0); // 新帧唤醒信号
        private bool _pipelineStarting;                      // 管线是否正在创建
        private volatile bool _pipelineReady;                // 管线是否就绪
        private bool _gotKeyframe;                           // 是否已收到首个关键帧
        private bool _firstFrameLogged;                      // 诊断：是否已记录首帧

        /// <summary>初始化主窗口并启动 AirPlay 接收服务</summary>
        public MainWindow()
        {
            this.InitializeComponent();

            // 获取窗口原生句柄并绑定 AppWindow（用于全屏控制）
            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            this.Title = "AirPlayer Receiver";
            this.Closed += MainWindow_Closed;

            // 默认设备名取本机计算机名
            string hostName = Environment.MachineName;
            DeviceNameText.Text = hostName;

            // 启动等待页脉动动画
            if (MainGrid.Resources.TryGetValue("PulseStoryboard", out object sbObj) && sbObj is Storyboard sb)
            {
                sb.Begin();
            }

            StartReceiver(hostName);
        }

        /// <summary>启动 AirPlay 接收服务</summary>
        /// <param name="deviceName">投屏显示名称</param>
        private void StartReceiver(string deviceName)
        {
            _cts = new CancellationTokenSource();

            // 设备 MAC 与密钥由接收器内部随机生成并持久化
            _receiver = new AirPlayReceiver(deviceName);
            _receiver.OnMirroringStartedReceived += Receiver_OnMirroringStarted;
            _receiver.OnMirroringStoppedReceived += Receiver_OnMirroringStopped;
            _receiver.OnH264DataReceived += Receiver_OnH264DataReceived;

            // 后台线程启动网络服务，避免阻塞 UI
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

        /// <summary>镜像开始：重置状态、等待首帧创建管线</summary>
        private void Receiver_OnMirroringStarted(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _isMirroringActive = true;
                _gotKeyframe = false;
                _firstFrameLogged = false;
                lock (_queueLock) { _frameQueue.Clear(); }
            });
        }

        /// <summary>镜像结束：停止渲染线程并释放资源</summary>
        private void Receiver_OnMirroringStopped(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _isMirroringActive = false;
                StopRenderPipeline();

                // 解除并隐藏呈现面板，恢复引导页
                SwapPanel.SwapChain = null;
                SwapPanel.Visibility = Visibility.Collapsed;
                PromoGrid.Visibility = Visibility.Visible;
            });
        }

        /// <summary>每收到一帧 H264（Annex-B）时入队，交由渲染线程解码呈现</summary>
        private void Receiver_OnH264DataReceived(object? sender, H264Data data)
        {
            if (!_isMirroringActive) return;

            // 诊断：记录首帧
            if (!_firstFrameLogged)
            {
                _firstFrameLogged = true;
                DiagLog.Write($"[UI] 首个 H264 帧到达 {data.Width}x{data.Height} type={data.FrameType}");
            }

            // 丢弃首个关键帧之前的帧，确保解码器从 IDR 起步
            if (!_gotKeyframe)
            {
                if (data.FrameType != 5) return;
                _gotKeyframe = true;
            }

            lock (_queueLock)
            {
                _frameQueue.Enqueue(data);

                // 首帧触发唯一一次管线创建（在 UI 线程）
                if (!_pipelineStarting && !_pipelineReady)
                {
                    _pipelineStarting = true;
                    int w = data.Width, h = data.Height;
                    DispatcherQueue.TryEnqueue(() => CreateVideoPipeline(w, h));
                }
            }
            _frameSignal.Release();
        }

        /// <summary>在 UI 线程创建 Win2D 交换链、解码器并启动渲染线程</summary>
        private void CreateVideoPipeline(int width, int height)
        {
            if (!_isMirroringActive) return;

            try
            {
                // 创建 Win2D 交换链并挂到面板（dpi=96 时尺寸即像素）
                var device = CanvasDevice.GetSharedDevice();
                _swapChain = new CanvasSwapChain(device, width <= 0 ? 1920 : width, height <= 0 ? 1080 : height, 96f);
                SwapPanel.SwapChain = _swapChain;
                SwapPanel.Visibility = Visibility.Visible;
                PromoGrid.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                DiagLog.Write($"[SWAP] 交换链创建失败: {ex.Message}");
                return;
            }

            // 创建解码器
            _decoder = new H264Decoder();
            _decoder.SetFrameSize(width, height);

            // 启动渲染线程（Media Foundation 要求 MTA 套间）
            _renderRunning = true;
            _renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "VideoRender" };
            _renderThread.SetApartmentState(ApartmentState.MTA);
            _renderThread.Start();

            _pipelineReady = true;
            DiagLog.Write("[PIPE] 解码渲染管线就绪");
        }

        /// <summary>渲染线程：从队列取帧 → 解码 → 立即呈现（无时钟调度）</summary>
        private void RenderLoop()
        {
            while (_renderRunning)
            {
                _frameSignal.Wait(200);
                if (!_renderRunning) break;

                // 一次性排空队列中的所有帧，按序解码呈现（H264Data 是结构体，用 bool 判空）
                while (_renderRunning)
                {
                    H264Data frame;
                    lock (_queueLock)
                    {
                        if (_frameQueue.Count == 0) break;
                        frame = _frameQueue.Dequeue();
                    }

                    try
                    {
                        if (_decoder != null &&
                            _decoder.TryDecode(frame.Data, out byte[] bgra, out int w, out int h) &&
                            _swapChain != null)
                        {
                            PresentFrame(bgra, w, h);
                        }
                    }
                    catch (Exception ex)
                    {
                        DiagLog.Write($"[RENDER] 解码/呈现异常: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>把一帧 BGRA 画到交换链并 Present</summary>
        private void PresentFrame(byte[] bgra, int width, int height)
        {
            var sc = _swapChain;
            if (sc == null) return;

            using (var ds = sc.CreateDrawingSession(Microsoft.UI.Colors.Black))
            using (var bmp = CanvasBitmap.CreateFromBytes(sc.Device, bgra, width, height, DirectXPixelFormat.B8G8R8A8UIntNormalized))
            {
                // 按交换链尺寸缩放绘制（自适应窗口）
                ds.DrawImage(bmp, new Windows.Foundation.Rect(0, 0, sc.Size.Width, sc.Size.Height));
            }
            sc.Present();
        }

        /// <summary>停止渲染线程并释放解码器与交换链</summary>
        private void StopRenderPipeline()
        {
            _renderRunning = false;
            _frameSignal.Release();
            try { _renderThread?.Join(500); } catch { }
            _renderThread = null;

            lock (_queueLock) { _frameQueue.Clear(); }

            _decoder?.Dispose();
            _decoder = null;
            _swapChain?.Dispose();
            _swapChain = null;

            _pipelineStarting = false;
            _pipelineReady = false;
            _gotKeyframe = false;
            _firstFrameLogged = false;
        }

        /// <summary>全屏切换按钮</summary>
        private void FullScreenButton_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

        /// <summary>切换全屏/窗口模式</summary>
        private void ToggleFullScreen()
        {
            if (_appWindow == null) return;

            if (_isFullScreen)
            {
                _appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                _isFullScreen = false;
                FullScreenButton.Content = "";
            }
            else
            {
                _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                _isFullScreen = true;
                FullScreenButton.Content = "";
            }
        }

        /// <summary>窗口关闭：清理所有后台资源</summary>
        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            _cts?.Cancel();
            _receiver?.Dispose();
            StopRenderPipeline();
        }
    }
}
