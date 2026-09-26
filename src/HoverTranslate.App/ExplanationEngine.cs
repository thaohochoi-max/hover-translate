using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace HoverTranslate.App;

// Spec mục 6 "Explain deeper" + mục 14 Explanation Engine: separate from
// Translation Engine, allowed to be slower (mục 20), never called on every
// hover - only when the user explicitly asks. Talks to a local Ollama server
// (http://localhost:11434), architecture not locked to it per mục 14's own
// instruction ("Architecture không khóa vào Ollama").
internal static class ExplanationEngine
{
    private const string Model = "qwen2.5:1.5b";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly Uri GenerateUri = new("http://127.0.0.1:11434/api/generate");

    public static async Task<bool> IsAvailableAsync()
    {
        try
        {
            using var response = await Http.GetAsync("http://127.0.0.1:11434/api/tags");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<string?> ExplainAsync(string word, string sentence, string sourceLang, string targetLang)
    {
        string targetLangName = LanguageDisplayName(targetLang);
        string prompt =
            $"Từ/cụm: \"{word}\"\n" +
            $"Trong câu: \"{sentence}\"\n" +
            $"Ngôn ngữ nguồn: {LanguageDisplayName(sourceLang)}. Ngôn ngữ đích: {targetLangName}.\n\n" +
            $"Giải thích ngắn gọn bằng {targetLangName}, đúng các mục sau, mỗi mục 1-2 dòng, không lặp lại đề bài:\n" +
            "1. Nghĩa trong câu này (tại sao dùng nghĩa này, không phải nghĩa khác)\n" +
            "2. Nghĩa khác thường gặp của từ/cụm này (nếu có)\n" +
            "3. Một ví dụ câu khác dùng từ/cụm này kèm dịch\n" +
            "4. Từ đồng nghĩa / trái nghĩa (nếu phù hợp)\n" +
            "5. Collocation / cách dùng tự nhiên thường đi kèm\n" +
            "6. Lỗi người học hay mắc với từ/cụm này";

        var payload = new
        {
            model = Model,
            prompt,
            stream = false,
        };

        try
        {
            string json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync(GenerateUri, content);
            if (!response.IsSuccessStatusCode) return null;

            string body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("response", out var el) ? el.GetString()?.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string LanguageDisplayName(string code) => code switch
    {
        "vi" => "tiếng Việt",
        "en" => "tiếng Anh",
        "zh" => "tiếng Trung",
        "ja" => "tiếng Nhật",
        "ko" => "tiếng Hàn",
        "ru" => "tiếng Nga",
        "hi" => "tiếng Hindi",
        "th" => "tiếng Thái",
        "id" => "tiếng Indonesia",
        "ms" => "tiếng Mã Lai",
        "tl" => "tiếng Filipino",
        "fr" => "tiếng Pháp",
        "de" => "tiếng Đức",
        "es" => "tiếng Tây Ban Nha",
        "pt" => "tiếng Bồ Đào Nha",
        "ar" => "tiếng Ả Rập",
        _ => code,
    };
}
