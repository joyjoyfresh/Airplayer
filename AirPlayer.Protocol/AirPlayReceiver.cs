using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AirPlayer.Protocol.Listeners;
using AirPlayer.Protocol.Models;
using AirPlayer.Protocol.Models.Audio;
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
        public event EventHandler<PcmData>? OnPcmDataReceived;
        public event EventHandler? OnAudioFlushReceived;
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

        public AirPlayReceiver(string instance, int preferredWidth = 1920, int preferredHeight = 1080, ushort airTunesPort = 7020, ushort airPlayPort = 7021)
        {
            _instance = instance ?? throw new ArgumentNullException(nameof(instance));
            _airTunesPort = airTunesPort;
            _airPlayPort = airPlayPort;

            // 加载或生成本机随机身份（MAC + ED25519 种子），持久化后重启保持稳定
            var (deviceId, seed) = LoadOrCreateIdentity();
            _deviceId = deviceId;
            _airTunesListener = new AirTunesListener(this, _airTunesPort, _airPlayPort, new Models.Configs.DumpConfig(), _instance, _deviceId, seed, preferredWidth, preferredHeight);
        }

        /// <summary>
        /// 加载本机持久化身份；不存在时生成随机 MAC 与 ED25519 种子并保存
        /// </summary>
        private static (string deviceId, byte[] seed) LoadOrCreateIdentity()
        {
            // 身份文件存放在本地 AppData，保证每台机器唯一且重启后不变
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirPlayer");
            var file = System.IO.Path.Combine(dir, "identity.dat");
            try
            {
                if (System.IO.File.Exists(file))
                {
                    var lines = System.IO.File.ReadAllLines(file); // 第一行 MAC，第二行 Base64 种子
                    if (lines.Length >= 2)
                    {
                        var savedId = lines[0].Trim();
                        var savedSeed = Convert.FromBase64String(lines[1].Trim());
                        if (savedSeed.Length == 32 && !string.IsNullOrWhiteSpace(savedId))
                        {
                            return (savedId, savedSeed);
                        }
                    }
                }
            }
            catch { /* 读取损坏则重新生成 */ }

            // 生成新的随机种子
            var newSeed = new byte[32];
            System.Security.Cryptography.RandomNumberGenerator.Fill(newSeed);

            // 生成随机 MAC（置位「本地管理」位、清除「组播」位）
            var mac = new byte[6];
            System.Security.Cryptography.RandomNumberGenerator.Fill(mac);
            mac[0] = (byte)((mac[0] & 0xFE) | 0x02);
            var newDeviceId = string.Join(":", mac.Select(b => b.ToString("X2")));

            try
            {
                System.IO.Directory.CreateDirectory(dir); // 确保目录存在
                System.IO.File.WriteAllLines(file, new[] { newDeviceId, Convert.ToBase64String(newSeed) }); // 持久化身份
            }
            catch { /* 写入失败不影响本次运行，仅下次重启会变 */ }

            return (newDeviceId, newSeed);
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

            // 使用已绑定的控制端口发布服务，避免 iOS 连接到尚未监听的数据端口
            int actualAirTunesPort = _airTunesListener != null ? _airTunesListener.LocalPort : _airTunesPort;
            var publicKeyHex = _airTunesListener != null ? _airTunesListener.PublicKeyHex : string.Empty;

            var airTunes = new ServiceProfile($"{deviceIdInstance}@{_instance}", AirTunesType, (ushort)actualAirTunesPort);
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
            airTunes.AddProperty("pk", publicKeyHex);
            airTunes.AddProperty("pw", "false");
            airTunes.AddProperty("sf", "0x4");
            airTunes.AddProperty("tp", "UDP");
            airTunes.AddProperty("vn", "65537");
            airTunes.AddProperty("vs", "220.68");
            airTunes.AddProperty("vv", "2");

            var airPlay = new ServiceProfile(_instance, AirPlayType, (ushort)actualAirTunesPort);
            airPlay.AddProperty("deviceid", _deviceId);
            airPlay.AddProperty("features", "0x5A7FFFF7,0x1E");
            airPlay.AddProperty("pw", "false");
            airPlay.AddProperty("flags", "0x4");
            airPlay.AddProperty("model", "AppleTV5,3");
            airPlay.AddProperty("pk", publicKeyHex);
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

        public void OnPCMData(PcmData pcmData)
        {
            OnPcmDataReceived?.Invoke(this, pcmData);
        }

        public void OnAudioFlush()
        {
            OnAudioFlushReceived?.Invoke(this, EventArgs.Empty);
        }

        public void OnMirroringStarted()
        {
            OnMirroringStartedReceived?.Invoke(this, EventArgs.Empty);
        }

        public void OnMirroringStopped()
        {
            OnMirroringStoppedReceived?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>主动停止当前的镜像投影会话</summary>
        public void StopActiveMirroring()
        {
            // 关闭所有正在连接的 RTSP 控制会话，迫使 Apple 发送端立即感知连接断开并退出投屏状态
            _airTunesListener?.CloseActiveConnections();

            foreach (var session in Services.SessionManager.Current.GetActiveSessions())
            {
                if (session.MirroringListener != null)
                {
                    try { session.MirroringListener.StopAsync().Wait(); } catch { }
                    session.MirroringListener = null;
                }
                if (session.StreamingListener != null)
                {
                    try { session.StreamingListener.StopAsync().Wait(); } catch { }
                    session.StreamingListener = null;
                }
                if (session.AudioControlListener != null)
                {
                    try { session.AudioControlListener.StopAsync().Wait(); } catch { }
                    session.AudioControlListener = null;
                }
                session.SpsPps = null;
                session.StreamConnectionId = null;
                session.MirroringSession = null;
                session.AudioFormat = Models.Enums.AudioFormat.Unknown;
            }
            OnMirroringStopped();
        }

        public void Dispose()
        {
            _mdns?.Stop();
            _airTunesListener?.StopAsync().Wait();
        }
    }
}
