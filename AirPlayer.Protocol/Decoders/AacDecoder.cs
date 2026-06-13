using System;
using System.Runtime.InteropServices;
using AirPlayer.Protocol.Models.Enums;
using AirPlayer.Protocol.Utils;
using Vortice.MediaFoundation;
using SharpGen.Runtime;

namespace AirPlayer.Protocol.Decoders
{
    /// <summary>
    /// 基于 Media Foundation MFT 的 AAC 解码器。
    /// 输入 AAC 帧，输出 L16 PCM（44100Hz, 2ch, 16-bit）。
    /// </summary>
    public sealed class AacDecoder : IDecoder, IDisposable
    {
        // Microsoft AAC 解码器 MFT 的 CLSID
        private static readonly Guid CLSID_CMSAACDecMFT = new Guid("2EEB4ADF-4558-4D24-BEF7-23F434336868");

        // MFMediaType_Audio = FOURCC('audt')
        private static readonly Guid MFMediaType_Audio = new Guid("73647561-0000-0010-8000-00AA00389B71");
        // MFAudioFormat_AAC
        private static readonly Guid MFAudioFormat_AAC = new Guid("00001610-0000-0010-8000-00AA00389B71");
        // MFAudioFormat_PCM
        private static readonly Guid MFAudioFormat_PCM = new Guid("00000001-0000-0010-8000-00AA00389B71");

        // MF_MT_AUDIO_SAMPLES_PER_SECOND
        private static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new Guid("5DFE7C6E-3F70-4021-A2D3-98F60153D1F9");
        // MF_MT_AUDIO_NUM_CHANNELS
        private static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new Guid("B7E5F0F9-780C-493F-B04F-81C1F48CC60E");
        // MF_MT_AUDIO_BITS_PER_SAMPLE
        private static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new Guid("F2DECC5E-5C3C-4858-A502-B43C688504F2");

        private const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);

        private IMFTransform? _mft;
        private int _outputBufferSize;
        private bool _disposed;

        public AudioFormat Type => AudioFormat.AacMain;

        public int GetOutputStreamLength()
        {
            // 最大 AAC 帧输出：1024 samples * 2 channels * 2 bytes (16-bit)
            return 1024 * 2 * 2;
        }

        public int Config(int sampleRate, int channels, int bitDepth, int frameLength)
        {
            try
            {
                // 创建 AAC 解码器 MFT
                _mft = CreateDecoder(CLSID_CMSAACDecMFT);
                DiagLog.Write("[AAC] MFT 实例已创建");

                // 设置输入类型：AAC
                var inType = MediaFactory.MFCreateMediaType();
                inType.Set(MediaTypeAttributeKeys.MajorType, MFMediaType_Audio);
                inType.Set(MediaTypeAttributeKeys.Subtype, MFAudioFormat_AAC);
                inType.Set(MF_MT_AUDIO_SAMPLES_PER_SECOND, (int)sampleRate);
                inType.Set(MF_MT_AUDIO_NUM_CHANNELS, (int)channels);
                _mft.SetInputType(0, inType, 0);
                DiagLog.Write("[AAC] 输入类型已设置");

                // 设置输出类型：PCM 16-bit
                var outType = MediaFactory.MFCreateMediaType();
                outType.Set(MediaTypeAttributeKeys.MajorType, MFMediaType_Audio);
                outType.Set(MediaTypeAttributeKeys.Subtype, MFAudioFormat_PCM);
                outType.Set(MF_MT_AUDIO_SAMPLES_PER_SECOND, (int)sampleRate);
                outType.Set(MF_MT_AUDIO_NUM_CHANNELS, (int)channels);
                outType.Set(MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
                _mft.SetOutputType(0, outType, 0);
                DiagLog.Write("[AAC] 输出类型已设置");

                // 获取输出缓冲大小
                var outputStreamInfo = _mft.GetOutputStreamInfo(0);
                _outputBufferSize = Math.Max((int)outputStreamInfo.Size, GetOutputStreamLength());
                DiagLog.Write($"[AAC] 输出缓冲大小: {_outputBufferSize}");

                // 开始流
                _mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
                _mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

                return 0;
            }
            catch (Exception ex)
            {
                DiagLog.Write($"[AAC] Config 失败: {ex.Message}");
                return -1;
            }
        }

        public int DecodeFrame(byte[] input, ref byte[] output, int length)
        {
            if (_mft == null) return -1;

            try
            {
                // 构造输入样本并送入解码器（使用 input.Length 而非 length）
                // length 是输出缓冲大小（4096），不是输入帧大小；AAC 帧通常只有 100-400 字节，
                // 用错误的 length 会导致 Marshal.Copy 越界抛异常 → 每帧解码失败 → 无声音
                var inputSample = CreateSampleFromBytes(input, input.Length);
                try
                {
                    _mft.ProcessInput(0, inputSample, 0);
                }
                catch (SharpGenException ex)
                {
                    DiagLog.Write($"[AAC] ProcessInput 异常: 0x{ex.ResultCode.Code:X8}");
                    return -1;
                }
                finally
                {
                    inputSample.Dispose();
                }

                // 尝试取出解码后的 PCM
                var outBuffer = MediaFactory.MFCreateMemoryBuffer(_outputBufferSize);
                var outSample = MediaFactory.MFCreateSample();
                outSample.AddBuffer(outBuffer);
                outBuffer.Dispose();

                var dataBuffer = new OutputDataBuffer
                {
                    StreamID = 0,
                    Sample = outSample
                };

                Result result;
                try
                {
                    result = _mft.ProcessOutput(ProcessOutputFlags.None, 1, ref dataBuffer, out ProcessOutputStatus _);
                }
                catch (SharpGenException ex)
                {
                    result = ex.ResultCode;
                }

                if (result.Code == MF_E_TRANSFORM_NEED_MORE_INPUT)
                {
                    outSample.Dispose();
                    return 0; // 需要更多输入，非错误
                }

                if (result.Failure)
                {
                    outSample.Dispose();
                    return -1;
                }

                // 提取 PCM 数据
                using var buffer = dataBuffer.Sample!.ConvertToContiguousBuffer();
                buffer.Lock(out IntPtr data, out int _, out int curLen);
                try
                {
                    if (output.Length < curLen)
                        output = new byte[curLen];
                    Marshal.Copy(data, output, 0, curLen);
                DiagLog.Write($"[AAC-DECODE] 输出 PCM len={curLen}");
                }
                finally
                {
                    buffer.Unlock();
                }

                outSample.Dispose();
                return curLen;
            }
            catch (Exception ex)
            {
                DiagLog.Write($"[AAC] DecodeFrame 异常: {ex.Message}");
                return -1;
            }
        }

        /// <summary>通过 CLSID 创建 MFT，并 QueryInterface 到 IMFTransform</summary>
        private static IMFTransform CreateDecoder(Guid clsid)
        {
            Guid iidTransform = new Guid("BF94C121-5B05-4E6F-8000-BA598961414D");
            Type t = System.Type.GetTypeFromCLSID(clsid, throwOnError: true)!;
            object obj = Activator.CreateInstance(t)!;
            IntPtr unk = Marshal.GetIUnknownForObject(obj);
            try
            {
                int hr = Marshal.QueryInterface(unk, ref iidTransform, out IntPtr ppv);
                if (hr < 0)
                    throw new InvalidOperationException($"QueryInterface(IMFTransform) 失败: 0x{hr:X8}");
                return new IMFTransform(ppv);
            }
            finally
            {
                Marshal.Release(unk);
            }
        }

        /// <summary>用字节数组创建 IMFSample</summary>
        private static IMFSample CreateSampleFromBytes(byte[] data, int length)
        {
            var buffer = MediaFactory.MFCreateMemoryBuffer(length);
            buffer.Lock(out IntPtr ptr, out int _, out int _);
            Marshal.Copy(data, 0, ptr, length);
            buffer.Unlock();
            buffer.CurrentLength = length;

            var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            buffer.Dispose();
            return sample;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _mft?.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
                _mft?.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
            }
            catch { }

            _mft?.Dispose();
            _mft = null;
        }
    }
}
