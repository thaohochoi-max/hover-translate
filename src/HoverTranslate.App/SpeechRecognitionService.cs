using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace HoverTranslate.App;

internal readonly record struct TranscriptionResult(string Text, string Language);

// Quản lý vòng đời scripts/stt_server.py (faster-whisper) + gọi HTTP vào nó -
// cùng mẫu với OfflineTranslator.cs gọi translate_server.py. Khác biệt quan
// trọng: sidecar này KHÔNG tự khởi động cùng app (máy RAM thấp đã đo được
// ~8GB, chỉ còn vài trăm MB rảnh lúc test) - chỉ Start() khi bấm nút Start ở
// LiveTranslateWindow, Stop() giải phóng RAM ngay khi dừng.
internal sealed class SpeechRecognitionService
{
    private const int Port = 5056;
    private static readonly Uri BaseUri = new($"http://127.0.0.1:{Port}/");
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private Process? _process;

    public bool IsRunning => _process is { HasExited: false };

    public void Start(string modelSize)
    {
        if (IsRunning) return;

        string? script = FindScript("stt_server.py");
        if (script is null) throw new FileNotFoundException("Không tìm thấy scripts/stt_server.py");

        var psi = new ProcessStartInfo(PythonRuntime.Executable, $"\"{script}\" {Port} {modelSize}")
        {
            WorkingDirectory = Path.GetDirectoryName(script)!,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        _process = Process.Start(psi);
        if (_process is not null) ProcessJobObject.AttachToLifetimeOfThisApp(_process);
    }

    public void Stop()
    {
        try
        {
            if (_process is { HasExited: false }) _process.Kill();
        }
        catch { /* đã tự thoát hoặc không kill được - không quan trọng bằng việc không throw ở đây */ }
        finally
        {
            _process = null;
        }
    }

    // Đợi tới khi model load xong (constructor WhisperModel() có thể mất vài
    // giây tới vài chục giây tuỳ model size + tốc độ máy) hoặc hết thời gian.
    public async Task<bool> WaitUntilReadyAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await IsHealthyAsync()) return true;
            await Task.Delay(300);
        }
        return false;
    }

    public async Task<bool> IsHealthyAsync()
    {
        try
        {
            using var response = await _http.GetAsync(new Uri(BaseUri, "health"));
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // wavBytes: 1 đoạn audio hoàn chỉnh (đã cắt theo khoảng lặng ở
    // LiveAudioService), giữ trong RAM, không ghi ra đĩa - gửi thẳng qua
    // localhost rồi vứt bỏ (spec mục 13: không lưu audio tạm lâu dài).
    public async Task<TranscriptionResult?> TranscribeAsync(byte[] wavBytes, string languageHint = "auto")
    {
        try
        {
            using var content = new ByteArrayContent(wavBytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            using var response = await _http.PostAsync(new Uri(BaseUri, $"transcribe?lang={languageHint}"), content);
            if (!response.IsSuccessStatusCode) return null;

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            string text = doc.RootElement.GetProperty("text").GetString() ?? "";
            string lang = doc.RootElement.TryGetProperty("language", out var l) ? l.GetString() ?? "en" : "en";
            return new TranscriptionResult(text, lang);
        }
        catch
        {
            return null;
        }
    }

    private static string? FindScript(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "scripts", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
