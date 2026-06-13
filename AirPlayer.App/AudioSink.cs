using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using AirPlayer.Protocol.Models.Audio;
using AirPlayer.Protocol.Utils;
using Windows.Media.Audio;
using Windows.Media.Render;
using Windows.Devices.Enumeration;
using Windows.Media;
using Windows.Media.MediaProperties;

namespace AirPlayer.App
{
    [ComImport]
    [Guid("5B0D3235-4DBE-4DFA-8240-C7396FDE4115")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMemoryBufferByteAccess
    {
        unsafe void GetBuffer(out byte* buffer, out uint capacity);
    }

    /// <summary>
    /// 音频播放器：接收解码后的 PCM 帧，通过 Windows WASAPI / AudioGraph API 低延迟播放。
    /// </summary>
    public sealed class AudioSink : IDisposable
    {
        // PCM 参数（AirPlay 固定 44100Hz, 2ch, 16-bit）
        private const int SAMPLE_RATE = 44100;
        private const int CHANNELS = 2;
        private const int BITS_PER_SAMPLE = 16;
        private const int BLOCK_ALIGN = CHANNELS * BITS_PER_SAMPLE / 8; // 4 字节/帧

        // 待播队列最大积压字节数（≈150ms）；超出则丢弃最旧帧以保持音画同步
        private const int MAX_QUEUED_BYTES = (int)(SAMPLE_RATE * BLOCK_ALIGN * 0.15);

        // AudioGraph 实例
        private AudioGraph? _audioGraph;
        private AudioFrameInputNode? _frameInputNode;
        private readonly string? _preferredDeviceId;
        private volatile bool _isInitialized;

        // 实例状态
        private bool _disposed;
        private volatile bool _running;
        private bool _isPlaying;

        // 待播队列与积压计数
        private readonly ConcurrentQueue<PcmData> _frameQueue = new();
        private long _queuedBytes;
        private readonly object _lock = new();

        // 唤醒事件与线程
        private readonly AutoResetEvent _dataEvent = new(false);
        private Thread? _playbackThread;

        // PTS 时钟
        private ulong _basePts;
        private bool _basePtsSet;
        private long _totalSamplesPlayed;

        // 调试计数
        private long _enqueueCount;
        private long _queueCount;

        /// <summary>当前音频时钟（微秒，NTP epoch）— 供视频同步参考</summary>
        public ulong CurrentClock => _basePtsSet
            ? _basePts + (ulong)((double)Interlocked.Read(ref _totalSamplesPlayed) / SAMPLE_RATE * 1_000_000)
            : 0;

        /// <summary>音频是否已开始播放</summary>
        public bool IsPlaying => _isPlaying;

        public AudioSink(string? deviceId = null)
        {
            _preferredDeviceId = deviceId;
        }

        /// <summary>初始化音频播放器并启动播放线程与 AudioGraph。</summary>
        public void Initialize()
        {
            try
            {
                AudioDiagLog.Write("[INIT] 开始初始化 AudioSink (AudioGraph)...");

                _running = true;
                _playbackThread = new Thread(PlaybackLoop)
                {
                    IsBackground = true,
                    Name = "AudioPlayback"
                };
                _playbackThread.Start();

                // 异步启动 AudioGraph 的创建和配置
                System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        await InitializeAudioGraphAsync();
                    }
                    catch (Exception ex)
                    {
                        AudioDiagLog.Write($"[INIT] AudioGraph 初始化异常: {ex}");
                    }
                });
            }
            catch (Exception ex)
            {
                AudioDiagLog.Write($"[INIT] AudioSink 初始化发生异常: {ex}");
            }
        }

        private async System.Threading.Tasks.Task InitializeAudioGraphAsync()
        {
            AudioDiagLog.Write("[INIT] 异步初始化 AudioGraph...");

            var settings = new AudioGraphSettings(AudioRenderCategory.Media)
            {
                QuantumSizeSelectionMode = QuantumSizeSelectionMode.LowestLatency
            };

            if (!string.IsNullOrEmpty(_preferredDeviceId))
            {
                try
                {
                    var device = await DeviceInformation.CreateFromIdAsync(_preferredDeviceId);
                    settings.PrimaryRenderDevice = device;
                    AudioDiagLog.Write($"[INIT] 使用指定的音频播放设备: {device.Name}");
                }
                catch (Exception ex)
                {
                    AudioDiagLog.Write($"[INIT] 创建指定设备失败，回退到默认设备: {ex.Message}");
                }
            }
            else
            {
                AudioDiagLog.Write("[INIT] 未指定设备，使用系统默认输出设备");
            }

            var createResult = await AudioGraph.CreateAsync(settings);
            if (createResult.Status != AudioGraphCreationStatus.Success)
            {
                AudioDiagLog.Write($"[INIT] AudioGraph 创建失败: {createResult.Status}");
                return;
            }

            _audioGraph = createResult.Graph;

            // 16-bit PCM, 44100Hz, 2ch
            var encodingProperties = AudioEncodingProperties.CreatePcm(SAMPLE_RATE, CHANNELS, BITS_PER_SAMPLE);
            _frameInputNode = _audioGraph.CreateFrameInputNode(encodingProperties);

            // 创建物理输出节点
            var outputNodeResult = await _audioGraph.CreateDeviceOutputNodeAsync();
            if (outputNodeResult.Status != AudioDeviceNodeCreationStatus.Success)
            {
                AudioDiagLog.Write($"[INIT] 创建音频设备输出节点失败: {outputNodeResult.Status}");
                return;
            }

            // 连接到物理设备输出节点
            _frameInputNode.AddOutgoingConnection(outputNodeResult.DeviceOutputNode);

            // 启动音频图播放
            _audioGraph.Start();

            lock (_lock)
            {
                _isInitialized = true;
            }

            AudioDiagLog.Write("[INIT] AudioGraph 初始化完成并已启动");
        }

        /// <summary>投递一帧 PCM 数据到播放队列（协议层线程调用，线程安全）。</summary>
        public void EnqueueFrame(PcmData pcmData)
        {
            if (_disposed || pcmData == null || pcmData.Length <= 0) return;

            _enqueueCount++;

            // 设置时间基准（首帧 PTS）
            if (!_basePtsSet && pcmData.Pts > 0)
            {
                _basePts = pcmData.Pts;
                _basePtsSet = true;
            }

            _frameQueue.Enqueue(pcmData);
            Interlocked.Add(ref _queuedBytes, pcmData.Length);

            // 积压过多则丢弃最旧帧，保持音频贴近实时（与视频最新帧对齐 → 音画同步）
            int dropped = 0;
            while (Interlocked.Read(ref _queuedBytes) > MAX_QUEUED_BYTES && _frameQueue.TryDequeue(out var old))
            {
                Interlocked.Add(ref _queuedBytes, -old.Length);
                dropped++;
            }
            if (dropped > 0 && (_enqueueCount <= 10 || _enqueueCount % 500 == 0))
                AudioDiagLog.Write($"[ENQ] #{_enqueueCount}: 丢弃 {dropped} 旧帧以保持同步, 队列={_frameQueue.Count}");

            if (_enqueueCount <= 5)
                AudioDiagLog.Write($"[ENQ] #{_enqueueCount}: len={pcmData.Length} queued={Interlocked.Read(ref _queuedBytes)}");

            // 唤醒播放线程
            _dataEvent.Set();
        }

        /// <summary>清空缓冲队列</summary>
        public void Flush()
        {
            lock (_lock)
            {
                while (_frameQueue.TryDequeue(out _)) { }
                Interlocked.Exchange(ref _queuedBytes, 0);

                // WASAPI/AudioGraph 在清空队列后，无需手动 DiscardAllFrames。
                // 停止送数后，底层会自动将已排队但未来得及播出的少量样本播完并静音。
            }
            DiagLog.Write("[AUDIO] 队列已清空");
        }

        /// <summary>播放工作线程：维持 AudioFrameInputNode 的缓冲水位，防止断流并保障低延迟。</summary>
        private void PlaybackLoop()
        {
            AudioDiagLog.Write("[PLAY] 播放线程启动");
            var lastStat = DateTime.UtcNow;
            var startTime = DateTime.UtcNow;
            bool noPcmWarned = false;

            while (_running && !_disposed)
            {
                bool didWork = false;
                AudioFrameInputNode? node = null;
                bool initialized = false;

                lock (_lock)
                {
                    initialized = _isInitialized;
                    node = _frameInputNode;
                }

                if (initialized && node != null)
                {
                    // 缓冲控制目标：在 node 内部保持大约 20ms - 40ms 的待播放样本
                    // 44100Hz 时，20ms ≈ 882 个样本，40ms ≈ 1764 个样本
                    // 如果 node 的排队样本小于 20ms 且队列中有数据，则继续填充，直到达到 40ms
                    while (_running && !_disposed && node.QueuedSampleCount < 1764)
                    {
                        if (_frameQueue.TryDequeue(out var pcmData))
                        {
                            Interlocked.Add(ref _queuedBytes, -pcmData.Length);
                            SubmitFrameToNode(node, pcmData);
                            didWork = true;

                            if (!_isPlaying)
                            {
                                _isPlaying = true;
                                AudioDiagLog.Write("[PLAY] 播放已启动!");
                            }
                        }
                        else
                        {
                            break; // 队列空，跳出
                        }
                    }
                }

                // 诊断：长时间未接收到 PCM
                if (!noPcmWarned && _enqueueCount == 0 && (DateTime.UtcNow - startTime).TotalSeconds >= 6)
                {
                    noPcmWarned = true;
                    AudioDiagLog.Write("==================================================================");
                    AudioDiagLog.Write("[诊断] 播放线程已运行，但至今未收到任何一帧 PCM 音频。");
                    AudioDiagLog.Write("==================================================================");
                }

                // 每 2 秒输出一次运行统计
                if (_isPlaying && (DateTime.UtcNow - lastStat).TotalSeconds >= 2)
                {
                    lastStat = DateTime.UtcNow;
                    uint queuedNodeSamples = node != null ? (uint)node.QueuedSampleCount : 0;
                    AudioDiagLog.Write($"[PLAY] WASAPI 健康度: 队列字节={Interlocked.Read(ref _queuedBytes)} 节点积压样本={queuedNodeSamples} 已播样本={Interlocked.Read(ref _totalSamplesPlayed)} 提交={_queueCount}");
                }

                if (!didWork)
                {
                    // 避免 CPU 占满，如果没有处理数据或者缓冲已经填满，休眠等待
                    _dataEvent.WaitOne(5);
                }
            }

            AudioDiagLog.Write("[PLAY] 播放线程退出");
        }

        private void SubmitFrameToNode(AudioFrameInputNode node, PcmData pcmData)
        {
            try
            {
                uint byteCount = (uint)pcmData.Length;
                var frame = new AudioFrame(byteCount);

                using (var audioBuffer = frame.LockBuffer(AudioBufferAccessMode.Write))
                using (var reference = audioBuffer.CreateReference())
                {
                    // 在 .NET 8 / CsWinRT 环境中，reference (IMemoryBufferReference) 无法直接转换为 IMemoryBufferByteAccess。
                    // 必须通过低级别 COM 的 QueryInterface 获取原生 IUnknown 指针来进行转换。
                    IntPtr pUnknown = Marshal.GetIUnknownForObject(reference);
                    try
                    {
                        Guid guid = new Guid("5B0D3235-4DBE-4DFA-8240-C7396FDE4115");
                        int hr = Marshal.QueryInterface(pUnknown, ref guid, out IntPtr pByteAccess);
                        if (hr == 0)
                        {
                            try
                            {
                                var byteAccess = (IMemoryBufferByteAccess)Marshal.GetObjectForIUnknown(pByteAccess);
                                unsafe
                                {
                                    byte* pBuffer;
                                    uint capacity;
                                    byteAccess.GetBuffer(out pBuffer, out capacity);

                                    int copyLen = Math.Min((int)capacity, pcmData.Length);
                                    Marshal.Copy(pcmData.Data, 0, (IntPtr)pBuffer, copyLen);
                                }
                            }
                            finally
                            {
                                Marshal.Release(pByteAccess);
                            }
                        }
                        else
                        {
                            throw new COMException("QueryInterface for IMemoryBufferByteAccess failed", hr);
                        }
                    }
                    finally
                    {
                        Marshal.Release(pUnknown);
                    }
                }

                int sampleCount = pcmData.Length / BLOCK_ALIGN;
                frame.Duration = TimeSpan.FromSeconds((double)sampleCount / SAMPLE_RATE);

                node.AddFrame(frame);

                Interlocked.Add(ref _totalSamplesPlayed, sampleCount);
                _queueCount++;

                if (_queueCount <= 5 || _queueCount % 500 == 0)
                    AudioDiagLog.Write($"[QUEUE] #{_queueCount}: 提交 WASAPI 缓冲 {pcmData.Length} 字节, 样本={sampleCount}");
            }
            catch (Exception ex)
            {
                AudioDiagLog.Write($"[QUEUE] 提交 WASAPI 缓冲异常: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _running = false;

            _dataEvent.Set();
            try { _playbackThread?.Join(1000); } catch { }
            _playbackThread = null;

            lock (_lock)
            {
                if (_frameInputNode != null)
                {
                    try
                    {
                        _frameInputNode.Dispose();
                    }
                    catch { }
                    _frameInputNode = null;
                }

                if (_audioGraph != null)
                {
                    try
                    {
                        _audioGraph.Stop();
                        _audioGraph.Dispose();
                    }
                    catch { }
                    _audioGraph = null;
                }
            }

            _dataEvent.Dispose();

            while (_frameQueue.TryDequeue(out _)) { }

            DiagLog.Write("[AUDIO] AudioSink 已释放");
        }
    }
}
