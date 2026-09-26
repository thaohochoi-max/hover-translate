using NAudio.Wave;

namespace HoverTranslate.App;

// Điều phối: AudioCaptureService (capture) -> phân đoạn theo khoảng lặng
// (thay vì streaming liên tục - xem plan) -> SpeechRecognitionService (STT)
// -> Program.TranslatePublicAsync (TÁI SỬ DỤNG pipeline dịch có sẵn, không
// tạo engine mới) -> sự kiện cho LiveTranslateWindow/SubtitleOverlay hiển thị.
//
// KHÔNG chạy trong Program.Tick() (vòng lặp hover 50ms) - đây là 1 luồng xử
// lý độc lập, chỉ hoạt động khi người dùng bấm Start ở màn Live Translate.
internal sealed class LiveAudioService : IDisposable
{
    private const double SilenceGapMs = 450;
    private const double MaxSegmentMs = 6000;
    private const double MinSegmentMs = 600;
    private const float EnergyThreshold = 0.015f;

    private readonly AudioCaptureService _capture = new();
    private readonly SpeechRecognitionService _stt = new();
    private readonly List<byte> _segmentBuffer = new();
    private DateTime _lastVoiceAt = DateTime.MinValue;
    private DateTime _segmentStartedAt = DateTime.MinValue;
    private volatile bool _running;
    private volatile bool _processing;
    private volatile bool _paused;

    public string TargetLanguage { get; set; } = "vi";
    public string ModelSize { get; set; } = "tiny";
    public AudioSourceKind AudioSource { get; set; } = AudioSourceKind.SystemAudio;

    // "auto" để Whisper tự nhận diện ngôn ngữ, hoặc ép cứng ("en", "vi"...).
    // Tự nhận diện không đáng tin với đoạn ngắn (1-6s) - đã xác nhận lúc debug
    // selftest-stt: auto ra tiếng Thái vô nghĩa, ép "en" mới ra kết quả đọc
    // được. Ép cứng đúng ngôn ngữ là cách chính giúp bản chép lời (và do đó cả
    // bản dịch) chính xác hơn hẳn.
    public string SourceLanguageHint { get; set; } = "auto";

    public event Action<string>? StatusChanged;
    public event Action<string, string, string, string, double>? SegmentReady;

    public async Task<bool> StartAsync()
    {
        StatusChanged?.Invoke("Đang khởi động model nhận dạng giọng nói...");
        try
        {
            // "Từ" = Tiếng Việt -> dùng PhoWhisper (VinAI, luyện riêng cho
            // tiếng Việt) thay cho Whisper gốc - demo so sánh trực tiếp cho
            // thấy chính xác hơn hẳn ở cùng cỡ model. Quyết định 1 lần lúc
            // Start (giống Model - đổi phải Stop/Start lại), không đổi giữa
            // chừng vì phải load lại model, tốn vài giây.
            string effectiveModel = SourceLanguageHint == "vi" ? $"phowhisper-{ModelSize}" : ModelSize;
            _stt.Start(effectiveModel);
        }
        catch (Exception ex)
        {
            CrashLog.Write("LiveAudioService.StartAsync (stt.Start)", ex);
            StatusChanged?.Invoke($"Không khởi động được STT: {ex.Message}");
            return false;
        }

        bool ready = await _stt.WaitUntilReadyAsync(TimeSpan.FromSeconds(60));
        if (!ready)
        {
            StatusChanged?.Invoke("Model STT chưa sẵn sàng (quá thời gian chờ - máy có thể đang thiếu RAM)");
            _stt.Stop();
            return false;
        }

        if (!_capture.Start(AudioSource))
        {
            string deviceKind = AudioSource == AudioSourceKind.Microphone ? "thu (micro)" : "phát";
            StatusChanged?.Invoke($"Không capture được audio (không tìm thấy thiết bị {deviceKind} mặc định)");
            _stt.Stop();
            return false;
        }

        _capture.DataAvailable += OnAudioData;
        _capture.Stopped += ex =>
        {
            if (ex is not null) StatusChanged?.Invoke($"Audio bị ngắt: {ex.Message}");
        };

        _running = true;
        _segmentBuffer.Clear();
        StatusChanged?.Invoke("● Listening...");
        return true;
    }

    public void Stop()
    {
        _running = false;
        _capture.DataAvailable -= OnAudioData;
        _capture.Stop();
        _stt.Stop(); // giải phóng RAM ngay khi dừng, đúng yêu cầu "unload khi thích hợp"
        _segmentBuffer.Clear();
        StatusChanged?.Invoke("Đã dừng");
    }

    // Tạm dừng KHÔNG tắt sidecar STT - giữ model trong RAM để Resume tức thì
    // thay vì phải load lại (vài giây tới vài chục giây tuỳ model/máy).
    public void Pause() { _paused = true; _segmentBuffer.Clear(); StatusChanged?.Invoke("⏸ Đã tạm dừng"); }
    public void Resume() { _paused = false; StatusChanged?.Invoke("● Listening..."); }

    private void OnAudioData(byte[] chunk, WaveFormat format)
    {
        if (!_running || _paused) return;

        float rms = ComputeRms(chunk, format);
        bool hasVoice = rms > EnergyThreshold;

        if (hasVoice)
        {
            if (_segmentBuffer.Count == 0) _segmentStartedAt = DateTime.UtcNow;
            _segmentBuffer.AddRange(chunk);
            _lastVoiceAt = DateTime.UtcNow;
        }
        else if (_segmentBuffer.Count > 0)
        {
            // Vẫn giữ audio trong lúc im lặng ngắn (chưa đủ ngưỡng cắt) để
            // không cắt cụt đuôi câu ngay trước khoảng nghỉ.
            _segmentBuffer.AddRange(chunk);
        }

        if (_segmentBuffer.Count == 0) return;

        bool silenceGapPassed = (DateTime.UtcNow - _lastVoiceAt).TotalMilliseconds >= SilenceGapMs;
        bool maxDurationPassed = (DateTime.UtcNow - _segmentStartedAt).TotalMilliseconds >= MaxSegmentMs;

        if ((silenceGapPassed || maxDurationPassed) && !_processing)
        {
            double durationMs = _segmentBuffer.Count / (double)format.AverageBytesPerSecond * 1000;
            byte[] segment = _segmentBuffer.ToArray();
            _segmentBuffer.Clear();

            if (durationMs >= MinSegmentMs)
            {
                _processing = true;
                _ = ProcessSegmentAsync(segment, format).ContinueWith(_ => _processing = false);
            }
        }
    }

    private async Task ProcessSegmentAsync(byte[] rawPcm, WaveFormat format)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            byte[] wav = AudioConvert.ToWav16kMono(rawPcm, format);
            // Audio chỉ sống trong biến cục bộ tới đây - không ghi ra đĩa,
            // gửi qua localhost rồi thoát khỏi scope này (spec mục 13).
            var transcription = await _stt.TranscribeAsync(wav, SourceLanguageHint);
            if (transcription is null || string.IsNullOrWhiteSpace(transcription.Value.Text)) return;

            string text = transcription.Value.Text.Trim();
            string sourceLang = NormalizeWhisperLang(transcription.Value.Language);
            // KHÔNG tự "lật" targetLang khi trùng sourceLang (khác hover-flow
            // vốn chỉ có 1 lựa chọn ngôn ngữ đích nên lật là hợp lý) - ở đây
            // người dùng đã chọn TƯỜNG MINH cả "Từ" lẫn "To" qua 2 ComboBox
            // riêng, đổi ngầm không hiện trên UI khiến ô "To" hiển thị 1 đằng
            // mà bản dịch ra 1 nẻo (bug thật đã gặp: để Việt->Việt, ứng dụng
            // âm thầm dịch ra tiếng Anh không rõ lý do). Trùng ngôn ngữ thì cứ
            // dịch trùng - Program.TranslatePublicAsync/GoogleTranslateOnline
            // đã tự xử lý gọn trường hợp này (trả nguyên văn).
            string? translated = await Program.TranslatePublicAsync(text, sourceLang, TargetLanguage);
            sw.Stop();
            SegmentReady?.Invoke(text, translated ?? "(không dịch được)", sourceLang, TargetLanguage, sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            CrashLog.Write("LiveAudioService.ProcessSegmentAsync", ex);
        }
    }

    private static float ComputeRms(byte[] chunk, WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            int sampleCount = chunk.Length / 4;
            if (sampleCount == 0) return 0;
            double sumSquares = 0;
            for (int i = 0; i < sampleCount; i++)
            {
                float sample = BitConverter.ToSingle(chunk, i * 4);
                sumSquares += (double)sample * sample;
            }
            return (float)Math.Sqrt(sumSquares / sampleCount);
        }
        if (format.BitsPerSample == 16)
        {
            int sampleCount = chunk.Length / 2;
            if (sampleCount == 0) return 0;
            double sumSquares = 0;
            for (int i = 0; i < sampleCount; i++)
            {
                short sample = BitConverter.ToInt16(chunk, i * 2);
                float f = sample / 32768f;
                sumSquares += (double)f * f;
            }
            return (float)Math.Sqrt(sumSquares / sampleCount);
        }
        return 0;
    }

    // faster-whisper trả mã ngôn ngữ ISO chuẩn; chỉ map về tập app hỗ trợ,
    // ngôn ngữ lạ mặc định coi là "en" (thà dịch nhầm hướng còn hơn crash).
    private static string NormalizeWhisperLang(string code) => code switch
    {
        "vi" or "en" or "zh" or "ja" or "ko" or "ru" or "hi"
            or "th" or "id" or "ms" or "tl" or "fr" or "de" or "es" or "pt" or "ar" => code,
        _ => "en",
    };

    public void Dispose()
    {
        Stop();
        _capture.Dispose();
    }
}
