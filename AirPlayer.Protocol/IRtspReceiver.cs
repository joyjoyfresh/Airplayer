using System;
using AirPlayer.Protocol.Models;
using AirPlayer.Protocol.Models.Audio;

namespace AirPlayer.Protocol
{
    /// <summary>
    /// RTSP 接收器回调接口（内部协议层使用）
    /// </summary>
    public interface IRtspReceiver
    {
        void OnSetVolume(decimal volume);
        void OnData(H264Data data);
        void OnPCMData(PcmData pcmData);
        void OnAudioFlush();
        void OnMirroringStarted();
        void OnMirroringStopped();
    }
}
