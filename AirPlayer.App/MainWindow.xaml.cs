using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Core;
using Windows.Media.Playback;
using AirPlayer.Protocol;
using AirPlayer.Protocol.Models;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Windowing;
using Microsoft.UI;

namespace AirPlayer.App
{
    /// <summary>
    /// 主窗口后置代码类：管理 AirPlay 服务启动、事件监听、视频流接管及窗口全屏状态。
    /// </summary>
    public partial class MainWindow : Window
    {
        // AirPlay 接收核心实例
        private AirPlayReceiver? _receiver;
        
        // 接收监听器取消句柄
        private CancellationTokenSource? _cts;
        
        // 视频喂流组件
        private VideoSink? _videoSink;
        
        // 系统媒体播放器
        private MediaPlayer? _mediaPlayer;
        
        // 应用窗口抽象，用于控制全屏
        private AppWindow? _appWindow;
        
        // 全屏状态标志
        private bool _isFullScreen;
        
        // 镜像连接是否活动中
        private bool _isMirroringActive;

        // 诊断：是否已记录首个 H264 帧
        private bool _h264HandlerLogged;

        // 播放管线创建的同步锁，防止并发重复创建
        private readonly object _sinkLock = new object();

        // 管线创建期间临时缓存的帧，建好后按序补喂
        private System.Collections.Generic.List<H264Data>? _preBuffer;

        /// <summary>
        /// 初始化主窗口并启动 AirPlay 接收服务
        /// </summary>
        public MainWindow()
        {
            // 初始化 XAML 设计的控件与层级
            this.InitializeComponent();
            
            // 使用 WinRT 互操作接口获取窗口的原生 HWND 句柄
            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            // 转换为 WinUI 3 WindowId
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            // 绑定 AppWindow 以进行高级窗体控制 (如全屏)
            _appWindow = AppWindow.GetFromWindowId(windowId);
            
            // 设置窗口初始标题
            this.Title = "AirPlayer Receiver";
            // 绑定窗口关闭事件以释放后台资源
            this.Closed += MainWindow_Closed;
            
            // 获取本机计算机名作为默认的 AirPlay 设备名称
            string hostName = Environment.MachineName;
            DeviceNameText.Text = hostName;
            
            // 启动就绪页面的脉动呼吸渐变动画
            if (MainGrid.Resources.TryGetValue("PulseStoryboard", out object sbObj) && sbObj is Storyboard sb)
            {
                sb.Begin();
            }
            
            // 在后台线程启动 AirPlay 接收服务，防止阻塞 UI 渲染
            StartReceiver(hostName);
        }

        /// <summary>
        /// 启动 AirPlay 接收协议的核心发现和监听服务
        /// </summary>
        /// <param name="deviceName">投屏在 Apple 设备上显示的名称</param>
        private void StartReceiver(string deviceName)
        {
            _cts = new CancellationTokenSource();

            // 初始化 AirPlay 接收器；设备 MAC 与密钥由接收器内部随机生成并持久化（identity.dat）
            _receiver = new AirPlayReceiver(deviceName);
            // 绑定镜像开启事件
            _receiver.OnMirroringStartedReceived += Receiver_OnMirroringStarted;
            // 绑定镜像关闭事件
            _receiver.OnMirroringStoppedReceived += Receiver_OnMirroringStopped;
            // 绑定 H264 视频帧到达事件
            _receiver.OnH264DataReceived += Receiver_OnH264DataReceived;

            // 异步在线程池中启动网络服务
            Task.Run(async () =>
            {
                try
                {
                    // 启动 RTSP TCP 端口监听器
                    await _receiver.StartListeners(_cts.Token);
                    // 启动 mDNS (Bonjour) 局域网服务发现广播
                    await _receiver.StartMdnsAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"AirPlay 接收器启动异常: {ex}");
                }
            });
        }

        /// <summary>
        /// 局域网内 iPhone 发起屏幕镜像成功连接时的回调
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">参数</param>
        private void Receiver_OnMirroringStarted(object? sender, EventArgs e)
        {
            // 通过 Dispatcher 将 UI 操作安全投递回主 UI 线程
            DispatcherQueue.TryEnqueue(() =>
            {
                // 标记镜像已连接活动
                _isMirroringActive = true;
                // 隐藏紫色呼吸引导页
                PromoGrid.Visibility = Visibility.Collapsed;
                // 显示视频播放渲染元素
                PlayerElement.Visibility = Visibility.Visible;
            });
        }

        /// <summary>
        /// 手机主动断开屏幕镜像或网络中断时的回调
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">参数</param>
        private void Receiver_OnMirroringStopped(object? sender, EventArgs e)
        {
            // 将界面重置逻辑投递给主 UI 线程
            DispatcherQueue.TryEnqueue(() =>
            {
                // 重置镜像状态
                _isMirroringActive = false;
                
                // 停止播放，解除数据绑定
                if (_mediaPlayer != null)
                {
                    _mediaPlayer.Pause();
                    _mediaPlayer.Source = null;
                }
                
                // 销毁并重置低延迟喂流管道
                lock (_sinkLock)
                {
                    _videoSink?.Dispose();
                    _videoSink = null;
                    _preBuffer = null;
                    // 重置首帧诊断标志，便于下次连接重新记录
                    _h264HandlerLogged = false;
                }

                // 隐藏视频播放区域
                PlayerElement.Visibility = Visibility.Collapsed;
                // 重新展示高大上的紫色呼吸引导界面
                PromoGrid.Visibility = Visibility.Visible;
            });
        }

        /// <summary>
        /// 当接收到后台解密出的每一个 H264 Annex-B 视频帧时的回调
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="data">H264 原始帧，包含数据包、分辨率和时间戳信息</param>
        private void Receiver_OnH264DataReceived(object? sender, H264Data data)
        {
            // 诊断：首帧到达 UI 层
            if (!_h264HandlerLogged)
            {
                _h264HandlerLogged = true;
                AirPlayer.Protocol.Utils.DiagLog.Write($"[UI] 首个 H264 帧到达 mirroringActive={_isMirroringActive} sink={(_videoSink == null ? "null" : "ok")} {data.Width}x{data.Height}");
            }

            // 如果连接已经失效，丢弃多余帧
            if (!_isMirroringActive) return;

            lock (_sinkLock)
            {
                // 管线已就绪，直接入队
                if (_videoSink != null)
                {
                    _videoSink.EnqueueFrame(data);
                    return;
                }

                // 管线正在创建中，先把帧缓存起来，待创建完成按序补喂
                if (_preBuffer != null)
                {
                    _preBuffer.Add(data);
                    return;
                }

                // 首帧：仅在此处触发唯一一次创建，并把首帧放入缓存
                _preBuffer = new System.Collections.Generic.List<H264Data> { data };
                int w = data.Width, h = data.Height;
                // 真正的播放管线创建必须在 UI 线程
                DispatcherQueue.TryEnqueue(() => CreateVideoPipeline(w, h));
            }
        }

        /// <summary>
        /// 在 UI 线程创建唯一的视频播放管线，并补喂创建期间缓存的帧
        /// </summary>
        /// <param name="width">画面宽度</param>
        /// <param name="height">画面高度</param>
        private void CreateVideoPipeline(int width, int height)
        {
            // 防御：创建排队期间用户已退出投屏
            if (!_isMirroringActive)
            {
                lock (_sinkLock) { _preBuffer = null; }
                return;
            }

            // 实例化低延迟喂流管道并按首帧分辨率初始化
            var sink = new VideoSink();
            sink.Initialize(width, height);

            // 创建系统媒体播放对象
            var player = new MediaPlayer();
            // 将自定义的 MediaStreamSource 打包成 MediaSource
            player.Source = MediaSource.CreateFromMediaStreamSource(sink.MediaStreamSource);
            // 诊断：捕获解码/播放失败与播放状态变化
            player.MediaFailed += (s, args) =>
                AirPlayer.Protocol.Utils.DiagLog.Write($"[PLAYER] MediaFailed: {args.Error} / {args.ErrorMessage} / {args.ExtendedErrorCode}");
            player.MediaOpened += (s, args) =>
                AirPlayer.Protocol.Utils.DiagLog.Write("[PLAYER] MediaOpened（媒体已打开，开始解码）");
            player.PlaybackSession.PlaybackStateChanged += (s, args) =>
                AirPlayer.Protocol.Utils.DiagLog.Write($"[PLAYER] PlaybackState={s.PlaybackState}");
            // 实时播放，降低延迟
            player.RealTimePlayback = true;
            // 关联到 XAML 渲染层并开始播放
            PlayerElement.SetMediaPlayer(player);
            player.Play();

            // 发布管线并补喂创建期间缓存的帧（按序，保证解码器从首帧关键帧起步）
            lock (_sinkLock)
            {
                _mediaPlayer = player;
                _videoSink = sink;
                if (_preBuffer != null)
                {
                    foreach (var f in _preBuffer)
                    {
                        _videoSink.EnqueueFrame(f);
                    }
                    _preBuffer = null;
                }
            }
        }

        /// <summary>
        /// 全屏切换悬浮按钮的点击回调
        /// </summary>
        /// <param name="sender">按钮实例</param>
        /// <param name="e">点击参数</param>
        private void FullScreenButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleFullScreen();
        }

        /// <summary>
        /// 切换窗口的全屏（电视模式）与普通窗口模式
        /// </summary>
        private void ToggleFullScreen()
        {
            if (_appWindow == null) return;

            if (_isFullScreen)
            {
                // 设置为主流的窗口样式
                _appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                _isFullScreen = false;
                // 将全屏按钮的图标切回“进入全屏”
                FullScreenButton.Content = "\uE740"; 
            }
            else
            {
                // 设置为系统的纯净全屏样式
                _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                _isFullScreen = true;
                // 将全屏按钮的图标切换为“退出全屏”
                FullScreenButton.Content = "\uE73F"; 
            }
        }

        /// <summary>
        /// 窗口关闭时的清理逻辑，确保杀死所有的后台线程和端口占用
        /// </summary>
        /// <param name="sender">窗口实例</param>
        /// <param name="args">事件参数</param>
        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            // 触发取消标志，安全中断 Socket 与发现服务
            _cts?.Cancel();
            // 释放 AirPlay 接收模块端口和 mDNS 服务
            _receiver?.Dispose();
            // 释放视频通道
            _videoSink?.Dispose();
            // 释放系统播放器占用的解码管线
            _mediaPlayer?.Dispose();
        }
    }
}
