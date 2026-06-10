using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using AirPlayer.Protocol.Models;
using AirPlayer.Protocol.Utils;

namespace AirPlayer.App
{
    /// <summary>
    /// 视频接收与喂流管道类：负责将 RTSP 接收的 H264 数据转换并实时喂给 WinUI 3 MediaPlayerElement。
    /// </summary>
    public class VideoSink : IDisposable
    {
        // 线程安全的视频帧队列
        private readonly ConcurrentQueue<H264Data> _frameQueue = new ConcurrentQueue<H264Data>();
        
        // 媒体流源，用于自定义喂流给解密和解码管道
        private MediaStreamSource? _mss;
        
        // 挂起的请求延迟（当队列没有数据时，保留请求直到新帧到达）
        private MediaStreamSourceSampleRequestDeferral? _activeDeferral;
        
        // 挂起的当前请求上下文
        private MediaStreamSourceSampleRequest? _activeRequest;
        
        // 线程同步锁
        private readonly object _lock = new object();

        // 释放标志
        private bool _isDisposed;

        // 第一帧的 PTS，用于把绝对时间戳归零（否则播放器时间轴会一直等到那个绝对时刻导致黑屏）
        private long? _basePts;

        // 上一帧的相对时间（微秒），用于按真实间隔估算每帧时长
        private long _lastRelUs = -1;

        // 呈现缓冲（微秒）：把时间戳整体后移，吸收网络+解码延迟，避免帧被判为迟到而丢弃导致冻结
        private const long PresentationCushionUs = 250000;

        // 诊断计数
        private int _enqueueCount;
        private int _sampleServedCount;

        // 是否已收到首个关键帧(IDR)；在此之前的 P 帧必须丢弃，否则解码器无参考帧导致白屏
        private bool _gotKeyframe;

        /// <summary>
        /// 获取生成的媒体流源
        /// </summary>
        public MediaStreamSource MediaStreamSource => _mss ?? throw new InvalidOperationException("未初始化 VideoSink");

        /// <summary>
        /// 初始化媒体流源，配置 H264 解码器参数
        /// </summary>
        /// <param name="width">画面像素宽度</param>
        /// <param name="height">画面像素高度</param>
        public void Initialize(int width, int height)
        {
            // 创建 H264 视频编码属性
            var videoProperties = VideoEncodingProperties.CreateH264();
            // 设置宽度
            videoProperties.Width = (uint)width;
            // 设置高度
            videoProperties.Height = (uint)height;

            // 创建视频描述符
            var videoDescriptor = new VideoStreamDescriptor(videoProperties);
            
            // 初始化媒体源并开启实时流属性以消除缓冲延迟
            _mss = new MediaStreamSource(videoDescriptor)
            {
                // 实时流不需要缓冲区缓冲，降低播放延迟
                BufferTime = TimeSpan.Zero,
                // 标记为实时流
                IsLive = true
            };

            // 绑定样本请求事件回调
            _mss.SampleRequested += OnSampleRequested;
            // 绑定起始事件：把实时流的起始位置固定为 0，避免播放器按绝对时间戳等待
            _mss.Starting += OnStarting;

            // 诊断：媒体源已初始化
            DiagLog.Write($"[SINK] Initialize {width}x{height}");
        }

        /// <summary>
        /// MediaStreamSource 启动时回调，将起始播放位置设为 0
        /// </summary>
        private void OnStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
        {
            // 直接把实际起始位置设为 0（实时流）
            args.Request.SetActualStartPosition(TimeSpan.Zero);
        }

        /// <summary>
        /// 由 H264 帧构造一个时间戳已归零的 MediaStreamSample
        /// </summary>
        /// <param name="frame">H264 帧数据</param>
        private MediaStreamSample CreateSample(H264Data frame)
        {
            // 以第一帧的 PTS 作为基准
            if (_basePts == null)
            {
                _basePts = frame.Pts;
            }

            // 计算相对时间戳并防止为负
            long relativeUs = frame.Pts - _basePts.Value;
            if (relativeUs < 0)
            {
                relativeUs = 0;
            }

            // 将 byte 数组转为 Windows 运行时 IBuffer
            var buffer = frame.Data.AsBuffer();
            // 时间戳 = 归零后的相对时间 + 呈现缓冲；微秒转 100 纳秒 Ticks（1us = 10 ticks）
            var timestamp = TimeSpan.FromTicks((relativeUs + PresentationCushionUs) * 10);
            // 创建样本
            var sample = MediaStreamSample.CreateFromBuffer(buffer, timestamp);

            // 每帧时长按与上一帧的真实间隔估算，避免固定 16ms 与实际帧率不符导致时间轴错乱
            long durUs = (_lastRelUs >= 0) ? (relativeUs - _lastRelUs) : 33000;
            if (durUs < 1000) durUs = 1000;        // 下限 1ms
            if (durUs > 200000) durUs = 200000;    // 上限 200ms
            sample.Duration = TimeSpan.FromTicks(durUs * 10);
            _lastRelUs = relativeUs;

            // I 帧标记为关键帧
            if (frame.FrameType == 5)
            {
                sample.KeyFrame = true;
            }

            return sample;
        }

        /// <summary>
        /// 将接收到的 H264 帧放入渲染队列或立即递送给挂起的请求
        /// </summary>
        /// <param name="frame">H264 帧数据对象</param>
        public void EnqueueFrame(H264Data frame)
        {
            lock (_lock)
            {
                // 如果已释放则不再处理
                if (_isDisposed) return;

                // 解码器必须从关键帧(IDR)开始：丢弃首个关键帧之前的所有 P 帧
                if (!_gotKeyframe)
                {
                    if (frame.FrameType != 5)
                    {
                        DiagLog.Write($"[SINK] 丢弃关键帧前的 P 帧 type={frame.FrameType}");
                        return;
                    }
                    _gotKeyframe = true;
                    DiagLog.Write("[SINK] 收到首个关键帧(IDR)，开始喂解码器");
                }

                // 诊断：入队帧计数
                _enqueueCount++;
                if (_enqueueCount == 1 || _enqueueCount % 60 == 0)
                    DiagLog.Write($"[SINK] EnqueueFrame#{_enqueueCount} len={frame.Data?.Length ?? 0} type={frame.FrameType} 挂起请求={(_activeDeferral != null)}");

                // 检查是否有系统挂起的渲染样本请求
                if (_activeDeferral != null && _activeRequest != null)
                {
                    // 构造时间戳已归零的样本并赋值给挂起的请求
                    _activeRequest.Sample = CreateSample(frame);

                    // 诊断：通过挂起请求向解码器提供样本
                    _sampleServedCount++;
                    if (_sampleServedCount == 1 || _sampleServedCount % 60 == 0)
                        DiagLog.Write($"[SINK] 提供样本#{_sampleServedCount}(挂起路径) 给解码器");

                    // 释放挂起的 Deferral，通知 MediaStreamSource 消费该帧
                    _activeDeferral.Complete();
                    
                    // 重置挂起上下文
                    _activeDeferral = null;
                    _activeRequest = null;
                }
                else
                {
                    // 没有挂起的请求，则将帧存入线程安全队列
                    _frameQueue.Enqueue(frame);
                }
            }
        }

        /// <summary>
        /// 当系统音频/视频管道需要新帧样本进行解码时的回调函数
        /// </summary>
        /// <param name="sender">媒体源发送者</param>
        /// <param name="args">事件参数，包含请求和样本设置属性</param>
        private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {
            var request = args.Request;
            lock (_lock)
            {
                // 若已释放则忽略请求
                if (_isDisposed) return;

                // 尝试从队列中出队一帧
                if (_frameQueue.TryDequeue(out var frame))
                {
                    // 构造时间戳已归零的样本并喂给请求
                    request.Sample = CreateSample(frame);
                    // 诊断：已向解码器提供样本计数
                    _sampleServedCount++;
                    if (_sampleServedCount == 1 || _sampleServedCount % 60 == 0)
                        DiagLog.Write($"[SINK] 提供样本#{_sampleServedCount} 给解码器");
                }
                else
                {
                    // 队列无数据时，不应返回 null，否则会触发流结束 (EndOfStream)。
                    // 我们使用 Deferral 挂起该请求，直到 EnqueueFrame 中有新帧到来再重新激活它。
                    _activeRequest = request;
                    _activeDeferral = request.GetDeferral();
                }
            }
        }

        /// <summary>
        /// 清理队列和所有挂起的延迟请求
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                // 清空队列
                _frameQueue.Clear();
                // 重置基准 PTS，下一次连接重新归零
                _basePts = null;
                // 重置帧间隔基准
                _lastRelUs = -1;
                // 重置关键帧标志
                _gotKeyframe = false;
                // 强行关闭挂起的请求以防泄露
                if (_activeDeferral != null)
                {
                    _activeDeferral.Complete();
                    _activeDeferral = null;
                    _activeRequest = null;
                }
            }
        }

        /// <summary>
        /// 释放资源和事件绑定
        /// </summary>
        public void Dispose()
        {
            lock (_lock)
            {
                if (_isDisposed) return;
                _isDisposed = true;
                
                // 清理挂起帧
                Clear();
                
                // 取消事件绑定
                if (_mss != null)
                {
                    _mss.SampleRequested -= OnSampleRequested;
                    _mss.Starting -= OnStarting;
                    _mss = null;
                }
            }
        }
    }
}
