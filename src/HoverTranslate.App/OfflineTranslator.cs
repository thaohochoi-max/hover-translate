using System.Net.Http;
using System.Text.Json;

namespace HoverTranslate.App;

// Talks to scripts/translate_server.py (Argos Translate) over localhost.
// This is the real, offline path the product is supposed to ship with
// (docs/PHASE0_FINDINGS.md mục 6, hướng A) - text never leaves the machine.
// OnlineTranslator (MyMemory) stays only as a fallback for language pairs the
// offline server doesn't have installed yet.
internal static class OfflineTranslator
{
    private const int Port = 5055;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };
    private static readonly Uri BaseUri = new($"http://127.0.0.1:{Port}/");

    public static async Task<bool> IsAvailableAsync()
    {
        try
        {
            using var response = await Http.GetAsync(new Uri(BaseUri, "health"));
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<string?> TranslateSentenceAsync(string text, string sourceLang, string targetLang)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (sourceLang == targetLang) return text;
        try
        {
            string url = $"translate?text={Uri.EscapeDataString(text)}&from={sourceLang}&to={targetLang}";
            using var response = await Http.GetAsync(new Uri(BaseUri, url));
            if (!response.IsSuccessStatusCode) return null;

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("translated", out var el)) return null;
            return el.GetString();
        }
        catch
        {
            return null;
        }
    }
}
