using System;
using System.IO;
using System.Runtime.InteropServices;
using Vortice.MediaFoundation;
using AirPlayer.Protocol.Utils;

namespace AirPlayer.App
{
    /// <summary>
    /// MP4 录像器：接收原始 H.264 视频帧（Passthrough）与解码后 PCM 音频（AAC 实时编码）并封装为 MP4。
    /// 底层利用 Windows Media Foundation IMFSinkWriter，提供高画质与低系统消耗。
    /// </summary>
    public sealed class Mp4Recorder : IDisposable
    {
        private IMFSinkWriter? _sinkWriter;
        private int _videoStreamIndex = -1;
        private int _audioStreamIndex = -1;
        private readonly object _writerLock = new object();
        private bool _isWriting;
        private bool _isDisposed;

        /// <summary>当前是否正在进行录制</summary>
        public bool IsWriting => _isWriting;

        /// <summary>
        /// 启动 MP4 录像
        /// </summary>
        /// <param name="filePath">输出 MP4 文件的绝对路径</param>
        /// <param name="width">视频宽度</param>
        /// <param name="height">视频高度</param>
        public void Start(string filePath, int width, int height)
        {
            lock (_writerLock)
            {
                if (_isWriting) return;

                try
                {
                    // 确保目标文件夹存在
                    string? dir = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    // 1. 初始化 MF 环境 (MFStartup 是有引用计数的，配对使用)
                    MediaFactory.MFStartup();

                    // 2. 创建 SinkWriter
                    _sinkWriter = MediaFactory.MFCreateSinkWriterFromURL(filePath, null, null);

                    // 3. 配置视频输出类型 (H.264)
                    var videoOutType = MediaFactory.MFCreateMediaType();
                    videoOutType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                    videoOutType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
                    videoOutType.Set(MediaTypeAttributeKeys.FrameSize, PackLong(width, height));
                    videoOutType.Set(MediaTypeAttributeKeys.FrameRate, PackLong(30, 1)); // 标称 30fps
                    videoOutType.Set(MediaTypeAttributeKeys.PixelAspectRatio, PackLong(1, 1));
                    
                    _videoStreamIndex = _sinkWriter.AddStream(videoOutType);

                    // 4. 配置视频输入类型 (H.264 Passthrough，无重编码开销)
                    var videoInType = MediaFactory.MFCreateMediaType();
                    videoInType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                    videoInType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
                    videoInType.Set(MediaTypeAttributeKeys.FrameSize, PackLong(width, height));
                    
                    _sinkWriter.SetInputMediaType(_videoStreamIndex, videoInType, null);

                    // 5. 配置音频输出类型 (AAC-LC, 约128kbps)
                    var audioOutType = MediaFactory.MFCreateMediaType();
                    audioOutType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                    audioOutType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
                    audioOutType.Set(MediaTypeAttributeKeys.AudioNumChannels, 2);
                    audioOutType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, 44100);
                    audioOutType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16);
                    audioOutType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, 16000); // 16000 B/s = 128 kbps

                    _audioStreamIndex = _sinkWriter.AddStream(audioOutType);

                    // 6. 配置音频输入类型 (16-bit Stereo PCM 44100Hz)
                    var audioInType = MediaFactory.MFCreateMediaType();
                    audioInType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                    audioInType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
                    audioInType.Set(MediaTypeAttributeKeys.AudioNumChannels, 2);
                    audioInType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, 44100);
                    audioInType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16);
                    audioInType.Set(MediaTypeAttributeKeys.AudioBlockAlignment, 4); // 2 channels * 2 bytes
                    audioInType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, 44100 * 4);

                    _sinkWriter.SetInputMediaType(_audioStreamIndex, audioInType, null);

                    // 7. 开始写入
                    _sinkWriter.BeginWriting();
                    _isWriting = true;

                    DiagLog.Write($"[REC] 录屏启动: 尺寸={width}x{height}, 路径={filePath}");
                }
                catch (Exception ex)
                {
                    DiagLog.Write($"[REC][错误] 启动录制失败: {ex.Message}");
                    Cleanup();
                    throw;
                }
            }
        }

        /// <summary>
        /// 写入一帧视频裸流 (H.264 NAL)
        /// </summary>
        /// <param name="data">H.264 数据包</param>
        /// <param name="length">有效数据长度</param>
        /// <param name="ptsMicroseconds">微秒时间戳</param>
        /// <param name="isKeyframe">是否为关键帧</param>
        public void WriteVideoFrame(byte[] data, int length, long ptsMicroseconds, bool isKeyframe)
        {
            if (!_isWriting || _sinkWriter == null || _videoStreamIndex == -1) return;

            lock (_writerLock)
            {
                if (!_isWriting) return;

                IMFSample? sample = null;
                try
                {
                    // 将微秒转换为 Media Foundation 时间单位（100 纳秒 = 10^-7 秒，即微秒 * 10）
                    long pts100ns = ptsMicroseconds * 10;
                    sample = CreateSample(data, length, pts100ns);

                    if (isKeyframe)
                    {
                        sample.Set(SampleAttributeKeys.CleanPoint, true);
                    }

                    _sinkWriter.WriteSample(_videoStreamIndex, sample);
                }
                catch (Exception ex)
                {
                    DiagLog.Write($"[REC][错误] 视频帧写入失败: {ex.Message}");
                }
                finally
                {
                    if (sample != null)
                    {
                        Marshal.ReleaseComObject(sample);
                    }
                }
            }
        }

        /// <summary>
        /// 写入一帧音频 PCM 数据，MF 自动编码为 AAC 封装进轨道
        /// </summary>
        /// <param name="data">PCM 字节流</param>
        /// <param name="length">有效字节长度</param>
        /// <param name="ptsMicroseconds">微秒时间戳</param>
        public void WriteAudioFrame(byte[] data, int length, long ptsMicroseconds)
        {
            if (!_isWriting || _sinkWriter == null || _audioStreamIndex == -1) return;

            lock (_writerLock)
            {
                if (!_isWriting) return;

                IMFSample? sample = null;
                try
                {
                    long pts100ns = ptsMicroseconds * 10;
                    sample = CreateSample(data, length, pts100ns);

                    _sinkWriter.WriteSample(_audioStreamIndex, sample);
                }
                catch (Exception ex)
                {
                    DiagLog.Write($"[REC][错误] 音频帧写入失败: {ex.Message}");
                }
                finally
                {
                    if (sample != null)
                    {
                        Marshal.ReleaseComObject(sample);
                    }
                }
            }
        }

        /// <summary>
        /// 停止录制并保存 MP4 文件
        /// </summary>
        public void Stop()
        {
            lock (_writerLock)
            {
                if (!_isWriting) return;

                try
                {
                    _sinkWriter?.Finalize();
                    DiagLog.Write("[REC] 录屏结束，MP4 文件封装完成");
                }
                catch (Exception ex)
                {
                    DiagLog.Write($"[REC][错误] 结束录像写入异常: {ex.Message}");
                }
                finally
                {
                    Cleanup();
                }
            }
        }

        private IMFSample CreateSample(byte[] data, int length, long pts100ns)
        {
            IMFMediaBuffer? buffer = null;
            IMFSample? sample = null;
            try
            {
                buffer = MediaFactory.MFCreateMemoryBuffer(length);

                buffer.Lock(out IntPtr ptr, out int maxLen, out int currentLen);
                try
                {
                    Marshal.Copy(data, 0, ptr, length);
                }
                finally
                {
                    buffer.Unlock();
                }
                buffer.CurrentLength = length;

                sample = MediaFactory.MFCreateSample();
                sample.AddBuffer(buffer);
                sample.SampleTime = pts100ns;

                return sample;
            }
            catch
            {
                if (sample != null) Marshal.ReleaseComObject(sample);
                if (buffer != null) Marshal.ReleaseComObject(buffer);
                throw;
            }
            finally
            {
                if (buffer != null)
                {
                    Marshal.ReleaseComObject(buffer);
                }
            }
        }

        private static long PackLong(int high, int low) => ((long)high << 32) | (uint)low;

        private void Cleanup()
        {
            _isWriting = false;

            if (_sinkWriter != null)
            {
                Marshal.ReleaseComObject(_sinkWriter);
                _sinkWriter = null;
            }

            _videoStreamIndex = -1;
            _audioStreamIndex = -1;

            try
            {
                MediaFactory.MFShutdown();
            }
            catch { /* 忽略 */ }
        }

        public void Dispose()
        {
            lock (_writerLock)
            {
                if (_isDisposed) return;
                Stop();
                _isDisposed = true;
            }
        }
    }
}
