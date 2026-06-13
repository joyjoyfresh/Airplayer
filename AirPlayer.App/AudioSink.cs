using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using AirPlayer.Protocol.Models.Audio;
using AirPlayer.Protocol.Utils;

namespace AirPlayer.App
{
    /// <summary>
    /// 音频播放器：接收解码后的 PCM 帧，通过 Windows waveOut API 低延迟播放。
    /// 设计要点：
    ///  1. 用 CALLBACK_EVENT + 专用工作线程回收/续填缓冲，避免在 waveOut 回调里调用
    ///     waveOut 函数导致的死锁（旧实现的隐患）。
    ///  2. 有界、丢旧的待播队列，使音频保持“贴近实时”，与视频（始终呈现最新帧）天然对齐，
    ///     实现音画同步。
    /// </summary>
    public sealed class AudioSink : IDisposable
    {
        // PCM 参数（AirPlay 固定 44100Hz, 2ch, 16-bit）
        private const int SAMPLE_RATE = 44100;
        private const int CHANNELS = 2;
        private const int BITS_PER_SAMPLE = 16;
        private const int BLOCK_ALIGN = CHANNELS * BITS_PER_SAMPLE / 8; // 4 字节/帧

        // waveOut 常量
        private const int CALLBACK_EVENT = 0x50000;   // 用事件方式通知缓冲完成
        private const int WAVE_FORMAT_PCM = 1;
        private const int MMSYSERR_NOERROR = 0;
        private const uint WHDR_DONE = 0x00000001;     // 缓冲已播放完成
        private const uint WHDR_PREPARED = 0x00000002; // 缓冲已 Prepare

        // 缓冲区数量和大小（4×1024 样本 ≈ 93ms，吸收抖动又不过度增加延迟）
        private const int NUM_BUFFERS = 4;
        private const int BUFFER_SIZE_SAMPLES = 1024;
        private const int BUFFER_SIZE_BYTES = BUFFER_SIZE_SAMPLES * BLOCK_ALIGN;

        // 待播队列最大积压字节数（≈150ms）；超出则丢弃最旧帧以保持音画同步
        private const int MAX_QUEUED_BYTES = (int)(SAMPLE_RATE * BLOCK_ALIGN * 0.15);

        // ── P/Invoke 声明（WAVEHDR 用 IntPtr 传递，便于持久化非托管头部）────────
        [DllImport("winmm.dll")]
        private static extern int waveOutOpen(out IntPtr hWaveOut, int uDeviceID, ref WAVEFORMATEX lpFormat, IntPtr dwCallback, IntPtr dwInstance, int dwFlags);

        [DllImport("winmm.dll")]
        private static extern int waveOutClose(IntPtr hWaveOut);

        [DllImport("winmm.dll")]
        private static extern int waveOutPrepareHeader(IntPtr hWaveOut, IntPtr lpWaveHdr, int uSize);

        [DllImport("winmm.dll")]
        private static extern int waveOutUnprepareHeader(IntPtr hWaveOut, IntPtr lpWaveHdr, int uSize);

        [DllImport("winmm.dll")]
        private static extern int waveOutWrite(IntPtr hWaveOut, IntPtr lpWaveHdr, int uSize);

        [DllImport("winmm.dll")]
        private static extern int waveOutReset(IntPtr hWaveOut);

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        // 实例状态
        private IntPtr _hWaveOut;
        private bool _disposed;
        private volatile bool _running;
        private bool _isPlaying;

        // 缓冲：托管数据缓冲（固定）+ 非托管 WAVEHDR（生命周期内地址稳定）
        private readonly GCHandle[] _bufferHandles = new GCHandle[NUM_BUFFERS];
        private readonly byte[][] _buffers = new byte[NUM_BUFFERS][];
        private readonly IntPtr[] _hdrPtr = new IntPtr[NUM_BUFFERS];
        private readonly bool[] _bufferInUse = new bool[NUM_BUFFERS];
        private readonly object _lock = new();
        private int _hdrSize;

        // 待播队列与积压计数
        private readonly ConcurrentQueue<PcmData> _frameQueue = new();
        private long _queuedBytes;

        // 同步与唤醒事件
        private AutoResetEvent? _waveEvent;   // waveOut 缓冲完成时由驱动置位
        private AutoResetEvent? _dataEvent;   // 新帧到达时置位
        private Thread? _playbackThread;

        // PTS 时钟（供需要时做音画同步参考，非必须）
        private ulong _basePts;
        private bool _basePtsSet;
        private long _totalSamplesPlayed;

        /// <summary>当前音频时钟（微秒，NTP epoch）— 供视频同步参考</summary>
        public ulong CurrentClock => _basePtsSet
            ? _basePts + (ulong)((double)Interlocked.Read(ref _totalSamplesPlayed) / SAMPLE_RATE * 1_000_000)
            : 0;

        /// <summary>音频是否已开始播放</summary>
        public bool IsPlaying => _isPlaying;

        /// <summary>初始化音频播放器并启动播放线程。</summary>
        public void Initialize()
        {
            try
            {
                AudioDiagLog.Write("[INIT] 开始初始化 AudioSink (CALLBACK_EVENT)...");

                _hdrSize = Marshal.SizeOf<WAVEHDR>();

                // 波形格式：L16 PCM, 44100Hz, 2ch
                var format = new WAVEFORMATEX
                {
                    wFormatTag = WAVE_FORMAT_PCM,
                    nChannels = CHANNELS,
                    nSamplesPerSec = SAMPLE_RATE,
                    nAvgBytesPerSec = (uint)(SAMPLE_RATE * BLOCK_ALIGN),
                    nBlockAlign = BLOCK_ALIGN,
                    wBitsPerSample = BITS_PER_SAMPLE,
                    cbSize = 0
                };

                // 分配数据缓冲（固定）与非托管头部
                for (int i = 0; i < NUM_BUFFERS; i++)
                {
                    _buffers[i] = new byte[BUFFER_SIZE_BYTES];
                    _bufferHandles[i] = GCHandle.Alloc(_buffers[i], GCHandleType.Pinned);
                    _hdrPtr[i] = Marshal.AllocHGlobal(_hdrSize);
                }

                // 完成通知事件
                _waveEvent = new AutoResetEvent(false);
                _dataEvent = new AutoResetEvent(false);

                // 以事件回调方式打开（dwCallback = 事件句柄）
                IntPtr evtHandle = _waveEvent.SafeWaitHandle.DangerousGetHandle();
                int result = waveOutOpen(out _hWaveOut, -1 /* WAVE_MAPPER */, ref format, evtHandle, IntPtr.Zero, CALLBACK_EVENT);
                AudioDiagLog.Write($"[INIT] waveOutOpen 结果: {result}, hWaveOut=0x{_hWaveOut.ToInt64():X}");
                if (result != MMSYSERR_NOERROR)
                {
                    AudioDiagLog.Write($"[INIT] waveOutOpen 失败! 错误码: {result}");
                    return;
                }

                // 启动播放工作线程
                _running = true;
                _playbackThread = new Thread(PlaybackLoop)
                {
                    IsBackground = true,
                    Name = "AudioPlayback"
                };
                _playbackThread.Start();

                AudioDiagLog.Write("[INIT] AudioSink 初始化完成");
            }
            catch (Exception ex)
            {
                AudioDiagLog.Write($"[INIT] AudioSink 初始化异常: {ex}");
            }
        }

        private long _enqueueCount;

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

            // 唤醒播放线程去填充空闲缓冲
            _dataEvent?.Set();
        }

        /// <summary>清空缓冲队列（Seek / Flush 时调用）</summary>
        public void Flush()
        {
            lock (_lock)
            {
                while (_frameQueue.TryDequeue(out _)) { }
                Interlocked.Exchange(ref _queuedBytes, 0);
                _partial = null; _partialOffset = 0; _partialEnd = 0;

                if (_hWaveOut != IntPtr.Zero)
                {
                    waveOutReset(_hWaveOut); // 停止并标记所有缓冲为完成
                    for (int i = 0; i < NUM_BUFFERS; i++)
                    {
                        if (_bufferInUse[i])
                        {
                            UnprepareIfNeeded(i);
                            _bufferInUse[i] = false;
                        }
                    }
                }
            }
            DiagLog.Write("[AUDIO] 队列已清空");
        }

        /// <summary>播放工作线程：回收已完成缓冲 + 续填空闲缓冲。</summary>
        private void PlaybackLoop()
        {
            AudioDiagLog.Write("[PLAY] 播放线程启动");
            var waits = new WaitHandle[] { _waveEvent!, _dataEvent! };
            var lastStat = DateTime.UtcNow;
            var startTime = DateTime.UtcNow;
            bool noPcmWarned = false;

            while (_running && !_disposed)
            {
                bool didWork;
                int inUse;
                lock (_lock)
                {
                    didWork = RecycleCompleted();   // 回收播完的缓冲
                    didWork |= FillFreeBuffers();    // 用队列数据续填空闲缓冲
                    inUse = 0;
                    for (int i = 0; i < NUM_BUFFERS; i++) if (_bufferInUse[i]) inUse++;
                }

                // 关键诊断：运行 6 秒仍没收到任何 PCM → 解码器没出声，把原因直接写在这个日志里
                if (!noPcmWarned && _enqueueCount == 0 && (DateTime.UtcNow - startTime).TotalSeconds >= 6)
                {
                    noPcmWarned = true;
                    AudioDiagLog.Write("==================================================================");
                    AudioDiagLog.Write("[诊断] 播放线程已运行，但至今未收到任何一帧 PCM 音频。");
                    AudioDiagLog.Write("[诊断] 说明问题不在播放器，而在【解码器】：投屏音频(AAC-ELD)没被解出来。");
                    AudioDiagLog.Write("[诊断] 详细原因请看另一个日志文件 airplay-video.log 里的 [FDK]/[DEC] 行。");
                    AudioDiagLog.Write("[诊断] 最常见原因：缺少 fdk-aac.dll（程序会尝试自动下载，失败则需手动放置）。");
                    AudioDiagLog.Write("==================================================================");
                }

                // 每 2 秒打一条播放健康度：队列字节、在用缓冲、已播样本
                if (_isPlaying && (DateTime.UtcNow - lastStat).TotalSeconds >= 2)
                {
                    lastStat = DateTime.UtcNow;
                    AudioDiagLog.Write($"[PLAY] 健康度: 队列字节={Interlocked.Read(ref _queuedBytes)} 在用缓冲={inUse}/{NUM_BUFFERS} 已播样本={Interlocked.Read(ref _totalSamplesPlayed)} 提交={_queueCount}");
                }

                if (!didWork)
                {
                    // 无事可做时等待：缓冲完成或新数据到达，最多等 10ms 兜底
                    WaitHandle.WaitAny(waits, 10);
                }
            }

            AudioDiagLog.Write("[PLAY] 播放线程退出");
        }

        /// <summary>回收所有已置 WHDR_DONE 的缓冲（须在 _lock 内调用）。</summary>
        private bool RecycleCompleted()
        {
            bool any = false;
            if (_hWaveOut == IntPtr.Zero) return false;

            for (int i = 0; i < NUM_BUFFERS; i++)
            {
                if (!_bufferInUse[i]) continue;

                var hdr = Marshal.PtrToStructure<WAVEHDR>(_hdrPtr[i]);
                if ((hdr.dwFlags & WHDR_DONE) != 0)
                {
                    // 统计已播放样本，推进音频时钟
                    Interlocked.Add(ref _totalSamplesPlayed, hdr.dwBufferLength / BLOCK_ALIGN);
                    UnprepareIfNeeded(i);
                    _bufferInUse[i] = false;
                    any = true;
                }
            }
            return any;
        }

        /// <summary>用队列中的 PCM 填充并提交所有空闲缓冲（须在 _lock 内调用）。</summary>
        private bool FillFreeBuffers()
        {
            if (_disposed || _hWaveOut == IntPtr.Zero) return false;

            bool any = false;
            for (int i = 0; i < NUM_BUFFERS; i++)
            {
                if (_bufferInUse[i]) continue;

                int bytesFilled = FillBuffer(_buffers[i]);
                if (bytesFilled <= 0) break; // 队列空，停止

                if (QueueBuffer(i, bytesFilled))
                {
                    any = true;
                    if (!_isPlaying)
                    {
                        _isPlaying = true;
                        AudioDiagLog.Write("[PLAY] 播放已启动!");
                    }
                }
            }
            return any;
        }

        private long _fillCount;
        // 跨缓冲边界时未拷完的残留帧数据（仅工作线程在 _lock 内访问，保证顺序）
        private byte[]? _partial;
        private int _partialOffset;
        private int _partialEnd;   // 残留数据的有效结束位置（帧 Data 可能大于有效长度）

        /// <summary>从残留帧 + 帧队列取数据填满一个缓冲；返回填充字节数（严格保序）。</summary>
        private int FillBuffer(byte[] buffer)
        {
            int offset = 0;

            // 先消费上次跨界的残留数据
            if (_partial != null)
            {
                int avail = _partialEnd - _partialOffset;
                int copy = Math.Min(avail, buffer.Length - offset);
                Array.Copy(_partial, _partialOffset, buffer, offset, copy);
                offset += copy;
                _partialOffset += copy;
                if (_partialOffset >= _partialEnd) { _partial = null; _partialOffset = 0; }
            }

            // 再从队列取整帧
            while (offset < buffer.Length && _frameQueue.TryDequeue(out var pcmData))
            {
                Interlocked.Add(ref _queuedBytes, -pcmData.Length);

                int bytesToCopy = Math.Min(pcmData.Length, buffer.Length - offset);
                Array.Copy(pcmData.Data, 0, buffer, offset, bytesToCopy);
                offset += bytesToCopy;

                // 帧未拷完：把余下部分留作残留，下个缓冲优先消费（保持顺序）
                if (bytesToCopy < pcmData.Length)
                {
                    _partial = pcmData.Data;
                    _partialOffset = bytesToCopy;
                    _partialEnd = pcmData.Length;
                    break;
                }
            }

            _fillCount++;
            if (offset > 0 && (_fillCount <= 5 || _fillCount % 500 == 0))
                AudioDiagLog.Write($"[FILL] #{_fillCount}: 填充 {offset} 字节, 队列剩余 {_frameQueue.Count}");
            return offset;
        }

        private long _queueCount;

        /// <summary>Prepare + Write 一个缓冲到 waveOut。</summary>
        private bool QueueBuffer(int i, int byteCount)
        {
            if (_hWaveOut == IntPtr.Zero) return false;

            var hdr = new WAVEHDR
            {
                lpData = _bufferHandles[i].AddrOfPinnedObject(),
                dwBufferLength = (uint)byteCount,
                dwBytesRecorded = 0,
                dwUser = IntPtr.Zero,
                dwFlags = 0,
                dwLoops = 0,
                lpNext = IntPtr.Zero,
                reserved = IntPtr.Zero
            };
            Marshal.StructureToPtr(hdr, _hdrPtr[i], false);

            int prep = waveOutPrepareHeader(_hWaveOut, _hdrPtr[i], _hdrSize);
            if (prep != MMSYSERR_NOERROR)
            {
                AudioDiagLog.Write($"[QUEUE] waveOutPrepareHeader 失败: {prep}");
                return false;
            }

            _bufferInUse[i] = true;
            int wr = waveOutWrite(_hWaveOut, _hdrPtr[i], _hdrSize);
            if (wr != MMSYSERR_NOERROR)
            {
                AudioDiagLog.Write($"[QUEUE] waveOutWrite 失败: {wr}");
                _bufferInUse[i] = false;
                waveOutUnprepareHeader(_hWaveOut, _hdrPtr[i], _hdrSize);
                return false;
            }

            _queueCount++;
            if (_queueCount <= 5 || _queueCount % 500 == 0)
                AudioDiagLog.Write($"[QUEUE] #{_queueCount}: 提交缓冲[{i}] {byteCount} 字节");
            return true;
        }

        /// <summary>若头部已 Prepare 则 Unprepare（避免句柄泄漏）。</summary>
        private void UnprepareIfNeeded(int i)
        {
            if (_hWaveOut == IntPtr.Zero) return;
            var hdr = Marshal.PtrToStructure<WAVEHDR>(_hdrPtr[i]);
            if ((hdr.dwFlags & WHDR_PREPARED) != 0)
                waveOutUnprepareHeader(_hWaveOut, _hdrPtr[i], _hdrSize);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _running = false;

            // 唤醒并结束播放线程
            _dataEvent?.Set();
        