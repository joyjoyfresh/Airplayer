using System;
using System.Runtime.InteropServices;

namespace AirPlayer.App.Interop
{
    /// <summary>
    /// WinUI3 SwapChainPanel 的原生 COM 接口，用于将 DXGI 交换链绑定到 XAML 面板。
    /// IID 63AAD0B8-7C24-40FF-85A8-640D944CC325 对应 WinAppSDK (Microsoft.UI.Xaml) 版本。
    /// </summary>
    [ComImport]
    [Guid("63AAD0B8-7C24-40FF-85A8-640D944CC325")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ISwapChainPanelNative
    {
        /// <summary>将 DXGI 交换链绑定到此面板</summary>
        /// <param name="swapChain">IDXGISwapChain 的原生指针</param>
        /// <returns>HRESULT</returns>
        [PreserveSig]
        int SetSwapChain(IntPtr swapChain);
    }
}
