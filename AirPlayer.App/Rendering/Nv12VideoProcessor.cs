using System;
using System.Collections.Generic;
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

        /// <summary>铺满模式：true = 裁切源填满目标；false = 保持比例信箱/柱箱（默认）</summary>
        public volatile bool FillMode;

        private ID3D11RenderTargetView? _cachedRtv;
        private ID3D11VideoProcessorOutputView? _cachedOutputView;
        private IntPtr _cachedBackBufferPtr;

        // 输入视图缓存：解码器输出的是固定纹理数组（同一纹理、不同数组层），
        // 按(纹理指针,数组层)缓存输入视图，避免每帧重建（每帧 CreateVideoProcessorInputView 是已知高 CPU 坑）。
        private readonly Dictionary<(IntPtr Ptr, uint Slice), ID3D11VideoProcessorInputView> _inputViewCache = new();

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

            // 枚举器重建后，旧输入视图失效，必须清空缓存
            ClearInputViewCache();

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

            // 输入视图：优先取缓存，未命中才创建（解码器纹理数组稳定，缓存可跨帧复用）
            var key = (nv12Texture.NativePointer, subresourceIndex);
            if (!_inputViewCache.TryGetValue(key, out ID3D11VideoProcessorInputView? inputView))
            {
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
                inputView = _videoDevice!.CreateVideoProcessorInputView(nv12Texture, _enumerator, inputViewDesc);
                _inputViewCache[key] = inputView; // 缓存，勿在此处 Dispose（由 ClearInputViewCache 统一释放）
            }

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

            if (FillMode)
            {
                var (srcRect, destRect) = CalcFillRects(_srcWidth, _srcHeight, _dstWidth, _dstHeight);
                _videoContext!.VideoProcessorSetStreamSourceRect(_processor, 0, true, srcRect);
                _videoContext!.VideoProcessorSetStreamDestRect(_processor, 0, true, destRect);
            }
            else
            {
                // 信箱模式同样裁掉纹理对齐 padding：H.264 解码纹理宽度向上对齐到 16 的倍数
                // （如 886→896），右侧存在 padding。若用整个对齐纹理作源，padding 黑边会被映射进
                // 画面造成横向压缩——切到铺满（已裁 padding）时显得"横向拉伸"。
                // 这里与铺满统一：只取有效内容 _srcWidth x _srcHeight 作为源。
                RawRect srcRect = new RawRect(0, 0, _srcWidth, _srcHeight);
                _videoContext!.VideoProcessorSetStreamSourceRect(_processor, 0, true, srcRect);
                RawRect destRect = CalcLetterboxRect(_srcWidth, _srcHeight, _dstWidth, _dstHeight);
                _videoContext!.VideoProcessorSetStreamDestRect(_processor, 0, true, destRect);
            }

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

        /// <summary>信箱/柱箱：保持宽高比，不足部分留黑边。</summary>
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
        /// 铺满裁切：裁切源中心部分以匹配目标宽高比，输出目标为整个缓冲区。
        /// </summary>
        private static (RawRect src, RawRect dst) CalcFillRects(int srcW, int srcH, int dstW, int dstH)
        {
            if (srcW <= 0 || srcH <= 0)
                return (new RawRect(0, 0, srcW, srcH), new RawRect(0, 0, dstW, dstH));

            float srcAspect = (float)srcW / srcH;
            float dstAspect = (float)dstW / dstH;

            RawRect srcRect;
            if (srcAspect > dstAspect)
            {
                // 源比目标更宽 → 裁切源的左右
                int cropW = (int)(srcH * dstAspect);
                int cropX = (srcW - cropW) / 2;
                srcRect = new RawRect(cropX, 0, cropX + cropW, srcH);
            }
            else
            {
                // 源比目标更高 → 裁切源的上下
                int cropH = (int)(srcW / dstAspect);
                int cropY = (srcH - cropH) / 2;
                srcRect = new RawRect(0, cropY, srcW, cropY + cropH);
            }

            return (srcRect, new RawRect(0, 0, dstW, dstH));
        }

        /// <summary>
        /// 释放缓存的输出视图和 RTV（ResizeBuffers 前必须调用，否则后台缓冲仍被引用）。
        /// 注意：输入视图引用的是解码器纹理而非交换链，故此处不清，避免窗口缩放时丢失缓存。
        /// </summary>
        public void ReleaseCachedViews()
        {
            _cachedOutputView?.Dispose();
            _cachedOutputView = null;
            _cachedRtv?.Dispose();
            _cachedRtv = null;
            _cachedBackBufferPtr = 0;
        }

        /// <summary>释放并清空输入视图缓存（枚举器重建或销毁时调用）。</summary>
        private void ClearInputViewCache()
        {
            foreach (var v in _inputViewCache.Values)
                v.Dispose();
            _inputViewCache.Clear();
        }

        public void Dispose()
        {
            ClearInputViewCache();
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
