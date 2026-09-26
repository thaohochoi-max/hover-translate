using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace HoverTranslate.App;

// Endpoint công khai mà trang translate.google.com tự dùng (không cần API
// key, không cần tài khoản Google Cloud/billing - khớp với sở thích "ưu tiên
// API miễn phí" của người dùng). Đây KHÔNG phải Google Cloud Translation API
// chính thức - không có SLA, có thể bị chặn/đổi bất cứ lúc nào, nên vẫn giữ
// OnlineTranslator (MyMemory) làm dự phòng khi endpoint này lỗi.
// 1 nhóm nghĩa theo loại từ (danh từ/động từ/tính từ...) - Google chỉ trả về
// khối này (dt=bd) khi input là 1 TỪ ĐƠN, không có với câu/cụm dài.
internal readonly record struct DictionaryEntry(string PartOfSpeech, IReadOnlyList<string> Meanings);

internal static class GoogleTranslateOnline
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private static readonly IReadOnlyList<DictionaryEntry> EmptyDictionary = Array.Empty<DictionaryEntry>();

    public static async Task<string?> TranslateSentenceAsync(string text, string sourceLang, string targetLang)
    {
        var result = await TranslateWithDetailsAsync(text, sourceLang, targetLang);
        return result.Translated;
    }

    // Google tự sửa lỗi chính tả ngầm khi dịch (gõ "transcrip" vẫn ra nghĩa
    // đúng của "transcript") - không cần chờ người dùng sửa mới dịch. Cái
    // thiếu chỉ là hiển thị gợi ý "Có phải ý bạn là...". Với từ đa nghĩa (vd
    // "transcript" = bản ghi chép/bảng điểm...), bản dịch NMT chính (index 0)
    // chỉ chọn 1 nghĩa nên có thể lệch ngữ cảnh - dt=bd trả thêm danh sách các
    // nghĩa khác theo loại từ để người dùng tự chọn, giống Google Dịch trang
    // web thật vẫn làm với từ đơn. Gộp cả 3 (dịch + gợi ý chính tả + từ điển)
    // trong 1 lần gọi, tránh tăng số request lên endpoint miễn phí này.
    public static async Task<(string? Translated, string? DidYouMean, IReadOnlyList<DictionaryEntry> Dictionary)> TranslateWithDetailsAsync(string text, string sourceLang, string targetLang)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null, EmptyDictionary);
        if (sourceLang == targetLang) return (text, null, EmptyDictionary);
        try
        {
            string url = "https://translate.googleapis.com/translate_a/single"
                + $"?client=gtx&sl={sourceLang}&tl={targetLang}&dt=t&dt=sp&dt=bd&q={Uri.EscapeDataString(text)}";
            using var response = await Http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return (null, null, EmptyDictionary);

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            // Dạng trả về: [[["đoạn dịch 1","đoạn gốc 1",...],["đoạn dịch 2",...]],...]
            // Câu dài bị Google tự chia nhỏ thành nhiều đoạn - ghép lại đủ cả câu.
            var sb = new StringBuilder();
            foreach (var segment in doc.RootElement[0].EnumerateArray())
            {
                string? part = segment[0].GetString();
                if (!string.IsNullOrEmpty(part)) sb.Append(part);
            }

            string result = sb.ToString().Trim();
            string? didYouMean = TryExtractSpellingSuggestion(doc.RootElement, text);
            var dictionary = TryExtractDictionary(doc.RootElement);
            return (result.Length == 0 ? null : result, didYouMean, dictionary);
        }
        catch
        {
            return (null, null, EmptyDictionary);
        }
    }

    // Gợi ý sửa chính tả nằm ở 1 vị trí cố định trong mảng JSON khi có dt=sp
    // (dạng [chuỗi_có_html_in_đậm, chuỗi_thường]) - nhưng đây là endpoint
    // không chính thức, không có tài liệu chính thức, cấu trúc có thể đổi bất
    // cứ lúc nào. Parse phòng thủ, im lặng bỏ qua nếu không khớp - TUYỆT ĐỐI
    // không để lỗi ở đây làm hỏng bản dịch chính (đã lấy được ở trên rồi).
    private static string? TryExtractSpellingSuggestion(JsonElement root, string originalText)
    {
        try
        {
            if (root.GetArrayLength() <= 7) return null;
            var block = root[7];
            if (block.ValueKind != JsonValueKind.Array || block.GetArrayLength() < 2) return null;
            string? corrected = block[1].GetString();
            if (string.IsNullOrWhiteSpace(corrected)) return null;
            if (string.Equals(corrected.Trim(), originalText.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
            return corrected.Trim();
        }
        catch
        {
            return null;
        }
    }

    // Khối từ điển theo loại từ nằm ở index 1 khi có dt=bd (dạng
    // [[pos_name, [nghĩa...], ...]*]) - tương tự dt=sp, endpoint không chính
    // thức nên parse phòng thủ, thiếu/lỗi thì trả danh sách rỗng thay vì làm
    // hỏng bản dịch chính.
    private static IReadOnlyList<DictionaryEntry> TryExtractDictionary(JsonElement root)
    {
        try
        {
            if (root.GetArrayLength() <= 1 || root[1].ValueKind != JsonValueKind.Array) return EmptyDictionary;

            var entries = new List<DictionaryEntry>();
            foreach (var group in root[1].EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Array || group.GetArrayLength() < 2) continue;
                string? pos = group[0].GetString();
                if (string.IsNullOrWhiteSpace(pos) || group[1].ValueKind != JsonValueKind.Array) continue;

                var meanings = new List<string>();
                foreach (var m in group[1].EnumerateArray())
                {
                    string? s = m.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) meanings.Add(s);
                }
                if (meanings.Count > 0) entries.Add(new DictionaryEntry(pos, meanings));
            }
            return entries;
        }
        catch
        {
            return EmptyDictionary;
        }
    }
}
