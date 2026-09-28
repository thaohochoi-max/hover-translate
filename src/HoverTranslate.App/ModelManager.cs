using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace HoverTranslate.App;

internal enum ModelStatus { NotInstalled, Downloading, InstalledNotReady, Ready }

internal sealed record ModelInfo(string Name, ModelStatus Status, string Detail);

// Spec mục 19: Model Manager - hiển thị Translation/Explanation/OCR model,
// trạng thái (Not Installed/Downloading/Installed/Ready), dung lượng trước
// khi tải. Tải một lần rồi dùng offline mãi.
internal static class ModelManager
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };
    public static volatile bool TranslationDownloading;
    public static volatile bool ExplanationDownloading;
    public static volatile bool OcrDownloading;

    public static async Task<ModelInfo> CheckTranslationAsync()
    {
        const string name = "Translation (Argos EN↔VI)";
        if (TranslationDownloading) return new ModelInfo(name, ModelStatus.Downloading, "Đang tải...");
        if (await OfflineTranslator.IsAvailableAsync())
            return new ModelInfo(name, ModelStatus.Ready, "Sẵn sàng, offline");

        // Server sidecar (scripts/translate_server.py) mất ~5-10s để khởi
        // động sau khi app mở - health check thất bại lúc đó không có nghĩa
        // là CHƯA CÀI, chỉ là chưa kịp lên. Kiểm tra thẳng thư mục package
        // của Argos để phân biệt 2 trường hợp.
        return HasArgosPackagesInstalled()
            ? new ModelInfo(name, ModelStatus.InstalledNotReady, "Đã cài, server đang khởi động...")
            : new ModelInfo(name, ModelStatus.NotInstalled, "~150MB, cần Python");
    }

    private static bool HasArgosPackagesInstalled()
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "argos-translate", "packages");
            return Directory.Exists(dir) && Directory.GetDirectories(dir).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<ModelInfo> CheckExplanationAsync()
    {
        if (ExplanationDownloading) return new ModelInfo("Explanation (Ollama qwen2.5:1.5b)", ModelStatus.Downloading, "Đang tải...");
        bool ready = await ExplanationEngine.IsAvailableAsync();
        return ready
            ? new ModelInfo("Explanation (Ollama qwen2.5:1.5b)", ModelStatus.Ready, "Sẵn sàng, offline")
            : new ModelInfo("Explanation (Ollama qwen2.5:1.5b)", ModelStatus.NotInstalled, "~1GB, cần Ollama");
    }

    public static ModelInfo CheckOcr()
    {
        if (OcrDownloading) return new ModelInfo("OCR (Tesseract eng+vie)", ModelStatus.Downloading, "Đang tải...");
        string? dir = FindTessDataDir();
        bool ready = dir is not null
            && File.Exists(Path.Combine(dir, "eng.traineddata"))
            && File.Exists(Path.Combine(dir, "vie.traineddata"));
        return ready
            ? new ModelInfo("OCR (Tesseract eng+vie)", ModelStatus.Ready, "Sẵn sàng, offline")
            : new ModelInfo("OCR (Tesseract eng+vie)", ModelStatus.NotInstalled, "~28MB");
    }

    public static volatile bool WhisperDownloading;

    // Sidecar STT (stt_server.py) chỉ chạy khi Live Translate đang bật (không
    // health-check được lúc bình thường) - kiểm tra thẳng cache Hugging Face
    // giống cách CheckTranslationAsync kiểm tra thư mục package Argos.
    public static ModelInfo CheckWhisper()
    {
        const string name = "Speech-to-Text (Whisper tiny)";
        if (WhisperDownloading) return new ModelInfo(name, ModelStatus.Downloading, "Đang tải...");
        string cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "huggingface", "hub", "models--Systran--faster-whisper-tiny");
        return Directory.Exists(cacheDir)
            ? new ModelInfo(name, ModelStatus.Ready, "Sẵn sàng, offline")
            : new ModelInfo(name, ModelStatus.NotInstalled, "~75MB (model tiny), cần Python");
    }

    public static void InstallWhisper()
    {
        if (WhisperDownloading) return;
        WhisperDownloading = true;
        Task.Run(() =>
        {
            try
            {
                string? script = FindScript("install_whisper_model.py");
                if (script is null) return;
                var psi = new ProcessStartInfo(PythonRuntime.Executable, $"\"{script}\"")
                {
                    WorkingDirectory = Path.GetDirectoryName(script)!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                p?.WaitForExit();
            }
            catch { }
            finally { WhisperDownloading = false; }
        });
    }

    public static void InstallTranslation()
    {
        if (TranslationDownloading) return;
        TranslationDownloading = true;
        Task.Run(() =>
        {
            try
            {
                string? script = FindScript("install_packages.py");
                if (script is null) return;
                var psi = new ProcessStartInfo(PythonRuntime.Executable, $"\"{script}\"")
                {
                    WorkingDirectory = Path.GetDirectoryName(script)!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                p?.WaitForExit();
            }
            catch { /* status check will just keep reporting NotInstalled */ }
            finally { TranslationDownloading = false; }
        });
    }

    public static void InstallExplanation()
    {
        if (ExplanationDownloading) return;
        ExplanationDownloading = true;
        Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo("ollama", "pull qwen2.5:1.5b")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                p?.WaitForExit();
            }
            catch { }
            finally { ExplanationDownloading = false; }
        });
    }

    public static void InstallOcr()
    {
        if (OcrDownloading) return;
        OcrDownloading = true;
        Task.Run(async () =>
        {
            try
            {
                string dir = Path.Combine(FindRepoRoot() ?? AppContext.BaseDirectory, "tessdata");
                Directory.CreateDirectory(dir);
                foreach (var lang in new[] { "eng", "vie" })
                {
                    string path = Path.Combine(dir, $"{lang}.traineddata");
                    if (File.Exists(path)) continue;
                    string url = $"https://github.com/tesseract-ocr/tessdata_best/raw/main/{lang}.traineddata";
                    using var response = await new HttpClient { Timeout = TimeSpan.FromMinutes(3) }.GetAsync(url);
                    if (!response.IsSuccessStatusCode) continue;
                    await using var fs = File.Create(path);
                    await response.Content.CopyToAsync(fs);
                }
            }
            catch { }
            finally { OcrDownloading = false; }
        });
    }

    private static string? FindTessDataDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "tessdata");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
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

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "scripts"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
