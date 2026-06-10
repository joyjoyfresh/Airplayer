using System;

namespace AirPlayer.Protocol.Models.Audio
{
    public class PcmData
    {
        public byte[] Data { get; set; }
        public int Length { get; set; }
        public ulong Pts { get; set; }
    }
}
