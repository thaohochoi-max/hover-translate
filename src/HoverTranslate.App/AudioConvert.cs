using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace HoverTranslate.App;

// Quy đổi 1 đoạn PCM thô (định dạng WASAPI loopback trả về, thường là IEEE
// float 44.1/48kHz stereo) sang WAV 16kHz mono PCM16 chuẩn mà
// faster-whisper/Python wave module đọc trực tiếp được. Chỉ dùng 1 lần cho
// mỗi ĐOẠN hoàn chỉnh (không phải resample liên tục theo từng frame) nên
// dùng thẳng WdlResamplingSampleProvider (managed, không cần Media Foundation
// COM startup/shutdown, đơn giản hơn MediaFoundationResampler cho use case
// này).
internal static class AudioConvert
{
    public static byte[] ToWav16kMono(byte[] rawPcm, WaveFormat sourceFormat)
    {
        using var sourceStream = new RawSourceWaveStream(new MemoryStream(rawPcm), sourceFormat);
        ISampleProvider sampleProvider = sourceStream.ToSampleProvider();

        if (sampleProvider.WaveFormat.Channels == 2)
            sampleProvider = new StereoToMonoSampleProvider(sampleProvider) { LeftVolume = 0.5f, RightVolume = 0.5f };
        else if (sampleProvider.WaveFormat.Channels > 2)
            // MultiplexingSampleProvider (thử trước đây) cho ra audio hỏng -
            // xác nhận qua selftest-mic-e2e: mic thu được nội dung thật (RMS
            // giống hệt nhau ở cả 4 kênh, không câm), nhưng sau khi qua
            // MultiplexingSampleProvider thì Whisper không nhận diện được gì
            // cả (không phải sai từ - trống trơn). Tự trích kênh 0 thủ công,
            // không phụ thuộc cách map mặc định của NAudio.
            sampleProvider = new SingleChannelExtractSampleProvider(sampleProvider, channelIndex: 0);

        var resampled = new WdlResamplingSampleProvider(sampleProvider, 16000);
        IWaveProvider pcm16 = resampled.ToWaveProvider16();

        using var outputStream = new MemoryStream();
        WaveFileWriter.WriteWavFileToStream(outputStream, pcm16);
        return outputStream.ToArray();
    }

    private sealed class SingleChannelExtractSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _channelIndex;
        private readonly int _sourceChannels;
        private float[] _sourceBuffer = Array.Empty<float>();

        public SingleChannelExtractSampleProvider(ISampleProvider source, int channelIndex)
        {
            _source = source;
            _sourceChannels = source.WaveFormat.Channels;
            _channelIndex = channelIndex;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int sourceCount = count * _sourceChannels;
            if (_sourceBuffer.Length < sourceCount) _sourceBuffer = new float[sourceCount];
            int sourceSamplesRead = _source.Read(_sourceBuffer, 0, sourceCount);
            int framesRead = sourceSamplesRead / _sourceChannels;
            for (int i = 0; i < framesRead; i++)
                buffer[offset + i] = _sourceBuffer[i * _sourceChannels + _channelIndex];
            return framesRead;
        }
    }
}
