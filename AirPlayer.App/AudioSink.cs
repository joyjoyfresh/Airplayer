using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using AirPlayer.Protocol.Models.Audio;
using AirPlayer.Protocol.Utils;

namespace AirPlayer.App
{
    /// <summary>
    /// 音频播放器：接收解码后的 PCM 帧，通过 waveOut API 低延迟播放。
    /// 使用 8 个轮转缓冲区（每个约 23ms），轮询 WHDR_DONE 标志进行调度。
    /// </summary>
    public sealed class AudioSink : IDisposable
    {
        #region WinMM P/Invoke

        private const int  WAVE_MAPPER      = -1;       // 系统默认音频输出设备
        private const int  CALLBACK_NULL    = 0;        // 无回调（轮询模式）
        private const uint WHDR_DONE        = 0x00000001; // 缓冲区已播放完毕
        private const uint WHDR_PREPARED    = 0x00000002; // 缓冲区已 Prepare
        private const int  MMSYSERR_NOERROR = 0;         // 成功返回值

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;       // 格式标识：1 = PCM
            public ushort nChannels;        // 声道数
            public uint   nSamplesPerSec;   // 采样率（Hz）
            public uint   nAvgBytesPerSec;  // 平均字节率
            public ushort nBlockAlign;      // 块对齐（字节/帧）
            public ushort wBitsPerSample;   // 位深度
            public ushort cbSize;           // 扩展字节数（PCM 置 0）
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEHDR
        {
            public IntPtr lpData;           // 指向数据缓冲区
            public uint   dwBufferLength;   // 数据长度（字节）
            public uint   dwBytesRecorded;  // 录制字节数（播放时忽略）
            public IntPtr dwUser;           // 用户自定义数据
            public uint   dwFlags;          // 标志位（WHDR_DONE 等）
            public uint   dwLoops;          // 循环次数（0 = 不循环）
            public IntPtr lpNext;           // 内部链表指针（保留）
            public IntPtr reserved;         // 保留
        }

        [DllImport("winmm.dll")]
        private static extern int waveOutOpen(
            out IntPtr hWaveOut, int uDeviceID,
            ref WAVEFORMATEX lpFormat,
            IntPtr dwCallback, IntPtr dwInstance, int fdwOpen);

        [DllImport("winmm.dll")]
        private static extern int waveOutPrepareHeader(
            IntPtr hWaveOut, IntPtr lpWaveOutHdr, int uSize);

        [DllImport("winmm.dll")]
        private static extern int waveOutUnprepareHeader(
            IntPtr hWaveOut, IntPtr lpWaveOutHdr, int uSize);

        [DllImport("winmm.dll")]
        private static extern int waveOutWrite(
            IntPtr hWaveOut, IntPtr lpWaveOutHdr, int uSize);

        [DllImport("winmm.dll")]
        private static extern int waveOutReset(IntPtr hWaveOut);

        [DllImport("winmm.dll")]
        private static extern int waveOutClose(IntPtr hWaveOut);

        [DllImport("winmm.dll")]
        private static extern int waveOutGetNumDevs(); // 获取系统 waveOut 设备数量

        // waveOut 设备能力结构体，szPname 最多 31 个字符（截断）
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct WAVEOUTCAPS
        {
            public ushort wMid;            // 制造商 ID
            public ushort wPid;            // 产品 ID
            public uint   vDriverVersion;  // 驱动版本
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szPname;         // 设备名称（最多 31 字符）
            public uint   dwFormats;       // 支持的格式位掩码
            public ushort wChannels;       // 最大声道数
            public ushort wReserved1;      // 保留
            public uint   dwSupport;       // 功能标志
        }

        [DllImport("winmm.dll", CharSet = CharSet.Auto)]
        private static extern int waveOutGetDevCaps(
            int uDeviceID, ref WAVEOUTCAPS lpCaps, int cbCaps); // 获取指定索引设备的能力

        #endregion

        // ──────────────────────────── 音频参数 ────────────────────────────

        private const int  SAMPLE_RATE     = 44100; // 采样率（AirPlay 固定）
        private const int  CHANNELS        = 2;     // 立体声
        private const int  BITS_PER_SAMPLE = 16;    // 16-bit PCM
        private const int  BLOCK_ALIGN     = CHANNELS * BITS_PER_SAMPLE / 8; // 4 字节/帧

        // ──────────────────────────── 缓冲区配置 ──────────────────────────

        // 8 个轮转缓冲区，每个 4096 字节 ≈ 23ms；
        // AAC-ELD 每帧 1920 字节（480 样本），不足时补静音填满整个缓冲区
        private const int BUFFER_COUNT   = 8;    // 轮转缓冲区数量
        private const int BUFFER_BYTES   = 4096; // 每个缓冲区字节数（约 23ms）

        // ──────────────────────────── 原生资源 ────────────────────────────

        private IntPtr   _hWaveOut = IntPtr.Zero; // waveOut 设备句柄
        private readonly IntPtr[] _hdrPtrs  = new IntPtr[BUFFER_COUNT]; // WAVEHDR 原生内存指针
        private readonly IntPtr[] _dataPtrs = new IntPtr[BUFFER_COUNT]; // 数据区原生内存指针
        private int _nextBuf; // 下一个待写入的缓冲区槽位

        // ──────────────────────────── 队列与状态 ──────────────────────────

        private readonly ConcurrentQueue<PcmData> _frameQueue = new(); // 待播 PCM 队列
        private long _queuedBytes;  // 队列积压字节数
        // 最大积压约 150ms，超出则丢弃旧帧以保持实时性
        private const int MAX_QUEUED_BYTES = (int)(SAMPLE_RATE * BLOCK_ALIGN * 0.15);

        private Thread?          _playbackThread; // 播放工作线程
        private volatile bool    _running;        // 线程运行标志
        private bool             _initialized;    // 是否已成功初始化
        private bool             _disposed;       // 是否已释放
        private readonly AutoResetEvent _dataEvent = new(false); // 唤醒播放线程
        private readonly string? _preferredDeviceId; // 首选设备名称，Initialize() 时通过 waveOutGetDevCaps 匹配索引

        // ──────────────────────────── PTS 时钟 ────────────────────────────

        private ulong _basePts;           // 首帧 PTS（NTP 微秒）
        private bool  _basePtsSet;        // 是否已设置时间基准
        private long  _totalSamplesPlayed; // 累计已播样本数

        // ──────────────────────────── 诊断计数 ────────────────────────────

        private long _enqueueCount; // 入队帧计数
        private long _submitCount;  // 提交 waveOut 次数

        // ──────────────────────────── 音量增益 ────────────────────────────

        // 软件播放增益，[0,1]，1=原始音量；由 iOS 端音量调整经 SetVolume 设置（volatile 保证跨线程可见）
        private volatile float _gain = 1.0f;

        /// <summary>当前音频时钟（微秒，NTP epoch）— 供视频同步参考</summary>
        public ulong CurrentClock => _basePtsSet
            ? _basePts + (ulong)((double)Interlocked.Read(ref _totalSamplesPlayed) / SAMPLE_RATE * 1_000_000)
            : 0;

        /// <summary>音频是否已开始播放</summary>
        public bool IsPlaying { get; private set; }

        public AudioSink(string? deviceId = null)
        {
            _preferredDeviceId = deviceId; // 设备名称，Initialize 时按名称查找 waveOut 索引
        }

        /// <summary>初始化 waveOut 设备并分配缓冲区，启动播放线程。</summary>
        public void Initialize()
        {
            try
            {
                AudioDiagLog.Write("[INIT] 开始初始化 AudioSink (waveOut)...");

                // 按首选设备名称查找 waveOut 设备索引，找不到则回退到系统默认
                int deviceIndex = WAVE_MAPPER; // 默认使用系统默认设备
                if (!string.IsNullOrEmpty(_preferredDeviceId))
                {
                    int numDevs = waveOutGetNumDevs(); // 枚举所有 waveOut 设备
                    for (int i = 0; i < numDevs; i++)
                    {
                        var caps = new WAVEOUTCAPS();
                        if (waveOutGetDevCaps(i, ref caps, Marshal.SizeOf<WAVEOUTCAPS>()) == MMSYSERR_NOERROR)
                        {
                            // szPname 最多 31 字符，可能被截断，做前缀匹配
                            bool nameMatch =
                                _preferredDeviceId.StartsWith(caps.szPname, StringComparison.OrdinalIgnoreCase) ||
                                caps.szPname.StartsWith(
                                    _preferredDeviceId.Substring(0, Math.Min(_preferredDeviceId.Length, 31)),
                                    StringComparison.OrdinalIgnoreCase);
                            if (nameMatch)
                            {
                                deviceIndex = i; // 命中，使用该索引
                                AudioDiagLog.Write($"[INIT] 匹配到音频设备 [{i}]: {caps.szPname}");
                                break;
                            }
                        }
                    }
                    if (deviceIndex == WAVE_MAPPER)
                        AudioDiagLog.Write($"[INIT] 未找到设备 '{_preferredDeviceId}'，回退到系统默认");
                }

                // 打开 waveOut 设备（deviceIndex = WAVE_MAPPER 时使用系统默认）
                var fmt = new WAVEFORMATEX
                {
                    wFormatTag     = 1, // WAVE_FORMAT_PCM
                    nChannels      = CHANNELS,
                    nSamplesPerSec = SAMPLE_RATE,
                    nAvgBytesPerSec = (uint)(SAMPLE_RATE * BLOCK_ALIGN),
                    nBlockAlign    = BLOCK_ALIGN,
                    wBitsPerSample = BITS_PER_SAMPLE,
                    cbSize         = 0
                };

                int mm = waveOutOpen(out _hWaveOut, deviceIndex, ref fmt,
                                     IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL); // 使用匹配到的设备索引
                if (mm != MMSYSERR_NOERROR)
                {
                    AudioDiagLog.Write($"[INIT] waveOutOpen 失败: {mm}");
                    return;
                }

                // 分配原生内存：WAVEHDR + 数据区；初始化 WHDR_DONE 使槽位标记为"可用"
                int hdrSize = Marshal.SizeOf<WAVEHDR>();
                for (int i = 0; i < BUFFER_COUNT; i++)
                {
                    // 分配静音数据缓冲区
                    _dataPtrs[i] = Marshal.AllocHGlobal(BUFFER_BYTES);
                    ZeroMemory(_dataPtrs[i], BUFFER_BYTES);

                    // 分配并初始化 WAVEHDR
                    _hdrPtrs[i] = Marshal.AllocHGlobal(hdrSize);
                    var hdr = new WAVEHDR
                    {
                        lpData         = _dataPtrs[i],
                        dwBufferLength = (uint)BUFFER_BYTES,
                        dwFlags        = WHDR_DONE // 初始标记已完成，表示该槽位可立即使用
                    };
                    Marshal.StructureToPtr(hdr, _hdrPtrs[i], false);
                }

                _initialized = true;
                _running     = true;

                _playbackThread = new Thread(PlaybackLoop)
                {
                    IsBackground = true,
                    Name         = "AudioPlayback"
                };
                _playbackThread.Start();

                AudioDiagLog.Write("[INIT] waveOut AudioSink 初始化完成，播放线程已启动");
            }
            catch (Exception ex)
            {
                AudioDiagLog.Write($"[INIT] AudioSink 初始化异常: {ex}");
            }
        }

        /// <summary>投递一帧 PCM 数据到播放队列（线程安全）。</summary>
        public void EnqueueFrame(PcmData pcmData)
        {
            if (_disposed || pcmData == null || pcmData.Length <= 0) return;

            _enqueueCount++;

            // 首帧 PTS 作为时间基准
            if (!_basePtsSet && pcmData.Pts > 0)
            {
                _basePts    = pcmData.Pts;
                _basePtsSet = true;
            }

            _frameQueue.Enqueue(pcmData);
            Interlocked.Add(ref _queuedBytes, pcmData.Length);

            // 积压过多则丢弃最旧帧，保持音频贴近实时
            int dropped = 0;
            while (Interlocked.Read(ref _queuedBytes) > MAX_QUEUED_BYTES &&
                   _frameQueue.TryDequeue(out var old))
            {
                Interlocked.Add(ref _queuedBytes, -old.Length);
                dropped++;
            }
            if (dropped > 0 && (_enqueueCount <= 10 || _enqueueCount % 500 == 0))
                AudioDiagLog.Write($"[ENQ] #{_enqueueCount}: 丢弃 {dropped} 旧帧，保持实时");

            if (_enqueueCount <= 5)
                AudioDiagLog.Write($"[ENQ] #{_enqueueCount}: len={pcmData.Length} queued={Interlocked.Read(ref _queuedBytes)}");

            _dataEvent.Set(); // 唤醒播放线程
        }

        /// <summary>清空待播队列（SETUP/TEARDOWN 时调用）。</summary>
        public void Flush()
        {
            while (_frameQueue.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _queuedBytes, 0);
            DiagLog.Write("[AUDIO] 队列已清空");
        }

        /// <summary>设置播放音量（线程安全，可在网络线程调用）。</summary>
        /// <param name="airplayVolume">
        /// AirPlay 音量值，单位分贝(dB)：0 = 最大音量，约 -30 = 最小音量，-144 = 静音。
        /// 内部按 增益 = 10^(dB/20) 换算为 [0,1] 线性增益。
        /// </param>
        public void SetVolume(double airplayVolume)
        {
            double gain;
            if (airplayVolume <= -144.0) gain = 0.0;            // 协议约定的静音值
            else if (airplayVolume >= 0.0) gain = 1.0;          // 满音量（0 dB）
            else gain = Math.Pow(10.0, airplayVolume / 20.0);   // 分贝 → 线性增益
            _gain = (float)Math.Clamp(gain, 0.0, 1.0);          // 钳位到 [0,1]
            AudioDiagLog.Write($"[VOL] iOS 音量 {airplayVolume:0.##} dB → 增益 {_gain:0.###}");
        }

        /// <summary>按当前增益就地缩放 16-bit PCM 采样（小端）。</summary>
        private void ApplyGain(byte[] data, int length)
        {
            float g = _gain;
            if (g >= 0.999f) return;            // 接近原始音量：无需处理
            if (g <= 0.0001f)                   // 静音：直接清零
            {
                Array.Clear(data, 0, length);
                return;
            }
            // 逐个 16-bit 采样乘以增益并钳位，防止溢出
            for (int i = 0; i + 1 < length; i += 2)
            {
                short sample = (short)(data[i] | (data[i + 1] << 8)); // 小端还原有符号采样
                int scaled = (int)(sample * g);                       // 应用增益
                if (scaled > short.MaxValue) scaled = short.MaxValue;
                else if (scaled < short.MinValue) scaled = short.MinValue;
                data[i]     = (byte)(scaled & 0xFF);                  // 写回低字节
                data[i + 1] = (byte)((scaled >> 8) & 0xFF);           // 写回高字节
            }
        }

        // ──────────────────────────── 播放线程 ────────────────────────────

        /// <summary>
        /// 播放工作线程：从队列取 PCM 帧，写入轮转缓冲区，提交到 waveOut。
        /// 在提交下一帧前轮询 WHDR_DONE 确认缓冲区已播放完毕。
        /// </summary>
        private void PlaybackLoop()
        {
            AudioDiagLog.Write("[PLAY] 播放线程启动 (waveOut)");
            int hdrSize  = Marshal.SizeOf<WAVEHDR>();
            var lastStat = DateTime.UtcNow;
            int noPcmWarnSecs = 6;
            bool noPcmWarned = false;

            while (_running && !_disposed)
            {
                // ── 取帧 ──────────────────────────────────────────────────
                if (!_frameQueue.TryDequeue(out var pcmData))
                {
                    // 诊断：长时间无音频
                    if (!noPcmWarned && _enqueueCount == 0 &&
                        (DateTime.UtcNow - lastStat).TotalSeconds >= noPcmWarnSecs)
                    {
                        noPcmWarned = true;
                        AudioDiagLog.Write("==================================================================");
                        AudioDiagLog.Write("[诊断] 播放线程已运行，但至今未收到任何 PCM 音频帧。");
                        AudioDiagLog.Write("==================================================================");
                    }
                    _dataEvent.WaitOne(5);
                    continue;
                }
                Interlocked.Add(ref _queuedBytes, -pcmData.Length);

                if (!_initialized || _hWaveOut == IntPtr.Zero) continue;

                // ── 等待当前槽位的缓冲区被 waveOut 播放完毕 ──────────────
                int spinMs = 0;
                while (_running && !_disposed)
                {
                    var hdr = Marshal.PtrToStructure<WAVEHDR>(_hdrPtrs[_nextBuf]);
                    if ((hdr.dwFlags & WHDR_DONE) != 0) break; // 已完成，可重用
                    Thread.Sleep(1);
                    spinMs++;
                    if (spinMs % 100 == 0)
                        AudioDiagLog.Write($"[PLAY] 等待缓冲区槽 {_nextBuf} 释放（已等 {spinMs}ms）...");
                }
                if (!_running || _disposed) break;

                // ── 释放旧的 Prepare（如果有）────────────────────────────
                {
                    var hdr = Marshal.PtrToStructure<WAVEHDR>(_hdrPtrs[_nextBuf]);
                    if ((hdr.dwFlags & WHDR_PREPARED) != 0)
                        waveOutUnprepareHeader(_hWaveOut, _hdrPtrs[_nextBuf], hdrSize);
                }

                // ── 拷入 PCM 数据；不足 BUFFER_BYTES 的部分补静音 ─────────
                int copyLen = Math.Min(pcmData.Length, BUFFER_BYTES);
                ApplyGain(pcmData.Data, copyLen); // 按 iOS 端设置的音量增益缩放采样
                Marshal.Copy(pcmData.Data, 0, _dataPtrs[_nextBuf], copyLen);
                if (copyLen < BUFFER_BYTES)
                    ZeroMemory(_dataPtrs[_nextBuf] + copyLen, BUFFER_BYTES - copyLen);

                // ── 更新 WAVEHDR 并提交 ────────────────────────────────────
                var newHdr = new WAVEHDR
                {
                    lpData         = _dataPtrs[_nextBuf],
                    dwBufferLength = (uint)copyLen, // 只播实际有效字节
                    dwFlags        = 0
                };
                Marshal.StructureToPtr(newHdr, _hdrPtrs[_nextBuf], false);

                int pr = waveOutPrepareHeader(_hWaveOut, _hdrPtrs[_nextBuf], hdrSize);
                if (pr != MMSYSERR_NOERROR)
                {
                    AudioDiagLog.Write($"[PLAY] waveOutPrepareHeader 失败: {pr}");
                    _nextBuf = (_nextBuf + 1) % BUFFER_COUNT;
                    continue;
                }

                int wr = waveOutWrite(_hWaveOut, _hdrPtrs[_nextBuf], hdrSize);
                if (wr != MMSYSERR_NOERROR)
                {
                    AudioDiagLog.Write($"[PLAY] waveOutWrite 失败: {wr}");
                }
                else
                {
                    _submitCount++;
                    if (!IsPlaying)
                    {
                        IsPlaying = true;
                        AudioDiagLog.Write("[PLAY] 播放已启动！");
                    }
                    Interlocked.Add(ref _totalSamplesPlayed, copyLen / BLOCK_ALIGN);

                    if (_submitCount <= 5 || _submitCount % 500 == 0)
                        AudioDiagLog.Write($"[PLAY] #{_submitCount}: 提交 {copyLen} 字节 / 槽{_nextBuf} / 队列积压={Interlocked.Read(ref _queuedBytes)}");
                }

                // ── 每 2 秒输出一次健康统计 ───────────────────────────────
                if (IsPlaying && (DateTime.UtcNow - lastStat).TotalSeconds >= 2)
                {
                    lastStat = DateTime.UtcNow;
                    AudioDiagLog.Write($"[PLAY] 统计: 提交={_submitCount} 已播样本={Interlocked.Read(ref _totalSamplesPlayed)} 队列积压={Interlocked.Read(ref _queuedBytes)}");
                }

                _nextBuf = (_nextBuf + 1) % BUFFER_COUNT; // 移动到下一个槽位
            }

            AudioDiagLog.Write("[PLAY] 播放线程退出");
        }

        // ──────────────────────────── 释放 ────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _running  = false;

            _dataEvent.Set(); // 唤醒线程使其退出
            try { _playbackThread?.Join(1000); } catch { }

            if (_hWaveOut != IntPtr.Zero)
            {
                try { waveOutReset(_hWaveOut); } catch { } // 停止所有挂起缓冲区

                int hdrSize = Marshal.SizeOf<WAVEHDR>();
                for (int i = 0; i < BUFFER_COUNT; i++)
                {
                    if (_hdrPtrs[i] != IntPtr.Zero)
                    {
                        try
                        {
                            // 仅在已 Prepare 的情况下 Unprepare
                            var hdr = Marshal.PtrToStructure<WAVEHDR>(_hdrPtrs[i]);
                            if ((hdr.dwFlags & WHDR_PREPARED) != 0)
                                waveOutUnprepareHeader(_hWaveOut, _hdrPtrs[i], hdrSize);
                        }
                        catch { }
                        Marshal.FreeHGlobal(_hdrPtrs[i]); // 释放 WAVEHDR 内存
                        _hdrPtrs[i] = IntPtr.Zero;
                    }
                    if (_dataPtrs[i] != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(_dataPtrs[i]); // 释放数据缓冲区内存
                        _dataPtrs[i] = IntPtr.Zero;
                    }
                }

                try { waveOutClose(_hWaveOut); } catch { }
                _hWaveOut = IntPtr.Zero;
            }

            _dataEvent.Dispose();
            while (_frameQueue.TryDequeue(out _)) { } // 清空剩余队列

            DiagLog.Write("[AUDIO] AudioSink (waveOut) 已释放");
        }

        // ──────────────────────────── 工具 ────────────────────────────────

        /// <summary>将原生内存区域清零（补静音）。</summary>
        private static unsafe void ZeroMemory(IntPtr ptr, int length)
        {
            var span = new Span<byte>(ptr.ToPointer(), length); // 创建到原生内存的 Span
            span.Clear();                                        // 全部清零（写入静音）
        }
    }
}
