using System;
using System.Threading;
using System.Threading.Tasks;
using AirPlayer.Protocol.Models;
using AirPlayer.Protocol.Models.Audio;

namespace AirPlayer.Protocol
{
    /// <summary>
    /// AirPlay 接收器对外暴露的接口（供 WinUI3 App 订阅事件）
    /// </summary>
    public interface IAirPlayReceiver
    {
        /// <summary>音量变更事件</summary>
        event EventHandler<decimal> OnSetVolumeReceived;
        /// <summary>H264 视频帧事件</summary>
        event EventHandler<H264Data> OnH264DataReceived;
        /// <summary>PCM 音频帧事件</summary>
        event EventHandler<PcmData> OnPcmDataReceived;
        /// <summary>音频刷新事件（Seek / 跳转时触发）</summary>
        event EventHandler OnAudioFlushReceived;
        /// <summary>镜像开始事件</summary>
        event EventHandler OnMirroringStartedReceived;
        /// <summary>镜像停止事件</summary>
        event EventHandler OnMirroringStoppedReceived;

        /// <summary>启动 RTSP/TCP 监听器</summary>
        Task StartListeners(CancellationToken cancellationToken);
        /// <summary>启动 mDNS 广播服务发现</summary>
        Task StartMdnsAsync();
    }
}
