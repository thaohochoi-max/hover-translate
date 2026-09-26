namespace HoverTranslate.App;

// Lightweight heuristic detector (script/diacritic ranges, no ML model) — good
// enough for MVP's stated language set (spec mục 13: EN, VI, ZH, JA, KO).
// A real Language Detection step (fastText/CLD3-class model) is a later swap-in,
// same as the Translation/OCR engines — interface stays small on purpose.
internal static class LanguageDetector
{
    private static readonly char[] VietnameseOnlyChars =
        "ăâđêôơưĂÂĐÊÔƠƯ".ToCharArray();

    // Vietnamese also uses combining tone marks over plain Latin vowels
    // (e.g. "à", "á", "ả", "ã", "ạ") which overlap other Latin languages,
    // so only treat those as a Vietnamese signal alongside the unique chars above.
    private const string ToneMarkedVowels = "àáảãạèéẻẽẹìíỉĩịòóỏõọùúủũụỳýỷỹỵ";

    public static string Detect(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "en";

        foreach (char c in text)
        {
            if (c is >= '぀' and <= 'ヿ') return "ja"; // hiragana/katakana
            if (c is >= '가' and <= '힣') return "ko"; // hangul
            if (c is >= '一' and <= '鿿') return "zh"; // CJK unified ideographs
            if (c is >= 'Ѐ' and <= 'ӿ') return "ru"; // Cyrillic
            if (c is >= 'ऀ' and <= 'ॿ') return "hi"; // Devanagari
            if (c is >= '฀' and <= '๿') return "th"; // Thai
            if (c is >= '؀' and <= 'ۿ') return "ar"; // Arabic
        }

        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "en";

        int vietnameseWordCount = 0;
        foreach (var word in words)
        {
            if (HasVietnameseMark(word)) vietnameseWordCount++;
        }

        // Đoạn ngắn (hover 1-3 từ): 1 từ có dấu là đủ - vẫn cần nhạy để bắt
        // đúng khi hover trực tiếp vào 1 từ tiếng Việt. Đoạn dài (OCR nguyên
        // câu/đoạn văn): xét theo TỈ LỆ, để 1 từ lạc (tên riêng, từ mượn...)
        // không kéo cả đoạn tiếng Anh bị hiểu nhầm thành tiếng Việt.
        bool isVietnamese = words.Length <= 3
            ? vietnameseWordCount > 0
            : (double)vietnameseWordCount / words.Length >= 0.15;

        return isVietnamese ? "vi" : "en";
    }

    private static bool HasVietnameseMark(string word)
    {
        foreach (char c in word)
        {
            if (Array.IndexOf(VietnameseOnlyChars, c) >= 0) return true;
            if (ToneMarkedVowels.IndexOf(c) >= 0) return true;
        }
        return false;
    }

    // Simple default direction: VI source -> EN target, everything else -> VI
    // (matches spec mục 13's near-term roadmap: EN/ZH/JA/KO -> VI, VI <-> EN).
    public static string DefaultTargetFor(string sourceLang) => sourceLang == "vi" ? "en" : "vi";
}
