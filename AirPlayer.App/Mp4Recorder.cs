using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using AirPlayer.Protocol.Utils;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace AirPlayer.App
{
    /// <summary>
    /// 投屏录制器：把解码后的画面（NV12，经 MF 硬件 H264 编码器重编码）与 PCM 音频
    /// （经内置 AAC 编码器编码）通过 Media Foundation SinkWriter 混流写入 MP4。
    /// 重编码而非直通：编码器在起始处自插 IDR，可在任意时刻立即开录，无需等待 iOS 关键帧。
    /// 视频帧由渲染线程读回 NV12 投递，音频帧由网络线程投递，统一进单一后台写入线程。
    /// </summary>
    public sealed class Mp4Recorder : IDisposable
    {
        // 启用硬件 MFT（让 SinkWriter 优先选用硬件 H264 编码器；无则回退软件编码器）
        private static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new Guid("A634A91C-822B-41B9-A494-4DE4643612B0");
        private static readonly Guid MF_SINK_WRITER_DISABLE_THROTTLING       = new Guid("08B845D8-2B74-4AFE-9D53-BE16D2D5AE4F");

        // 媒体类型属性 GUID（用规范的 MFSetAttributeSize/Ratio 设置，确保 UINT64 打包正确）
        private static readonly Guid MF_MT_FRAME_SIZE          = new Guid("1652C33D-D6B2-4012-B834-72030849A37D");
        private static readonly Guid MF_MT_FRAME_RATE          = new Guid("C459A2E8-3D2C-4E44-B132-FEE5156C7BB0");
        private static readonly Guid MF_MT_PIXEL_ASPECT_RATIO  = new Guid("C6376A1E-8D0A-4027-BE45-6D9A0AD39BB6");

        // 音频固定参数（与 AirPlay 解码输出一致）
        private const int AudioSampleRate    = 44100; // 采样率
        private const int AudioChannels      = 2;     // 立体声
        private const int AudioBitsPerSample = 16;    // 位深
        private const int AudioBlockAlign    = AudioChannels * AudioBitsPerSample / 8; // 4 字节/帧

        // 一条待写样本（视频或音频）
        private sealed class RecSample
        {
            public bool   IsVideo;   // true=视频，false=音频
            public byte[] Data = Array.Empty<byte>(); // 已拷贝的样本字节（视频=NV12，音频=PCM）
            public long   TimeTicks; // 样本时间戳（100ns 单位）
            public long   DurTicks;  // 样本时长（100ns 单位）
        }

        // 有界队列：磁盘/编码慢时丢弃新样本而非无限堆积撑爆内存
        private readonly BlockingCollection<RecSample> _queue =
            new BlockingCollection<RecSample>(boundedCapacity: 300);

        private readonly Thread _writerThread; // 后台写入线程
        private readonly Stopwatch _sw = new Stopwatch(); // 录制时基

        // MF SinkWriter 与流索引（在写入线程内、收到首个视频帧后惰性创建）
        private IMFSinkWriter? _writer;
        private int _videoStream = -1;
        private int _audioStream = -1;
        private bool _writerReady;     // SinkWriter 是否已创建并 BeginWriting
        private bool _gotVideoFrame;   // 是否已收到首个视频帧（音频在此之后才写）
        private bool _faulted;         // 写入发生致命错误，停止后续写入

        private long _videoFrameDurTicks; // 视频帧时长估算（相邻帧间隔）

        // 视频编码尺寸（取自首个 NV12 帧，录制期间固定）
        private int _width;
        private int _height;
        private readonly int _fps;

        // 音频时间线（按累计样本数推进，保证连续无缝）
        private long _audioBaseTicks = -1;
        private long _audioSamples;

        // 全局零点：首个视频样本的时刻，所有样本减去它使文件从 0 开始
        private long _baseTicks = -1;

        private readonly string _path;     // 输出文件路径
        private volatile bool _stopping;   // 已请求停止
        private bool _disposed;
        private int _videoFrameCount;      // 已投递视频帧计数（诊断）

        /// <summary>是否正在录制（已创建未停止）。</summary>
        public bool IsRecording => !_stopping && !_disposed;

        /// <summary>输出文件路径。</summary>
        public string FilePath => _path;

        /// <param name="filePath">输出 MP4 路径</param>
        /// <param name="fps">录制帧率（用于流元数据与帧时长上限）</param>
        public Mp4Recorder(string filePath, int fps)
        {
            _path = filePath;
            _fps = fps > 0 ? fps : 30;
            MediaFactory.MFStartup(); // 引用计数，与解码器各自配对 MFShutdown
            _sw.Start();
            _writerThread = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "Mp4Recorder"
            };
            _writerThread.Start();
            DiagLog.Write($"[REC] 录制器已创建，输出 {_path}");
        }

        // ──────────────────────────────────────────────────────────────────
        // 投递接口
        // ──────────────────────────────────────────────────────────────────

        /// <summary>投递一帧解码后的 NV12 画面（由渲染线程读回，连续打包 stride=width）。</summary>
        /// <param name="nv12">连续 NV12 字节：Y 平面 width*height，紧接 UV 平面 width*height/2</param>
        public void WriteVideoNv12(byte[] nv12, int width, int height)
        {
            if (_stopping || _disposed || nv12 == null || width <= 0 || height <= 0) return;

            // 录制期间编码尺寸固定：尺寸突变（如旋转）的帧直接丢弃，避免编码器报错
            if (_gotVideoFrame && (width != _width || height != _height)) return;
            if (!_gotVideoFrame) { _width = width; _height = height; _gotVideoFrame = true; }

            long now = _sw.Elapsed.Ticks;
            long dur = _videoFrameDurTicks > 0 ? _videoFrameDurTicks : 0;

            // 调用方已为本帧分配独立缓冲，这里直接持有引用入队（不再二次拷贝）
            _queue.TryAdd(new RecSample { IsVideo = true, Data = nv12, TimeTicks = now, DurTicks = dur });
            _videoFrameCount++;
        }

        /// <summary>投递一帧 PCM（44100Hz/立体声/16bit 小端）。立即拷贝字节以避免与播放线程的增益处理竞争。</summary>
        public void WriteAudio(byte[] pcm, int length)
        {
            if (_stopping || _disposed || pcm == null || length <= 0) return;
            if (!_gotVideoFrame) return; // 视频流建好前不写音频

            int len = Math.Min(length, pcm.Length);
            int samples = len / AudioBlockAlign;
            if (samples <= 0) return;

            long arrival = _sw.Elapsed.Ticks;
            if (_audioBaseTicks < 0) _audioBaseTicks = arrival;

            long ts  = _audioBaseTicks + _audioSamples * 10_000_000L / AudioSampleRate;
            long dur = (long)samples * 10_000_000L / AudioSampleRate;
            _audioSamples += samples;

            var copy = new byte[len];
            Buffer.BlockCopy(pcm, 0, copy, 0, len);
            _queue.TryAdd(new RecSample { IsVideo = false, Data = copy, TimeTicks = ts, DurTicks = dur });
        }

        // ──────────────────────────────────────────────────────────────────
        // 写入线程
        // ──────────────────────────────────────────────────────────────────

        private void WriterLoop()
        {
            try
            {
                long lastVideoTicks = -1;
                foreach (var s in _queue.GetConsumingEnumerable())
                {
                    if (_faulted) continue; // 已出错则仅排空队列

                    try
                    {
                        if (s.IsVideo)
                        {
                            if (!_writerReady) InitWriter();
                            if (!_writerReady) continue;

                            long dur = s.DurTicks;
                            if (lastVideoTicks >= 0)
                            {
                                long delta = s.TimeTicks - lastVideoTicks;
                                if (delta > 0) { dur = delta; _videoFrameDurTicks = delta; }
                            }
                            if (dur <= 0) dur = 10_000_000L / _fps;
                            lastVideoTicks = s.TimeTicks;

                            if (_baseTicks < 0) _baseTicks = s.TimeTicks;
                            long vts = Math.Max(0, s.TimeTicks - _baseTicks);
                            WriteToSink(_videoStream, s.Data, vts, dur);
                        }
                        else
                        {
                            if (!_writerReady || _baseTicks < 0) continue;
                            long ats = Math.Max(0, s.TimeTicks - _baseTicks);
                            WriteToSink(_audioStream, s.Data, ats, s.DurTicks);
                        }
                    }
                    catch (Exception ex)
                    {
                        _faulted = true;
                        DiagLog.Write($"[REC] 写入样本异常，终止录制: {ex.Message}");
                    }
                }

                FinalizeWriter();
            }
            catch (Exception ex)
            {
                DiagLog.Write($"[REC] 写入线程异常: {ex}");
            }
        }

        /// <summary>收到首个视频帧后创建 SinkWriter：视频 NV12→H264（硬件优先），音频 PCM→AAC。</summary>
        private void InitWriter()
        {
            string phase = "start";
            try
            {
                // 创建参数：允许硬件 MFT，并关闭节流以贴近实时
                phase = "MFCreateAttributes";
                var attrs = MediaFactory.MFCreateAttributes(2);
                attrs.Set(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1u);
                attrs.Set(MF_SINK_WRITER_DISABLE_THROTTLING, 1u);

                phase = "MFCreateSinkWriterFromURL";
                _writer = MediaFactory.MFCreateSinkWriterFromURL(_path, null, attrs);

                // ── 视频输出类型（写入文件）：H264 ─────────────────────────
                var vOut = MediaFactory.MFCreateMediaType();
                vOut.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                vOut.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
                MediaFactory.MFSetAttributeSize(vOut, MF_MT_FRAME_SIZE, (uint)_width, (uint)_height);
                MediaFactory.MFSetAttributeRatio(vOut, MF_MT_FRAME_RATE, (uint)_fps, 1u);
                MediaFactory.MFSetAttributeRatio(vOut, MF_MT_PIXEL_ASPECT_RATIO, 1u, 1u);
                vOut.Set(MediaTypeAttributeKeys.AvgBitrate, EstimateBitrate());
                vOut.Set(MediaTypeAttributeKeys.InterlaceMode, 2); // 2 = Progressive
                phase = "AddStream(video H264)";
                _videoStream = _writer.AddStream(vOut);

                // ── 视频输入类型：NV12 ───────────────────────────────────
                var vIn = MediaFactory.MFCreateMediaType();
                vIn.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                vIn.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
                MediaFactory.MFSetAttributeSize(vIn, MF_MT_FRAME_SIZE, (uint)_width, (uint)_height);
                MediaFactory.MFSetAttributeRatio(vIn, MF_MT_FRAME_RATE, (uint)_fps, 1u);
                MediaFactory.MFSetAttributeRatio(vIn, MF_MT_PIXEL_ASPECT_RATIO, 1u, 1u);
                vIn.Set(MediaTypeAttributeKeys.InterlaceMode, 2);
                vIn.Set(MediaTypeAttributeKeys.DefaultStride, _width); // 连续打包，行宽=width
                phase = "SetInputMediaType(video NV12)";
                _writer.SetInputMediaType(_videoStream, vIn, null);

                // ── 音频输出类型：AAC ────────────────────────────────────
                var aOut = MediaFactory.MFCreateMediaType();
                aOut.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                aOut.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
                aOut.Set(MediaTypeAttributeKeys.AudioBitsPerSample, AudioBitsPerSample);
                aOut.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, AudioSampleRate);
                aOut.Set(MediaTypeAttributeKeys.AudioNumChannels, AudioChannels);
                aOut.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, 16000); // 128 kbps
                aOut.Set(MediaTypeAttributeKeys.AacPayloadType, 0);
                aOut.Set(MediaTypeAttributeKeys.AacAudioProfileLevelIndication, 0x29); // AAC-LC
                phase = "AddStream(audio AAC)";
                _audioStream = _writer.AddStream(aOut);

                // ── 音频输入类型：PCM ────────────────────────────────────
                var aIn = MediaFactory.MFCreateMediaType();
                aIn.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                aIn.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
                aIn.Set(MediaTypeAttributeKeys.AudioBitsPerSample, AudioBitsPerSample);
                aIn.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, AudioSampleRate);
                aIn.Set(MediaTypeAttributeKeys.AudioNumChannels, AudioChannels);
                aIn.Set(MediaTypeAttributeKeys.AudioBlockAlignment, AudioBlockAlign);
                aIn.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, AudioSampleRate * AudioBlockAlign);
                phase = "SetInputMediaType(audio PCM)";
                _writer.SetInputMediaType(_audioStream, aIn, null);

                phase = "BeginWriting";
                _writer.BeginWriting();
                _writerReady = true;
                DiagLog.Write($"[REC] SinkWriter 就绪 {_width}x{_height}@{_fps} videoStream={_videoStream} audioStream={_audioStream}");
            }
            catch (Exception ex)
            {
                _faulted = true;
                int hr = ex is SharpGenException se ? se.ResultCode.Code : ex.HResult;
                DiagLog.Write($"[REC] SinkWriter 初始化失败 阶段=[{phase}] 0x{hr:X8} {_width}x{_height}@{_fps} 码率={EstimateBitrate()}: {ex.Message}");
            }
        }

        /// <summary>按分辨率/帧率粗估目标码率（H264，约 0.07 bit/像素/帧）。</summary>
        private int EstimateBitrate()
        {
            long bps = (long)(_width * _height * _fps * 0.07);
            return (int)Math.Clamp(bps, 4_000_000, 40_000_000);
        }

        /// <summary>构造 IMFSample 并写入指定流。</summary>
        private void WriteToSink(int stream, byte[] data, long timeTicks, long durTicks)
        {
            var buffer = MediaFactory.MFCreateMemoryBuffer(data.Length);
            buffer.Lock(out IntPtr ptr, out _, out _);
            Marshal.Copy(data, 0, ptr, data.Length);
            buffer.Unlock();
            buffer.CurrentLength = data.Length;

            var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            buffer.Dispose();

            sample.SampleTime = timeTicks;
            sample.SampleDuration = durTicks;

            _writer!.WriteSample(stream, sample);
            sample.Dispose();
        }

        /// <summary>正常收尾：Finalize 写出索引并释放。</summary>
        private void FinalizeWriter()
        {
            try
            {
                if (_writerReady && _writer != null && !_faulted)
                {
                    _writer.Finalize();
                    DiagLog.Write($"[REC] 录制完成已保存: {_path}（视频帧 {_videoFrameCount}）");
                }
                else
                {
                    DiagLog.Write($"[REC] 未写出有效文件 writerReady={_writerReady} faulted={_faulted} 视频帧={_videoFrameCount}");
                }
            }
            catch (Exception ex)
            {
                DiagLog.Write($"[REC] Finalize 失败: {ex.Message}");
            }
            finally
            {
                _writer?.Dispose();
                _writer = null;
                try { MediaFactory.MFShutdown(); } catch { }
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // 停止 / 释放
        // ──────────────────────────────────────────────────────────────────

        /// <summary>请求停止录制，等待队列排空与文件收尾。</summary>
        public void Stop()
        {
            if (_stopping) return;
            _stopping = true;
            _queue.CompleteAdding();
            try { _writerThread.Join(8000); } catch { }
            DiagLog.Write("[REC] 录制已停止");
        }

        public void Dispose()
        {
            if (_disposed) return;
            Stop();
            _disposed = true;
            try { _queue.Dispose(); } catch { }
        }
    }
}
