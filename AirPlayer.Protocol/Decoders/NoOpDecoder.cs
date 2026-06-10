using System;
using AirPlayer.Protocol.Models.Enums;

namespace AirPlayer.Protocol.Decoders
{
    public class NoOpDecoder : IDecoder
    {
        public AudioFormat Type => AudioFormat.Pcm;

        public int Config(int sampleRate, int channels, int bitDepth, int frameLength)
        {
            return 0;
        }

        public int DecodeFrame(byte[] input, ref byte[] output, int length)
        {
            return 0;
        }

        public int GetOutputStreamLength()
        {
            return 0;
        }

        public void Dispose() { }
    }
}
