using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using WpfPoint = System.Windows.Point;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfFontFamily = System.Windows.Media.FontFamily;

// Phase 0 isolated test harness.
//
// This app owns its own window and its own sample text -- it never touches the
// user's real Notepad or Chrome windows. It hosts two kinds of text controls:
//   1. A native WPF TextBox (TextBoxAutomationPeer)
//   2. A WinForms RichTextBox (wraps the native Win32 RICHEDIT control, the same
//      control family modern Notepad uses -- ClassName "RichEditD2DPT")
// It then spawns HoverProbe.exe (a separate process, mirroring how the real
// hover-translate app will be a separate process from whatever the user is
// hovering over) against computed screen points for known words, and checks
// whether UI Automation correctly reports the word + sentence context.

internal sealed record TestCase(string ContainerLabel, string Word, int OccurrenceIndex, string ExpectedSentenceContains);

internal sealed class HarnessWindow : Window
{
    private const string SampleText =
        "We need a different approach.\n" +
        "They sat on the river bank.\n" +
        "I deposited money in the bank.";

    private readonly WpfTextBox _wpfTextBox;
    private readonly System.Windows.Forms.RichTextBox _winFormsRichTextBox;
    private readonly TextBlock _resultsBlock;

    public HarnessWindow()
    {
        Title = "HoverProbe Test Harness (isolated - not your real apps)";
        Width = 700;
        Height = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var panel = new StackPanel { Margin = new Thickness(12) };

        panel.Children.Add(new TextBlock
        {
            Text = "WPF TextBox sample:",
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 4)
        });

        _wpfTextBox = new WpfTextBox
        {
            Text = SampleText,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 16,
            Height = 90,
            FontFamily = new WpfFontFamily("Consolas")
        };
        panel.Children.Add(_wpfTextBox);

        panel.Children.Add(new TextBlock
        {
            Text = "WinForms RichTextBox sample (native RICHEDIT control, same family Notepad uses):",
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 12, 0, 4)
        });

        _winFormsRichTextBox = new System.Windows.Forms.RichTextBox
        {
            Text = SampleText,
            Font = new System.Drawing.Font("Consolas", 12),
            Multiline = true
        };
        var host = new WindowsFormsHost { Height = 90, Child = _winFormsRichTextBox };
        panel.Children.Add(host);

        _resultsBlock = new TextBlock
        {
            Margin = new Thickness(0, 16, 0, 0),
            FontFamily = new WpfFontFamily("Consolas"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        };
        panel.Children.Add(_resultsBlock);

        Content = new ScrollViewer { Content = panel };

        ContentRendered += async (_, _) => await RunTestsAsync();
    }

    private async System.Threading.Tasks.Task RunTestsAsync()
    {
        // Let layout settle so GetRectFromCharacterIndex / GetPositionFromCharIndex are accurate.
        await System.Threading.Tasks.Task.Delay(400);

        string hoverProbeExe;
        try
        {
            hoverProbeExe = FindHoverProbeExe();
        }
        catch (Exception ex)
        {
            _resultsBlock.Text = $"FAILED to locate HoverProbe.exe: {ex.Message}";
            return;
        }

        var cases = new[]
        {
            new TestCase("WPF TextBox", "approach", 0, "different approach"),
            new TestCase("WPF TextBox", "bank", 0, "river bank"),
            new TestCase("WPF TextBox", "bank", 1, "deposited money"),
            new TestCase("WinForms RichTextBox", "approach", 0, "different approach"),
            new TestCase("WinForms RichTextBox", "bank", 0, "river bank"),
            new TestCase("WinForms RichTextBox", "bank", 1, "deposited money"),
        };

        var sb = new StringBuilder();
        int pass = 0, fail = 0;

        foreach (var tc in cases)
        {
            var (screenPoint, ok, why) = tc.ContainerLabel == "WPF TextBox"
                ? GetScreenPointForWord(_wpfTextBox, tc.Word, tc.OccurrenceIndex)
                : GetScreenPointForWord(_winFormsRichTextBox, tc.Word, tc.OccurrenceIndex);

            if (!ok)
            {
                sb.AppendLine($"[SETUP-FAIL] {tc.ContainerLabel} '{tc.Word}'#{tc.OccurrenceIndex}: {why}");
                fail++;
                continue;
            }

            var probeResult = await RunHoverProbeAsync(hoverProbeExe, (int)screenPoint.X, (int)screenPoint.Y);
            bool wordMatch = probeResult.Word.Equals(tc.Word, StringComparison.OrdinalIgnoreCase);
            bool contextMatch = probeResult.Sentence.Contains(tc.ExpectedSentenceContains, StringComparison.OrdinalIgnoreCase);
            bool testPass = probeResult.Supported && wordMatch && contextMatch;

            if (testPass) pass++; else fail++;

            sb.AppendLine($"[{(testPass ? "PASS" : "FAIL")}] {tc.ContainerLabel} '{tc.Word}'#{tc.OccurrenceIndex} @({screenPoint.X:F0},{screenPoint.Y:F0})");
            sb.AppendLine($"       supported={probeResult.Supported} word='{probeResult.Word}' sentence='{Truncate(probeResult.Sentence, 60)}' class={probeResult.ClassName} controlType={probeResult.ControlType} ms={probeResult.ElapsedMs}");
            if (!string.IsNullOrEmpty(probeResult.Error)) sb.AppendLine($"       error={probeResult.Error}");
        }

        sb.AppendLine();
        sb.AppendLine($"TOTAL: {pass} pass / {fail} fail");
        _resultsBlock.Text = sb.ToString();
        Console.WriteLine(sb.ToString());

        var resultsPath = Path.Combine(AppContext.BaseDirectory, "phase0_results.txt");
        File.WriteAllText(resultsPath, sb.ToString());
        Console.WriteLine($"Results written to {resultsPath}");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    private static (WpfPoint point, bool ok, string why) GetScreenPointForWord(WpfTextBox textBox, string word, int occurrence)
    {
        string text = textBox.Text;
        int idx = IndexOfNth(text, word, occurrence);
        if (idx < 0) return (default, false, $"word not found (occurrence {occurrence})");

        int midCharIndex = idx + word.Length / 2;
        Rect rect = textBox.GetRectFromCharacterIndex(midCharIndex);
        if (rect.IsEmpty) return (default, false, "GetRectFromCharacterIndex returned empty rect");

        var localPoint = new WpfPoint(rect.X, rect.Y + rect.Height / 2);
        WpfPoint screenPoint = textBox.PointToScreen(localPoint);
        return (screenPoint, true, "");
    }

    private static (WpfPoint point, bool ok, string why) GetScreenPointForWord(System.Windows.Forms.RichTextBox rtb, string word, int occurrence)
    {
        string text = rtb.Text;
        int idx = IndexOfNth(text, word, occurrence);
        if (idx < 0) return (default, false, $"word not found (occurrence {occurrence})");

        int midCharIndex = idx + word.Length / 2;
        System.Drawing.Point localPoint = rtb.GetPositionFromCharIndex(midCharIndex);
        // GetPositionFromCharIndex returns the top-left of the character cell;
        // nudge a few px right/down so the point lands inside the glyph, not on its edge.
        var adjusted = new System.Drawing.Point(localPoint.X + 3, localPoint.Y + 8);
        System.Drawing.Point screenPoint = rtb.PointToScreen(adjusted);
        return (new WpfPoint(screenPoint.X, screenPoint.Y), true, "");
    }

    private static int IndexOfNth(string text, string word, int occurrence)
    {
        int idx = -1;
        for (int i = 0; i <= occurrence; i++)
        {
            idx = text.IndexOf(word, idx + 1, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return -1;
        }
        return idx;
    }

    private static string FindHoverProbeExe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "HoverProbe");
            if (Directory.Exists(candidate))
            {
                var exe = Directory.GetFiles(candidate, "HoverProbe.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (exe is not null) return exe;
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not find HoverProbe.exe under a sibling 'HoverProbe' folder. Build HoverProbe first.");
    }

    private static async System.Threading.Tasks.Task<ProbeClientResult> RunHoverProbeAsync(string exePath, int x, int y)
    {
        var psi = new ProcessStartInfo(exePath, $"probe {x} {y}")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        string output = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return ProbeClientResult.ParseJson(output.Trim());
    }
}

internal sealed class ProbeClientResult
{
    public bool Supported { get; set; }
    public string Word { get; set; } = "";
    public string Sentence { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string ControlType { get; set; } = "";
    public long ElapsedMs { get; set; }
    public string? Error { get; set; }

    // Minimal hand-rolled parser: HoverProbe emits a flat, single-line JSON object
    // with no nested objects/arrays, so a full JSON library is unnecessary here.
    public static ProbeClientResult ParseJson(string json)
    {
        var result = new ProbeClientResult();
        var fields = SplitTopLevelFields(json);
        foreach (var (key, value) in fields)
        {
            switch (key)
            {
                case "supported": result.Supported = value == "true"; break;
                case "word": result.Word = Unescape(value); break;
                case "sentence": result.Sentence = Unescape(value); break;
                case "class": result.ClassName = Unescape(value); break;
                case "controlType": result.ControlType = Unescape(value); break;
                case "ms": long.TryParse(value, out var ms); result.ElapsedMs = ms; break;
                case "error": result.Error = value == "null" ? null : Unescape(value); break;
            }
        }
        return result;
    }

    private static List<(string Key, string Value)> SplitTopLevelFields(string json)
    {
        var result = new List<(string, string)>();
        json = json.Trim().TrimStart('{').TrimEnd('}');
        int i = 0;
        while (i < json.Length)
        {
            while (i < json.Length && (json[i] == ',' || json[i] == ' ')) i++;
            if (i >= json.Length) break;

            int keyStart = json.IndexOf('"', i) + 1;
            int keyEnd = json.IndexOf('"', keyStart);
            string key = json[keyStart..keyEnd];
            int colon = json.IndexOf(':', keyEnd);
            int valStart = colon + 1;

            string value;
            int nextComma;
            if (valStart < json.Length && json[valStart] == '"')
            {
                int valEnd = valStart + 1;
                while (valEnd < json.Length && !(json[valEnd] == '"' && json[valEnd - 1] != '\\')) valEnd++;
                value = json[valStart..(valEnd + 1)];
                nextComma = valEnd + 1;
            }
            else
            {
                nextComma = json.IndexOf(',', valStart);
                if (nextComma < 0) nextComma = json.Length;
                value = json[valStart..nextComma].Trim();
            }

            result.Add((key, value.Trim('"')));
            i = nextComma;
        }
        return result;
    }

    private static string Unescape(string s) => s.Replace("\\\"", "\"").Replace("\\\\", "\\");
}

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var app = new System.Windows.Application();
        var window = new HarnessWindow();
        app.Run(window);
    }
}
