using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AirPlayer.Protocol.Listeners;
using AirPlayer.Protocol.Models;
using Makaretu.Dns;

namespace AirPlayer.Protocol
{
    /// <summary>
    /// AirPlay 接收器核心：管理 mDNS 广播和 RTSP 会话，对外暴露视频/镜像事件
    /// </summary>
    public class AirPlayReceiver : IRtspReceiver, IAirPlayReceiver, IDisposable
    {
        public event EventHandler<decimal>? OnSetVolumeReceived;
        public event EventHandler<H264Data>? OnH264DataReceived;
        public event EventHandler? OnMirroringStartedReceived;
        public event EventHandler? OnMirroringStoppedReceived;

        public const string AirPlayType = "_airplay._tcp";
        public const string AirTunesType = "_raop._tcp";

        private MulticastService? _mdns = null;
        private AirTunesListener? _airTunesListener = null;
        private readonly string _instance;
        private readonly ushort _airTunesPort;
        private readonly ushort _airPlayPort;
        private readonly string _deviceId;

        public AirPlayReceiver(string instance, string deviceId = "11:22:33:44:55:66", ushort airTunesPort = 7000, ushort airPlayPort = 7001)
        {
            _instance = instance ?? throw new ArgumentNullException(nameof(instance));
            _deviceId = deviceId;
            _airTunesPort = airTunesPort;
            _airPlayPort = airPlayPort;
            _airTunesListener = new AirTunesListener(this, _airTunesPort, _airPlayPort, new Models.Configs.DumpConfig());
        }

        public async Task StartListeners(CancellationToken cancellationToken)
        {
            if (_airTunesListener != null)
            {
                await _airTunesListener.StartAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public Task StartMdnsAsync()
        {
            var rDeviceId = new Regex("^(([0-9a-fA-F][0-9a-fA-F]):){5}([0-9a-fA-F][0-9a-fA-F])$");
            var mDeviceId = rDeviceId.Match(_deviceId);
            if (!mDeviceId.Success)
            {
                throw new ArgumentException("Device id must be a mac address", _deviceId);
            }

            var deviceIdInstance = string.Join(string.Empty, mDeviceId.Groups[2].Captures) + mDeviceId.Groups[3].Value;

            _mdns = new MulticastService();
            var sd = new ServiceDiscovery(_mdns);

            var airTunes = new ServiceProfile($"{deviceIdInstance}@{_instance}", AirTunesType, _airTunesPort);
            airTunes.AddProperty("ch", "2");
            airTunes.AddProperty("cn", "0,1,2,3");
            airTunes.AddProperty("et", "0,3,5");
            airTunes.AddProperty("md", "0,1,2");
            airTunes.AddProperty("sr", "44100");
            airTunes.AddProperty("ss", "16");
            airTunes.AddProperty("da", "true");
            airTunes.AddProperty("sv", "false");
            airTunes.AddProperty("ft", "0x5A7FFFF7,0x1E");
            airTunes.AddProperty("am", "AppleTV5,3");
            airTunes.AddProperty("pk", "29fbb183a58b466e05b9ab667b3c429d18a6b785637333d3f0f3a34baa89f45e");
            airTunes.AddProperty("sf", "0x4");
            airTunes.AddProperty("tp", "UDP");
            airTunes.AddProperty("vn", "65537");
            airTunes.AddProperty("vs", "220.68");
            airTunes.AddProperty("vv", "2");

            var airPlay = new ServiceProfile(_instance, AirPlayType, _airPlayPort);
            airPlay.AddProperty("deviceid", _deviceId);
            airPlay.AddProperty("features", "0x5A7FFFF7,0x1E");
            airPlay.AddProperty("flags", "0x4");
            airPlay.AddProperty("model", "AppleTV5,3");
            airPlay.AddProperty("pk", "29fbb183a58b466e05b9ab667b3c429d18a6b785637333d3f0f3a34baa89f45e");
            airPlay.AddProperty("pi", "aa072a95-0318-4ec3-b042-4992495877d3");
            airPlay.AddProperty("srcvers", "220.68");
            airPlay.AddProperty("vv", "2");

            sd.Advertise(airTunes);
            sd.Advertise(airPlay);
            _mdns.Start();

            return Task.CompletedTask;
        }

        public void OnSetVolume(decimal volume)
        {
            OnSetVolumeReceived?.Invoke(this, volume);
        }

        public void OnData(H264Data data)
        {
            OnH264DataReceived?.Invoke(this, data);
        }

        public void OnPCMData(byte[] pcmData)
        {
            // v1 不处理音频，直接丢弃
        }

        public void OnAudioFlush()
        {
            // v1 不处理音频
        }

        public void OnMirroringStarted()
        {
            OnMirroringStartedReceived?.Invoke(this, EventArgs.Empty);
        }

        public void OnMirroringStopped()
        {
            OnMirroringStoppedReceived?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            _mdns?.Stop();
            _airTunesListener?.StopAsync().Wait();
        }
    }
}
