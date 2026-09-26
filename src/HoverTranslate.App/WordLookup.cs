using System.Text;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace HoverTranslate.App;

internal readonly record struct WordLookupResult(bool Supported, string Word, string Sentence);

// Same UIA mechanism validated in poc/HoverProbe (Phase 0), now in-process for
// low latency instead of shelling out to a separate exe per hover.
internal static class WordLookup
{
    private const int MaxWordChars = 40;

    public static WordLookupResult AtPoint(System.Windows.Point point)
    {
        try
        {
            AutomationElement? element = AutomationElement.FromPoint(point);
            if (element is null) return default;

            AutomationElement? searchElement = element;
            for (int depth = 0; depth < 4 && searchElement is not null; depth++)
            {
                if (searchElement.TryGetCurrentPattern(TextPattern.Pattern, out var patternObj))
                {
                    var textPattern = (TextPattern)patternObj;
                    try
                    {
                        // Đã bôi đen (chọn) sẵn 1 đoạn thì dịch đúng đoạn đó,
                        // không chỉ 1 từ dưới con trỏ - lựa chọn thủ công là ý
                        // định rõ ràng của người dùng, ưu tiên hơn hover.
                        var selectionResult = TryGetSelection(textPattern);
                        if (selectionResult.Supported) return selectionResult;

                        TextPatternRange range = textPattern.RangeFromPoint(point);
                        var result = LookupFromRange(range);
                        if (result.Supported) return result;
                    }
                    catch
                    {
                        // fall through and try an ancestor
                    }
                }
                searchElement = TreeWalker.RawViewWalker.GetParent(searchElement);
            }

            return default;
        }
        catch
        {
            return default;
        }
    }

    // Test-only entry point: exercises ScanWordAtPoint against a range built
    // from a known window + character offset instead of a screen point, so the
    // character-walk logic can be verified without depending on window z-order
    // (screen-coordinate probing proved unreliable in this remote-desktop setup).
    public static string DebugScan(IntPtr hwnd, string containing)
    {
        var root = AutomationElement.FromHandle(hwnd);
        var cond = new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true);
        var results = new List<string>();
        foreach (AutomationElement el in root.FindAll(TreeScope.Descendants, cond))
        {
            var pattern = (TextPattern)el.GetCurrentPattern(TextPattern.Pattern);
            var doc = pattern.DocumentRange;
            string full = doc.GetText(-1);
            int idx = full.IndexOf(containing, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;

            var r = doc.Clone();
            r.Move(TextUnit.Character, idx + containing.Length / 2);
            r.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, 1);
            var result = LookupFromRange(r);
            results.Add($"[{el.Current.ClassName}] word='{result.Word}' sentence='{result.Sentence}'");
        }
        return string.Join(" | ", results);
    }

    // Shared by AtPoint (real screen-point hovers) and DebugScan (self-test):
    // given a TextPatternRange anchored somewhere in the text, produce the word
    // under it plus its containing sentence (via SentenceSegmenter, not UIA's
    // unreliable Paragraph unit - see docs/PHASE0_FINDINGS.md rủi ro #1).
    private static WordLookupResult LookupFromRange(TextPatternRange range)
    {
        var (word, wordRange) = ScanWordAtPoint(range);
        if (word.Length == 0) return default;

        TextPatternRange contextRange;
        try
        {
            contextRange = range.Clone();
            contextRange.ExpandToEnclosingUnit(TextUnit.Paragraph);
        }
        catch
        {
            contextRange = range.Clone();
            contextRange.ExpandToEnclosingUnit(TextUnit.Line);
        }
        string contextText = contextRange.GetText(-1);

        int wordOffset = 0;
        if (wordRange is not null)
        {
            try
            {
                var offsetRange = contextRange.Clone();
                offsetRange.MoveEndpointByRange(TextPatternRangeEndpoint.End, wordRange, TextPatternRangeEndpoint.Start);
                wordOffset = offsetRange.GetText(-1).Length;
            }
            catch { wordOffset = 0; }
        }
        string sentence = SentenceSegmenter.ExtractSentenceContaining(contextText, wordOffset, word.Length);

        return new WordLookupResult(true, word, sentence);
    }

    // Sentence để rỗng: đoạn đã bôi đen chính là thứ cần dịch, không cần thêm
    // ngữ cảnh câu bao quanh (khác với hover 1 từ, nơi câu chứa nó mới là
    // ngữ cảnh). Popup ẩn dòng trích dẫn khi Sentence rỗng - khỏi lặp lại
    // đúng đoạn đã hiện làm tiêu đề.
    private static WordLookupResult TryGetSelection(TextPattern textPattern)
    {
        try
        {
            var selection = textPattern.GetSelection();
            if (selection is null || selection.Length == 0) return default;

            string selectedText = (selection[0].GetText(2000) ?? "").Trim();
            // Chuỗi rỗng = chỉ có con trỏ nhấp nháy (caret), không phải bôi đen
            // thật. Quá dài (>2 câu bình thường) thì bỏ qua, để tránh dịch
            // nguyên cả tài liệu nếu người dùng lỡ Ctrl+A.
            if (selectedText.Length == 0 || selectedText.Length > 400) return default;

            return new WordLookupResult(true, selectedText, "");
        }
        catch
        {
            return default;
        }
    }

    // Test-only: xác nhận TryGetSelection hoạt động đúng qua window handle,
    // không phụ thuộc toạ độ màn hình/z-order (môi trường remote desktop này
    // khiến probing theo pixel không ổn định - xem docs/PHASE0_FINDINGS.md).
    public static string DebugSelection(IntPtr hwnd)
    {
        var root = AutomationElement.FromHandle(hwnd);
        var cond = new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true);
        var results = new List<string>();
        foreach (AutomationElement el in root.FindAll(TreeScope.Descendants, cond))
        {
            var pattern = (TextPattern)el.GetCurrentPattern(TextPattern.Pattern);
            var result = TryGetSelection(pattern);
            results.Add($"[{el.Current.ClassName}] supported={result.Supported} word='{result.Word}'");
        }
        return string.Join(" | ", results);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '\'' || c == '_';

    // Walks outward from the clicked point one character at a time in both
    // directions, stopping at the first non-word character - a manual
    // ExpandToEnclosingUnit(Word) that works even when the control's own Word
    // unit is coarse or unimplemented.
    private static (string Word, TextPatternRange? Range) ScanWordAtPoint(TextPatternRange pointRange)
    {
        var core = pointRange.Clone();
        core.ExpandToEnclosingUnit(TextUnit.Character);
        string centerChar = core.GetText(1);
        if (centerChar.Length == 0 || !IsWordChar(centerChar[0])) return ("", null);

        var sb = new StringBuilder();
        sb.Append(centerChar[0]);

        // Left side: shrink the Start endpoint back one character at a time.
        var leftEdge = core.Clone();
        for (int i = 0; i < MaxWordChars; i++)
        {
            var probe = leftEdge.Clone();
            if (probe.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -1) == 0) break;
            probe.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, -1);
            string ch = probe.GetText(1);
            if (ch.Length == 0 || !IsWordChar(ch[0])) break;
            sb.Insert(0, ch[0]);
            leftEdge = probe;
        }

        // Right side: push the End endpoint forward one character at a time.
        var rightEdge = core.Clone();
        for (int i = 0; i < MaxWordChars; i++)
        {
            var probe = rightEdge.Clone();
            if (probe.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, 1) == 0) break;
            probe.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, 1);
            string ch = probe.GetText(1);
            if (ch.Length == 0 || !IsWordChar(ch[0])) break;
            sb.Append(ch[0]);
            rightEdge = probe;
        }

        var wordRange = core.Clone();
        wordRange.MoveEndpointByRange(TextPatternRangeEndpoint.Start, leftEdge, TextPatternRangeEndpoint.Start);
        wordRange.MoveEndpointByRange(TextPatternRangeEndpoint.End, rightEdge, TextPatternRangeEndpoint.End);

        return (sb.ToString(), wordRange);
    }
}
