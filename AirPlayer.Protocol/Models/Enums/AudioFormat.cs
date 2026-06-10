using System;

namespace AirPlayer.Protocol.Models.Enums
{
    public enum AudioFormat
    {
        Unknown = -1,
        Pcm = 0x0,
        AppleLossless = 0x40000,
        ALAC = 0x40000,
        AacMain = 0x400000,
        AAC = 0x400000,
        AacEld = 0x1000000
    }
}
