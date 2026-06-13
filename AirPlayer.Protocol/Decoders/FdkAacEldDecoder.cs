using System;
using System.IO;
using System.Runtime.InteropServices;
using AirPlayer.Protocol.Models.Enums;
using AirPlayer.Protocol.Utils;

namespace AirPlayer.Protocol.Decoders
{
    /// <summary>
    /// 基于 FDK-AAC 原生库的 AAC-ELD 解码器。
    /// iOS/iPadOS 屏幕镜像（投屏）的音频固定为 AAC-ELD 44100Hz 2ch（每帧 480 样本），
    /// 微软 Media Foundation 的 AAC 解码器不支持 ELD，故改用 FDK-AAC。
    /// 输入：单个 AAC-ELD 访问单元；输出：L16 PCM（44100Hz, 2ch, 16-bit, 小端）。
    /// 依赖：需在程序输出目录放置 fdk-aac 原生库（fdk-aac.dll / libfdk-aac-2.dll）。
    /// 本类带有详尽的诊断日志：DLL 是否找到、配置/解码错误码都会写入 airplay-video.log。
    /// </summary>
    public sealed class FdkAacEldDecoder : IDecoder, IDisposable
    {
        // FDK-AAC 动态库逻辑名（实际文件名由下方 DllImportResolver 解析）
        private const string LIB = "fdk-aac";

        // 解析器会依次尝试的候选文件名
        private static readonly string[] CandidateNames =
        {
            "fdk-aac.dll", "libfdk-aac-2.dll", "libfdk-aac.dll", "fdk-aac-2.dll", "fdkaac.dll"
        };

        // 传输类型：TT_MP4_RAW = 0（裸 AAC 帧，配合 ConfigRaw 提供 ASC）
        private const int TT_MP4_RAW = 0;

        // 解码返回码
        private const int AAC_DEC_OK = 0x0000;                  // 解码成功
        private const int AAC_DEC_NOT_ENOUGH_BITS = 0x1002;     // 数据不足，需要更多输入（非致命）

        // ── P/Invoke 声明（WAVEHDR 用 IntPtr 传递，便于持久化非托管头部）────────
        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr aacDecoder_Open(int transportFmt, uint nrOfLayers);

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        private static extern int aacDecoder_ConfigRaw(IntPtr self, IntPtr[] conf, uint[] length);

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        private static extern int aacDecoder_Fill(IntPtr self, IntPtr[] pBuffer, uint[] bufferSize, ref uint bytesValid);

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        private static extern int aacDecoder_DecodeFrame(IntPtr self, short[] pTimeData, int timeDataSize, uint flags);

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr aacDecoder_GetStreamInfo(IntPtr self);

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        private static extern void aacDecoder_Close(IntPtr self);

        // 解码器原生句柄
        private IntPtr _handle;
        // 是否已释放
        private bool _disposed;
        // 库是否已成功加载（用于给出清晰诊断）
        private bool _ready;

        // 固定的 ASC 缓冲句柄（ConfigRaw 期间需保持内存地址稳定）
        private GCHandle _ascHandle;
        // 固定的输入缓冲（避免每帧重复分配/固定）
        private byte[] _inBuf = new byte[8192];
        private GCHandle _inHandle;
        private IntPtr _inPtr;
        // 输出 PCM 缓冲（short）：最多 1024 样本 * 2 声道，留足余量
        private readonly short[] _pcm = new short[1024 * 2 * 2];
        // aacDecoder_Fill 的参数数组（复用，避免每帧分配）
        private readonly IntPtr[] _fillBuf = new IntPtr[1];
        private readonly uint[] _fillSize = new uint[1];

        // 诊断计数
        private long _decOk;          // 成功次数
        private long _decErr;         // 失败次数

        // 通过统一解析器注册 DLL 查找逻辑（一个程序集只能注册一次）
        static FdkAacEldDecoder() => NativeLibResolver.Ensure();

        // 解码器对外标识为 AAC-ELD
        public AudioFormat Type => AudioFormat.AacEld;

        // 输出缓冲建议长度：1024 样本 * 2 声道 * 2 字节（取最大值，实际每帧约 1920 字节）
        public int GetOutputStreamLength() => 1024 * 2 * 2;

        /// <summary>
        /// 缺少 fdk-aac.dll 时，尝试从 MSYS2 官方仓库自动下载并解出 libfdk-aac-2.dll
        /// 放到程序目录（命名为 fdk-aac.dll）。best-effort：任何失败都安静返回 false。
        /// 需要：可访问外网 + 系统自带 tar.exe（Win10 1903+ 支持解 .zst）。
        /// </summary>
        private static bool _autoFetchTried; // 整个进程只自动下载一次，避免反复阻塞

        private static bool TryAutoFetchDll(string baseDir)
        {
            if (_autoFetchTried) return false;
            _autoFetchTried = true;
            try
            {
                string target = Path.Combine(baseDir, "fdk-aac.dll");
                DiagLog.Write("[FDK] 未发现 fdk-aac.dll，尝试自动从 MSYS2 下载...");

                using var http = new System.Net.Http.HttpClient();
                http.Timeout = TimeSpan.FromSeconds(30);
                const string baseUrl = "https://repo.msys2.org/mingw/mingw64/";

                string listing = http.GetStringAsync(baseUrl).GetAwaiter().GetResult();
                var matches = System.Text.RegularExpressions.Regex.Matches(
                    listing, @"mingw-w64-x86_64-fdk-aac-[0-9][^""<> ]*?-any\.pkg\.tar\.zst");
                if (matches.Count == 0) { DiagLog.Write("[FDK] 自动下载失败：仓库列表未匹配到 fdk-aac 包"); return false; }

                string pkg = matches[matches.Count - 1].Value; // 取最后一个（一般为最新版）
                string tmpDir = Path.Combine(Path.GetTempPath(), "airplayer_fdk");
                Directory.CreateDirectory(tmpDir);
                string pkgPath = Path.Combine(tmpDir, pkg);

                DiagLog.Write($"[FDK] 下载 {baseUrl}{pkg}");
                byte[] data = http.GetByteArrayAsync(baseUrl + pkg).GetAwaiter().GetResult();
                File.WriteAllBytes(pkgPath, data);

                // 用系统 tar 解压 .pkg.tar.zst
                var psi = new System.Diagnostics.ProcessStartInfo("tar", $"-xf \"{pkgPath}\" -C \"{tmpDir}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };
                var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) { DiagLog.Write("[FDK] 自动下载失败：无法启动 tar"); return false; }
                proc.WaitForExit(30000);

                var dlls = Directory.GetFiles(tmpDir, "libfdk-aac-2.dll", SearchOption.AllDirectories);
                if (dlls.Length == 0) { DiagLog.Write("[FDK] 自动下载失败：包内未找到 libfdk-aac-2.dll（tar 可能不支持 .zst）"); return false; }

                File.Copy(dlls[0], target, true);
                DiagLog.Write($"[FDK] 自动下载成功，已写入 {target}");
                return true;
            }
            catch (Exception ex)
            {
                DiagLog.Write($"[FDK] 自动下载异常: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 初始化解码器。AirPlay 镜像固定 AAC-ELD 44100/2ch/480，使用标准 ASC。
        /// 会把 DLL 探测结果、错误码等详细信息写入日志。
        /// </summary>
        public int Config(int sampleRate, int channels, int bitDepth, int frameLength)
        {
            // ── 先做 DLL 存在性探测，给出最清晰的诊断 ──────────────────────────
            string baseDir = AppContext.BaseDirectory;
            string? found = null;
            foreach (var name in CandidateNames)
            {
                string p = Path.Combine(baseDir, name);
                if (File.Exists(p)) { found = p; break; }
            }

            // 缺库时尝试自动下载（best-effort），让用户零操作即可用
            if (found == null && TryAutoFetchDll(baseDir))
            {
                foreach (var name in CandidateNames)
                {
                    string p = Path.Combine(baseDir, name);
                    if (File.Exists(p)) { found = p; break; }
                }
            }

            if (found == null)
            {
                // 缺库：打印醒目横幅 + 精确放置路径，避免用户再次摸不着头脑
                DiagLog.Write("==================================================================");
                DiagLog.Write("[FDK][致命] 未找到 AAC-ELD 解码所需的原生库 fdk-aac.dll！");
                DiagLog.Write("[FDK][致命] 这是“投屏有画面但没声音”的根本原因。");
                DiagLog.Write($"[FDK][致命] 请把 fdk-aac.dll（x64）放到此目录：{baseDir}");
                DiagLog.Write($"[FDK][致命] 可接受的文件名：{string.Join(" / ", CandidateNames)}");
                DiagLog.Write("[FDK][致命] 获取方式见 tools\\get-fdk-aac.ps1 或 音频修复说明.md");
                DiagLog.Write("==================================================================");
                _ready = false;
                return -1;
            }

            DiagLog.Write($"[FDK] 找到原生库: {found}");

            try
            {
                // 打开 RAW 传输类型的解码器（单层）
                _handle = aacDecoder_Open(TT_MP4_RAW, 1);
                if (_handle == IntPtr.Zero)
                {
                    DiagLog.Write("[FDK][致命] aacDecoder_Open 返回空句柄（DLL 可能损坏或位数不符，需 x64）");
                    return -1;
                }

                // 固定输入缓冲，取得稳定指针
                _inHandle = GCHandle.Alloc(_inBuf, GCHandleType.Pinned);
                _inPtr = _inHandle.AddrOfPinnedObject();

                // AAC-ELD / 44100Hz / 2ch / frameLength=480 的标准 AudioSpecificConfig
                // 位流含义：AOT=39(ELD)，采样率索引=4(44100)，声道配置=2，ELD frameLengthFlag=1(480)
                byte[] asc = { 0xF8, 0xE8, 0x50, 0x00 };
                _ascHandle = GCHandle.Alloc(asc, GCHandleType.Pinned);
                var confArr = new IntPtr[] { _ascHandle.AddrOfPinnedObject() };
                var lenArr = new uint[] { (uint)asc.Length };

                // 应用裸配置
                int err = aacDecoder_ConfigRaw(_handle, confArr, lenArr);
                if (err != AAC_DEC_OK)
                {
                    DiagLog.Write($"[FDK][致命] aacDecoder_ConfigRaw 失败: 0x{err:X4} ({ErrName(err)})");
                    return -1;
                }

                _ready = true;
                DiagLog.Write($"[FDK] AAC-ELD 解码器就绪 (sr={sampleRate}, ch={channels}, frame={frameLength}, ASC=F8E85000)");
                return 0;
            }
            catch (DllNotFoundException)
            {
                DiagLog.Write($"[FDK][致命] 加载 fdk-aac 失败（DllNotFound）。请确认 {found} 是 64 位 DLL 且依赖完整。");
                return -1;
            }
            catch (BadImageFormatException)
            {
                DiagLog.Write($"[FDK][致命] {found} 位数不符（需要 x64）。");
                return -1;
            }
            catch (Exception ex)
            {
                DiagLog.Write($"[FDK][致命] Config 异常: {ex.GetType().Name}: {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// 解码一帧 AAC-ELD。
        /// 返回实际解出的 PCM 字节数（&gt;0）；数据不足返回 0；出错返回 -1。
        /// 每 200 次或首次失败都会把 FDK 错误码写入日志，便于定位。
        /// </summary>
        public int DecodeFrame(byte[] input, ref byte[] output, int length)
        {
            if (_disposed || input == null || input.Length == 0)
                return -1;

            // 句柄无效（多半因为缺 DLL，Config 已打过横幅），这里只做节流提示
            if (_handle == IntPtr.Zero || !_ready)
            {
                _decErr++;
                if (_decErr == 1 || _decErr % 500 == 0)
                    DiagLog.Write($"[FDK] 解码器不可用(句柄空)，已累计丢弃 {_decErr} 帧音频 → 无声");
                return -1;
            }

            try
            {
                int inLen = input.Length;
                // 输入超过预分配缓冲时重新固定一块更大的
                if (inLen > _inBuf.Length)
                {
                    if (_inHandle.IsAllocated) _inHandle.Free();
                    _inBuf = new byte[inLen];
                    _inHandle = GCHandle.Alloc(_inBuf, GCHandleType.Pinned);
                    _inPtr = _inHandle.AddrOfPinnedObject();
                }

                // 拷入固定缓冲
                Buffer.BlockCopy(input, 0, _inBuf, 0, inLen);

                uint bytesValid = (uint)inLen;
                _fillBuf[0] = _inPtr;          // 复用数组，避免每帧分配
                _fillSize[0] = (uint)inLen;

                // 填入待解码数据
                int errFill = aacDecoder_Fill(_handle, _fillBuf, _fillSize, ref bytesValid);
                if (errFill != AAC_DEC_OK)
                {
                    LogDecErr("Fill", errFill, inLen);
                    return -1;
                }

                // 解一帧
                int errDec = aacDecoder_DecodeFrame(_handle, _pcm, _pcm.Length, 0);
                if (errDec != AAC_DEC_OK)
                {
                    // 数据不足属正常情况（无输出），其它视为错误
                    if (errDec == AAC_DEC_NOT_ENOUGH_BITS) return 0;
                    LogDecErr("Decode", errDec, inLen);
                    return -1;
                }

                // 从流信息读取帧大小与声道数，计算输出字节数
                IntPtr info = aacDecoder_GetStreamInfo(_handle);
                if (info == IntPtr.Zero) return 0;
                int frameSize = Marshal.ReadInt32(info, 4);   // 第 2 个 int：frameSize（每声道样本数）
                int numCh = Marshal.ReadInt32(info, 8);       // 第 3 个 int：numChannels
                if (frameSize <= 0 || numCh <= 0) return 0;

                int outBytes = frameSize * numCh * 2;         // 16-bit → 每样本 2 字节
                if (outBytes > _pcm.Length * 2) outBytes = _pcm.Length * 2; // 安全钳制

                // 输出缓冲不足则扩容
                if (output == null || output.Length < outBytes)
                    output = new byte[outBytes];

                // 拷出 PCM
                Buffer.BlockCopy(_pcm, 0, output, 0, outBytes);

                _decOk++;
                // 首帧及每 500 帧打一条成功统计，确认音频链路畅通
                if (_decOk == 1 || _decOk % 500 == 0)
                    DiagLog.Write($"[FDK] 解码OK 累计成功={_decOk} 失败={_decErr} 本帧 in={inLen} out={outBytes} (frame={frameSize}x{numCh})");
                return outBytes;
            }
            catch (Exception ex)
            {
                _decErr++;
                if (_decErr == 1 || _decErr % 200 == 0)
                    DiagLog.Write($"[FDK] DecodeFrame 异常: {ex.GetType().Name}: {ex.Message}");
                return -1;
            }
        }

        // 节流记录解码错误
        private void LogDecErr(string stage, int code, int inLen)
        {
            _decErr++;
            if (_decErr == 1 || _decErr % 200 == 0)
                DiagLog.Write($"[FDK] {stage} 失败: 0x{code:X4} ({ErrName(code)}) inLen={inLen} 累计失败={_decErr}");
        }

        // 常见 FDK 错误码名称（便于日志阅读）
        private static string ErrName(int code) => code switch
        {
            0x0000 => "OK",
            0x1001 => "TRANSPORT_SYNC_ERROR",
            0x1002 => "NOT_ENOUGH_BITS",
            0x2001 => "INVALID_HANDLE",
            0x2002 => "UNSUPPORTED_AOT",
            0x2003 => "UNSUPPORTED_FORMAT",
            0x2004 => "UNSUPPORTED_ER_FORMAT",
            0x2005 => "UNSUPPORTED_EPCONFIG",
            0x2006 => "UNSUPPORTED_MULTILAYER",
            0x2007 => "UNSUPPORTED_CHANNELCONFIG",
            0x2008 => "UNSUPPORTED_SAMPLINGRATE",
            0x2009 => "INVALID_SBR_CONFIG",
            0x200A => "SET_PARAM_FAIL",
            0x200C => "OUTPUT_BUFFER_TOO_SMALL",
            0x4001 => "TRANSPORT_ERROR",
            0x4002 => "PARSE_ERROR",
            0x4004 => "DECODE_FRAME_ERROR",
            0x4005 => "CRC_ERROR",
            _ => "UNKNOWN"
        };

        // 释放原生与固定资源
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_handle != IntPtr.Zero)
            {
                try { aacDecoder_Close(_handle); } catch { }
                _handle = IntPtr.Zero;
            }
            if (_ascHandle.IsAllocated) _ascHandle.Free();
            if (_inHandle.IsAllocated) _inHandle.Free();
        }
    }
}
