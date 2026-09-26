using System.Net.Http;
using System.Text.Json;

namespace HoverTranslate.App;

// Temporary online translation path (user's explicit choice: "both directions" -
// online demo now, offline Argos Translate in parallel per docs/PHASE0_FINDINGS.md
// mục 6). This sends hovered sentence text to a third-party API and therefore
// breaks the product spec's offline-first/privacy principle (mục 17) - it exists
// only to unblock phrase/sentence-level UX testing until the offline engine lands.
internal static class OnlineTranslator
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public static async Task<string?> TranslateSentenceAsync(string text, string sourceLang, string targetLang)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (sourceLang == targetLang) return text;
        try
        {
            string url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(text)}&langpair={sourceLang}|{targetLang}";
            using var response = await Http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement
                .GetProperty("responseData")
                .GetProperty("translatedText")
                .GetString();
        }
        catch
        {
            return null;
        }
    }
}
