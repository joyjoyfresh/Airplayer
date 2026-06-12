using System;
using System.Runtime.CompilerServices;
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
        private int _outputBufferSize;      // 解码器声明的输出缓冲大小
        private bool _started;              // 是否已创建输入端并开始收流
        private bool _outputTypeSet;        // 是否已完成输出类型协商

        // 缓存 DrainOutputWithoutExtract 的输出缓冲，减少 GC 压力
        private IMFMediaBuffer? _drainOutBuffer;
        private IMFSample? _drainOutSample;

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

            // 通过 CLSID 创建系统 H264 解码器 MFT（显式 QueryInterface 到 IMFTransform）
            _decoder = CreateDecoder(CLSID_CMSH264DecoderMFT);
            DiagLog.Write("[DEC] MFT 实例已创建");

            // 配置输入类型：H264；输出类型必须等 SPS/IDR 喂入后再由 MFT 报告
            var inType = MediaFactory.MFCreateMediaType();
            inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            inType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            inType.Set(MediaTypeAttributeKeys.FrameSize, PackLong(width, height));
            _decoder!.SetInputType(0, inType, 0);
            DiagLog.Write("[DEC] 输入类型已设置");

            _width = width;
            _height = height;

            // 开始流（第二参为 nuint）
            _decoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            _decoder.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

            _started = true;
            _outputTypeSet = false;
            _outputBufferSize = Math.Max(width * height * 3 / 2 + 4096, 4096);
            DiagLog.Write($"[DEC] H264 解码器输入端已初始化 {width}x{height}");
        }

        /// <summary>
        /// 仅喂入一帧 Annex-B 数据到解码器（不做 ProcessOutput，不做 NV12→BGRA 转换）。
        /// 用于中间帧的轻量级解码，保持参考帧链完整。
        /// </summary>
        public void FeedFrame(byte[] annexB)
        {
            EnsureStarted(_width <= 0 ? 1920 : _width, _height <= 0 ? 1080 : _height);

            var inputSample = CreateSampleFromBytes(annexB);
            try
            {
                _decoder!.ProcessInput(0, inputSample, 0);
            }
            catch (SharpGenException ex)
            {
                DiagLog.Write($"[DEC] FeedFrame ProcessInput 异常: 0x{ex.ResultCode.Code:X8}");
            }
            finally
            {
                inputSample.Dispose();
            }

            // 首次喂入后尝试协商输出类型
            if (!_outputTypeSet)
            {
                TrySelectNv12OutputType();
            }

            // 排空解码器内部缓存的输出帧（丢弃，不做昂贵的 NV12→BGRA 转换）
            DrainOutputWithoutExtract();
        }

        /// <summary>排空解码器输出队列，丢弃中间帧不做像素转换（复用缓冲减少分配）</summary>
        private void DrainOutputWithoutExtract()
        {
            if (!_outputTypeSet) return;

            int outSize = Math.Max(_outputBufferSize, _width * _height * 3 / 2 + 4096);

            while (true)
            {
                // 复用输出缓冲，避免每次循环都分配
                EnsureDrainBuffer(outSize);

                var dataBuffer = new OutputDataBuffer { StreamID = 0, Sample = _drainOutSample! };

                Result result;
                try
                {
                    result = _decoder!.ProcessOutput(ProcessOutputFlags.None, 1, ref dataBuffer, out ProcessOutputStatus _);
                }
                catch (SharpGenException ex)
                {
                    result = ex.ResultCode;
                }

                if (result.Code == MF_E_TRANSFORM_NEED_MORE_INPUT) break;
                if (result.Code == MF_E_TRANSFORM_STREAM_CHANGE)
                {
                    HandleStreamChange();
                    if (!_outputTypeSet) break;
                    continue;
                }
                if (result.Failure) break;
            }
        }

        /// <summary>确保排空用的输出缓冲已创建且容量足够</summary>
        private void EnsureDrainBuffer(int minSize)
        {
            if (_drainOutBuffer == null || _drainOutSample == null)
            {
                _drainOutBuffer = MediaFactory.MFCreateMemoryBuffer(minSize);
                _drainOutSample = MediaFactory.MFCreateSample();
                _drainOutSample.AddBuffer(_drainOutBuffer);
                _drainOutBuffer.Dispose(); // 样本持有引用，释放本地引用
            }
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

            if (!_outputTypeSet)
            {
                TrySelectNv12OutputType(); // 首个 SPS/IDR 输入后再协商输出类型
            }

            bool produced = false;

            // 使用解码器协商后声明的输出缓冲大小，避免输出样本尺寸不符合 MFT 要求
            int outSize = Math.Max(_outputBufferSize, _width * _height * 3 / 2 + 4096);

            while (true)
            {
                if (!_outputTypeSet)
                {
                    break; // 输出类型尚未可用时先等待后续帧
                }

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

                Result result; // ProcessOutput 返回码
                try
                {
                    result = _decoder!.ProcessOutput(ProcessOutputFlags.None, 1, ref dataBuffer, out ProcessOutputStatus _); // 尝试取出一帧解码图像
                }
                catch (SharpGenException ex)
                {
                    result = ex.ResultCode; // Vortice 可能把失败 HRESULT 直接抛成异常
                }

                if (result.Code == MF_E_TRANSFORM_NEED_MORE_INPUT)
                {
                    outSample.Dispose();
                    break; // 这一帧暂时没有可输出图像
                }
                if (result.Code == MF_E_TRANSFORM_STREAM_CHANGE)
                {
                    outSample.Dispose();
                    HandleStreamChange();
                    if (!_outputTypeSet) break; // 重新协商失败时等待更多输入
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

        /// <summary>尝试从解码器可用输出类型中选择 NV12</summary>
        private bool TrySelectNv12OutputType()
        {
            for (int index = 0; ; index++)
            {
                IMFMediaType outType; // 当前枚举到的输出媒体类型
                try
                {
                    outType = _decoder!.GetOutputAvailableType(0, index); // 读取 MFT 推荐的完整输出类型
                }
                catch (SharpGenException)
                {
                    break; // 没有更多输出类型
                }

                using (outType)
                {
                    Guid subtype = outType.GetGUID(MediaTypeAttributeKeys.Subtype); // 读取像素格式
                    if (subtype != VideoFormatGuids.NV12) continue; // 只接受 NV12，便于后续 CPU 转 BGRA

                    try
                    {
                        _decoder!.SetOutputType(0, outType, 0); // 使用 MFT 推荐类型完成输出协商，不强行改写帧尺寸
                        _outputTypeSet = true; // 标记输出类型已协商
                        UpdateOutputSizeFromCurrentType(); // 从实际输出类型读取尺寸
                        _outputBufferSize = Math.Max(_decoder.GetOutputStreamInfo(0).Size, _width * _height * 3 / 2 + 4096); // 更新缓冲大小
                        DiagLog.Write($"[DEC] 输出类型已设置 NV12 index={index} {_width}x{_height} out={_outputBufferSize}B"); // 记录选中的输出类型序号
                        return true;
                    }
                    catch (SharpGenException ex)
                    {
                        DiagLog.Write($"[DEC] 跳过不可用 NV12 输出类型 index={index} hr=0x{ex.ResultCode.Code:X8}"); // 记录失败类型并继续尝试
                    }
                }
            }

            DiagLog.Write("[DEC] 暂未获得可用 NV12 输出类型，等待更多输入"); // 输出类型可能需要更多码流信息
            return false;
        }

        /// <summary>处理输出格式变化：重新选择 NV12 输出类型</summary>
        private void HandleStreamChange()
        {
            _outputTypeSet = false; // 标记输出格式需要重新协商
            TrySelectNv12OutputType(); // 重新按 MFT 推荐类型协商输出格式
            _outputBufferSize = Math.Max(_decoder!.GetOutputStreamInfo(0).Size, _width * _height * 3 / 2 + 4096); // 更新输出缓冲大小
            DiagLog.Write("[DEC] 输出格式变化，已尝试重设 NV12 输出类型"); // 记录格式变化
        }

        /// <summary>从当前输出类型读取实际帧尺寸</summary>
        private void UpdateOutputSizeFromCurrentType()
        {
            // 当前 Vortice 包装未暴露统一的 UINT64 读取方法，继续沿用 AirPlay 镜像头给出的显示尺寸
        }

        /// <summary>从 NV12 输出样本提取像素并转换为 BGRA</summary>
        private void ExtractNv12(IMFSample sample, out byte[] bgra, out int width, out int height)
        {
            width = _width;
            height = _height;

            using var buffer = sample.ConvertToContiguousBuffer();
            buffer.Lock(out IntPtr data, out int _, out int curLen);
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
            if (stride < width) stride = (width + 15) & ~15;
            if (stride * codedHeight * 3 / 2 > curLen)
            {
                codedHeight = height; // 某些解码器输出显示高度而不是编码对齐高度
                stride = Math.Max(width, (int)((long)curLen * 2 / (3L * Math.Max(1, codedHeight))));
            }

            int pixels = width * height;
            // 必须正好等于 宽×高×4，CanvasBitmap.CreateFromBytes 会校验长度
            if (_bgraBuffer == null || _bgraBuffer.Length != pixels * 4)
                _bgraBuffer = new byte[pixels * 4];

            Nv12ToBgra(_nv12Buffer!, stride, codedHeight, width, height, _bgraBuffer!);
            bgra = _bgraBuffer!;
        }

        #region NV12 → BGRA 转换（unsafe 指针 + 2 像素成对优化）

        /// <summary>NV12 → BGRA（BT.601），使用 unsafe 指针 + 2 像素成对处理减少 UV 重复读取</summary>
        private static void Nv12ToBgra(byte[] nv12, int stride, int codedHeight, int width, int height, byte[] bgra)
        {
            unsafe
            {
                fixed (byte* pNv12 = nv12)
                fixed (byte* pBgra = bgra)
                {
                    byte* uvStart = pNv12 + stride * codedHeight; // UV 平面起始偏移

                    for (int y = 0; y < height; y++)
                    {
                        byte* yRow = pNv12 + y * stride;           // 当前行 Y 数据指针
                        byte* uvRow = uvStart + (y >> 1) * stride; // 当前行对应 UV 数据指针
                        byte* outRow = pBgra + y * width * 4;      // 输出 BGRA 行首地址

                        int x;
                        // 2 像素成对处理：相邻两像素共享同一 UV 对，减少 UV 读取次数
                        for (x = 0; x <= width - 2; x += 2)
                        {
                            // 读取 2 个 Y 值和 1 对 UV
                            int yy0 = yRow[x] & 0xFF;
                            int yy1 = yRow[x + 1] & 0xFF;
                            int u = (uvRow[x] & 0xFF) - 128;
                            int v = (uvRow[x + 1] & 0xFF) - 128;

                            // 第一个像素的 BT.601 YUV→RGB
                            int c0 = yy0 - 16;
                            int off = x * 4;
                            outRow[off]     = ClampByte((298 * c0 + 516 * u + 128) >> 8); // B
                            outRow[off + 1] = ClampByte((298 * c0 - 100 * u - 208 * v + 128) >> 8); // G
                            outRow[off + 2] = ClampByte((298 * c0 + 409 * v + 128) >> 8); // R
                            outRow[off + 3] = 255; // A

                            // 第二个像素（复用同一 U、V）
                            int c1 = yy1 - 16;
                            int off1 = off + 4;
                            outRow[off1]     = ClampByte((298 * c1 + 516 * u + 128) >> 8); // B
                            outRow[off1 + 1] = ClampByte((298 * c1 - 100 * u - 208 * v + 128) >> 8); // G
                            outRow[off1 + 2] = ClampByte((298 * c1 + 409 * v + 128) >> 8); // R
                            outRow[off1 + 3] = 255; // A
                        }

                        // 奇数宽度剩余的最后一个像素
                        if (x < width)
                        {
                            int uvCol = x & ~1;
                            int c0 = (yRow[x] & 0xFF) - 16;
                            int u = (uvRow[uvCol] & 0xFF) - 128;
                            int v = (uvRow[uvCol + 1] & 0xFF) - 128;
                            int off = x * 4;
                            outRow[off]     = ClampByte((298 * c0 + 516 * u + 128) >> 8); // B
                            outRow[off + 1] = ClampByte((298 * c0 - 100 * u - 208 * v + 128) >> 8); // G
                            outRow[off + 2] = ClampByte((298 * c0 + 409 * v + 128) >> 8); // R
                            outRow[off + 3] = 255; // A
                        }
                    }
                }
            }
        }

        private static byte ClampByte(int v) => (byte)(v < 0 ? 0 : (v > 255 ? 255 : v));

        #endregion

        /// <summary>把 (width,height) 打包为 MF_MT_FRAME_SIZE 的 64 位值</summary>
        private static long PackLong(int high, int low) => ((long)high << 32) | (uint)low;

        /// <summary>用字节数组创建 IMFSample</summary>
        private static IMFSample CreateSampleFromBytes(byte[] data)
        {
            var buffer = MediaFactory.MFCreateMemoryBuffer(data.Length);
            buffer.Lock(out IntPtr ptr, out int _, out int _);
            Marshal.Copy(data, 0, ptr, data.Length);
            buffer.Unlock();
            buffer.CurrentLength = data.Length;

            var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            buffer.Dispose();
            return sample;
        }

        /// <summary>通过 CLSID 创建 MFT，并显式 QueryInterface 到 IMFTransform</summary>
        private static IMFTransform CreateDecoder(Guid clsid)
        {
            // IID_IMFTransform
            Guid iidTransform = new Guid("BF94C121-5B05-4E6F-8000-BA598961414D");

            Type t = Type.GetTypeFromCLSID(clsid, throwOnError: true)!;
            object obj = Activator.CreateInstance(t)!;          // 创建 MFT 的 RCW
            IntPtr unk = Marshal.GetIUnknownForObject(obj);     // 取 IUnknown*
            try
            {
                int hr = Marshal.QueryInterface(unk, ref iidTransform, out IntPtr ppv);
                if (hr < 0)
                    throw new InvalidOperationException($"QueryInterface(IMFTransform) 失败: 0x{hr:X8}");
                return new IMFTransform(ppv); // 用 QI 得到的指针构造 Vortice 包装
            }
            finally
            {
                Marshal.Release(unk);
            }
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

            // 释放排空缓冲
            _drainOutSample?.Dispose();
            _drainOutSample = null;

            try { MediaFactory.MFShutdown(); } catch { }
        }
    }
}
