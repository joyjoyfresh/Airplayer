using System;
using AirPlayer.Protocol.Models;

namespace AirPlayer.Protocol
{
    /// <summary>
    /// RTSP 接收器回调接口（内部协议层使用）
    /// </summary>
    public interface IRtspReceiver
    {
        void OnSetVolume(decimal volume);
        void OnData(H264Data data);
        void OnPCMData(byte[] pcmData);
        void OnAudioFlush();
        void OnMirroringStarted();
        void OnMirroringStopped();
    }
}
