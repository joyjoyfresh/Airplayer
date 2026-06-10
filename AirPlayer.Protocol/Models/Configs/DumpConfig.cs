using System;

namespace AirPlayer.Protocol.Models.Configs
{
    public class DumpConfig
    {
        public bool DumpRawAudio { get; set; } = false;
        public bool DumpDecodedAudio { get; set; } = false;
        public bool DumpVideo { get; set; } = false;
    }
}
