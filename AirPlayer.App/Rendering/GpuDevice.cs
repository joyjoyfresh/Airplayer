using System;
using System.Runtime.InteropServices;
using AirPlayer.Protocol.Utils;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using SharpGen.Runtime;

namespace AirPlayer.App.Rendering
{
    /// <summary>
    /// 持有全管线共享的 D3D11 设备、设备上下文以及 MF DXGI 设备管理器。
    /// 解码器、视频处理器、交换链三者共用同一 D3D11 设备，全程 GPU 内流转。
    /// <para>生命周期：在 VideoPresenter 初始化时创建，随 VideoPresenter 一同释放。</para>
    /// </summary>
    public sealed class GpuDevice : IDisposable
    {
        // ──────────────────────────────────────────────────────────────────
        // 公开属性（只读，供其他组件使用）
        // ──────────────────────────────────────────────────────────────────

        /// <summary>共享的 D3D11 设备</summary>
        public ID3D11Device Device { get; private set; }

        /// <summary>即时设备上下文（绘制/复制命令在此提交）</summary>
        public ID3D11DeviceContext DeviceContext { get; private set; }

        /// <summary>MF DXGI 设备管理器，用于将 D3D11 设备注册给硬件解码器</summary>
        public IMFDXGIDeviceManager DeviceManager { get; private set; }

        // ──────────────────────────────────────────────────────────────────
        // 构造
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// 创建 D3D11 设备（BgraSupport + VideoSupport），开启多线程保护，
        /// 并创建/注册 DXGI 设备管理器。
        /// </summary>
        public GpuDevice()
        {
            // 创建设备所需的功能标志
            // BgraSupport：XAML/合成层要求；VideoSupport：视频解码/处理接口要求
            DeviceCreationFlags flags =
                DeviceCreationFlags.BgraSupport |
                DeviceCreationFlags.VideoSupport;

            // 优先使用的功能级别（从高到低）
            FeatureLevel[] featureLevels = new[]
            {
                FeatureLevel.Level_11_1,    // 首选 D3D11.1
                FeatureLevel.Level_11_0,    // 备选 D3D11.0
                FeatureLevel.Level_10_1,    // 低端 GPU 回落
                FeatureLevel.Level_10_0,
            };

            // 创建硬件 D3D11 设备；失败则抛异常（由调用方捕获并决策回退）
            Result hr = D3D11.D3D11CreateDevice(
                adapter: null,                          // null = 系统默认 GPU
                driverType: DriverType.Hardware,        // 硬件驱动
                flags: flags,
                featureLevels: featureLevels,
                out ID3D11Device device,
                out FeatureLevel selectedLevel,
                out ID3D11DeviceContext context);

            if (hr.Failure)
                throw new InvalidOperationException($"[GPU] D3D11CreateDevice 失败: 0x{hr.Code:X8}");

            Device = device;           // 保存设备
            DeviceContext = context;   // 保存即时上下文

            DiagLog.Write($"[GPU] D3D11 设备已创建，功能级别 = {selectedLevel}");

            // ── 开启多线程保护 ──────────────────────────────────────────────
            // MF 解码线程与渲染线程共用同一设备上下文，必须开启保护
            using (ID3D11Multithread mt = Device.QueryInterface<ID3D11Multithread>())
            {
                mt.SetMultithreadProtected(true); // 开启多线程互斥锁
                DiagLog.Write("[GPU] 多线程保护已开启");
            }

            // ── 创建并注册 DXGI 设备管理器 ──────────────────────────────────
            // MFCreateDXGIDeviceManager() 无参版本：内部保存 resetToken 为 ResetToken 属性
            IMFDXGIDeviceManager manager = MediaFactory.MFCreateDXGIDeviceManager();

            // ResetDevice(ComObject) 单参版本：自动使用内部 ResetToken
            // ID3D11Device 继承 ComObject，可直接传入
            manager.ResetDevice(Device);

            DeviceManager = manager; // 保存设备管理器
            DiagLog.Write("[GPU] DXGI 设备管理器已创建并注册设备");
        }

        // ──────────────────────────────────────────────────────────────────
        // 释放
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// 按顺序释放：设备管理器 → 上下文 → 设备。
        /// 调用前须确保所有使用此设备的组件（解码器、处理器、交换链）已先释放。
        /// </summary>
        public void Dispose()
        {
            DeviceManager?.Dispose();   // 先释放设备管理器（可能持有内部引用）
            DeviceContext?.Dispose();   // 释放即时上下文
            Device?.Dispose();          // 最后释放设备本体
            DiagLog.Write("[GPU] GpuDevice 已释放");
        }
    }
}
