using System.IO;
using System.Text.Json;

namespace HoverTranslate.App;

// Spec mục 16: cache key = source text * context * source lang * target lang *
// model version. Hover lại nội dung đã dịch -> trả kết quả ngay (mục 20:
// "Cached translation: <100ms"). Persisted to disk so it survives app restarts
// too, not just this session.
internal static class TranslationCache
{
    // Bumping this invalidates all cached entries if the translation pipeline
    // (engine choice, prompt, etc.) changes in a way that makes old results stale.
    private const string ModelVersion = "v1-argos+mymemory";

    private static readonly Dictionary<string, string> Cache = new();
    private static readonly object Lock = new();
    private static readonly string CacheFilePath = GetCacheFilePath();

    static TranslationCache()
    {
        Load();
    }

    // "text" here is already the full sentence/context string the caller wants
    // translated, so it doubles as both "source text" and "context" from spec
    // mục 16's key description - there's no separate narrower text to key on.
    public static string? TryGet(string text, string sourceLang, string targetLang)
    {
        string key = BuildKey(text, sourceLang, targetLang);
        lock (Lock)
        {
            return Cache.TryGetValue(key, out var value) ? value : null;
        }
    }

    public static void Set(string text, string sourceLang, string targetLang, string translated)
    {
        string key = BuildKey(text, sourceLang, targetLang);
        lock (Lock)
        {
            Cache[key] = translated;
        }
        SaveAsync();
    }

    private static string BuildKey(string text, string sourceLang, string targetLang)
        => $"{ModelVersion}|{sourceLang}|{targetLang}|{text}";

    private static string GetCacheFilePath()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HoverTranslate");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "translation_cache.json");
    }

    private static void Load()
    {
        try
        {
            if (!File.Exists(CacheFilePath)) return;
            string json = File.ReadAllText(CacheFilePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (loaded is null) return;
            lock (Lock)
            {
                // Tự dọn entry rác kiểu "dịch ra y hệt chữ gốc" đã lỡ lọt vào
                // file từ trước khi Set() có guard này (xem Program.cs) - tự
                // sửa file cũ mà không cần người dùng thao tác gì.
                foreach (var (k, v) in loaded)
                {
                    if (!IsEchoEntry(k, v)) Cache[k] = v;
                }
            }
        }
        catch
        {
            // Corrupt or unreadable cache file - start fresh rather than crash the app.
        }
    }

    private static bool IsEchoEntry(string key, string translated)
    {
        // key = "{ModelVersion}|{sourceLang}|{targetLang}|{text}" - tìm dấu |
        // thứ 3 để tách phần text gốc, vì bản thân text có thể chứa ký tự |.
        int sepsSeen = 0, textStart = -1;
        for (int i = 0; i < key.Length; i++)
        {
            if (key[i] != '|') continue;
            sepsSeen++;
            if (sepsSeen == 3) { textStart = i + 1; break; }
        }
        if (textStart < 0) return false;
        string sourceText = key[textStart..];
        return string.Equals(sourceText.Trim(), translated.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static void SaveAsync()
    {
        Dictionary<string, string> snapshot;
        lock (Lock)
        {
            snapshot = new Dictionary<string, string>(Cache);
        }
        Task.Run(() =>
        {
            try
            {
                string json = JsonSerializer.Serialize(snapshot);
                File.WriteAllText(CacheFilePath, json);
            }
            catch
            {
                // Best-effort persistence; an in-memory cache still works for this session.
            }
        });
    }
}
