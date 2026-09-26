using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace HoverTranslate.App;

internal enum AudioSourceKind { SystemAudio, Microphone }

// Bắt System Audio qua WASAPI loopback, hoặc Microphone qua WASAPI capture
// thường (NAudio) - cùng họ API (WasapiCapture là lớp cha của
// WasapiLoopbackCapture trong NAudio), chỉ khác device/DataFlow nên dùng
// chung 1 field kiểu lớp cha, không cần 2 đường code riêng.
internal sealed class AudioCaptureService : IDisposable
{
    private WasapiCapture? _capture;

    public event Action<byte[], WaveFormat>? DataAvailable;
    public event Action<Exception?>? Stopped;

    public bool IsCapturing => _capture is not null;

    // Trả về false (không throw) nếu không tìm được thiết bị phát/thu mặc
    // định - spec mục 12: "Không tìm thấy audio device" phải báo được, không
    // crash.
    public bool Start(AudioSourceKind source = AudioSourceKind.SystemAudio)
    {
        try
        {
            var enumerator = new MMDeviceEnumerator();
            _capture = source switch
            {
                AudioSourceKind.Microphone => new WasapiCapture(enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)),
                _ => new WasapiLoopbackCapture(enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)),
            };
            _capture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded > 0)
                {
                    // Copy vì buffer nội bộ của NAudio bị dùng lại giữa các lần gọi.
                    var chunk = new byte[e.BytesRecorded];
                    Array.Copy(e.Buffer, chunk, e.BytesRecorded);
                    DataAvailable?.Invoke(chunk, _capture.WaveFormat);
                }
            };
            _capture.RecordingStopped += (_, e) =>
            {
                Stopped?.Invoke(e.Exception);
                _capture?.Dispose();
                _capture = null;
            };

            _capture.StartRecording();
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("AudioCaptureService.Start", ex);
            _capture = null;
            return false;
        }
    }

    public void Stop()
    {
        try { _capture?.StopRecording(); }
        catch (Exception ex) { CrashLog.Write("AudioCaptureService.Stop", ex); }
    }

    public void Dispose() => Stop();
}
