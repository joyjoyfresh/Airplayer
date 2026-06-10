using System;
using System.Runtime.InteropServices;
using AirPlayer.Protocol.Utils;
using Vortice.MediaFoundation;
using SharpGen.Runtime;

namespace AirPlayer.App.Rendering
{
    /// <summary>
    /// 基于 Media Foundation 的 H264 解码器：输入 Annex-B 帧，输出 BGRA 像素。
    /// 采用「喂一帧、取一帧」的低延迟方式，不做时钟调度。
    /// </summary>
    public sealed class H264Decoder : IDisposable
    {
        // Microsoft H264 解码器 MFT 的 CLSID
        private static readonly Guid CLSID_CMSH264DecoderMFT = new Guid("62CE7E72-4C71-4D20-B15D-452831A87D9D");

        // ProcessOutput 返回「需要更多输入」的 HRESULT
        private const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
        // ProcessOutput 返回「输出格式变化」的 HRESULT
        private const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);

        private IMFTransform? _decoder;     // 解码器 MFT
        private int _width;                 // 显示宽度
        private int _height;                // 显示高度
        private byte[]? _nv12Buffer;        // NV12 临时缓冲
        private byte[]? _bgraBuffer;        // BGRA 输出缓冲
        private bool _started;              // 是否已配置并开始

        /// <summary>构造时启动 Media Foundation</summary>
        public H264Decoder()
        {
            MediaFactory.MFStartup(); // 初始化 MF 运行时
        }

        /// <summary>外部在收到首帧或分辨率变化时设置帧尺寸</summary>
        public void SetFrameSize(int width, int height)
        {
            if (width > 0) _width = width;
            if (height > 0) _height = height;
        }

        /// <summary>用首帧分辨率配置解码器（仅一次）</summary>
        private void EnsureStarted(int width, int height)
        {
            if (_started) return;

            // 通过 CLSID 创建系统 H264 解码器 MFT
            _decoder = ComObject.As<IMFTransform>(CreateComObject(CLSID_CMSH264DecoderMFT));

            // 配置输入类型：H264
            var inType = MediaFactory.MFCreateMediaType();
            inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            inType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            inType.Set(MediaTypeAttributeKeys.FrameSize, PackLong(width, height));
            _decoder!.SetInputType(0, inType, 0);

            // 配置输出类型：NV12
            var outType = MediaFactory.MFCreateMediaType();
            outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            outType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            _decoder.SetOutputType(0, outType, 0);

            // 开始流（第二参为 nuint）
            _decoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            _decoder.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

            _width = width;
            _height = height;
            _started = true;
            DiagLog.Write($"[DEC] H264 解码器已初始化 {width}x{height}");
        }

        /// <summary>
        /// 解码一帧 Annex-B 数据。成功解出图像时返回 true，并通过 out 参数给出 BGRA 数据与尺寸。
        /// </summary>
        public bool TryDecode(byte[] annexB, out byte[] bgra, out int width, out int height)
        {
            bgra = Array.Empty<byte>();

            EnsureStarted(_width <= 0 ? 1920 : _width, _height <= 0 ? 1080 : _height);
            width = _width;
            height = _height;

            // 构造输入样本并送入
            var inputSample = CreateSampleFromBytes(annexB);
            try
            {
                _decoder!.ProcessInput(0, inputSample, 0);
            }
            catch (SharpGenException ex)
            {
                DiagLog.Write($"[DEC] ProcessInput 异常: 0x{ex.ResultCode.Code:X8}");
                return false;
            }
            finally
            {
                inputSample.Dispose();
            }

            bool produced = false;

            // 预估输出样本大小（按对齐到 16 的尺寸计算 NV12，富余 4KB）
            int codedW = (_width + 15) & ~15;
            int codedH = (_height + 15) & ~15;
            int outSize = codedW * codedH * 3 / 2 + 4096;

            while (true)
            {
                // 自行分配输出样本（MS H264 解码器不自带输出样本）
                var outBuffer = MediaFactory.MFCreateMemoryBuffer(outSize);
                var outSample = MediaFactory.MFCreateSample();
                outSample.AddBuffer(outBuffer);
                outBuffer.Dispose();

                var dataBuffer = new OutputDataBuffer
                {
                    StreamID = 0,
                    Sample = outSample
                };

                Result result = _decoder!.ProcessOutput(ProcessOutputFlags.None, 1, ref dataBuffer, out ProcessOutputStatus _);

                if (result.Code == MF_E_TRANSFORM_NEED_MORE_INPUT)
                {
                    outSample.Dispose();
                    break; // 这一帧暂时没有可输出图像
                }
                if (result.Code == MF_E_TRANSFORM_STREAM_CHANGE)
                {
                    outSample.Dispose();
                    HandleStreamChange();
                    continue;
                }
                if (result.Failure)
                {
                    DiagLog.Write($"[DEC] ProcessOutput 失败: 0x{result.Code:X8}");
                    outSample.Dispose();
                    break;
                }

                // 成功取到一帧 NV12
                ExtractNv12(dataBuffer.Sample, out bgra, out width, out height);
                produced = true;
                dataBuffer.Sample.Dispose();
                // 继续循环，取走可能缓存的其它帧（保留最后一帧）
            }

            return produced;
        }

        /// <summary>处理输出格式变化：重新设置 NV12 输出类型</summary>
        private void HandleStreamChange()
        {
            var newOut = MediaFactory.MFCreateMediaType();
            newOut.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            newOut.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            _decoder!.SetOutputType(0, newOut, 0);
            DiagLog.Write("[DEC] 输出格式变化，已重设 NV12 输出类型");
        }

        /// <summary>从 NV12 输出样本提取像素并转换为 BGRA</summary>
        private void ExtractNv12(IMFSample sample, out byte[] bgra, out int width, out int height)
        {
            width = _width;
            height = _height;

            using var buffer = sample.ConvertToContiguousBuffer();
            IntPtr data = buffer.Lock(out int _, out int curLen);
            try
            {
                if (_nv12Buffer == null || _nv12Buffer.Length < curLen)
                    _nv12Buffer = new byte[curLen];
                Marshal.Copy(data, _nv12Buffer, 0, curLen);
            }
            finally
            {
                buffer.Unlock();
            }

            // 由缓冲总长度反推行跨距：curLen = stride * codedHeight * 3/2
            int codedHeight = (height + 15) & ~15;
            int stride = (int)((long)curLen * 2 / (3L * codedHeight));
            if (stride < width) stride = width;

            int pixels = width * height;
            // 必须正好等于 宽×高×4，CanvasBitmap.CreateFromBytes 会校验长度
            if (_bgraBuffer == null || _bgraBuffer.Length != pixels * 4)
                _bgraBuffer = new byte[pixels * 4];

            Nv12ToBgra(_nv12Buffer!, stride, codedHeight, width, height, _bgraBuffer!);
            bgra = _bgraBuffer!;
        }

        /// <summary>NV12 → BGRA（BT.601），CPU 转换</summary>
        private static void Nv12ToBgra(byte[] nv12, int stride, int codedHeight, int width, int height, byte[] bgra)
        {
            int uvStart = stride * codedHeight;  // UV 平面起始（完整 Y 平面之后）
            for (int y = 0; y < height; y++)
            {
                int yRow = y * stride;
                int uvRow = uvStart + (y >> 1) * stride;
                int outRow = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int yy = nv12[yRow + x] & 0xFF;
                    int uvCol = x & ~1;
                    int u = (nv12[uvRow + uvCol] & 0xFF) - 128;
                    int v = (nv12[uvRow + uvCol + 1] & 0xFF) - 128;

                    int c = yy - 16;
                    int r = (298 * c + 409 * v + 128) >> 8;
                    int g = (298 * c - 100 * u - 208 * v + 128) >> 8;
                    int b = (298 * c + 516 * u + 128) >> 8;

                    int o = outRow + x * 4;
                    bgra[o + 0] = ClampByte(b);
                    bgra[o + 1] = ClampByte(g);
                    bgra[o + 2] = ClampByte(r);
                    bgra[o + 3] = 255;
                }
            }
        }

        private static byte ClampByte(int v) => (byte)(v < 0 ? 0 : (v > 255 ? 255 : v));

        /// <summary>把 (width,height) 打包为 MF_MT_FRAME_SIZE 的 64 位值</summary>
        private static long PackLong(int high, int low) => ((long)high << 32) | (uint)low;

        /// <summary>用字节数组创建 IMFSample</summary>
        private static IMFSample CreateSampleFromBytes(byte[] data)
        {
            var buffer = MediaFactory.MFCreateMemoryBuffer(data.Length);
            IntPtr ptr = buffer.Lock(out int _, out int _);
            Marshal.Copy(data, 0, ptr, data.Length);
            buffer.Unlock();
            buffer.CurrentLength = data.Length;

            var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            buffer.Dispose();
            return sample;
        }

        /// <summary>通过 CLSID 创建未托管 COM 对象并返回其 ComObject 包装</summary>
        private static ComObject CreateComObject(Guid clsid)
        {
            Type t = Type.GetTypeFromCLSID(clsid, throwOnError: true)!;
            object obj = Activator.CreateInstance(t)!;
            IntPtr unk = Marshal.GetIUnknownForObject(obj);
            return new ComObject(unk);
        }

        public void Dispose()
        {
            try
            {
                _decoder?.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
                _decoder?.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
            }
            catch { }
            _decoder?.Dispose();
            _decoder = null;
            try { MediaFactory.MFShutdown(); } catch { }
        }
    }
}
