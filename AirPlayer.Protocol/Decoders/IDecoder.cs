using System;
using System.Threading.Tasks;
using AirPlayer.Protocol.Models.Enums;

namespace AirPlayer.Protocol
{
    public interface IDecoder
    {
        AudioFormat Type { get; }
        int GetOutputStreamLength();
        int Config(int sampleRate, int channels, int bitDepth, int frameLength);
        int DecodeFrame(byte[] input, ref byte[] output, int length);
    }
}
