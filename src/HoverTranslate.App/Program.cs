using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using WpfApplication = System.Windows.Application;
using WpfPoint = System.Windows.Point;

namespace HoverTranslate.App;

internal static class NativeMethods
{
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int vKey);
    [DllImport("kernel32.dll")] internal static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    internal const int VK_MENU = 0x12; // Alt
    internal const int VK_SHIFT = 0x10;
    internal const int VK_S = 0x53;
    internal const int VK_Q = 0x51;
    internal const int VK_T = 0x54;
    internal const int VK_L = 0x4C;
    internal const int SW_HIDE = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X; public int Y; }
}

internal static class Program
{
    private const int HoverDelayMs = 400;
    // Bán kính "coi như đứng yên" cho bộ đếm hover. 3px (giá trị cũ) nhỏ hơn độ
    // rung tay/nhiễu cảm biến chuột bình thường, nên bộ đếm 400ms gần như luôn
    // bị reset trước khi hoàn thành → Alt+Hover "lúc được lúc không". 18px xấp
    // xỉ bề rộng một ký tự, đủ rộng để tay hơi run vẫn không bị coi là "di chuyển".
    private const int MoveThresholdPx = 18;
    private const int PollIntervalMs = 50;
    // Khoảng trễ trước khi ẩn popup sau khi thả Alt - đủ thời gian để di chuột
    // từ chữ đang hover sang popup (đang bấm 📌/Explain deeper/✕) mà không bị
    // tắt giữa chừng. ContainsScreenPoint đã chặn hoàn toàn 1 khi chuột TỚI
    // popup rồi; khoảng trễ này che khoảng thời gian đang DI CHUYỂN tới đó.
    private const int HideGraceMs = 800;

    private static PopupWindow? _popup;
    private static NativeMethods.POINT _stablePoint;
    private static DateTime _stableSince = DateTime.MinValue;
    // true = đã tìm thấy chữ và hiện popup cho lần "đứng yên" này rồi, khỏi
    // dò UI Automation lại mỗi tick nữa. Reset về false mỗi khi bắt đầu một
    // lần đứng yên MỚI (di chuyển > MoveThresholdPx) - xem nhánh bên dưới.
    private static bool _processedThisStablePoint;
    // Ứng dụng hoàn toàn không expose text qua Accessibility (vd Zalo với 1 số
    // vùng UI) sẽ khiến lookup thất bại mãi mãi ở cùng 1 điểm nếu không giới
    // hạn - dò UI Automation liên tục vô thời hạn rất lãng phí. Dừng thử lại
    // sau chừng này lần thất bại liên tiếp cho tới khi di chuyển sang chỗ mới.
    private const int MaxRetriesPerStablePoint = 15;
    private static int _failedAttemptsThisStablePoint;
    // Tăng lên mỗi khi bắt đầu 1 lần "đứng yên" mới (di chuyển sang điểm
    // khác). TryOcrHoverFallbackAsync chạy song song với Tick() trong lúc
    // đang OCR (không giống Alt+Q thủ công là hành động rời rạc) - nếu không
    // kiểm tra token này, kết quả OCR chậm hơn có thể "giật" popup quay lại vị
    // trí cũ sau khi người dùng đã rê chuột sang chỗ khác (bug đã gặp thực tế).
    private static int _hoverToken;
    private static string? _lastShownWord;
    private static DateTime _altReleasedSince = DateTime.MinValue;
    private static DateTime _popupHoverSince = DateTime.MinValue;
    private static Process? _offlineServerProcess;
    private static SettingsWindow? _settingsWindow;
    private static QuickTranslateWindow? _quickTranslateWindow;
    private static LiveTranslateWindow? _liveTranslateWindow;
    private static bool _settingsHotkeyWasDown;
    private static bool _ocrHotkeyWasDown;
    private static bool _quickTranslateHotkeyWasDown;
    private static bool _liveTranslateHotkeyWasDown;
    private static bool _debugMode;

    [STAThread]
    private static void Main(string[] args)
    {
        // Hidden self-test hook (not for end users): probes the real WordLookup
        // code used in production against a given screen point and exits. Used
        // to verify changes against the isolated TestHarness before asking the
        // user to retest on their real Word/VS Code.
        if (args.Length == 3 && args[0] == "selftest-probe"
            && int.TryParse(args[1], out int sx) && int.TryParse(args[2], out int sy))
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            var r = WordLookup.AtPoint(new WpfPoint(sx, sy));
            Console.WriteLine($"{{\"supported\":{r.Supported.ToString().ToLower()},\"word\":\"{r.Word}\",\"sentence\":\"{r.Sentence}\"}}");
            return;
        }

        if (args.Length == 3 && args[0] == "selftest-scan" && long.TryParse(args[1], out long hwndVal))
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine(WordLookup.DebugScan(new IntPtr(hwndVal), args[2]));
            return;
        }

        if (args.Length == 1 && args[0] == "selftest-lang")
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            var cases = new[]
            {
                ("My name is Thảo, tonight I am going to make a presentation about working remotely issues.", "en"),
                ("Chúng ta cần một cách tiếp cận mới.", "vi"),
                ("approach", "en"),
                ("khác", "vi"),
                ("Hello world", "en"),
            };
            foreach (var (text, expected) in cases)
            {
                string actual = LanguageDetector.Detect(text);
                Console.WriteLine($"[{(actual == expected ? "PASS" : "FAIL")}] '{text}' -> {actual} (expect {expected})");
            }
            return;
        }

        if (args.Length == 1 && args[0] == "selftest-selection")
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            RunSelectionSelfTest();
            return;
        }

        if (args.Length == 1 && args[0] == "selftest-cache")
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            string probe = $"selftest sentence {Guid.NewGuid()}";
            Console.WriteLine($"before set, TryGet: {TranslationCache.TryGet(probe, "en", "vi") ?? "(null)"}");
            TranslationCache.Set(probe, "en", "vi", "câu kiểm tra cache");
            Console.WriteLine($"after set, TryGet (same process): {TranslationCache.TryGet(probe, "en", "vi") ?? "(null)"}");
            System.Threading.Thread.Sleep(300); // let the async save finish
            return;
        }

        if (args.Length == 1 && args[0] == "selftest-ocr")
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            RunOcrSelfTest();
            return;
        }

        if (args.Length == 1 && args[0] == "selftest-gtdict")
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            RunGoogleDictionarySelfTestAsync().GetAwaiter().GetResult();
            return;
        }

        if (args.Length == 1 && args[0] == "selftest-mic")
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            RunMicSelfTestAsync().GetAwaiter().GetResult();
            return;
        }

        if (args.Length == 1 && args[0] == "selftest-mic-e2e")
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            RunMicEndToEndSelfTestAsync().GetAwaiter().GetResult();
            return;
        }

        if (args.Length == 1 && args[0] == "selftest-sysaudio-e2e")
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            RunSystemAudioEndToEndSelfTestAsync().GetAwaiter().GetResult();
            return;
        }

        if (args.Length == 1 && args[0] == "selftest-ocr-word")
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            RunOcrWordSelfTest();
            return;
        }

        if (args.Length == 1 && args[0] == "selftest-stt")
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            RunSttSelfTestAsync().GetAwaiter().GetResult();
            return;
        }

        // Chỉ cho 1 bản chạy cùng lúc - user vô tình mở lần 2 (double-click lại
        // exe, không để ý đã có sẵn 1 bản chạy nền vì tray icon dễ bị che) từng
        // gây lỗi khó hiểu: 2 tiến trình cùng lắng nghe hotkey nên Alt+T bật ra
        // 2 popup, còn Alt+Shift+S thì Settings bị các cửa sổ topmost của tiến
        // trình kia đè lên nhìn như "không ra". Giữ biến ở scope Main() (không
        // dispose sớm) vì app.Run() bên dưới chạy đồng bộ suốt vòng đời app.
        var singleInstanceMutex = new System.Threading.Mutex(true, "HoverTranslate.App.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            System.Windows.Forms.MessageBox.Show(
                "Hover Translate đang chạy rồi (xem khay hệ thống ở góc dưới màn hình).",
                "Hover Translate", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Information);
            return;
        }

        // Real (non-selftest) launch: this project is built as a console-subsystem
        // exe only so selftest-probe's output can be captured reliably (WinExe
        // subsystem apps don't reliably expose Console.Out even when a parent
        // redirects it). Hide the console immediately so normal users never see it.
        _debugMode = args.Contains("--debug");
        if (_debugMode) Console.OutputEncoding = System.Text.Encoding.UTF8;
        else NativeMethods.ShowWindow(NativeMethods.GetConsoleWindow(), NativeMethods.SW_HIDE);

        var app = new WpfApplication { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };

        // Spec mục 28 "Polish": app chạy nền liên tục, 1 lỗi bất ngờ ở đâu đó
        // (1 popup update, 1 lần hover...) không được phép làm sập cả app -
        // ghi log lại để chẩn đoán sau, rồi cố gắng chạy tiếp thay vì crash.
        app.DispatcherUnhandledException += (_, e) =>
        {
            CrashLog.Write("DispatcherUnhandledException", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            CrashLog.Write("AppDomain.UnhandledException (fatal)", e.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        _popup = new PopupWindow();
        _settingsWindow = new SettingsWindow();
        _quickTranslateWindow = new QuickTranslateWindow();
        _liveTranslateWindow = new LiveTranslateWindow();
        TryLaunchOfflineServer();

        var trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = "Hover Translate (Alt + rê chuột vào chữ)"
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Hover Translate — giữ Alt và rê chuột vào chữ", null, (_, _) => { }).Enabled = false;
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Cài đặt (Alt+Shift+S)", null, (_, _) => _settingsWindow!.ShowAndActivate());
        menu.Items.Add("Dịch nhanh (Alt+T)", null, (_, _) => _quickTranslateWindow!.ShowAndFocus());
        menu.Items.Add("Live Translate (Alt+L)", null, (_, _) => _liveTranslateWindow!.ShowAndFocus());

        var targetLangMenu = new System.Windows.Forms.ToolStripMenuItem("Ngôn ngữ đích");
        var targetLangItems = new List<System.Windows.Forms.ToolStripMenuItem>();
        foreach (var (code, label) in AppSettings.SupportedTargetLanguages)
        {
            var item = new System.Windows.Forms.ToolStripMenuItem(label) { Checked = code == AppSettings.TargetLanguage };
            item.Click += (_, _) =>
            {
                AppSettings.TargetLanguage = code;
                foreach (var other in targetLangItems) other.Checked = false;
                item.Checked = true;
            };
            targetLangItems.Add(item);
            targetLangMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(targetLangMenu);

        var modeMenu = new System.Windows.Forms.ToolStripMenuItem("Chế độ dịch");
        var modeItems = new List<(TranslationMode Mode, System.Windows.Forms.ToolStripMenuItem Item)>();
        void AddModeItem(TranslationMode mode, string label)
        {
            var item = new System.Windows.Forms.ToolStripMenuItem(label) { Checked = mode == AppSettings.Mode };
            item.Click += (_, _) =>
            {
                AppSettings.Mode = mode;
                foreach (var (_, other) in modeItems) other.Checked = false;
                item.Checked = true;
            };
            modeItems.Add((mode, item));
            modeMenu.DropDownItems.Add(item);
        }
        AddModeItem(TranslationMode.Auto, "Tự động (offline trước, online dự phòng)");
        AddModeItem(TranslationMode.OfflineOnly, "Chỉ offline");
        AddModeItem(TranslationMode.OnlineOnly, "Chỉ online");
        menu.Items.Add(modeMenu);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Mở log lỗi", null, (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(CrashLog.LogFilePath) { UseShellExecute = true }); }
            catch { /* chưa có lỗi nào -> file chưa tồn tại, không sao */ }
        });
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Thoát", null, (_, _) => app.Shutdown());
        trayIcon.ContextMenuStrip = menu;
        // NotifyIcon only opens ContextMenuStrip automatically on a right-click;
        // make a left-click do the same so the menu is easy to find.
        trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left) menu.Show(System.Windows.Forms.Cursor.Position);
        };
        trayIcon.ShowBalloonTip(5000, "Hover Translate", "Đang chạy nền. Click (trái hoặc phải) vào icon này để chỉnh ngôn ngữ đích/chế độ dịch. Giữ Alt và rê chuột vào chữ để dịch.", System.Windows.Forms.ToolTipIcon.Info);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PollIntervalMs) };
        timer.Tick += (_, _) =>
        {
            try { Tick(); }
            catch (Exception ex) { CrashLog.Write("Tick()", ex); }
        };
        timer.Start();

        app.Exit += (_, _) =>
        {
            trayIcon.Dispose();
            try { if (_offlineServerProcess is { HasExited: false }) _offlineServerProcess.Kill(); } catch { }
        };

        if (args.Contains("--show-settings")) _settingsWindow.ShowAndActivate();

        app.Run();
    }

    // Starts scripts/translate_server.py (Argos Translate) as a background
    // sidecar so the .NET app has a real offline engine to call, per docs/
    // PHASE0_FINDINGS.md mục 6 hướng A. Fire-and-forget: if this fails (no
    // Python, packages missing) Tick() just falls back to OnlineTranslator.
    private static void TryLaunchOfflineServer()
    {
        try
        {
            string? scriptPath = FindTranslateServerScript();
            if (scriptPath is null) return;

            var psi = new ProcessStartInfo(PythonRuntime.Executable, $"\"{scriptPath}\" 5055")
            {
                WorkingDirectory = Path.GetDirectoryName(scriptPath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // No stdout/stderr redirect: this process runs for the app's whole
            // lifetime, and an unread redirected pipe fills up and blocks the
            // child once enough gets logged (only a couple of startup lines
            // here, but no reason to risk it).
            _offlineServerProcess = Process.Start(psi);
            if (_offlineServerProcess is not null) ProcessJobObject.AttachToLifetimeOfThisApp(_offlineServerProcess);
        }
        catch
        {
            _offlineServerProcess = null;
        }
    }

    // Dùng chung cho hover, OCR, và QuickTranslateWindow - 1 pipeline dịch
    // duy nhất (cache + offline-first + Google/MyMemory dự phòng) cho toàn app.
    public static Task<string?> TranslatePublicAsync(string text, string sourceLang, string targetLang)
        => TranslateWithFallbackAsync(text, sourceLang, targetLang);

    // Offline-first per spec mục 4's whole design principle: try the local
    // engine, only reach for the network path if it isn't ready/available -
    // unless the user forced a specific mode from the tray menu.
    private static async Task<string?> TranslateWithFallbackAsync(string text, string sourceLang, string targetLang)
    {
        string? cached = TranslationCache.TryGet(text, sourceLang, targetLang);
        if (cached is not null) return $"[cache] {cached}";

        string? result = await TranslateFreshAsync(text, sourceLang, targetLang);

        // Only cache clean engine output, never the placeholder/error strings
        // (those must keep re-trying next time, e.g. once the offline server
        // finishes starting up). Bản dịch ra Y HỆT chữ gốc gần như luôn là dấu
        // hiệu engine dự phòng (MyMemory) bị lỗi/timeout và trả lại nguyên văn
        // thay vì báo lỗi thật - không phải bản dịch hợp lệ. Gặp thực tế: cache
        // "transcrip"->"transcrip" từ 1 lần lỗi cũ, bị kẹt vĩnh viễn ở đó cho
        // tới khi người dùng xoá cache thủ công. Không cache case này - chấp
        // nhận mất phần hiếm hoi dịch ra y hệt thật sự đúng (vd "OK"->"OK"),
        // đổi lại tránh cache rác lặp lại kiểu trên.
        if (result is not null && (result.StartsWith("[offline] ") || result.StartsWith("[online] ")))
        {
            string plain = result[(result.IndexOf(' ') + 1)..];
            if (!string.Equals(plain.Trim(), text.Trim(), StringComparison.OrdinalIgnoreCase))
                TranslationCache.Set(text, sourceLang, targetLang, plain);
        }

        return result;
    }

    private static async Task<string?> TranslateFreshAsync(string text, string sourceLang, string targetLang)
    {
        if (AppSettings.Mode == TranslationMode.OnlineOnly)
        {
            string? online = await TranslateOnlineAsync(text, sourceLang, targetLang);
            return online is null ? null : $"[online] {online}";
        }

        string? offlineResult = await OfflineTranslator.TranslateSentenceAsync(text, sourceLang, targetLang);
        if (offlineResult is not null) return $"[offline] {offlineResult}";

        if (AppSettings.Mode == TranslationMode.OfflineOnly)
            return "(server offline chưa sẵn sàng cho cặp ngôn ngữ này)";

        string? onlineResult = await TranslateOnlineAsync(text, sourceLang, targetLang);
        return onlineResult is null ? null : $"[online] {onlineResult}";
    }

    // Google (endpoint công khai, miễn phí, dịch chính xác hơn hẳn cho tiếng
    // Việt kiểu chat viết tắt - lý do người dùng yêu cầu) trước, MyMemory làm
    // dự phòng nếu endpoint đó lỗi/bị chặn.
    private static async Task<string?> TranslateOnlineAsync(string text, string sourceLang, string targetLang)
    {
        string? google = await GoogleTranslateOnline.TranslateSentenceAsync(text, sourceLang, targetLang);
        if (google is not null) return google;
        return await OnlineTranslator.TranslateSentenceAsync(text, sourceLang, targetLang);
    }

    // Dev-only: builds its own isolated window with known text and runs it
    // through the real OcrEngine/ScreenCapture code - same "test on something
    // we control, never the user's real screen" approach as TestHarness in
    // poc/. Confirms Windows OCR actually works on this machine before asking
    // the user to try Alt+Q on their own content.
    // Dev-only: tạo cửa sổ WPF TextBox cô lập, bôi đen 1 cụm bằng code
    // (TextBox.Select), rồi gọi đúng WordLookup.AtPoint thật để xác nhận nó
    // trả về cả cụm đã chọn thay vì chỉ 1 từ.
    private static void RunSelectionSelfTest()
    {
        var app = new WpfApplication();
        const string sampleText = "We need a different approach to marketing this quarter.";
        var textBox = new System.Windows.Controls.TextBox
        {
            Text = sampleText,
            FontSize = 20,
            Width = 480,
            Height = 60,
        };
        var win = new System.Windows.Window
        {
            Title = "Selection selftest (isolated)",
            Width = 520,
            Height = 140,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen,
            Content = textBox,
        };

        win.ContentRendered += async (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
            NativeMethods.SetForegroundWindow(hwnd);
            win.Activate();
            for (int i = 0; i < 30 && NativeMethods.GetForegroundWindow() != hwnd; i++)
                await Task.Delay(100);

            const string phrase = "different approach";
            int start = sampleText.IndexOf(phrase, StringComparison.Ordinal);
            textBox.Focus();
            textBox.Select(start, phrase.Length);
            await Task.Delay(500);

            Console.WriteLine($"Actual selection in textbox: '{textBox.SelectedText}'");
            // Qua hwnd trực tiếp - không phụ thuộc toạ độ màn hình/z-order
            // (đã thấy pixel-probing không ổn định trong môi trường này).
            Console.WriteLine(WordLookup.DebugSelection(hwnd));

            app.Shutdown();
        };

        app.Run(win);
    }

    // Dev-only: dùng Windows.Media.SpeechSynthesis (WinRT có sẵn, không cần
    // thêm dependency) để tạo 1 câu tiếng Anh mẫu thành audio, đẩy thẳng qua
    // SpeechRecognitionService (bỏ qua AudioCaptureService thật) để xác nhận
    // stt_server.py hoạt động đúng - không đụng mic/audio thật của người dùng.
    private static async Task RunSttSelfTestAsync()
    {
        const string expectedPhrase = "hello world";
        const string sentence = "Hello world, this is a test sentence for speech recognition.";

        Console.WriteLine("Đang tạo audio mẫu bằng Windows Speech Synthesis...");
        var synth = new Windows.Media.SpeechSynthesis.SpeechSynthesizer();
        var stream = await synth.SynthesizeTextToStreamAsync(sentence);

        var reader = new Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        byte[] wavBytes = new byte[stream.Size];
        reader.ReadBytes(wavBytes);
        Console.WriteLine($"Audio mẫu: {wavBytes.Length} bytes, ContentType: {stream.ContentType}");
        string debugDumpPath = Path.Combine(Path.GetTempPath(), "hovertranslate_selftest_stt.wav");
        File.WriteAllBytes(debugDumpPath, wavBytes);
        Console.WriteLine($"(debug) đã lưu tạm để kiểm tra: {debugDumpPath}");

        Console.WriteLine("Khởi động stt_server.py (model tiny)...");
        var stt = new SpeechRecognitionService();
        stt.Start("tiny");
        try
        {
            bool ready = await stt.WaitUntilReadyAsync(TimeSpan.FromSeconds(60));
            Console.WriteLine($"STT sẵn sàng: {ready}");
            if (!ready) return;

            var autoResult = await stt.TranscribeAsync(wavBytes);
            Console.WriteLine($"Auto-detect ngôn ngữ: '{autoResult?.Text}' (nhận diện: {autoResult?.Language})");

            // Giọng TTS robotic của Windows đôi khi đánh lừa auto-LID của
            // model "tiny" (đã thấy nhận nhầm sang tiếng Thái) - ép "en" để
            // xác nhận riêng PIPELINE (capture->encode->HTTP->model->kết quả)
            // hoạt động đúng, tách khỏi vấn đề độ chính xác auto-detect với
            // giọng tổng hợp (âm thanh người thật/video thực tế nên ổn hơn).
            var enResult = await stt.TranscribeAsync(wavBytes, "en");
            Console.WriteLine($"Ép ngôn ngữ en: '{enResult?.Text}'");
            bool pass = enResult?.Text.Contains(expectedPhrase, StringComparison.OrdinalIgnoreCase) == true;
            Console.WriteLine($"[{(pass ? "PASS" : "FAIL")}] Pipeline hoạt động đúng (chứa '{expectedPhrase}' khi ép ngôn ngữ): {pass}");
        }
        finally
        {
            stt.Stop();
        }
    }

    // So sánh trực tiếp qua HttpClient của chính app (đang gọi được, khác
    // PowerShell của mình đang bị Google rate-limit riêng do dò code lúc nãy)
    // - kiểm tra xem từ chia động từ ("getting") có thật sự thiếu dữ liệu
    // dt=bd từ Google hay là lỗi parse ở TryExtractDictionary().
    private static async Task RunGoogleDictionarySelfTestAsync()
    {
        foreach (var word in new[] { "transcrip", "strancrip", "recieve", "definately" })
        {
            var (translated, suggestion, dictionary) = await GoogleTranslateOnline.TranslateWithDetailsAsync(word, "en", "vi");
            Console.WriteLine($"--- '{word}' ---");
            Console.WriteLine($"translated='{translated}' suggestion='{suggestion}'");
            Console.WriteLine($"dictionary entries: {dictionary.Count}");
            foreach (var entry in dictionary)
                Console.WriteLine($"  {entry.PartOfSpeech}: {string.Join(", ", entry.Meanings)}");
        }
    }

    // Xác nhận pipeline capture Microphone khởi động được và có dữ liệu chảy
    // qua (không cần người dùng nói gì - im lặng/tiếng ồn nền cũng tính, WASAPI
    // vẫn bắn sự kiện DataAvailable đều đặn bất kể mức âm lượng). Không đụng
    // tới UI/màn hình thật, chỉ test riêng lớp capture.
    private static async Task RunMicSelfTestAsync()
    {
        var capture = new AudioCaptureService();
        int chunksReceived = 0;
        long bytesReceived = 0;
        capture.DataAvailable += (chunk, format) =>
        {
            chunksReceived++;
            bytesReceived += chunk.Length;
            if (chunksReceived == 1) Console.WriteLine($"WaveFormat: {format}");
        };
        capture.Stopped += ex => Console.WriteLine(ex is null ? "Stopped (bình thường)" : $"Stopped (lỗi): {ex.Message}");

        Console.WriteLine("Đang mở thiết bị Microphone mặc định...");
        bool ok = capture.Start(AudioSourceKind.Microphone);
        Console.WriteLine($"Start() = {ok}");
        if (!ok)
        {
            Console.WriteLine("[FAIL] Không mở được microphone - có thể máy không có mic, hoặc Windows chưa cấp quyền truy cập microphone cho app.");
            return;
        }

        await Task.Delay(2000);
        capture.Stop();
        await Task.Delay(300);

        Console.WriteLine($"Nhận được {chunksReceived} chunk, tổng {bytesReceived} bytes trong 2s.");
        Console.WriteLine(chunksReceived > 0 ? "[PASS] Pipeline capture Microphone hoạt động." : "[FAIL] Không nhận được dữ liệu nào.");
        capture.Dispose();
    }

    // Test đầu-cuối thật sự cho Microphone: PHÁT tiếng nói tổng hợp qua LOA
    // (không phải ghi ra file như các selftest khác) trong lúc mic đang ghi -
    // giả lập chính xác việc "microphone nghe được người nói" mà không cần
    // người dùng tự nói. Kiểm tra luôn per-channel RMS để xác định mic 4 kênh
    // có bị chọn nhầm kênh câm (MultiplexingSampleProvider mặc định lấy kênh
    // 0) hay không - nghi ngờ chính cho báo cáo "không nghe được giọng nói".
    private static async Task RunMicEndToEndSelfTestAsync()
    {
        const string phrase = "Hello, this is a microphone test, one two three four five.";
        var capture = new AudioCaptureService();
        var raw = new List<byte>();
        NAudio.Wave.WaveFormat? format = null;
        capture.DataAvailable += (chunk, fmt) =>
        {
            format = fmt;
            lock (raw) raw.AddRange(chunk);
        };

        Console.WriteLine("Mở microphone...");
        if (!capture.Start(AudioSourceKind.Microphone))
        {
            Console.WriteLine("[FAIL] Không mở được microphone.");
            return;
        }

        await Task.Delay(300); // đệm trước khi nói, tránh cắt đầu câu

        using (var synth = new System.Speech.Synthesis.SpeechSynthesizer())
        {
            Console.WriteLine($"Đang phát qua loa: \"{phrase}\"");
            synth.Speak(phrase); // blocking - phát xong mới trả về
        }

        await Task.Delay(500); // đệm sau khi nói
        capture.Stop();
        await Task.Delay(300);

        byte[] pcm;
        lock (raw) pcm = raw.ToArray();
        Console.WriteLine($"Đã ghi {pcm.Length} bytes, format: {format}");

        if (format is null || pcm.Length == 0)
        {
            Console.WriteLine("[FAIL] Không có dữ liệu audio nào được ghi.");
            return;
        }

        // Chẩn đoán per-channel RMS - IEEE Float 32-bit, N kênh interleaved.
        if (format.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            int channels = format.Channels;
            var sumSquares = new double[channels];
            int samplesPerChannel = 0;
            for (int i = 0; i + 4 * channels <= pcm.Length; i += 4 * channels)
            {
                for (int c = 0; c < channels; c++)
                {
                    float s = BitConverter.ToSingle(pcm, i + c * 4);
                    sumSquares[c] += (double)s * s;
                }
                samplesPerChannel++;
            }
            if (samplesPerChannel > 0)
            {
                for (int c = 0; c < channels; c++)
                {
                    double rms = Math.Sqrt(sumSquares[c] / samplesPerChannel);
                    Console.WriteLine($"  Kênh {c}: RMS = {rms:F6}");
                }
            }
        }

        byte[] wav = AudioConvert.ToWav16kMono(pcm, format);

        var stt = new SpeechRecognitionService();
        stt.Start("tiny");
        try
        {
            bool ready = await stt.WaitUntilReadyAsync(TimeSpan.FromSeconds(30));
            if (!ready) { Console.WriteLine("[FAIL] STT không sẵn sàng."); return; }

            var result = await stt.TranscribeAsync(wav, "en");
            Console.WriteLine($"Kết quả nhận diện: '{result?.Text}'");
            bool pass = result?.Text.Contains("microphone", StringComparison.OrdinalIgnoreCase) == true;
            Console.WriteLine(pass ? "[PASS] Nghe đúng nội dung phát qua loa." : "[FAIL] Không nhận diện đúng - kiểm tra channel RMS ở trên để xem có bị chọn nhầm kênh câm không.");
        }
        finally
        {
            stt.Stop();
        }
    }

    // Tương tự RunMicEndToEndSelfTestAsync nhưng test System Audio (loopback)
    // thay vì Microphone - đáng tin hơn hẳn vì loopback bắt trực tiếp luồng
    // digital từ OS mixer, không phụ thuộc loa/mic vật lý thật có kết nối âm
    // thanh với nhau hay không (môi trường dev này chưa chắc có).
    private static async Task RunSystemAudioEndToEndSelfTestAsync()
    {
        const string phrase = "Hello, this is a system audio loopback test, one two three four five.";
        var capture = new AudioCaptureService();
        var raw = new List<byte>();
        NAudio.Wave.WaveFormat? format = null;
        capture.DataAvailable += (chunk, fmt) =>
        {
            format = fmt;
            lock (raw) raw.AddRange(chunk);
        };

        Console.WriteLine("Mở System Audio (loopback)...");
        if (!capture.Start(AudioSourceKind.SystemAudio))
        {
            Console.WriteLine("[FAIL] Không mở được System Audio.");
            return;
        }

        await Task.Delay(300);

        using (var synth = new System.Speech.Synthesis.SpeechSynthesizer())
        {
            Console.WriteLine($"Đang phát: \"{phrase}\"");
            synth.Speak(phrase);
        }

        await Task.Delay(500);
        capture.Stop();
        await Task.Delay(300);

        byte[] pcm;
        lock (raw) pcm = raw.ToArray();
        Console.WriteLine($"Đã ghi {pcm.Length} bytes, format: {format}");
        if (format is null || pcm.Length == 0) { Console.WriteLine("[FAIL] Không có dữ liệu."); return; }

        byte[] wav = AudioConvert.ToWav16kMono(pcm, format);

        var stt = new SpeechRecognitionService();
        stt.Start("tiny");
        try
        {
            bool ready = await stt.WaitUntilReadyAsync(TimeSpan.FromSeconds(30));
            if (!ready) { Console.WriteLine("[FAIL] STT không sẵn sàng."); return; }

            var result = await stt.TranscribeAsync(wav, "en");
            Console.WriteLine($"Kết quả nhận diện: '{result?.Text}'");
            bool pass = result?.Text.Contains("loopback", StringComparison.OrdinalIgnoreCase) == true
                || result?.Text.Contains("system audio", StringComparison.OrdinalIgnoreCase) == true;
            Console.WriteLine(pass ? "[PASS]" : "[FAIL] Không khớp nội dung đã phát.");
        }
        finally
        {
            stt.Stop();
        }
    }

    private static void RunOcrSelfTest()
    {
        var app = new WpfApplication();
        var stack = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(20) };
        stack.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Hello World 12345",
            FontSize = 30,
            FontWeight = System.Windows.FontWeights.Bold,
            Foreground = System.Windows.Media.Brushes.Black,
        });
        stack.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Xin chào các bạn",
            FontSize = 30,
            FontWeight = System.Windows.FontWeights.Bold,
            Foreground = System.Windows.Media.Brushes.Black,
            Margin = new System.Windows.Thickness(0, 10, 0, 0),
        });
        var win = new System.Windows.Window
        {
            Title = "OCR selftest (isolated)",
            Width = 420,
            Height = 220,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen,
            Background = System.Windows.Media.Brushes.White,
            Content = stack,
        };

        win.Topmost = true;
        win.ContentRendered += async (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
            NativeMethods.SetForegroundWindow(hwnd);
            win.Activate();

            // Đợi tới khi cửa sổ này thực sự lên foreground (từng bị lệch vì
            // capture chạy trước khi window thật sự nổi lên trên cùng).
            for (int i = 0; i < 30 && NativeMethods.GetForegroundWindow() != hwnd; i++)
                await Task.Delay(100);
            await Task.Delay(300);

            NativeMethods.GetWindowRect(hwnd, out var r);
            var region = new System.Drawing.Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            Console.WriteLine($"Foreground matches our window: {NativeMethods.GetForegroundWindow() == hwnd}");
            Console.WriteLine($"Captured region: {region}");

            string? tesseractText;
            string? windowsText;
            using (var raw = ScreenCapture.Capture(region))
            using (var bitmap = ImagePreprocessor.Upscale(raw))
            {
                tesseractText = await TesseractOcrEngine.RecognizeAsync(bitmap);
            }
            using (var raw = ScreenCapture.Capture(region))
            using (var bitmap = ImagePreprocessor.Upscale(raw))
            {
                windowsText = await OcrEngine.RecognizeAsync(bitmap);
            }

            Console.WriteLine($"Tesseract (eng+vie) result: '{tesseractText}'");
            Console.WriteLine($"  contains 'Hello': {tesseractText?.Contains("Hello", StringComparison.OrdinalIgnoreCase) == true}");
            Console.WriteLine($"  contains 'Xin chào': {tesseractText?.Contains("Xin ch", StringComparison.OrdinalIgnoreCase) == true}");
            Console.WriteLine($"Windows OCR result: '{windowsText}'");
            app.Shutdown();
        };

        app.Run(win);
    }

    // Mô phỏng đúng tình huống lỗi thực tế đã gặp: 1 bảng nhiều "cột" chữ nằm
    // sát nhau trên cùng 1 dòng (giống bảng Why/Impact/Constraint trong report
    // của người dùng). Kiểm tra hover đúng vào từ giữa ("Constraint") chỉ trả
    // về đúng từ đó, không lẫn chữ garbled từ 2 cột bên cạnh - cửa sổ tự tạo,
    // cô lập, không đụng màn hình/app thật của người dùng.
    private static void RunOcrWordSelfTest()
    {
        var app = new WpfApplication();
        var row = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new System.Windows.Thickness(20),
        };
        var tb1 = new System.Windows.Controls.TextBlock { Text = "Constraint", FontSize = 22, Foreground = System.Windows.Media.Brushes.Black, Margin = new System.Windows.Thickness(0, 0, 50, 0) };
        var tb2 = new System.Windows.Controls.TextBlock { Text = "Gioi han la gi", FontSize = 22, Foreground = System.Windows.Media.Brushes.Black, Margin = new System.Windows.Thickness(0, 0, 50, 0) };
        var tb3 = new System.Windows.Controls.TextBlock { Text = "Danh sach uu tien", FontSize = 22, Foreground = System.Windows.Media.Brushes.Black };
        row.Children.Add(tb1);
        row.Children.Add(tb2);
        row.Children.Add(tb3);

        var win = new System.Windows.Window
        {
            Title = "OCR word-at-point selftest (isolated)",
            Width = 800,
            Height = 150,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen,
            Background = System.Windows.Media.Brushes.White,
            Content = row,
        };

        win.Topmost = true;
        win.ContentRendered += async (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
            NativeMethods.SetForegroundWindow(hwnd);
            win.Activate();
            for (int i = 0; i < 30 && NativeMethods.GetForegroundWindow() != hwnd; i++)
                await Task.Delay(100);
            await Task.Delay(300);

            // KHÔNG dùng tb1.PointToScreen() - trả về đơn vị DIP (logical) của
            // WPF, trong khi ScreenCapture (GDI+) cần pixel VẬT LÝ. Trên máy
            // này màn hình đang scale 150% nên 2 hệ đơn vị lệch nhau đúng hệ
            // số đó, y hệt bug thật đã phát hiện qua self-test này (cursor
            // GetCursorPos() thì luôn là pixel vật lý - production code không
            // dính lỗi này, chỉ self-test tự tạo điểm giả mới cần quy đổi).
            NativeMethods.GetWindowRect(hwnd, out var winRect);
            double scaleX = (winRect.Right - winRect.Left) / win.ActualWidth;
            double scaleY = (winRect.Bottom - winRect.Top) / win.ActualHeight;
            var centerInWindow = tb1.TransformToAncestor(win).Transform(new WpfPoint(tb1.ActualWidth / 2, tb1.ActualHeight / 2));
            int cursorX = winRect.Left + (int)(centerInWindow.X * scaleX);
            int cursorY = winRect.Top + (int)(centerInWindow.Y * scaleY);

            Console.WriteLine($"win physical rect via GetWindowRect: {winRect.Left},{winRect.Top},{winRect.Right},{winRect.Bottom} (scale {scaleX:F2}x{scaleY:F2})");
            Console.WriteLine($"tb1 center in window DIP: ({centerInWindow.X:F1},{centerInWindow.Y:F1}) -> physical cursor ({cursorX},{cursorY})");

            var region = ClampRegionToVirtualScreen(cursorX, cursorY, OcrHoverRegionSize);
            Console.WriteLine($"cursor=({cursorX},{cursorY}) hovering 'Constraint', capture region={region}");

            using var raw = ScreenCapture.Capture(region);
            using var bitmap = ImagePreprocessor.Upscale(raw, OcrHoverUpscaleFactor);
            var localPoint = new System.Drawing.Point(
                (int)((cursorX - region.Left) * OcrHoverUpscaleFactor),
                (int)((cursorY - region.Top) * OcrHoverUpscaleFactor));

            // Chẩn đoán: OCR nguyên khối vùng chụp (engine đã biết chạy đúng
            // qua selftest-ocr) - nếu "Constraint" không xuất hiện ở đây thì
            // lỗi nằm ở việc CHỤP SAI VÙNG (toạ độ), không phải ở logic chọn
            // từ gần con trỏ.
            using (var wholeRaw = ScreenCapture.Capture(region))
            using (var wholeBitmap = ImagePreprocessor.Upscale(wholeRaw, OcrHoverUpscaleFactor))
            {
                string? whole = await TesseractOcrEngine.RecognizeAsync(wholeBitmap);
                Console.WriteLine($"whole-region OCR (diagnostic): '{whole}'");
            }

            var hit = await TesseractOcrEngine.RecognizeWordAtPointAsync(bitmap, localPoint);
            Console.WriteLine($"localPoint in upscaled bitmap: {localPoint}");
            Console.WriteLine($"word='{hit?.Word}' line='{hit?.Line}'");
            bool pass = hit?.Word?.Contains("onstraint", StringComparison.OrdinalIgnoreCase) == true;
            Console.WriteLine($"[{(pass ? "PASS" : "FAIL")}] expect word to contain 'onstraint', got '{hit?.Word}'");
            app.Shutdown();
        };

        app.Run(win);
    }

    // Kích thước vùng tự động chụp quanh con trỏ khi hover thất bại - đủ rộng
    // để lấy trọn 1 dòng chat/1 ô bảng nhưng vẫn đủ nhỏ để Tesseract chạy
    // nhanh và tránh dính chữ của cột/dòng bên cạnh (bảng nhiều cột san sát
    // nhau từng bị chụp lẫn chữ cột kế bên, gây dịch ra nội dung rác thừa) -
    // vùng nhỏ hơn còn giúp việc chọn "từ gần con trỏ nhất" ở dưới chính xác
    // hơn vì có ít từ nhiễu để so khoảng cách.
    private static readonly System.Drawing.Size OcrHoverRegionSize = new(320, 56);
    private const float OcrHoverUpscaleFactor = 3f;

    // Spec mục 27 (Intelligent Fallback): 1 số app (Zalo...) không expose text
    // qua Accessibility API ở BẤT KỲ điểm nào trên cửa sổ - UIA vô dụng với
    // chúng, không phải lỗi tạm thời để retry thêm. Tự OCR vùng nhỏ quanh con
    // trỏ, tái dùng đúng pipeline Tesseract+preprocessing của Alt+Q, để hover
    // vẫn dịch được thay vì bắt người dùng tự chuyển sang Alt+Q mỗi lần.
    private static async Task TryOcrHoverFallbackAsync(int cursorX, int cursorY, int token)
    {
        const string label = "🔍 OCR (app này không hỗ trợ Accessibility)";

        // token khác _hoverToken hiện tại nghĩa là người dùng đã rê chuột sang
        // điểm khác kể từ lúc bắt đầu - đừng hiện gì cả, tránh popup "giật"
        // ngược về vị trí cũ đã lỗi thời.
        if (token != _hoverToken) return;

        _popup!.ShowAt(cursorX, cursorY, label, "Đang đọc chữ qua OCR...", "", "vi", "vi");
        _lastShownWord = label;
        // Hành động OCR không gắn với việc giữ Alt liên tục trong lúc chạy -
        // ghim sẵn để Tick() không tự ẩn mất kết quả giữa chừng (giống lý do
        // TriggerOcrCapture() đã ghim).
        _popup.SetPinned(true);

        try
        {
            var region = ClampRegionToVirtualScreen(cursorX, cursorY, OcrHoverRegionSize);

            string? word, line;
            using (var raw = ScreenCapture.Capture(region))
            using (var bitmap = ImagePreprocessor.Upscale(raw, OcrHoverUpscaleFactor))
            {
                // Toạ độ con trỏ trong hệ của bitmap ĐÃ upscale - phải nhân
                // cùng hệ số đã dùng để phóng to, không thì "điểm hover" sẽ
                // trỏ sai chỗ so với bounding box Tesseract trả về.
                var localPoint = new System.Drawing.Point(
                    (int)((cursorX - region.Left) * OcrHoverUpscaleFactor),
                    (int)((cursorY - region.Top) * OcrHoverUpscaleFactor));

                var hit = await TesseractOcrEngine.RecognizeWordAtPointAsync(bitmap, localPoint);
                if (hit is not null)
                {
                    word = hit.Value.Word;
                    line = hit.Value.Line;
                }
                else
                {
                    // Không tách được word box theo toạ độ (bố cục lạ/ảnh
                    // nhiễu) - dự phòng dịch nguyên khối còn hơn không trả gì.
                    word = await TesseractOcrEngine.RecognizeAsync(bitmap) ?? await OcrEngine.RecognizeAsync(bitmap);
                    line = word;
                }
            }
            // bitmap đã Dispose ở đây - không lưu screenshot (spec mục 17).

            if (token != _hoverToken) return; // đã rê sang chỗ khác trong lúc OCR chạy - kết quả không còn khớp nữa

            if (string.IsNullOrWhiteSpace(word))
            {
                _popup.ShowAt(cursorX, cursorY, "⚠ Không đọc được chữ ở đây",
                    "App này không hỗ trợ Accessibility API, và vùng OCR quanh con trỏ cũng không thấy chữ. Dùng Alt+Q để tự chọn đúng vùng.",
                    "", "vi", "vi");
                _lastShownWord = "⚠ accessibility-unavailable";
                return;
            }

            string detectFrom = string.IsNullOrWhiteSpace(line) ? word : line;
            string sourceLang = LanguageDetector.Detect(detectFrom);
            string targetLang = sourceLang == AppSettings.TargetLanguage
                ? LanguageDetector.DefaultTargetFor(sourceLang)
                : AppSettings.TargetLanguage;

            // Chỉ hiện dòng ngữ cảnh nếu nó thật sự khác chữ đang dịch - đúng
            // yêu cầu "chỉ dịch chữ đó, không thêm ký tự khác" khi không có gì
            // thêm để cho ngữ cảnh.
            string sentenceForPopup = string.Equals(line, word, StringComparison.Ordinal) ? "" : line ?? "";

            _popup.ShowAt(cursorX, cursorY, word, "Đang dịch...", sentenceForPopup, sourceLang, targetLang);
            _lastShownWord = word;

            string wordForThisRequest = word;
            _ = TranslateWithFallbackAsync(word, sourceLang, targetLang).ContinueWith(t =>
            {
                if (token != _hoverToken) return;
                _popup.Dispatcher.Invoke(() => _popup.SetWordMeaning(wordForThisRequest, t.Result));
            }, TaskScheduler.Default);

            if (!string.IsNullOrWhiteSpace(sentenceForPopup))
            {
                _ = TranslateWithFallbackAsync(sentenceForPopup, sourceLang, targetLang).ContinueWith(t =>
                {
                    if (token != _hoverToken) return;
                    _popup.Dispatcher.Invoke(() => _popup.SetSentenceTranslation(wordForThisRequest, t.Result));
                }, TaskScheduler.Default);
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("TryOcrHoverFallbackAsync()", ex);
            if (token == _hoverToken)
                _popup!.ShowAt(cursorX, cursorY, "⚠ Lỗi OCR", $"({ex.Message})", "", "vi", "vi");
        }
    }

    private static System.Drawing.Rectangle ClampRegionToVirtualScreen(int cursorX, int cursorY, System.Drawing.Size size)
    {
        int minLeft = (int)SystemParameters.VirtualScreenLeft;
        int minTop = (int)SystemParameters.VirtualScreenTop;
        int maxLeft = minLeft + (int)SystemParameters.VirtualScreenWidth - size.Width;
        int maxTop = minTop + (int)SystemParameters.VirtualScreenHeight - size.Height;

        int left = Math.Clamp(cursorX - size.Width / 2, minLeft, Math.Max(minLeft, maxLeft));
        int top = Math.Clamp(cursorY - size.Height / 2, minTop, Math.Max(minTop, maxTop));
        return new System.Drawing.Rectangle(left, top, size.Width, size.Height);
    }

    // Spec mục 9, 26: Alt+Q -> overlay chọn vùng -> capture -> OCR -> dịch.
    // ShowDialog() ở đây chặn Tick() trong lúc người dùng đang kéo chọn vùng -
    // đúng ý (một hành động rời rạc, không phải cái cần chạy mỗi tick), không
    // giống DragMove của popup vốn cũng làm tương tự cho việc kéo popup.
    private static async void TriggerOcrCapture()
    {
        const string ocrLabel = "📷 OCR";

        // Định vị popup theo vị trí con trỏ chuột HIỆN TẠI (giống hệt cách
        // popup hover vẫn đang hoạt động ổn định) thay vì toạ độ vùng chọn -
        // vùng chọn có thể ở góc màn hình xa, dễ đẩy popup ra ngoài vùng nhìn
        // thấy được (SystemParameters.WorkArea chỉ tính màn hình chính).
        NativeMethods.GetCursorPos(out var cursorPos);

        try
        {
            var overlay = new ScreenCaptureOverlay();
            overlay.ShowDialog();
            var region = overlay.SelectedRegion;
            if (region is null) return;

            string? text;
            using (var raw = ScreenCapture.Capture(region.Value))
            using (var bitmap = ImagePreprocessor.Upscale(raw))
            {
                // Tesseract (eng+vie) trước - Windows OCR không hỗ trợ tiếng Việt
                // trên máy này dù đã cài gói ngôn ngữ (đã tự kiểm chứng qua
                // AvailableRecognizerLanguages). Windows OCR làm dự phòng.
                text = await TesseractOcrEngine.RecognizeAsync(bitmap) ?? await OcrEngine.RecognizeAsync(bitmap);
            }
            // bitmap đã Dispose ở đây - không lưu screenshot (spec mục 17).

            if (string.IsNullOrWhiteSpace(text))
            {
                _popup!.ShowAt(cursorPos.X, cursorPos.Y, ocrLabel,
                    "(không nhận diện được chữ trong vùng đã chọn)", "", "en", AppSettings.TargetLanguage);
                _lastShownWord = ocrLabel;
                // OCR là hành động rời rạc (Alt+Q), không gắn với việc giữ
                // Alt liên tục - Alt gần như chắc chắn đã được thả ra từ lúc
                // kéo chọn vùng bằng chuột, trước khi OCR/dịch chạy xong. Ghim
                // sẵn để Tick() không tự ẩn mất kết quả theo logic của hover.
                _popup.SetPinned(true);
                return;
            }

            string sourceLang = LanguageDetector.Detect(text);
            string targetLang = sourceLang == AppSettings.TargetLanguage
                ? LanguageDetector.DefaultTargetFor(sourceLang)
                : AppSettings.TargetLanguage;

            _popup!.ShowAt(cursorPos.X, cursorPos.Y, ocrLabel,
                "Văn bản nhận diện qua OCR — xem bản dịch bên dưới", text, sourceLang, targetLang);
            _lastShownWord = ocrLabel;
            _popup.SetPinned(true);

            string? translated = await TranslateWithFallbackAsync(text, sourceLang, targetLang);
            _popup.SetSentenceTranslation(ocrLabel, translated);
        }
        catch (Exception ex)
        {
            // Không được thất bại trong im lặng - người dùng cần biết đã có
            // lỗi thay vì tưởng app bị treo/không phản hồi.
            CrashLog.Write("TriggerOcrCapture()", ex);
            _popup!.ShowAt(cursorPos.X, cursorPos.Y, ocrLabel, $"(lỗi OCR: {ex.Message})", "", "en", AppSettings.TargetLanguage);
            _lastShownWord = ocrLabel;
            _popup.SetPinned(true);
        }
    }

    private static string? FindTranslateServerScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "scripts", "translate_server.py");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    // GetAsyncKeyState trả 2 tín hiệu: bit cao (0x8000) = "đang nhấn NGAY LÚC
    // NÀY", bit thấp (0x0001) = "đã nhấn xuống ít nhất 1 lần kể từ lần gọi
    // trước đó (trên cùng thread)". Tick() chỉ poll mỗi 50ms - nếu chỉ xét bit
    // cao, 1 cú bấm-thả rất nhanh (rơi lọt giữa 2 lần poll) sẽ bị bỏ sót hoàn
    // toàn. Đây chính là lý do thực tế đã gặp: "Alt+Q lúc bấm ra, lúc không".
    // Dùng cho các phím CHỮ (bấm nhanh, không cần giữ) - Alt/Shift vẫn chỉ
    // xét bit cao vì 2 phím đó cần được GIỮ xuyên suốt cả thao tác.
    private static bool WasKeyDownRecently(int vKey) => (NativeMethods.GetAsyncKeyState(vKey) & 0x8001) != 0;

    private static void Tick()
    {
        // Người dùng đang kéo popup bằng chuột - không để logic hover phía dưới
        // (đổi từ, ẩn khi thả Alt) tranh chấp với thao tác kéo.
        if (_popup!.IsDragging) return;

        bool altHeld = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0;
        bool shiftHeld = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0;
        bool sHeld = WasKeyDownRecently(NativeMethods.VK_S);
        bool settingsHotkeyDown = altHeld && shiftHeld && sHeld;
        if (settingsHotkeyDown && !_settingsHotkeyWasDown)
        {
            // Cập nhật cờ TRƯỚC khi gọi hành động (giống 3 hotkey bên dưới) -
            // nếu ShowAndActivate() ném lỗi, cờ vẫn đã đổi nên Tick() sau
            // không lặp lại gọi hỏng liên tục mỗi 50ms trong lúc giữ phím
            // (bug thật đã gặp: SettingsWindow đóng qua nút X thật sự đóng
            // hẳn cửa sổ, gọi lại Show() ném InvalidOperationException, và vì
            // cờ chưa kịp đổi nên lặp lại ~20 lần/giây cho tới khi app sập).
            _settingsHotkeyWasDown = settingsHotkeyDown;
            _settingsWindow!.ShowAndActivate();
            return;
        }
        _settingsHotkeyWasDown = settingsHotkeyDown;

        // Alt+Q (spec mục 9): screen overlay -> chọn vùng -> OCR -> dịch.
        bool qHeld = WasKeyDownRecently(NativeMethods.VK_Q);
        bool ocrHotkeyDown = altHeld && qHeld && !shiftHeld;
        if (ocrHotkeyDown && !_ocrHotkeyWasDown)
        {
            if (_debugMode) Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ALT+Q TRIGGERED");
            _ocrHotkeyWasDown = ocrHotkeyDown;
            TriggerOcrCapture();
            return;
        }
        _ocrHotkeyWasDown = ocrHotkeyDown;

        // Alt+T: khung "Dịch nhanh" (gõ/dán chữ, không cần hover/OCR).
        bool tHeld = WasKeyDownRecently(NativeMethods.VK_T);
        bool quickTranslateHotkeyDown = altHeld && tHeld && !shiftHeld;
        if (quickTranslateHotkeyDown && !_quickTranslateHotkeyWasDown)
        {
            _quickTranslateHotkeyWasDown = quickTranslateHotkeyDown;
            _quickTranslateWindow!.ShowAndFocus();
            return;
        }
        _quickTranslateHotkeyWasDown = quickTranslateHotkeyDown;

        // Alt+L: panel Live Translate (nghe audio hệ thống, dịch gần realtime).
        bool lHeld = WasKeyDownRecently(NativeMethods.VK_L);
        bool liveTranslateHotkeyDown = altHeld && lHeld && !shiftHeld;
        if (liveTranslateHotkeyDown && !_liveTranslateHotkeyWasDown)
        {
            _liveTranslateHotkeyWasDown = liveTranslateHotkeyDown;
            _liveTranslateWindow!.ShowAndFocus();
            return;
        }
        _liveTranslateHotkeyWasDown = liveTranslateHotkeyDown;

        NativeMethods.GetCursorPos(out var pos);

        // Con trỏ đang ở trên chính popup (đang di chuyển tới để bấm nút, đọc
        // chữ...) - không được coi đây là "đang hover chữ mới" (sẽ đọc nhầm
        // chữ tiếng Việt của chính popup) hay để nó tự ẩn giữa chừng.
        if (_popup!.ContainsScreenPoint(pos.X, pos.Y))
        {
            if (_popupHoverSince == DateTime.MinValue) _popupHoverSince = DateTime.UtcNow;
            double overPopupMs = (DateTime.UtcNow - _popupHoverSince).TotalMilliseconds;

            // Popup đứng yên 1 chỗ nhưng người dùng thì đang rê chuột đi khắp
            // nơi để đọc các chữ khác - nếu popup (khá to) tình cờ nằm che
            // đúng chỗ chữ tiếp theo, người dùng tưởng "hover không ăn" trong
            // khi thực ra đang hover lên chính popup. Đứng yên trên popup quá
            // lâu mà không tương tác (không ghim) -> tự ẩn để lộ chữ bên dưới.
            if (overPopupMs > 1200 && !_popup.IsPinned)
            {
                if (_debugMode) Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] AUTO-HIDE: đứng yên trên popup >1.2s không tương tác, ẩn để lộ chữ bên dưới");
                _popup.HidePopup();
                _lastShownWord = null;
                _popupHoverSince = DateTime.MinValue;
                _stableSince = DateTime.MinValue;
            }
            else if (_debugMode)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] BLOCKED: con trỏ ({pos.X},{pos.Y}) đang nằm trên popup ({_popup.Left},{_popup.Top} - {_popup.ActualWidth}x{_popup.ActualHeight})");
            }
            return;
        }
        _popupHoverSince = DateTime.MinValue;

        if (!altHeld)
        {
            if (_altReleasedSince == DateTime.MinValue) _altReleasedSince = DateTime.UtcNow;
            bool graceExpired = (DateTime.UtcNow - _altReleasedSince).TotalMilliseconds >= HideGraceMs;

            // Đã bấm "Explain deeper"/ghim thì không tự ẩn theo việc thả Alt nữa
            // (spec mục 20: Deep Explain được phép chậm). Nếu chưa ghim, vẫn chờ
            // hết grace period (đủ thời gian di chuột từ chữ sang popup) trước
            // khi ẩn, thay vì ẩn ngay lập tức lúc vừa thả Alt.
            if (graceExpired && _lastShownWord is not null && !_popup!.IsPinned)
            {
                if (_debugMode) Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] HIDE: hết grace period sau khi thả Alt");
                _popup.HidePopup();
                _lastShownWord = null;
            }
            _stableSince = DateTime.MinValue;
            return;
        }
        _altReleasedSince = DateTime.MinValue;

        int dx = pos.X - _stablePoint.X;
        int dy = pos.Y - _stablePoint.Y;
        if (_stableSince == DateTime.MinValue || dx * dx + dy * dy > MoveThresholdPx * MoveThresholdPx)
        {
            _stablePoint = pos;
            _stableSince = DateTime.UtcNow;
            _processedThisStablePoint = false;
            _failedAttemptsThisStablePoint = 0;
            _hoverToken++;
            return;
        }

        bool hoveredLongEnough = (DateTime.UtcNow - _stableSince).TotalMilliseconds >= HoverDelayMs;

        if (hoveredLongEnough && !_processedThisStablePoint)
        {
            var result = WordLookup.AtPoint(new WpfPoint(pos.X, pos.Y));
            if (_debugMode) Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] LOOKUP tại ({pos.X},{pos.Y}): supported={result.Supported} word='{result.Word}'");

            if (result.Supported && result.Word.Length > 0)
            {
                // Chỉ khóa lại (khỏi dò UI Automation lại mỗi tick) khi đã TÌM
                // THẤY chữ. Nếu thất bại, cứ để tick sau thử lại - tay rung
                // nhẹ vài pixel trong lúc "đứng yên" đôi khi khiến điểm dò rơi
                // đúng khe hở giữa 2 ký tự; thử lại ở pixel hơi khác thường sẽ
                // trúng. Đây chính là nguyên nhân "lúc được lúc không".
                _processedThisStablePoint = true;
                if (!string.Equals(result.Word, _lastShownWord, StringComparison.OrdinalIgnoreCase))
                {
                    string detectLangFrom = string.IsNullOrWhiteSpace(result.Sentence) ? result.Word : result.Sentence;
                    string sourceLang = LanguageDetector.Detect(detectLangFrom);
                    // Nguồn trùng đích đã chọn (vd: đang để đích "Tiếng Việt" mà hover
                    // đúng chữ tiếng Việt) thì dịch sang đích thay thế hợp lý, tránh dịch
                    // một ngôn ngữ ra chính nó.
                    string targetLang = sourceLang == AppSettings.TargetLanguage
                        ? LanguageDetector.DefaultTargetFor(sourceLang)
                        : AppSettings.TargetLanguage;

                    // Từ điển ~70 từ demo cho kết quả tức thì với từ tiếng Anh
                    // thông dụng; mọi trường hợp khác (từ hiếm, mọi chiều dịch
                    // khác EN→VI) chờ engine dịch thật trả lời bên dưới.
                    string? instantMeaning = sourceLang == "en" && targetLang == "vi"
                        ? StubTranslator.TryTranslate(result.Word)
                        : null;
                    string meaning = instantMeaning ?? "Đang dịch...";

                    _popup!.ShowAt(pos.X, pos.Y, result.Word, meaning, result.Sentence, sourceLang, targetLang);
                    _lastShownWord = result.Word;

                    string wordForThisRequest = result.Word;

                    // Luôn dịch thật cho riêng từ/cụm đang hover - từ điển demo
                    // chỉ là tăng tốc hiển thị, không phải nguồn nghĩa duy nhất.
                    _ = TranslateWithFallbackAsync(result.Word, sourceLang, targetLang).ContinueWith(t =>
                    {
                        _popup.Dispatcher.Invoke(() => _popup.SetWordMeaning(wordForThisRequest, t.Result));
                    }, TaskScheduler.Default);

                    if (!string.IsNullOrWhiteSpace(result.Sentence))
                    {
                        string sentence = result.Sentence;
                        _ = TranslateWithFallbackAsync(sentence, sourceLang, targetLang).ContinueWith(t =>
                        {
                            _popup.Dispatcher.Invoke(() => _popup.SetSentenceTranslation(wordForThisRequest, t.Result));
                        }, TaskScheduler.Default);
                    }
                }
            }
            else if (++_failedAttemptsThisStablePoint >= MaxRetriesPerStablePoint)
            {
                // Ứng dụng này không expose text ở điểm này qua Accessibility
                // (vd Zalo) - dừng dò UI Automation liên tục, thay vào đó tự
                // động OCR 1 vùng nhỏ quanh con trỏ (spec mục 27 - Intelligent
                // Fallback) để vẫn giữ được trải nghiệm "hover là dịch" thay vì
                // chỉ báo lỗi và bắt người dùng tự bấm Alt+Q mỗi lần.
                if (_debugMode) Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] GIVE UP UIA: {MaxRetriesPerStablePoint} lần liên tiếp không thấy chữ, thử OCR quanh con trỏ");
                _processedThisStablePoint = true;
                _ = TryOcrHoverFallbackAsync(pos.X, pos.Y, _hoverToken);
            }
            // Rê ngang qua chỗ không có chữ (khoảng trống giữa chữ gốc và
            // popup, thanh taskbar, v.v.) trong lúc Alt vẫn giữ KHÔNG được ẩn
            // popup - chỉ thả Alt (hết grace period, xem nhánh !altHeld phía
            // trên) hoặc hover trúng một từ khác mới thay đổi popup.
        }
    }
}
