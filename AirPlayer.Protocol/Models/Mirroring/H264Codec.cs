using System;

namespace AirPlayer.Protocol.Models
{
    /// <summary>
    /// H264 编解码器参数结构（从 PayloadType=1 的镜像数据包解析）
    /// </summary>
    public struct H264Codec
    {
        public byte Compatibility;
        public short LengthOfPps;
        public short LengthOfSps;
        public byte Level;
        public short NumberOfPps;
        public byte[] PictureParameterSet;
        public byte ProfileHigh;
        public byte Reserved3AndSps;
        public byte Reserved6AndNal;
        public byte[] SequenceParameterSet;
        public byte Version;
    }
}
