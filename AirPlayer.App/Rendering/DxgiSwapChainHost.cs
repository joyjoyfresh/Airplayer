using System;
using System.Runtime.InteropServices;
using AirPlayer.Protocol.Utils;
using Microsoft.UI.Xaml.Controls;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AirPlayer.App.Rendering
{
    /// <summary>
    /// 封装 DXGI 翻转交换链的创建与 SwapChainPanel 绑定。
    /// 每帧由 VideoPresenter 取出后台缓冲，交给 Nv12VideoProcessor Blt，完毕后 Present。
    /// </summary>
    public sealed class DxgiSwapChainHost : IDisposable
    {
        // ──────────────────────────────────────────────────────────────────
        // 字段
        // ──────────────────────────────────────────────────────────────────

        private readonly GpuDevice _gpu;             // 共享 D3D11 设备
        private IDXGISwapChain1? _swapChain;         // 翻转交换链
        private int _width;                          // 当前交换链宽度
        private int _height;                         // 当前交换链高度

        // ISwapChainPanelNative 的 COM IID（WinAppSDK / Microsoft.UI.Xaml 版本）
        private static readonly Guid IID_ISwapChainPanelNative =
            new("63AAD0B8-7C24-40FF-85A8-640D944CC325");

        // ISwapChainPanelNative.SetSwapChain 的 vtable 函数委托
        // COM vtable: IUnknown(0=QI, 1=AddRef, 2=Release) + SetSwapChain(3)
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetSwapChainFn(IntPtr thisPtr, IntPtr swapChain);

        // ──────────────────────────────────────────────────────────────────
        // 公开属性
        // ──────────────────────────────────────────────────────────────────

        /// <summary>DXGI 交换链（供 VideoPresenter 调用 Present）</summary>
        public IDXGISwapChain1 SwapChain =>
            _swapChain ?? throw new InvalidOperationException("交换链尚未初始化");

        /// <summary>当前交换链宽度（像素）</summary>
        public int CurrentWidth => _width;

        /// <summary>当前交换链高度（像素）</summary>
        public int CurrentHeight => _height;

        // ──────────────────────────────────────────────────────────────────
        // 构造
        // ──────────────────────────────────────────────────────────────────

        /// <param name="gpu">共享 GPU 设备</param>
        public DxgiSwapChainHost(GpuDevice gpu)
        {
            _gpu = gpu; // 保存共享设备引用
        }

        // ──────────────────────────────────────────────────────────────────
        // 初始化：创建交换链并绑定面板
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// 创建交换链并将其绑定到 WinUI3 SwapChainPanel。
        /// 必须在 UI 线程调用（面板操作要求在 UI 线程）。
        /// </summary>
        /// <param name="panel">目标 SwapChainPanel</param>
        /// <param name="width">交换链宽度（像素），通常等于视频或面板宽度</param>
        /// <param name="height">交换链高度（像素）</param>
        public void Initialize(SwapChainPanel panel, int width, int height)
        {
            _width = width;
            _height = height;

            // 从 D3D11 设备取 DXGI 设备 → 适配器 → 工厂
            using IDXGIDevice dxgiDevice = _gpu.Device.QueryInterface<IDXGIDevice>();
            using IDXGIAdapter dxgiAdapter = dxgiDevice.GetAdapter();
            using IDXGIFactory2 factory = dxgiAdapter.GetParent<IDXGIFactory2>();

            // 描述翻转模型交换链（FlipSequential + B8G8R8A8_UNorm）
            var desc = new SwapChainDescription1
            {
                Width = (uint)width,                        // 交换链缓冲宽度
                Height = (uint)height,                      // 交换链缓冲高度
                Format = Format.B8G8R8A8_UNorm,             // BGRA8，XAML 合成要求
                Stereo = false,                             // 非立体视
                SampleDescription = new SampleDescription(1, 0), // 无多采样
                BufferUsage = Usage.RenderTargetOutput,     // 作为渲染目标
                BufferCount = 2,                            // 双缓冲
                Scaling = Scaling.Stretch,                  // 缩放策略
                SwapEffect = SwapEffect.FlipSequential,     // 翻转模型，低延迟
                AlphaMode = AlphaMode.Ignore,               // 不使用透明度
                Flags = SwapChainFlags.None
            };

            // 为合成层创建交换链（不绑定 HWND，由 ISwapChainPanelNative 接管）
            // Vortice 3.x：CreateSwapChainForComposition 返回 IDXGISwapChain1（3 参数，无 out）
            _swapChain = factory.CreateSwapChainForComposition(_gpu.Device, desc, null);

            // 通过 COM 接口将交换链挂载到 SwapChainPanel
            BindToPanel(panel);

            DiagLog.Write($"[SWP] 交换链已创建并绑定面板 {width}x{height}");
        }

        // ──────────────────────────────────────────────────────────────────
        // 面板绑定
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// 将交换链的原生指针交给面板的 ISwapChainPanelNative COM 接口。
        /// WinUI 3 的 SwapChainPanel 是 CsWinRT 投影对象，无法直接强转到 ComImport 接口，
        /// 必须通过 IUnknown → QueryInterface → vtable 手动调用。
        /// </summary>
        private void BindToPanel(SwapChainPanel panel)
        {
            // 1. 取原生 IUnknown 指针（CsWinRT 投影会将 IUnknown 委派给原生对象）
            IntPtr unkPtr = Marshal.GetIUnknownForObject(panel);
            try
            {
                // 2. 查询 ISwapChainPanelNative 接口（WinAppSDK 版 IID）
                Guid iid = IID_ISwapChainPanelNative; // readonly 字段需拷贝到局部变量才能传 ref
                int hr = Marshal.QueryInterface(unkPtr, ref iid, out IntPtr nativePtr);
                if (hr < 0)
                    throw new COMException($"[SWP] QI(ISwapChainPanelNative) 失败: 0x{hr:X8}", hr);
                try
                {
                    // 3. 通过 vtable 调用 SetSwapChain（索引 3 = IUnknown 3 个方法之后）
                    IntPtr vtable = Marshal.ReadIntPtr(nativePtr);
                    IntPtr fnPtr = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
                    var setSwapChain = (SetSwapChainFn)Marshal.GetDelegateForFunctionPointer(
                        fnPtr, typeof(SetSwapChainFn));
                    hr = setSwapChain(nativePtr, _swapChain!.NativePointer);
                    if (hr < 0)
                        throw new COMException($"[SWP] SetSwapChain 失败: 0x{hr:X8}", hr);
                }
                finally
                {
                    Marshal.Release(nativePtr); // 释放 QI 获得的引用
                }
            }
            finally
            {
                Marshal.Release(unkPtr); // 释放 GetIUnknownForObject 获得的引用
            }

            DiagLog.Write("[SWP] SwapChainPanel 绑定成功");
        }

        // ──────────────────────────────────────────────────────────────────
        // 后台缓冲访问
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// 取出索引 0 的后台缓冲纹理（调用方负责 Dispose）。
        /// VideoPresenter 在每帧 Blt 前调用，Blt 完成后 Present。
        /// </summary>
        public ID3D11Texture2D GetBackBuffer()
        {
            _swapChain!.GetBuffer<ID3D11Texture2D>(0, out ID3D11Texture2D backBuffer).CheckError();
            return backBuffer; // 调用方负责 Dispose
        }

        // ──────────────────────────────────────────────────────────────────
        // Resize
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// 面板尺寸变化时调用，重设交换链缓冲区大小。
        /// 必须确保 Nv12VideoProcessor 已释放所有输出视图后再调用。
        /// </summary>
        public void Resize(int newWidth, int newHeight)
        {
            if (newWidth == _width && newHeight == _height) return; // 无变化则跳过
            if (newWidth <= 0 || newHeight <= 0) return;

            _width = newWidth;
            _height = newHeight;

            // 释放后台缓冲引用后才能 ResizeBuffers（由调用方保证）
            _swapChain!.ResizeBuffers(
                bufferCount: 2,
                width: (uint)newWidth,
                height: (uint)newHeight,
                newFormat: Format.B8G8R8A8_UNorm,
                swapChainFlags: SwapChainFlags.None).CheckError();

            DiagLog.Write($"[SWP] ResizeBuffers → {newWidth}x{newHeight}");
        }

        // ──────────────────────────────────────────────────────────────────
        // 释放
        // ──────────────────────────────────────────────────────────────────

        public void Dispose()
        {
            _swapChain?.Dispose();  // 释放交换链
            _swapChain = null;
            DiagLog.Write("[SWP] DxgiSwapChainHost 已释放");
        }
    }
}
