using System;
using System.IO;

namespace AirPlayer.Protocol.Models
{
    /// <summary>
    /// AirPlay 镜像数据包头（128 字节）
    /// </summary>
    public class MirroringHeader
    {
        /// <summary>Payload 大小</summary>
        public int PayloadSize { get; }
        /// <summary>Payload 类型（0=视频帧, 1=编解码器配置）</summary>
        public short PayloadType { get; }
        /// <summary>Payload 选项</summary>
        public short PayloadOption { get; }
        /// <summary>NTP 时间戳</summary>
        public long PayloadNtp { get; }
        /// <summary>PTS 时间戳（微秒）</summary>
        public long PayloadPts { get; }
        /// <summary>源宽度</summary>
        public int WidthSource { get; }
        /// <summary>源高度</summary>
        public int HeightSource { get; }
        /// <summary>输出宽度</summary>
        public int Width { get; }
        /// <summary>输出高度</summary>
        public int Height { get; }

        public MirroringHeader(byte[] header)
        {
            var mem = new MemoryStream(header);
            using (var reader = new BinaryReader(mem))
            {
                PayloadSize = (int)reader.ReadUInt32();
                PayloadType = (short)(reader.ReadUInt16() & 0xff);
                PayloadOption = (short)reader.ReadUInt16();

                if (PayloadType == 0)
                {
                    PayloadNtp = (long)reader.ReadUInt64();
                    PayloadPts = NtpToPts(PayloadNtp);
                }
                if (PayloadType == 1)
                {
                    mem.Position = 40;
                    WidthSource = (int)reader.ReadSingle();
                    HeightSource = (int)reader.ReadSingle();

                    mem.Position = 56;
                    Width = (int)reader.ReadSingle();
                    Height = (int)reader.ReadSingle();
                }
            }
        }

        /// <summary>将 NTP 时间戳转换为 PTS（微秒）</summary>
        private long NtpToPts(long ntp)
        {
            long seconds = (ntp >> 32) & 0xffffffff;
            long fraction = ntp & 0xffffffff;
            return (seconds * 1000000) + ((fraction * 1000000) >> 32);
        }
    }
}
