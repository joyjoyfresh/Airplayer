using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AirPlayer.Protocol.Utils;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace AirPlayer.App.Rendering
{
    /// <summary>
    /// 硬件 H264 解码器：绑定 D3D11 设备管理器，输出 ID3D11Texture2D（NV12 格式）。
    /// 硬件模式下 MFT 自带输出样本，无需调用方分配内存缓冲。
    /// </summary>
    public sealed class HardwareH264Decoder : IDisposable
    {
        // Microsoft H264 解码器 MFT 的 CLSID
        private static readonly Guid CLSID_CMSH264DecoderMFT = new Guid("62CE7E72-4C71-4D20-B15D-452831A87D9D");
        // IMFTransform 接口 IID
        private static readonly Guid IID_IMFTransform = new Guid("BF94C121-5B05-4E6F-8000-BA598961414D");
        // MF_SA_D3D11_AWARE 属性 GUID（标识 MFT 支持 D3D11 硬件输出）
        private static readonly Guid MF_SA_D3D11_AWARE = new Guid("206B4FC8-FCEA-4594-8058-4A6BEBA6EBF6");
        // MF_LOW_LATENCY 属性 GUID（低延迟模式，减少解码器内部缓冲；与 CODECAPI_AVLowLatencyMode 同一 GUID）
        private static readonly Guid MF_LOW_LATENCY = new Guid("9C27891A-ED7A-40E1-88E8-B22727A024EE");
        // ICodecAPI 接口 IID
        private static readonly Guid IID_ICodecAPI = new Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
        // CODECAPI_AVDecNumWorkerThreads（解码工作线程数）
        private static readonly Guid CODECAPI_AVDecNumWorkerThreads = new Guid("9561C3E8-EA9E-4435-9B1E-A93E691894D8");

        // ProcessOutput 特殊返回码
        private const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);   // 需要更多输入
        private const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);     // 输出格式已变化

        private readonly GpuDevice _gpu;        // 共享 GPU 设备
        private IMFTransform? _decoder;         // MFT 解码器
        private int _width;                     // 显示宽度
        private int _height;                    // 显示高度
        private bool _started;                  // 是否已完成初始化并开始收流
        private bool _outputTypeSet;            // 是否已完成输出类型协商
        private bool _hardwareOutput;           // MFT 是否自带输出样本（硬件模式）
        private ID3D11Texture2D? _swStaging;    // 软解模式暂存 NV12 纹理（持久化，CPU 可写）
        private IMFSample? _pooledInputSample;   // 池化输入样本，避免每帧分配
        private IMFMediaBuffer? _pooledInputBuffer; // 池化输入缓冲
        private int _pooledInputBufferSize;      // 当前池化缓冲容量

        /// <summary>
        /// 解码器输出格式发生变化（STREAM_CHANGE）。
        /// 由 TryProcessOutput 内部设置，调用 ConsumeStreamChange() 后重置。
        /// </summary>
        public bool StreamChangeDetected { get; private set; }

        /// <summary>读取并重置 STREAM_CHANGE 标志</summary>
        public bool ConsumeStreamChange()
        {
            if (StreamChangeDetected)
            {
                StreamChangeDetected = false;
                return true;
            }
            return false;
        }

        /// <summary>
        /// 是否成功绑定 D3D11 硬件解码。
        /// false 表示 MFT 不支持 D3D11，走软解→上传纹理路径。
        /// </summary>
        public bool IsHardware { get; private set; }

        // ──────────────────────────────────────────────────────────────────
        // 构造
        // ──────────────────────────────────────────────────────────────────

        /// <param name="gpu">共享 GPU 设备（含 IMFDXGIDeviceManager）</param>
        public HardwareH264Decoder(GpuDevice gpu)
        {
            _gpu = gpu; // 保存共享设备引用
            MediaFactory.MFStartup();
        }

        // ──────────────────────────────────────────────────────────────────
        // 延迟初始化
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// 首帧（IDR）到达后调用，完成解码器初始化、D3D 绑定、输入类型配置。
        /// </summary>
        public void EnsureStarted(int width, int height)
        {
            if (_started) return;

            _width = width > 0 ? width : 1920;   // 保存显示宽度
            _height = height > 0 ? height : 1080; // 保存显示高度

            // 创建 MFT 并显式 QI 到 IMFTransform
            _decoder = CreateDecoderMft(CLSID_CMSH264DecoderMFT);
            DiagLog.Write("[HWD] MFT 实例已创建");

            // ── 注入 D3D11 设备管理器 ──────────────────────────────────────
            // 直接尝试注入，而非先检查 MF_SA_D3D11_AWARE 属性：
            // 某些 MFT 实现支持 D3D11 但属性查询可能失败（Vortice 包装问题）。
            // 若注入失败则回退软解。
            try
            {
                nuint managerPtr = (nuint)_gpu.DeviceManager.NativePointer;
                _decoder.ProcessMessage(TMessageType.MessageSetD3DManager, managerPtr);
                IsHardware = true;
                DiagLog.Write("[HWD] D3D11 设备管理器已注入解码器");
            }
            catch (Exception ex)
            {
                IsHardware = false;
                int hr = ex is SharpGenException se ? se.ResultCode.Code : ex.HResult;
                DiagLog.Write($"[HWD] D3D11 注入失败，回退软解: 0x{hr:X8}");
            }

            // ── 设置低延迟模式 ──────────────────────────────────────────
            // 注：MF_LOW_LATENCY 与 CODECAPI_AVLowLatencyMode 是同一个 GUID，设此属性即开启低延迟解码。
            try
            {
                // Vortice 3.x: IMFTransform.Attributes 属性，设置用统一的 Set(Guid, uint)
                using IMFAttributes attrs = _decoder.Attributes;
                attrs.Set(MF_LOW_LATENCY, 1u); // 1 = 开启低延迟
                DiagLog.Write("[HWD] 低延迟模式已开启");
            }
            catch (Exception ex)
            {
                DiagLog.Write($"[HWD] 低延迟设置失败（非致命）: {ex.Message}");
            }

            // ── 多线程解码（CODECAPI_AVDecNumWorkerThreads，须经 ICodecAPI 设置）──
            // 高码率/高帧率（如快速滑动的大帧）下并行解码更易跟上，减少积压与延迟。
            TrySetDecoderWorkerThreads((uint)Math.Min(Environment.ProcessorCount, 4));

            // ── 配置输入类型 ─────────────────────────────────────────────
            var inType = MediaFactory.MFCreateMediaType();
            inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);   // 视频流
            inType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);    // H264 编码
            inType.Set(MediaTypeAttributeKeys.FrameSize, PackLong(_width, _height)); // 帧尺寸
            _decoder.SetInputType(0, inType, 0);
            DiagLog.Write($"[HWD] 输入类型已设置 H264 {_width}x{_height}");

            // ── 开始流 ──────────────────────────────────────────────────
            _decoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            _decoder.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

            _started = true;
            _outputTypeSet = false;

            DiagLog.Write($"[HWD] 解码器已启动 IsHardware={IsHardware}");
        }

        // ──────────────────────────────────────────────────────────────────
        // 喂帧 + 取纹理
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// 送入一帧 Annex-B 数据并尝试取出解码纹理。
        /// </summary>
        /// <param name="annexB">Annex-B 格式 H264 帧字节</param>
        /// <param name="texture">输出：解码后的 NV12 纹理（调用方须 Dispose）</param>
        /// <param name="subresourceIndex">纹理数组子资源索引</param>
        /// <param name="needMoreInput">输出：true 表示帧已被解码器接收、只是尚未产出（流水线延迟，属正常，非丢帧）；
        /// 返回 false 且此值为 false 时才是真正的解码失败。</param>
        /// <returns>是否成功产出一帧纹理</returns>
        public bool TryDecode(byte[] annexB, out ID3D11Texture2D? texture, out uint subresourceIndex, out bool needMoreInput)
        {
            texture = null;
            subresourceIndex = 0;
            needMoreInput = false;

            // 构造输入样本并送入 MFT（使用池化样本，不要 Dispose）
            var inputSample = CreateSampleFromBytes(annexB);
            try
            {
                _decoder!.ProcessInput(0, inputSample, 0); // 送入一帧压缩数据
            }
            catch (SharpGenException ex)
            {
                DiagLog.Write($"[HWD] ProcessInput 异常: 0x{ex.ResultCode.Code:X8}");
                return false; // 真正失败
            }

            // 首次送入后尝试协商输出类型
            if (!_outputTypeSet)
                TrySelectNv12OutputType();

            if (!_outputTypeSet) { needMoreInput = true; return false; } // 还未协商成功，等待更多输入

            // ── 取出解码输出 ──────────────────────────────────────────────
            // STREAM_CHANGE 循环重试：MFT 首次输出格式变化时返回 STREAM_CHANGE，
            // 内部已重新协商输出类型。但 MFT 可能连续多次触发 STREAM_CHANGE
            // （不同输出属性依次就绪），因此需要循环直到不再 STREAM_CHANGE 或成功取帧。
            // 每次重试前检查 StreamChangeDetected 标志，避免无限循环。
            const int maxStreamChangeRetries = 4; // 防御性上限
            for (int attempt = 0; attempt < maxStreamChangeRetries; attempt++)
            {
                if (TryProcessOutput(out texture, out subresourceIndex, out bool nm))
                    return true; // 成功取出解码纹理

                if (!StreamChangeDetected)
                {
                    // 非 STREAM_CHANGE：nm 区分“需更多输入(正常)”与“真失败”，透出给上层用于丢帧判定
                    needMoreInput = nm;
                    return false;
                }

                // STREAM_CHANGE 已在 TryProcessOutput 内部处理并重新协商输出类型，
                // 清除标志后立即重试 ProcessOutput
                StreamChangeDetected = false;
                DiagLog.Write($"[HWD] STREAM_CHANGE 重试 #{attempt + 1}");
            }

            // 超过重试上限仍未成功，等待下一帧输入后再取（视为待定，不计丢帧）
            DiagLog.Write("[HWD] STREAM_CHANGE 重试次数耗尽，等待下一帧");
            needMoreInput = true;
            return false;
        }

        /// <summary>尝试从 MFT 取出一帧输出纹理。needMore=true 表示仅需更多输入（正常），非失败。</summary>
        private bool TryProcessOutput(out ID3D11Texture2D? texture, out uint subresourceIndex, out bool needMore)
        {
            texture = null;
            subresourceIndex = 0;
            needMore = false;

            // 根据模式决定是否由调用方提供输出样本
            IMFSample? outputSample = null;
            if (!_hardwareOutput)
            {
                // 软解模式：调用方必须分配输出样本
                int outSize = _width * _height * 3 / 2 + 4096;
                var outBuffer = MediaFactory.MFCreateMemoryBuffer(outSize);
                outputSample = MediaFactory.MFCreateSample();
                outputSample.AddBuffer(outBuffer);
                outBuffer.Dispose(); // 样本持有引用，释放本地引用
            }

            var dataBuffer = new OutputDataBuffer { StreamID = 0, Sample = outputSample };

            Result result;
            try
            {
                result = _decoder!.ProcessOutput(
                    ProcessOutputFlags.None, 1, ref dataBuffer, out ProcessOutputStatus _);
            }
            catch (SharpGenException ex)
            {
                result = ex.ResultCode; // Vortice 可能把失败 HRESULT 抛成异常
            }

            if (result.Code == MF_E_TRANSFORM_NEED_MORE_INPUT)
            {
                outputSample?.Dispose();
                needMore = true;
                return false; // 正常：帧已被接收，喂更多数据后再取（非丢帧）
            }

            if (result.Code == MF_E_TRANSFORM_STREAM_CHANGE)
            {
                DiagLog.Write("[HWD] ProcessOutput: STREAM_CHANGE，重新协商输出类型");
                outputSample?.Dispose();
                _outputTypeSet = false;
                StreamChangeDetected = true;
                TrySelectNv12OutputType();
                return false;
            }

            if (result.Failure)
            {
                DiagLog.Write($"[HWD] ProcessOutput 失败: 0x{result.Code:X8}");
                outputSample?.Dispose();
                return false;
            }

            if (dataBuffer.Sample == null)
            {
                DiagLog.Write("[HWD] ProcessOutput 成功但 Sample 为 null");
                return false;
            }

            // ── 从样本提取解码输出 ────────────────────────────────────
            bool ok;
            if (_hardwareOutput)
            {
                // 硬件模式：从 DXGI 缓冲提取纹理
                ok = TryExtractTexture(dataBuffer.Sample, out texture, out subresourceIndex);
            }
            else
            {
                // 软解模式：从内存缓冲提取 NV12 数据，上传至 D3D11 纹理
                ok = TryUploadNv12FromSample(dataBuffer.Sample, out texture);
                subresourceIndex = 0;
            }

            dataBuffer.Sample.Dispose(); // 提取完毕，释放 MF 样本引用
            return ok;
        }

        /// <summary>从 IMFSample 取出 ID3D11Texture2D 和子资源索引</summary>
        private static bool TryExtractTexture(IMFSample sample,
            out ID3D11Texture2D? texture, out uint subresourceIndex)
        {
            texture = null;
            subresourceIndex = 0;
            try
            {
                using IMFMediaBuffer buf = sample.GetBufferByIndex(0);          // 取第一个媒体缓冲
                using IMFDXGIBuffer dxgi = buf.QueryInterface<IMFDXGIBuffer>(); // QI 到 DXGI 缓冲接口
                // Vortice 3.x: GetSubresourceIndex 成为属性 SubresourceIndex
                subresourceIndex = dxgi.SubresourceIndex;                 // 纹理数组层索引
                // GetResource 为非泛型，需传入 Guid 并自行转换
                IntPtr texPtr = dxgi.GetResource(typeof(ID3D11Texture2D).GUID);
                texture = new ID3D11Texture2D(texPtr);                   // 调用方 Dispose
                return true;
            }
            catch (Exception ex)
            {
                DiagLog.Write($"[HWD] 提取纹理异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 软解模式：从 IMFSample 的内存缓冲提取 NV12 数据，上传至 D3D11 纹理。
        /// 使用持久化暂存纹理中转，每帧创建新的默认纹理（由调用方 Dispose）。
        /// </summary>
        private bool TryUploadNv12FromSample(IMFSample sample, out ID3D11Texture2D? texture)
        {
            texture = null;
            try
            {
                using IMFMediaBuffer buf = sample.ConvertToContiguousBuffer();
                buf.Lock(out IntPtr data, out int _, out int curLen);
                try
                {
                    // 由缓冲总长度反推行宽和对齐高度
                    int codedH = (_height + 15) & ~15;
                    int srcStride = (int)((long)curLen * 2 / (3L * codedH));
                    if (srcStride < _width) srcStride = (_width + 15) & ~15;
                    int codedW = srcStride; // 源行宽即为编码对齐宽度

                    // 获取或创建持久化暂存纹理（CPU 可写）
                    ID3D11Texture2D staging = EnsureStagingTexture(codedW, codedH);

                    // 映射暂存纹理并拷贝 NV12 数据（源/目标行宽可能不同，逐行拷贝）
                    _gpu.DeviceContext.Map(staging, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None, out MappedSubresource mapped);
                    try
                    {
                        int dstStride = (int)mapped.RowPitch; // D3D11 要求的目标行宽
                        unsafe
                        {
                            byte* src = (byte*)data;           // 源 NV12 数据起始
                            byte* dst = (byte*)mapped.DataPointer; // 目标暂存起始

                            // 拷贝 Y 平面
                            for (int y = 0; y < codedH; y++)
                                Buffer.MemoryCopy(src + y * srcStride, dst + y * dstStride, dstStride, srcStride);

                            // 拷贝 UV 平面（高度为 Y 的一半）
                            byte* uvSrc = src + srcStride * codedH;
                            byte* uvDst = dst + dstStride * codedH;
                            int uvH = codedH / 2;
                            for (int y = 0; y < uvH; y++)
                                Buffer.MemoryCopy(uvSrc + y * srcStride, uvDst + y * dstStride, dstStride, srcStride);
                        }
                    }
                    finally
                    {
                        _gpu.DeviceContext.Unmap(staging, 0);
                    }

                    // 创建目标 NV12 纹理（GPU 侧，每帧新建，由调用方 Dispose）
                    // D3D11_BIND_VIDEO_PROCESSOR = 0x400，Vortice 未暴露此枚举值，直接转型
                    var defaultDesc = new Texture2DDescription(
                        Format.NV12,             // 像素格式
                        (uint)codedW,            // 宽度
                        (uint)codedH,            // 高度
                        1,                       // MipLevels
                        1,                       // ArraySize
                        (BindFlags)0x400,        // D3D11_BIND_VIDEO_PROCESSOR
                        ResourceUsage.Default,   // GPU 侧默认用法
                        CpuAccessFlags.None,     // 无 CPU 访问
                        0,                       // SampleDescription.Count
                        0,                       // SampleDescription.Quality
                        ResourceOptionFlags.None // 无杂项标志
                    );
                    texture = _gpu.Device.CreateTexture2D(defaultDesc);

                    // 从暂存复制到默认纹理
                    _gpu.DeviceContext.CopyResource(staging, texture);

                    return true;
                }
                finally
                {
                    buf.Unlock();
                }
            }
            catch (Exception ex)
            {
                DiagLog.Write($"[HWD] NV12 上传失败: {ex.Message}");
                texture?.Dispose();
                texture = null;
                return false;
            }
        }

        /// <summary>获取或创建持久化暂存纹理（分辨率变化时重建）</summary>
        private ID3D11Texture2D EnsureStagingTexture(int codedW, int codedH)
        {
            // 检查现有暂存纹理尺寸是否匹配
            if (_swStaging != null &&
                _swStaging.Description.Width == codedW &&
                _swStaging.Description.Height == codedH)
                return _swStaging;

            // 释放旧暂存纹理
            _swStaging?.Dispose();

            // 创建暂存纹理（CPU 可写，用于上传 NV12 数据）
            var desc = new Texture2DDescription(
                Format.NV12,              // 像素格式
                (uint)codedW,             // 宽度
                (uint)codedH,             // 高度
                1,                        // MipLevels
                1,                        // ArraySize
                BindFlags.None,           // 暂存纹理无需绑定
                ResourceUsage.Staging,    // 暂存用途（CPU 可读写）
                CpuAccessFlags.Write,     // CPU 可写
                0,                        // SampleDescription.Count
                0,                        // SampleDescription.Quality
                ResourceOptionFlags.None  // 无杂项标志
            );
            _swStaging = _gpu.Device.CreateTexture2D(desc);
            DiagLog.Write($"[HWD] 软解暂存纹理已创建 {codedW}x{codedH}");
            return _swStaging;
        }

        // ──────────────────────────────────────────────────────────────────
        // 输出类型协商
        // ──────────────────────────────────────────────────────────────────

        /// <summary>枚举 MFT 可用输出类型，选择 NV12（硬件模式首选）</summary>
        private bool TrySelectNv12OutputType()
        {
            for (int i = 0; ; i++)
            {
                IMFMediaType outType;
                try { outType = _decoder!.GetOutputAvailableType(0, i); }
                catch (SharpGenException) { break; } // 无更多类型

                using (outType)
                {
                    Guid subtype = outType.GetGUID(MediaTypeAttributeKeys.Subtype);
                    if (subtype != VideoFormatGuids.NV12) continue; // 只接受 NV12

                    // 不覆写 FrameSize：MFT 从 SPS/PPS 解析出的编码尺寸可能与显示尺寸不同
                    // （H264 要求 16 像素宏块对齐，如 886→896），强行覆写会导致
                    // MF_E_INVALIDMEDIATYPE (0xC00D36BD)。让 MFT 使用自身协商的尺寸。

                    try
                    {
                        _decoder!.SetOutputType(0, outType, 0);
                        _outputTypeSet = true;

                        // 判断是否硬件模式（MFT 自带输出样本）
                        var streamInfo = _decoder.GetOutputStreamInfo(0);
                        // Vortice 3.x: 枚举名为 OutputStreamInfoFlags，成员为 OutputStreamProvidesSamples
                        _hardwareOutput = (streamInfo.Flags &
                            (int)OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0;

                        DiagLog.Write($"[HWD] NV12 输出类型已设置 index={i} hwOutput={_hardwareOutput}");
                        return true;
                    }
                    catch (SharpGenException ex)
                    {
                        DiagLog.Write($"[HWD] 设置输出类型失败 index={i} hr=0x{ex.ResultCode.Code:X8}");
                    }
                }
            }
            DiagLog.Write("[HWD] 暂未获得可用 NV12 输出类型");
            return false;
        }

        // ──────────────────────────────────────────────────────────────────
        // 工具方法
        // ──────────────────────────────────────────────────────────────────

        /// <summary>把 (width, height) 打包为 MF_MT_FRAME_SIZE 的 64 位值</summary>
        private static long PackLong(int high, int low) => ((long)high << 32) | (uint)low;

        /// <summary>
        /// 从字节数组创建 IMFSample，使用池化缓冲避免每帧分配。
        /// ProcessInput 会拷贝数据，因此缓冲可立即复用。
        /// 注意：返回的样本由内部池持有，调用方不应 Dispose。
        /// </summary>
        private IMFSample CreateSampleFromBytes(byte[] data)
        {
            if (_pooledInputBuffer == null || _pooledInputSample == null || _pooledInputBufferSize < data.Length)
            {
                _pooledInputSample?.Dispose();
                _pooledInputBuffer?.Dispose();
                _pooledInputBufferSize = Math.Max(data.Length, 64 * 1024);
                _pooledInputBuffer = MediaFactory.MFCreateMemoryBuffer(_pooledInputBufferSize);
                _pooledInputSample = MediaFactory.MFCreateSample();
                _pooledInputSample.AddBuffer(_pooledInputBuffer);
            }

            _pooledInputBuffer.Lock(out IntPtr ptr, out int _, out int _);
            Marshal.Copy(data, 0, ptr, data.Length);
            _pooledInputBuffer.Unlock();
            _pooledInputBuffer.CurrentLength = data.Length;

            return _pooledInputSample;
        }

        /// <summary>通过 CLSID 创建 MFT 并显式 QI 到 IMFTransform</summary>
        private static IMFTransform CreateDecoderMft(Guid clsid)
        {
            Type t = Type.GetTypeFromCLSID(clsid, throwOnError: true)!;
            object obj = Activator.CreateInstance(t)!;              // 创建 COM RCW
            IntPtr unk = Marshal.GetIUnknownForObject(obj);         // 取 IUnknown*
            try
            {
                Guid iid = IID_IMFTransform;
                int hr = Marshal.QueryInterface(unk, ref iid, out IntPtr ppv);
                if (hr < 0)
                    throw new InvalidOperationException($"QueryInterface(IMFTransform) 失败: 0x{hr:X8}");
                return new IMFTransform(ppv); // 用 QI 得到的指针构造 Vortice 包装
            }
            finally
            {
                Marshal.Release(unk); // 释放 QI 前的临时 IUnknown 引用
            }
        }

        // SetValue 是 ICodecAPI 的第 7 个方法 → 虚表槽位 = IUnknown(3) + 6 = 9
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CodecApiSetValueDelegate(IntPtr thisPtr, IntPtr api, IntPtr value);

        /// <summary>
        /// 通过 ICodecAPI 设置解码工作线程数（CODECAPI_AVDecNumWorkerThreads）。
        /// 该属性不在 MFT 属性上，必须 QI 到 ICodecAPI 调 SetValue。自包含 COM 互操作，失败不致命。
        /// </summary>
        private void TrySetDecoderWorkerThreads(uint count)
        {
            if (_decoder == null) return;
            IntPtr codecApi = IntPtr.Zero;
            try
            {
                Guid iid = IID_ICodecAPI;
                int hr = Marshal.QueryInterface(_decoder.NativePointer, ref iid, out codecApi);
                if (hr < 0 || codecApi == IntPtr.Zero)
                {
                    DiagLog.Write("[HWD] 解码器不支持 ICodecAPI，跳过多线程设置");
                    return;
                }

                IntPtr pVar = Marshal.AllocHGlobal(24);  // PROPVARIANT（64 位下 24 字节）
                IntPtr pGuid = Marshal.AllocHGlobal(16); // GUID
                try
                {
                    // 清零并填 VT_UI4(=19) + ulVal
                    Marshal.WriteInt64(pVar, 0, 0);
                    Marshal.WriteInt64(pVar, 8, 0);
                    Marshal.WriteInt64(pVar, 16, 0);
                    Marshal.WriteInt16(pVar, 0, 19);            // VT_UI4
                    Marshal.WriteInt32(pVar, 8, (int)count);    // ulVal

                    Marshal.Copy(CODECAPI_AVDecNumWorkerThreads.ToByteArray(), 0, pGuid, 16);

                    IntPtr vtbl = Marshal.ReadIntPtr(codecApi);
                    IntPtr setValuePtr = Marshal.ReadIntPtr(vtbl, 9 * IntPtr.Size); // 第 9 槽 = SetValue
                    var setValue = Marshal.GetDelegateForFunctionPointer<CodecApiSetValueDelegate>(setValuePtr);

                    int shr = setValue(codecApi, pGuid, pVar);
                    if (shr < 0) DiagLog.Write($"[HWD] 设置解码线程数失败: 0x{shr:X8}");
                    else DiagLog.Write($"[HWD] 解码器工作线程数已设为 {count}");
                }
                finally
                {
                    Marshal.FreeHGlobal(pVar);
                    Marshal.FreeHGlobal(pGuid);
                }
            }
            catch (Exception ex)
            {
                DiagLog.Write($"[HWD] 设置解码线程数异常: {ex.Message}");
            }
            finally
            {
                if (codecApi != IntPtr.Zero) Marshal.Release(codecApi);
            }
        }

        /// <summary>当前是否已初始化</summary>
        public bool IsStarted => _started;

        /// <summary>
        /// 清空解码器缓冲区和内部状态，用于丢帧或参考链断开时重新同步。
        /// </summary>
        public void Flush()
        {
            if (!_started) return;
            try
            {
                _decoder?.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero);
            }
            catch (Exception ex)
            {
                DiagLog.Write($"[HWD] Flush 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 重置解码器状态，以便用新分辨率重新初始化。
        /// iOS 屏幕旋转时调用。
        /// </summary>
        public void Reset()
        {
            if (!_started) return;

            try
            {
                _decoder?.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
                _decoder?.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
            }
            catch { }

            _decoder?.Dispose();
            _decoder = null;
            _started = false;
            _outputTypeSet = false;
            StreamChangeDetected = false;

            _pooledInputSample?.Dispose();
            _pooledInputSample = null;
            _pooledInputBuffer?.Dispose();
            _pooledInputBuffer = null;

            DiagLog.Write($"[HWD] 解码器已重置，等待新分辨率");
        }

        // ──────────────────────────────────────────────────────────────────
        // 释放
        // ──────────────────────────────────────────────────────────────────

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

            // 释放池化输入样本
            _pooledInputSample?.Dispose();
            _pooledInputSample = null;
            _pooledInputBuffer?.Dispose();
            _pooledInputBuffer = null;

            // 释放软解暂存纹理
            _swStaging?.Dispose();
            _swStaging = null;

            try { MediaFactory.MFShutdown(); } catch { }
            DiagLog.Write("[HWD] HardwareH264Decoder 已释放");
        }
    }
}
