using System;
using AirPlayer.Protocol.Utils;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace AirPlayer.App.Rendering
{
    /// <summary>
    /// 封装 ID3D11VideoProcessor：将 NV12 D3D11 纹理 GPU Blt 到交换链后台缓冲。
    /// 支持 BT.709 色彩空间转换、视频范围（16-235）→全范围（0-255）、
    /// 以及保持宽高比的信箱（letterbox）缩放。
    /// </summary>
    public sealed class Nv12VideoProcessor : IDisposable
    {
        private readonly GpuDevice _gpu;
        private ID3D11VideoDevice? _videoDevice;
        private ID3D11VideoContext? _videoContext;
        private ID3D11VideoProcessorEnumerator? _enumerator;
        private ID3D11VideoProcessor? _processor;

        private int _srcWidth;
        private int _srcHeight;
        private int _dstWidth;
        private int _dstHeight;

        private ID3D11RenderTargetView? _cachedRtv;
        private ID3D11VideoProcessorOutputView? _cachedOutputView;
        private IntPtr _cachedBackBufferPtr;

        public Nv12VideoProcessor(GpuDevice gpu)
        {
            _gpu = gpu;
            _videoDevice = _gpu.Device.QueryInterface<ID3D11VideoDevice>();
            _videoContext = _gpu.DeviceContext.QueryInterface<ID3D11VideoContext>();
            DiagLog.Write("[VPR] ID3D11VideoDevice/VideoContext 已获取");
        }

        public void Rebuild(int srcWidth, int srcHeight, int dstWidth, int dstHeight)
        {
            _enumerator?.Dispose();
            _enumerator = null;
            _processor?.Dispose();
            _processor = null;

            _cachedOutputView?.Dispose();
            _cachedOutputView = null;
            _cachedRtv?.Dispose();
            _cachedRtv = null;
            _cachedBackBufferPtr = 0;

            _srcWidth  = srcWidth;
            _srcHeight = srcHeight;
            _dstWidth  = dstWidth;
            _dstHeight = dstHeight;

            var contentDesc = new VideoProcessorContentDescription
            {
                InputFrameFormat  = VideoFrameFormat.Progressive,
                InputFrameRate    = new Rational(30, 1),
                InputWidth        = (uint)srcWidth,
                InputHeight       = (uint)srcHeight,
                OutputFrameRate   = new Rational(30, 1),
                OutputWidth       = (uint)dstWidth,
                OutputHeight      = (uint)dstHeight,
                Usage             = VideoUsage.PlaybackNormal
            };

            _enumerator = _videoDevice!.CreateVideoProcessorEnumerator(contentDesc);
            _processor = _videoDevice!.CreateVideoProcessor(_enumerator, 0);

            var inputCS = new VideoProcessorColorSpace
            {
                Usage         = 0,
                YCbCr_Matrix  = 1,
                YCbCr_xvYCC  = 0,
                Nominal_Range = 1
            };
            _videoContext!.VideoProcessorSetStreamColorSpace(_processor, 0, inputCS);

            var outputCS = new VideoProcessorColorSpace
            {
                Usage         = 0,
                RGB_Range     = 1,
                Nominal_Range = 2
            };
            _videoContext!.VideoProcessorSetOutputColorSpace(_processor, outputCS);

            DiagLog.Write($"[VPR] VideoProcessor 已重建 {srcWidth}x{srcHeight}→{dstWidth}x{dstHeight}");
        }

        public void Blt(ID3D11Texture2D nv12Texture, uint subresourceIndex, ID3D11Texture2D backBuffer)
        {
            if (_processor == null || _enumerator == null)
            {
                DiagLog.Write("[VPR] Blt 调用时 VideoProcessor 未初始化，跳过");
                return;
            }

            var inputViewDesc = new VideoProcessorInputViewDescription
            {
                FourCC        = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D     = new Texture2DVideoProcessorInputView
                {
                    MipSlice   = 0,
                    ArraySlice = subresourceIndex
                }
            };
            using ID3D11VideoProcessorInputView inputView =
                _videoDevice!.CreateVideoProcessorInputView(nv12Texture, _enumerator, inputViewDesc);

            IntPtr bbPtr = backBuffer.NativePointer;
            if (_cachedOutputView == null || _cachedBackBufferPtr != bbPtr)
            {
                _cachedOutputView?.Dispose();
                var outputViewDesc = new VideoProcessorOutputViewDescription
                {
                    ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                    Texture2D     = new Texture2DVideoProcessorOutputView { MipSlice = 0 }
                };
                _cachedOutputView = _videoDevice!.CreateVideoProcessorOutputView(backBuffer, _enumerator, outputViewDesc);
                _cachedBackBufferPtr = bbPtr;
            }

            ClearBackBuffer(backBuffer);

            RawRect destRect = CalcLetterboxRect(_srcWidth, _srcHeight, _dstWidth, _dstHeight);
            _videoContext!.VideoProcessorSetStreamDestRect(_processor, 0, true, destRect);

            var stream = new VideoProcessorStream
            {
                Enable       = true,
                InputSurface = inputView
            };

            _videoContext!.VideoProcessorBlt(
                _processor, _cachedOutputView, outputFrame: 0u, streamCount: 1u, streams: new[] { stream });
        }

        private void ClearBackBuffer(ID3D11Texture2D backBuffer)
        {
            if (_cachedRtv == null || _cachedBackBufferPtr != backBuffer.NativePointer)
            {
                _cachedRtv?.Dispose();
                _cachedRtv = _gpu.Device.CreateRenderTargetView(backBuffer, null);
            }
            _gpu.DeviceContext.ClearRenderTargetView(_cachedRtv, new Color4(0f, 0f, 0f, 1f));
        }

        private static RawRect CalcLetterboxRect(int srcW, int srcH, int dstW, int dstH)
        {
            if (srcW <= 0 || srcH <= 0)
                return new RawRect(0, 0, dstW, dstH);

            float videoAspect = (float)srcW / srcH;
            float panelAspect = (float)dstW / dstH;

            int x, y, w, h;
            if (panelAspect > videoAspect)
            {
                h = dstH;
                w = (int)(h * videoAspect);
                x = (dstW - w) / 2;
                y = 0;
            }
            else
            {
                w = dstW;
                h = (int)(w / videoAspect);
                x = 0;
                y = (dstH - h) / 2;
            }

            return new RawRect(x, y, x + w, y + h);
        }

        /// <summary>
        /// 释放缓存的输出视图和 RTV（ResizeBuffers 前必须调用，否则后台缓冲仍被引用）。
        /// </summary>
        public void ReleaseCachedViews()
        {
            _cachedOutputView?.Dispose();
            _cachedOutputView = null;
            _cachedRtv?.Dispose();
            _cachedRtv = null;
            _cachedBackBufferPtr = 0;
        }

        public void Dispose()
        {
            _processor?.Dispose();
            _enumerator?.Dispose();
            _cachedOutputView?.Dispose();
            _cachedRtv?.Dispose();
            _videoContext?.Dispose();
            _videoDevice?.Dispose();

            _processor      = null;
            _enumerator     = null;
            _cachedOutputView = null;
            _cachedRtv      = null;
            _cachedBackBufferPtr = 0;
            _videoContext   = null;
            _videoDevice    = null;

            DiagLog.Write("[VPR] Nv12VideoProcessor 已释放");
        }
    }
}
