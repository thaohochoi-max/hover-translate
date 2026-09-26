namespace HoverTranslate.App;

// Homegrown sentence splitter (Phase 0 finding, docs/PHASE0_FINDINGS.md rủi ro
// #1: UIA's Paragraph text unit isn't reliable across control families - on
// some it returns the whole document instead of one paragraph). Rather than
// trust the accessibility layer to hand us a single sentence, we take whatever
// block of text it gives us and find the sentence boundaries ourselves.
internal static class SentenceSegmenter
{
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "vs", "etc",
        "eg", "ie", "us", "no", "approx", "inc", "co", "ltd", "vd", "tp", "ts",
    };

    // Returns (start, length) spans for each sentence found in text, in order.
    public static List<(int Start, int Length)> Split(string text)
    {
        var result = new List<(int, int)>();
        if (string.IsNullOrEmpty(text)) return result;

        int start = 0;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];

            if (c is '\n' or '\r')
            {
                if (i > start) result.Add((start, i - start));
                while (i < text.Length && text[i] is '\n' or '\r') i++;
                start = i;
                continue;
            }

            if (c is '.' or '!' or '?')
            {
                int j = i + 1;
                while (j < text.Length && text[j] is '"' or '\'' or ')' or '”') j++;
                bool endOfText = j >= text.Length;
                bool followedByWhitespace = !endOfText && char.IsWhiteSpace(text[j]);

                if (endOfText || followedByWhitespace)
                {
                    bool skip = c == '.' && IsAbbreviationBefore(text, i);
                    if (!skip)
                    {
                        result.Add((start, j - start));
                        while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
                        start = j;
                        i = j;
                        continue;
                    }
                }
            }

            i++;
        }

        if (start < text.Length) result.Add((start, text.Length - start));
        return result;
    }

    // Finds the sentence that contains character offset `wordOffset` (as
    // returned by comparing UIA text ranges) and returns just that sentence,
    // trimmed. Falls back to a fixed window around the offset if segmentation
    // somehow doesn't cover it (should only happen on malformed input).
    public static string ExtractSentenceContaining(string text, int wordOffset, int wordLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return text ?? "";
        wordOffset = Math.Clamp(wordOffset, 0, Math.Max(0, text.Length - 1));

        foreach (var (s, len) in Split(text))
        {
            if (wordOffset >= s && wordOffset < s + len)
                return text.Substring(s, len).Trim();
        }

        int start = Math.Max(0, wordOffset - 80);
        int length = Math.Min(text.Length - start, 160);
        return text.Substring(start, length).Trim();
    }

    private static bool IsAbbreviationBefore(string text, int periodIndex)
    {
        int end = periodIndex;
        int start = end;
        while (start > 0 && char.IsLetter(text[start - 1])) start--;
        if (start == end) return false;

        string word = text[start..end];

        // "J." style initial (single capital letter) - not a sentence end.
        if (word.Length == 1 && char.IsUpper(word[0])) return true;

        return Abbreviations.Contains(word);
    }
}
