using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;

// Phase 0 POC: can we reliably get "word under cursor" + sentence context
// from an arbitrary screen point using Windows UI Automation?
//
// Usage:
//   HoverProbe.exe probe <x> <y>      -> one-shot lookup at a screen point
//   HoverProbe.exe watch              -> polls the live cursor and prints on change (manual testing)

internal static class NativeMethods
{
    [DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }
}

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Length == 3 && args[0] == "probe"
            && int.TryParse(args[1], out int x)
            && int.TryParse(args[2], out int y))
        {
            var result = Probe(new System.Windows.Point(x, y));
            Console.WriteLine(result.ToJson());
            return 0;
        }

        if (args.Length == 1 && args[0] == "watch")
        {
            Watch();
            return 0;
        }

        Console.WriteLine("Usage:");
        Console.WriteLine("  HoverProbe.exe probe <x> <y>");
        Console.WriteLine("  HoverProbe.exe watch");
        return 1;
    }

    private static void Watch()
    {
        Console.WriteLine("Watching cursor. Ctrl+C to stop.");
        ProbeResult? last = null;
        while (true)
        {
            NativeMethods.GetCursorPos(out var p);
            var point = new System.Windows.Point(p.X, p.Y);
            var result = Probe(point);
            if (last is null || result.Word != last.Word || result.ProcessName != last.ProcessName)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {result.ToJson()}");
                last = result;
            }
            Thread.Sleep(100);
        }
    }

    private static ProbeResult Probe(System.Windows.Point point)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            AutomationElement? element = AutomationElement.FromPoint(point);
            if (element is null)
            {
                return ProbeResult.Fail(point, sw.ElapsedMilliseconds, "NO_ELEMENT_AT_POINT");
            }

            var current = element.Current;
            string processName = "?";
            try
            {
                processName = Process.GetProcessById(current.ProcessId).ProcessName;
            }
            catch { /* process may have exited between calls */ }

            var baseInfo = new ProbeResult
            {
                ElapsedMs = sw.ElapsedMilliseconds,
                X = (int)point.X,
                Y = (int)point.Y,
                ProcessName = processName,
                ClassName = current.ClassName,
                ControlType = current.ControlType?.ProgrammaticName ?? "?",
                ElementName = current.Name,
            };

            // Try TextPattern on the element under the cursor first, then walk up
            // a few ancestors in case the hit-test landed on a leaf without the pattern.
            AutomationElement? searchElement = element;
            for (int depth = 0; depth < 4 && searchElement is not null; depth++)
            {
                if (searchElement.TryGetCurrentPattern(TextPattern.Pattern, out var patternObj))
                {
                    var textPattern = (TextPattern)patternObj;
                    try
                    {
                        TextPatternRange range = textPattern.RangeFromPoint(point);

                        var wordRange = range.Clone();
                        wordRange.ExpandToEnclosingUnit(TextUnit.Word);
                        string word = wordRange.GetText(-1).Trim();

                        // UIA's managed TextUnit enum has no "Sentence" value (Character, Format,
                        // Word, Line, Paragraph, Page, Document only), so sentence splitting has
                        // to happen ourselves in the Context Engine later. For Phase 0 we grab the
                        // enclosing paragraph (falling back to line) as the raw context string.
                        string sentence;
                        try
                        {
                            var paragraphRange = range.Clone();
                            paragraphRange.ExpandToEnclosingUnit(TextUnit.Paragraph);
                            sentence = paragraphRange.GetText(-1).Trim();
                        }
                        catch
                        {
                            var lineRange = range.Clone();
                            lineRange.ExpandToEnclosingUnit(TextUnit.Line);
                            sentence = lineRange.GetText(-1).Trim();
                        }

                        baseInfo.Supported = true;
                        baseInfo.TextPatternDepth = depth;
                        baseInfo.Word = word;
                        baseInfo.Sentence = sentence;
                        baseInfo.ElapsedMs = sw.ElapsedMilliseconds;
                        return baseInfo;
                    }
                    catch (Exception ex)
                    {
                        baseInfo.Error = $"TextPattern found but RangeFromPoint failed: {ex.GetType().Name}: {ex.Message}";
                        // keep walking up in case a parent works
                    }
                }

                searchElement = TreeWalker.RawViewWalker.GetParent(searchElement);
            }

            baseInfo.Supported = false;
            baseInfo.Error ??= "NO_TEXTPATTERN_IN_ANCESTOR_CHAIN";
            baseInfo.ElapsedMs = sw.ElapsedMilliseconds;
            return baseInfo;
        }
        catch (Exception ex)
        {
            return ProbeResult.Fail(point, sw.ElapsedMilliseconds, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}

internal sealed class ProbeResult
{
    public int X { get; set; }
    public int Y { get; set; }
    public long ElapsedMs { get; set; }
    public string ProcessName { get; set; } = "?";
    public string ClassName { get; set; } = "?";
    public string ControlType { get; set; } = "?";
    public string ElementName { get; set; } = "?";
    public bool Supported { get; set; }
    public int TextPatternDepth { get; set; } = -1;
    public string Word { get; set; } = "";
    public string Sentence { get; set; } = "";
    public string? Error { get; set; }

    public static ProbeResult Fail(System.Windows.Point point, long elapsedMs, string error) => new()
    {
        X = (int)point.X,
        Y = (int)point.Y,
        ElapsedMs = elapsedMs,
        Supported = false,
        Error = error,
    };

    public string ToJson() =>
        $"{{\"x\":{X},\"y\":{Y},\"ms\":{ElapsedMs},\"process\":\"{Esc(ProcessName)}\",\"class\":\"{Esc(ClassName)}\",\"controlType\":\"{Esc(ControlType)}\",\"elementName\":\"{Esc(ElementName)}\",\"supported\":{Supported.ToString().ToLower()},\"patternDepth\":{TextPatternDepth},\"word\":\"{Esc(Word)}\",\"sentence\":\"{Esc(Sentence)}\",\"error\":{(Error is null ? "null" : $"\"{Esc(Error)}\"")}}}";

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
}
