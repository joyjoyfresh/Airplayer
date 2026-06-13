using System;
using System.Threading.Channels;
using System.Threading;
using AirPlayer.Protocol.Models;
using AirPlayer.Protocol.Utils;
using Microsoft.UI.Xaml.Controls;
using SharpGen.Runtime;
using Vortice.Direct3D11;

namespace AirPlayer.App.Rendering
{
    /// <summary>
    /// 视频呈现编排器：统一管理帧队列、渲染线程、解码→Blt→Present 全链路。
    /// 包含健壮性处理：STREAM_CHANGE、面板 Resize、重连、错误恢复。
    /// </summary>
    public sealed class VideoPresenter : IDisposable
    {
        // ──────────────────────────────────────────────────────────────────
        // 子组件
        // ──────────────────────────────────────────────────────────────────

        private GpuDevice? _gpu;                    // 共享 D3D11 设备
        private DxgiSwapChainHost? _swapChain;      // 交换链
        private HardwareH264Decoder? _decoder;      // 硬件 H264 解码器
        private Nv12VideoProcessor? _processor;     // NV12→RGB 视频处理器

        // ──────────────────────────────────────────────────────────────────
        // 渲染线程 / 帧队列
        // ──────────────────────────────────────────────────────────────────

        private Thread? _renderThread;                          // 渲染线程（MTA）
        private volatile bool _renderRunning;                  // 渲染线程运行标志
        private readonly Channel<H264Data> _frameChannel = Channel.CreateBounded<H264Data>(
            new BoundedChannelOptions(MaxQueueDepth)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });

        // ──────────────────────────────────────────────────────────────────
        // 状态
        // ──────────────────────────────────────────────────────────────────

        private int _videoWidth;
        private int _videoHeight;
        private int _panelWidth;
        private int _panelHeight;
        private bool _processorReady;
        private int _consecutiveErrors;

        /// <summary>分辨率变化事件（在渲染线程触发，带宽高参数）</summary>
        public event EventHandler<(int Width, int Height)>? ResolutionChanged;

        // ──────────────────────────────────────────────────────────────────
        // 常量
        // ──────────────────────────────────────────────────────────────────

        // 帧通道最大积压深度（超出后丢弃最旧帧；16帧 ≈ 533ms 缓冲，充分吸收网络突发）
        private const int MaxQueueDepth = 16;
        // 连续失败多少帧后丢弃到下一 IDR 进行错误恢复
        private const int MaxConsecutiveErrors = 5;
        // 诊断：已成功解码帧数（用于周期性日志）
        private int _decodedFrameCount;
        // 诊断：已跳过帧数（TryDecode 返回 false）
        private int _skippedFrameCount;
        // 诊断：已 Present 帧数
        private int _presentCount;

        // ──────────────────────────────────────────────────────────────────
        // 初始化
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// 在 UI 线程调用，创建全 GPU 管线并启动渲染线程。
        /// </summary>
        /// <param name="panel">目标 SwapChainPanel</param>
        /// <param name="videoWidth">视频宽度（首帧分辨率）</param>
        /// <param name="videoHeight">视频高度（首帧分辨率）</param>
        /// <param name="panelPixelWidth">面板像素宽度（物理像素）</param>
        /// <param name="panelPixelHeight">面板像素高度（物理像素）</param>
        public void Initialize(SwapChainPanel panel,
            int videoWidth, int videoHeight,
            int panelPixelWidth, int panelPixelHeight)
        {
            _videoWidth   = videoWidth;
            _videoHeight  = videoHeight;
            _panelWidth   = panelPixelWidth  > 0 ? panelPixelWidth  : videoWidth;
            _panelHeight  = panelPixelHeight > 0 ? panelPixelHeight : videoHeight;

            // ── 创建各子组件 ──────────────────────────────────────────────
            _gpu       = new GpuDevice();
            _swapChain = new DxgiSwapChainHost(_gpu);
            _decoder   = new HardwareH264Decoder(_gpu);
            _processor = new Nv12VideoProcessor(_gpu);

            // 初始化交换链并绑定面板（必须在 UI 线程）
            _swapChain.Initialize(panel, _panelWidth, _panelHeight);

            // 渲染线程（MF 要求 MTA）
            _renderRunning = true;
            _renderThread = new Thread(RenderLoop)
            {
                IsBackground = true,
                Name = "VideoRender-GPU"
            };
            _renderThread.SetApartmentState(ApartmentState.MTA);
            _renderThread.Start();

            DiagLog.Write($"[PRS] VideoPresenter 初始化完成 {videoWidth}x{videoHeight}→{_panelWidth}x{_panelHeight}");
        }

        // ──────────────────────────────────────────────────────────────────
        // 公开：运行统计（供 HUD 显示）
        // ──────────────────────────────────────────────────────────────────

        /// <summary>视频管线运行统计快照（供 UI 线程读取，计数为单调递增，跨线程读取无害）。</summary>
        public readonly struct VideoStats
        {
            public readonly int Width;
            public readonly int Height;
            public readonly int Decoded;
            public readonly int Presented;
            public readonly int Skipped;
            public VideoStats(int width, int height, int decoded, int presented, int skipped)
            {
                Width = width; Height = height; Decoded = decoded; Presented = presented; Skipped = skipped;
            }
        }

        /// <summary>获取当前运行统计快照。</summary>
        public VideoStats GetStats() =>
            new VideoStats(_videoWidth, _videoHeight, _decodedFrameCount, _presentCount, _skippedFrameCount);

        // 截图请求（由 UI 线程置位，渲染线程在下一帧 Present 前执行）
        private volatile bool _screenshotPending;
        private string? _screenshotPath;

        /// <summary>请求把下一帧画面保存为 PNG（线程安全）。</summary>
        public void RequestScreenshot(string path)
        {
            _screenshotPath = path;
            _screenshotPending = true;
        }

        // ──────────────────────────────────────────────────────────────────
        // 公开：投递帧
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// 将收到的 H264 帧投递到通道（从网络线程调用，线程安全）。
        /// 通道满时自动丢弃最旧帧，无需手动加锁。
        /// </summary>
        public void EnqueueFrame(H264Data data)
        {
            _frameChannel.Writer.TryWrite(data);
        }

        // ──────────────────────────────────────────────────────────────────
        // 公开：面板尺寸变化（Resize）
        // ──────────────────────────────────────────────────────────────────

        // ──────────────────────────────────────────────────────────────────
        // 公开：旋转 / 面板尺寸变化
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// 手动旋转画面（0/90/180/270），通过交换 VideoProcessor 的源宽高实现。
        /// 90°/270° 时源宽高互换，信箱缩放自动适配。
        /// </summary>
        public void NotifyRotation(int degrees)
        {
            int srcW, srcH;
            if (degrees == 90 || degrees == 270)
            {
                srcW = _videoHeight; // 原始视频高度变为源宽度
                srcH = _videoWidth;  // 原始视频宽度变为源高度
            }
            else
            {
                srcW = _videoWidth;
                srcH = _videoHeight;
            }

            int pw = Volatile.Read(ref _panelWidth);
            int ph = Volatile.Read(ref _panelHeight);
            _processor?.ReleaseCachedViews();
            _processor?.Rebuild(srcW, srcH, pw, ph);
            DiagLog.Write($"[PRS] 旋转 {degrees}°: VideoProcessor {srcW}x{srcH}→{pw}x{ph}");
        }

        /// <summary>
        /// 面板物理像素尺寸变化时调用（在 UI 线程）。
        /// 渲染线程会在下一帧时感知到尺寸变化并重建输出链路。
        /// </summary>
        public void NotifyPanelSizeChanged(int newPixelWidth, int newPixelHeight)
        {
            if (newPixelWidth < 16 || newPixelHeight < 16) return; // 忽略过小尺寸（全屏过渡期）

            Volatile.Write(ref _panelWidth, newPixelWidth);
            Volatile.Write(ref _panelHeight, newPixelHeight);
            _processorReady = false;
        }

        // ──────────────────────────────────────────────────────────────────
        // 渲染线程
        // ──────────────────────────────────────────────────────────────────

        /// <summary>渲染线程主循环：消费帧通道 → 解码 → Blt → Present</summary>
        private void RenderLoop()
        {
            DiagLog.Write("[PRS] 渲染线程启动");

            var reader = _frameChannel.Reader;
            while (_renderRunning)
            {
                if (reader.TryRead(out var frame))
                {
                    try
                    {
                        // 批量解码：把队列中所有积压帧都送入解码器（维护参考链），
                        // 但只对最后一帧做 Blt+Present，避免花屏。
                        H264Data lastFrame = frame;
                        int decoded = 1;
                        while (reader.TryRead(out var next))
                        {
                            ProcessFrame(lastFrame, present: false); // 解码不呈现
                            lastFrame = next;
                            decoded++;
                        }
                        if (decoded > 1)
                            DiagLog.Write($"[PRS] 批量解码 {decoded} 帧，仅呈现最新");
                        ProcessFrame(lastFrame, present: true); // 最后一帧解码+呈现
                        // 周期性诊断：每 60 帧输出一次解码成功/跳过统计
                        _decodedFrameCount++;
                        if (_decodedFrameCount == 1 || _decodedFrameCount % 60 == 0)
                            DiagLog.Write($"[PRS] 解码统计: 成功={_decodedFrameCount} 跳过={_skippedFrameCount}");
                    }
                    catch (Exception ex)
                    {
                        DiagLog.Write($"[PRS] 渲染循环异常: {ex.Message}");
                    }
                }
                else
                {
                    Thread.Sleep(1);
                }
            }

            DiagLog.Write("[PRS] 渲染线程退出");
        }

        /// <summary>处理单帧：解码 → 检查 Resize → [可选] Blt → Present</summary>
        private void ProcessFrame(H264Data frame, bool present = true)
        {
            // ── 分辨率变化检测（iOS 旋转）───────────────────────────────
            // 仅在 IDR 帧（type=5）时才做分辨率变化检测：
            // AirPlay 镜像中，新 SPS/PPS 到达后 session.WidthSource/HeightSource 立即更新，
            // 但 P 帧可能仍是旧分辨率编码。若用 P 帧的 Width/Height 判断变化会误触发
            // 解码器重置，导致参考帧丢失、后续 P 帧全部无法解码。
            // IDR 帧必然伴随新的 SPS/PPS，是可靠的分辨率变化信号。
            if (_decoder!.IsStarted &&
                frame.FrameType == 5 &&
                (frame.Width != _videoWidth || frame.Height != _videoHeight))
            {
                DiagLog.Write($"[PRS] 分辨率变化 {_videoWidth}x{_videoHeight} → {frame.Width}x{frame.Height}，重置解码器");
                _videoWidth = frame.Width;
                _videoHeight = frame.Height;
                _decoder.Reset();
                _processorReady = false; // 触发 VideoProcessor 重建
                ResolutionChanged?.Invoke(this, (_videoWidth, _videoHeight));
            }

            // ── 首帧：初始化解码器 ────────────────────────────────────────
            _decoder.EnsureStarted(frame.Width, frame.Height);

            // ── 处理面板 Resize ───────────────────────────────────────────
            int pw = Volatile.Read(ref _panelWidth);
            int ph = Volatile.Read(ref _panelHeight);
            if (!_processorReady || pw != _swapChain!.CurrentWidth || ph != _swapChain.CurrentHeight)
            {
                HandleResize(pw, ph);
            }

            // ── 解码（硬件路径）──────────────────────────────────────────
            bool got = _decoder!.TryDecode(frame.Data, out ID3D11Texture2D? nv12Tex, out uint subIdx);

            // 解码器输出格式变化（含分辨率变化）：仅重建 VideoProcessor 适配新源尺寸
            if (_decoder.ConsumeStreamChange())
            {
                // 从解码纹理反推实际视频分辨率（MFT 解析 SPS 得到的编码尺寸可能与显示尺寸不同）
                if (got && nv12Tex != null)
                {
                    int texW = (int)nv12Tex.Description.Width;
                    int texH = (int)nv12Tex.Description.Height;
                    // NV12 纹理高度是 Y+UV 平面之和，实际视频高度为 2/3
                    int actualH = texH * 2 / 3;
                    if (actualH != _videoHeight || texW != _videoWidth)
                    {
                        DiagLog.Write($"[PRS] STREAM_CHANGE 实际分辨率 {_videoWidth}x{_videoHeight} → {texW}x{actualH}");
                        _videoWidth = texW;
                        _videoHeight = actualH;
                        ResolutionChanged?.Invoke(this, (_videoWidth, _videoHeight));
                    }
                }
                // 释放缓存视图 → 用新 _videoWidth/_videoHeight 重建 VideoProcessor
                _processor?.ReleaseCachedViews();
                _processor?.Rebuild(_videoWidth, _videoHeight,
                    Volatile.Read(ref _panelWidth), Volatile.Read(ref _panelHeight));
                DiagLog.Write($"[PRS] STREAM_CHANGE: VideoProcessor 已重建 {_videoWidth}x{_videoHeight}");
            }

            if (!got)
            {
                _skippedFrameCount++;
                // 首次跳过及每 60 次跳过输出诊断日志
                if (_skippedFrameCount == 1 || _skippedFrameCount % 60 == 0)
                    DiagLog.Write($"[PRS] TryDecode 返回 false (已跳过 {_skippedFrameCount} 帧) frameType={frame.FrameType}");
                return;
            }

            _consecutiveErrors = 0;

            if (!present)
            {
                // 仅解码不呈现：维护参考链，释放纹理
                nv12Tex?.Dispose();
                return;
            }

            // ── GPU Blt → Present ─────────────────────────────────────────
            using (nv12Tex)
            {
                try
                {
                    using ID3D11Texture2D backBuf = _swapChain!.GetBackBuffer();

                    _processor!.Blt(nv12Tex!, subIdx, backBuf);

                    // 截图：在 Present 前从后台缓冲抓取当前帧（此时已是渲染完成的画面）
                    if (_screenshotPending && _screenshotPath != null)
                    {
                        CaptureScreenshot(backBuf, _screenshotPath);
                        _screenshotPending = false;
                    }

                    Result presentHr = _swapChain.SwapChain.Present(1, 0); // 1=等垂直同步
                    // 诊断：首次 Present 成功及每 60 帧记录一次
                    _presentCount++;
                    if (_presentCount == 1 || _presentCount % 60 == 0)
                        DiagLog.Write($"[PRS] Present #{_presentCount} hr=0x{presentHr.Code:X8} texSize={nv12Tex.Description.Width}x{nv12Tex.Description.Height}");
                    if (presentHr.Code == unchecked((int)0x887A0007)) // DXGI_ERROR_DEVICE_RESET
                    {
                        DiagLog.Write("[PRS] 设备丢失 DEVICE_RESET，准备重建管线");
                        // 标记需要 UI 线程重建（此处仅日志，由 VideoPresenter 调用方处理）
                    }
                    else if (presentHr.Failure)
                    {
                        DiagLog.Write($"[PRS] Present 失败: 0x{presentHr.Code:X8}");
                    }
                }
                catch (SharpGenException ex)
                when (ex.ResultCode.Code == unchecked((int)0xC00D6D61)) // MF_E_TRANSFORM_STREAM_CHANGE
                {
                    // Blt 层 STREAM_CHANGE：释放缓存并用当前尺寸重建
                    DiagLog.Write($"[PRS] STREAM_CHANGE (Blt层): 重建 VideoProcessor");
                    _processor?.ReleaseCachedViews();
                    _processor?.Rebuild(_videoWidth, _videoHeight,
                        Volatile.Read(ref _panelWidth), Volatile.Read(ref _panelHeight));
                }
            }
        }

        /// <summary>
        /// 处理 Resize：重建交换链缓冲区和视频处理器。
        /// 必须在渲染线程中调用（保证没有 BackBuffer 被持有）。
        /// </summary>
        private void HandleResize(int newW, int newH)
        {
            if (newW <= 0 || newH <= 0) return;

            DiagLog.Write($"[PRS] HandleResize {newW}x{newH}");

            try
            {
                // 先释放 VideoProcessor 缓存的视图（持有旧后台缓冲引用）
                _processor?.ReleaseCachedViews();
                // 再重设交换链缓冲区大小
                _swapChain!.Resize(newW, newH);
                // 最后重建视频处理器
                _processor!.Rebuild(_videoWidth, _videoHeight, newW, newH);
                _processorReady = true;
                DiagLog.Write($"[PRS] Resize 完成 {newW}x{newH}");
            }
            catch (Exception ex)
            {
                DiagLog.Write($"[PRS] Resize 失败: {ex.Message}，保持当前状态");
                _processorReady = true; // 防止每帧重试导致错误循环
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // 截图
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// 从后台缓冲抓取当前帧并保存为 PNG。在渲染线程调用（持有 D3D 上下文）。
        /// 复制到 Staging 纹理 → Map 读出 BGRA → System.Drawing 编码 PNG。
        /// </summary>
        private void CaptureScreenshot(ID3D11Texture2D source, string path)
        {
            try
            {
                var d = source.Description;
                var stagingDesc = new Texture2DDescription
                {
                    Width = d.Width,
                    Height = d.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = d.Format,
                    SampleDescription = d.SampleDescription,
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    MiscFlags = ResourceOptionFlags.None
                };

                using ID3D11Texture2D staging = _gpu!.Device.CreateTexture2D(stagingDesc);
                _gpu.DeviceContext.CopyResource(staging, source);

                var map = _gpu.DeviceContext.Map(staging, 0, MapMode.Read, MapFlags.None);
                try
                {
                    // 后台缓冲为 B8G8R8A8，GDI+ 用 Format32bppRgb（忽略 Alpha，得到不透明图）
                    using var bmp = new System.Drawing.Bitmap(
                        (int)d.Width, (int)d.Height, (int)map.RowPitch,
                        System.Drawing.Imaging.PixelFormat.Format32bppRgb, map.DataPointer);
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
                finally
                {
                    _gpu.DeviceContext.Unmap(staging, 0);
                }

                DiagLog.Write($"[PRS] 截图已保存: {path}");
            }
            catch (Exception ex)
            {
                DiagLog.Write($"[PRS] 截图失败: {ex.Message}");
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // 错误恢复：等待下一个 IDR
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// 连续解码失败超阈值后调用；排空通道中非 IDR 帧，等待下一个关键帧恢复。
        /// </summary>
        private void DrainToNextKeyframe()
        {
            int drained = 0;
            var reader = _frameChannel.Reader;
            while (reader.TryRead(out var f))
            {
                if (f.FrameType == 5)
                {
                    // IDR：重新入队，让它正常解码成为参考帧
                    _frameChannel.Writer.TryWrite(f);
                    break;
                }
                drained++;
            }
            DiagLog.Write($"[PRS] 错误恢复：已丢弃 {drained} 帧，IDR 已保留");
        }

        // ──────────────────────────────────────────────────────────────────
        // 释放
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// 按顺序停止渲染线程 → 释放处理器 → 释放交换链 → 释放解码器 → 释放设备。
        /// </summary>
        public void Dispose()
        {
            // 1. 停止渲染线程
            _renderRunning = false;
            _frameChannel.Writer.TryComplete(); // 通知渲染线程退出
            try { _renderThread?.Join(1000); } catch { }
            _renderThread = null;

            // 2. 按依赖顺序释放组件（由内向外）
            _processor?.Dispose();   // 视频处理器（持有视图，先释放）
            _processor = null;

            _swapChain?.Dispose();   // 交换链
            _swapChain = null;

            _decoder?.Dispose();     // 解码器（可能持有设备管理器句柄）
            _decoder = null;

            _gpu?.Dispose();         // D3D11 设备（最后释放）
            _gpu = null;

            DiagLog.Write("[PRS] VideoPresenter 已释放");
        }
    }
}
