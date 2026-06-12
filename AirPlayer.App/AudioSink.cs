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
    /// 使用音频 PTS 为主时钟，实现音视频同步。
    /// </summary>
    public sealed class AudioSink : IDisposable
    {
        // PCM 参数（AirPlay 固定 44100Hz, 2ch, 16-bit）
        private const int SAMPLE_RATE = 44100;
        private const int CHANNELS = 2;
        private const int BITS_PER_SAMPLE = 16;
        private const int BLOCK_ALIGN = CHANNELS * BITS_PER_SAMPLE / 8; // 4

        // waveOut 常量
        private const int CALLBACK_FUNCTION = 0x30000;
        private const int WAVE_FORMAT_PCM = 1;
        private const int WHDR_DONE = 0x00000001;
        private const int MMSYSERR_NOERROR = 0;

        // 缓冲区数量和大小
        private const int NUM_BUFFERS = 4;
        private const int BUFFER_SIZE_SAMPLES = 1024; // 每个缓冲区的样本数
        private const int BUFFER_SIZE_BYTES = BUFFER_SIZE_SAMPLES * BLOCK_ALIGN;

        // P/Invoke 声明
        [DllImport("winmm.dll")]
        private static extern int waveOutOpen(out IntPtr hWaveOut, int uDeviceID, ref WAVEFORMATEX lpFormat, IntPtr dwCallback, IntPtr dwInstance, int dwFlags);

        [DllImport("winmm.dll")]
        private static extern int waveOutClose(IntPtr hWaveOut);

        [DllImport("winmm.dll")]
        private static extern int waveOutPrepareHeader(IntPtr hWaveOut, ref WAVEHDR lpWaveHdr, int uSize);

        [DllImport("winmm.dll")]
        private static extern int waveOutUnprepareHeader(IntPtr hWaveOut, ref WAVEHDR lpWaveHdr, int uSize);

        [DllImport("winmm.dll")]
        private static extern int waveOutWrite(IntPtr hWaveOut, ref WAVEHDR lpWaveHdr, int uSize);

        [DllImport("winmm.dll")]
        private static extern int waveOutReset(IntPtr hWaveOut);

        [DllImport("winmm.dll")]
        private static extern int waveOutPause(IntPtr hWaveOut);

        [DllImport("winmm.dll")]
        private static extern int waveOutRestart(IntPtr hWaveOut);

        [DllImport("winmm.dll")]
        private static extern int waveOutGetPosition(IntPtr hWaveOut, ref MMTIME lpMMTime, int uSize);

        // 回调委托
        private delegate void WaveOutCallback(IntPtr hWaveOut, int uMsg, IntPtr dwInstance, ref WAVEHDR wParam, ref WAVEHDR lParam);
        private static WaveOutCallback? _callbackDelegate; // 防止 GC 回收

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
            public uint reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MMTIME
        {
            public uint wType;
            public uint u;
        }

        // 实例状态
        private IntPtr _hWaveOut;
        private bool _disposed;
        private bool _isPlaying;
        private GCHandle[] _bufferHandles = new GCHandle[NUM_BUFFERS];
        private byte[][] _buffers = new byte[NUM_BUFFERS][];
        private bool[] _bufferInUse = new bool[NUM_BUFFERS];
        private readonly object _lock = new();

        // 缓冲队列
        private readonly ConcurrentQueue<PcmData> _frameQueue = new();

        // PTS 时钟
        private ulong _basePts;
        private bool _basePtsSet;
        private long _totalSamplesPlayed;

        /// <summary>当前音频时钟（微秒，NTP epoch）— 供视频同步使用</summary>
        public ulong CurrentClock => _basePtsSet
            ? _basePts + (ulong)((double)Interlocked.Read(ref _totalSamplesPlayed) / SAMPLE_RATE * 1_000_000)
            : 0;

        /// <summary>音频是否已开始播放</summary>
        public bool IsPlaying => _isPlaying;

        /// <summary>
        /// 初始化音频播放器。
        /// </summary>
        public void Initialize()
        {
            try
            {
                AudioDiagLog.Write("[INIT] 开始初始化 AudioSink...");

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

                // 先分配缓冲区，再打开 waveOut（避免回调在缓冲区分配前触发）
                for (int i = 0; i < NUM_BUFFERS; i++)
                {
                    _buffers[i] = new byte[BUFFER_SIZE_BYTES];
                    _bufferHandles[i] = GCHandle.Alloc(_buffers[i], GCHandleType.Pinned);
                }

                // 创建回调委托并固定
                _callbackDelegate = WaveOutCallbackHandler;
                IntPtr callbackPtr = Marshal.GetFunctionPointerForDelegate(_callbackDelegate);
                AudioDiagLog.Write($"[INIT] 回调指针: 0x{callbackPtr.ToInt64():X}");

                int result = waveOutOpen(out _hWaveOut, -1 /* WAVE_MAPPER */, ref format, callbackPtr, IntPtr.Zero, CALLBACK_FUNCTION);
                AudioDiagLog.Write($"[INIT] waveOutOpen 结果: {result}, hWaveOut=0x{_hWaveOut.ToInt64():X}");
                if (result != MMSYSERR_NOERROR)
                {
                    AudioDiagLog.Write($"[INIT] waveOutOpen 失败! 错误码: {result}");
                    return;
                }

                AudioDiagLog.Write("[INIT] AudioSink 初始化完成 (waveOut)");
            }
            catch (Exception ex)
            {
                AudioDiagLog.Write($"[INIT] AudioSink 初始化异常: {ex}");
            }
        }

        /// <summary>
        /// 投递一帧 PCM 数据到播放队列。
        /// 从协议层线程调用，线程安全。
        /// </summary>
        public void EnqueueFrame(PcmData pcmData)
        {
            if (_disposed || pcmData.Length <= 0) return;

            _enqueueCount++;
            if (_enqueueCount <= 5 || _enqueueCount % 1000 == 0)
                AudioDiagLog.Write($"[ENQ] #{_enqueueCount}: len={pcmData.Length} pts={pcmData.Pts} queue={_frameQueue.Count} hWaveOut=0x{_hWaveOut.ToInt64():X} playing={_isPlaying}");

            // 设置时间基准（首帧 PTS）
            if (!_basePtsSet && pcmData.Pts > 0)
            {
                _basePts = pcmData.Pts;
                _basePtsSet = true;
                AudioDiagLog.Write($"[ENQ] 基准时钟设置: pts={pcmData.Pts}");
            }

            _frameQueue.Enqueue(pcmData);

            // 首帧到达时启动播放
            if (!_isPlaying && _hWaveOut != IntPtr.Zero)
            {
                lock (_lock)
                {
                    if (!_isPlaying)
                    {
                        _isPlaying = true;
                        AudioDiagLog.Write("[ENQ] 播放已启动!");
                        // 开始填充缓冲区
                        FillAndQueueBuffers();
                    }
                }
            }
            else if (!_isPlaying && _hWaveOut == IntPtr.Zero)
            {
                if (_enqueueCount <= 5)
                    AudioDiagLog.Write($"[ENQ] 等待 waveOut 初始化... hWaveOut=0x{_hWaveOut.ToInt64():X}");
            }
        }

        private long _enqueueCount;

        /// <summary>清空缓冲队列（Seek / Flush 时调用）</summary>
        public void Flush()
        {
            while (_frameQueue.TryDequeue(out _)) { }
            if (_hWaveOut != IntPtr.Zero)
            {
                waveOutReset(_hWaveOut);
                lock (_lock)
                {
                    Array.Fill(_bufferInUse, false);
                }
            }
            DiagLog.Write("[AUDIO] 队列已清空");
        }

        /// <summary>从队列中取出 PCM 数据，填充缓冲区并提交到 waveOut</summary>
        private void FillAndQueueBuffers()
        {
            if (_disposed || _hWaveOut == IntPtr.Zero) return;

            for (int i = 0; i < NUM_BUFFERS; i++)
            {
                if (!_bufferInUse[i])
                {
                    int bytesFilled = FillBuffer(_buffers[i]);
                    if (bytesFilled > 0)
                    {
                        QueueBuffer(i, bytesFilled);
                    }
                }
            }
        }

        /// <summary>从帧队列中取出数据填充一个缓冲区</summary>
        private int FillBuffer(byte[] buffer)
        {
            int offset = 0;
            int framesRead = 0;

            while (offset < buffer.Length && _frameQueue.TryDequeue(out var pcmData))
            {
                framesRead++;
                int bytesToCopy = Math.Min(pcmData.Length, buffer.Length - offset);
                Array.Copy(pcmData.Data, 0, buffer, offset, bytesToCopy);
                offset += bytesToCopy;

                // 如果 pcmData 有剩余数据，放回队列（创建新的 PcmData）
                if (bytesToCopy < pcmData.Length)
                {
                    var remaining = new PcmData
                    {
                        Data = new byte[pcmData.Length - bytesToCopy],
                        Length = pcmData.Length - bytesToCopy,
                        Pts = pcmData.Pts
                    };
                    Array.Copy(pcmData.Data, bytesToCopy, remaining.Data, 0, remaining.Length);
                    _frameQueue.Enqueue(remaining);
                }
            }

            _fillCount++;
            if (offset > 0 && (_fillCount <= 5 || _fillCount % 500 == 0))
                AudioDiagLog.Write($"[FILL] #{_fillCount}: 填充 {offset} 字节, {framesRead} 帧, 队列剩余 {_frameQueue.Count}");

            return offset;
        }

        private long _fillCount;

        /// <summary>将填充好的缓冲区提交到 waveOut</summary>
        private void QueueBuffer(int bufferIndex, int byteCount)
        {
            if (_hWaveOut == IntPtr.Zero) return;

            var hdr = new WAVEHDR
            {
                lpData = _bufferHandles[bufferIndex].AddrOfPinnedObject(),
                dwBufferLength = (uint)byteCount,
                dwBytesRecorded = 0,
                dwUser = IntPtr.Zero,
                dwFlags = 0,
                dwLoops = 0,
                lpNext = IntPtr.Zero,
                reserved = 0
            };

            int prepareResult = waveOutPrepareHeader(_hWaveOut, ref hdr, Marshal.SizeOf<WAVEHDR>());
            if (prepareResult != MMSYSERR_NOERROR)
            {
                AudioDiagLog.Write($"[QUEUE] waveOutPrepareHeader 失败: {prepareResult}");
                return;
            }

            _bufferInUse[bufferIndex] = true;

            int writeResult = waveOutWrite(_hWaveOut, ref hdr, Marshal.SizeOf<WAVEHDR>());
            if (writeResult != MMSYSERR_NOERROR)
            {
                AudioDiagLog.Write($"[QUEUE] waveOutWrite 失败: {writeResult}");
                _bufferInUse[bufferIndex] = false;
                waveOutUnprepareHeader(_hWaveOut, ref hdr, Marshal.SizeOf<WAVEHDR>());
            }
            else
            {
                _queueCount++;
                if (_queueCount <= 5 || _queueCount % 500 == 0)
                    AudioDiagLog.Write($"[QUEUE] #{_queueCount}: 提交缓冲区[{bufferIndex}] {byteCount} 字节");
            }
        }

        private long _queueCount;

        /// <summary>waveOut 回调：缓冲区播放完成时触发</summary>
        private void WaveOutCallbackHandler(IntPtr hWaveOut, int uMsg, IntPtr dwInstance, ref WAVEHDR wParam, ref WAVEHDR lParam)
        {
            _callbackCount++;

            if (uMsg == 0x3C00 /* WOM_DONE */)
            {
                // 找到对应的缓冲区索引
                for (int i = 0; i < NUM_BUFFERS; i++)
                {
                    if (_bufferHandles[i].IsAllocated && _bufferHandles[i].AddrOfPinnedObject() == wParam.lpData)
                    {
                        waveOutUnprepareHeader(hWaveOut, ref wParam, Marshal.SizeOf<WAVEHDR>());
                        _bufferInUse[i] = false;

                        // 更新已播放样本计数
                        Interlocked.Add(ref _totalSamplesPlayed, wParam.dwBytesRecorded / BLOCK_ALIGN);

                        // 填充并提交下一个缓冲区
                        if (!_disposed && _isPlaying)
                        {
                            FillAndQueueBuffers();
                        }
                        break;
                    }
                }
            }

            if (_callbackCount <= 5 || _callbackCount % 500 == 0)
                AudioDiagLog.Write($"[CB] #{_callbackCount}: uMsg=0x{uMsg:X4}");
        }

        private long _callbackCount;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_hWaveOut != IntPtr.Zero)
            {
                waveOutReset(_hWaveOut);

                for (int i = 0; i < NUM_BUFFERS; i++)
                {
                    if (_bufferHandles[i].IsAllocated)
                        _bufferHandles[i].Free();
                }

                waveOutClose(_hWaveOut);
                _hWaveOut = IntPtr.Zero;
            }

            while (_frameQueue.TryDequeue(out _)) { }

            DiagLog.Write("[AUDIO] AudioSink 已释放");
        }
    }
}
