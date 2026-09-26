using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using CheckBox = System.Windows.Controls.CheckBox;
using Orientation = System.Windows.Controls.Orientation;

namespace HoverTranslate.App;

// Panel điều khiển Live Audio Translate - theo mẫu SettingsWindow.cs (cửa sổ
// bình thường, focusable, ScrollViewer phòng khi nội dung dài hơn màn hình).
internal sealed class LiveTranslateWindow : Window
{
    private readonly LiveAudioService _service = new();
    private readonly SubtitleOverlay _overlay = new();

    private readonly ComboBox _audioSourceCombo;
    private readonly ComboBox _sourceLangCombo;
    private readonly ComboBox _targetLangCombo;
    private readonly ComboBox _modelCombo;
    private readonly CheckBox _subtitleCheck;
    private readonly CheckBox _speakCheck;
    private readonly Button _startButton;
    private readonly Button _pauseButton;
    private readonly Button _stopButton;
    private readonly TextBlock _statusBlock;
    private readonly StackPanel _historyPanel;
    private readonly ScrollViewer _historyScroll;

    private bool _isRunning;
    private bool _isPaused;

    public LiveTranslateWindow()
    {
        Title = "Hover Translate — Live Translate";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(24, 24, 27));

        var root = new StackPanel { Margin = new Thickness(20) };

        root.Children.Add(new TextBlock
        {
            Text = "Live Translate",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 4),
        });
        root.Children.Add(new TextBlock
        {
            Text = "Nghe âm thanh hệ thống (YouTube, video, họp...) và dịch gần realtime. Máy RAM thấp nên mặc định dùng model Tiny.",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 168)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        root.Children.Add(Label("Audio Source"));
        _audioSourceCombo = new ComboBox { Margin = new Thickness(0, 2, 0, 10) };
        _audioSourceCombo.Items.Add(new ComboBoxItem { Content = "System Audio", Tag = AudioSourceKind.SystemAudio });
        _audioSourceCombo.Items.Add(new ComboBoxItem { Content = "Microphone", Tag = AudioSourceKind.Microphone });
        _audioSourceCombo.SelectedIndex = 0;
        root.Children.Add(_audioSourceCombo);

        root.Children.Add(Label("Từ (ngôn ngữ đang nói)"));
        _sourceLangCombo = new ComboBox { Margin = new Thickness(0, 2, 0, 2) };
        _sourceLangCombo.Items.Add(new ComboBoxItem { Content = "Tự động nhận diện", Tag = "auto" });
        foreach (var (code, label) in AppSettings.SupportedTargetLanguages) _sourceLangCombo.Items.Add(new ComboBoxItem { Content = label, Tag = code });
        _sourceLangCombo.SelectedIndex = 1 + Array.FindIndex(AppSettings.SupportedTargetLanguages, x => x.Code == "en");
        // Chỉ là 1 chuỗi hint gửi kèm mỗi đoạn ghi âm - không cần restart cả
        // phiên STT để đổi, nên cho đổi được ngay cả khi đang Listening (khác
        // Model, đổi Model bắt buộc phải load lại model nên vẫn phải khoá).
        _sourceLangCombo.SelectionChanged += (_, _) =>
        {
            if (_sourceLangCombo.SelectedItem is ComboBoxItem item)
                _service.SourceLanguageHint = item.Tag as string ?? "auto";
        };
        root.Children.Add(_sourceLangCombo);
        root.Children.Add(new TextBlock
        {
            Text = "Chọn đúng ngôn ngữ nguồn giúp nghe/dịch chính xác hơn hẳn so với Tự động, nhất là với câu ngắn. Chọn Tiếng Việt sẽ tự dùng PhoWhisper (luyện riêng cho tiếng Việt, chính xác hơn Whisper gốc) - đổi lúc đang Listening cần bấm Stop rồi Start lại mới áp dụng, vì phải load lại model.",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 148)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 10),
        });

        root.Children.Add(Label("To (ngôn ngữ đích)"));
        _targetLangCombo = new ComboBox { Margin = new Thickness(0, 2, 0, 10) };
        foreach (var (code, label) in AppSettings.SupportedTargetLanguages) _targetLangCombo.Items.Add(new ComboBoxItem { Content = label, Tag = code });
        _targetLangCombo.SelectedIndex = Array.FindIndex(AppSettings.SupportedTargetLanguages, x => x.Code == AppSettings.TargetLanguage);
        if (_targetLangCombo.SelectedIndex < 0) _targetLangCombo.SelectedIndex = 0;
        _targetLangCombo.SelectionChanged += (_, _) =>
        {
            if (_targetLangCombo.SelectedItem is ComboBoxItem item)
                _service.TargetLanguage = item.Tag as string ?? "vi";
        };
        root.Children.Add(_targetLangCombo);

        root.Children.Add(Label("Output"));
        _subtitleCheck = new CheckBox { Content = "Subtitle overlay", IsChecked = true, Foreground = Brushes.White, Margin = new Thickness(0, 2, 0, 2) };
        _speakCheck = new CheckBox { Content = "🔊 Speak Translation (sắp có)", IsChecked = false, IsEnabled = false, Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 128)) };
        root.Children.Add(_subtitleCheck);
        root.Children.Add(_speakCheck);

        root.Children.Add(Label("Model"));
        _modelCombo = new ComboBox { Margin = new Thickness(0, 2, 0, 10) };
        foreach (var m in new[] { "tiny", "base", "small", "medium" }) _modelCombo.Items.Add(m);
        _modelCombo.SelectedIndex = 0; // tiny mặc định - máy RAM thấp đã xác nhận base có thể lỗi cấp phát bộ nhớ
        root.Children.Add(_modelCombo);

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        _startButton = new Button { Content = "▶ START LIVE TRANSLATE", Padding = new Thickness(12, 6, 12, 6) };
        _pauseButton = new Button { Content = "⏸ Pause", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(6, 0, 0, 0), IsEnabled = false };
        _stopButton = new Button { Content = "■ Stop", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(6, 0, 0, 0), IsEnabled = false };
        buttonRow.Children.Add(_startButton);
        buttonRow.Children.Add(_pauseButton);
        buttonRow.Children.Add(_stopButton);
        root.Children.Add(buttonRow);

        var clearButton = new Button { Content = "Clear", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        clearButton.Click += (_, _) => _historyPanel.Children.Clear();
        root.Children.Add(clearButton);

        root.Children.Add(Label("Trạng thái", topMargin: 16));
        _statusBlock = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(150, 200, 255)), FontSize = 12, Margin = new Thickness(0, 2, 0, 8) };
        root.Children.Add(_statusBlock);

        root.Children.Add(Label("Lịch sử (cuộn để xem lại đoạn cũ)"));
        _historyPanel = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
        _historyScroll = new ScrollViewer
        {
            Height = 260,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = new SolidColorBrush(Color.FromRgb(18, 18, 20)),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 4, 0, 0),
            Content = _historyPanel,
        };
        root.Children.Add(_historyScroll);

        var closeButton = new Button { Content = "Đóng", Margin = new Thickness(0, 16, 0, 0), Padding = new Thickness(16, 6, 16, 6), HorizontalAlignment = HorizontalAlignment.Right };
        closeButton.Click += (_, _) => Hide();
        root.Children.Add(closeButton);

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = SystemParameters.WorkArea.Height - 80,
            Content = root,
        };

        _startButton.Click += async (_, _) => await OnStartAsync();
        _pauseButton.Click += (_, _) => OnPauseToggle();
        _stopButton.Click += (_, _) => OnStop();

        _service.StatusChanged += status => Dispatcher.Invoke(() =>
        {
            _statusBlock.Text = status;
            if (_subtitleCheck.IsChecked == true) _overlay.ShowStatus(status);
        });
        _service.SegmentReady += (original, translated, srcLang, tgtLang, latencySec) => Dispatcher.Invoke(() =>
        {
            AddHistoryEntry(original, translated, srcLang, latencySec);
            if (_subtitleCheck.IsChecked == true) _overlay.ShowSegment(original, translated);
        });

        Closing += (_, e) =>
        {
            // Đóng cửa sổ = Ẩn (giữ tiến trình dịch chạy nếu đang bật), không
            // phải thoát app - người dùng có thể mở lại để xem/điều khiển.
            if (_isRunning) { e.Cancel = true; Hide(); }
        };
    }

    // Mỗi đoạn nghe được = 1 mục lịch sử được NỐI THÊM vào _historyPanel (không
    // ghi đè mục trước) - người dùng cuộn lên trong _historyScroll để xem lại
    // các đoạn cũ, thanh cuộn hiện Auto. Tự cuộn xuống cuối mỗi khi có đoạn mới
    // để mặc định vẫn thấy câu mới nhất, nhưng không khoá người dùng lại đó.
    private void AddHistoryEntry(string original, string translated, string srcLang, double latencySec)
    {
        var entry = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        entry.Children.Add(new TextBlock
        {
            Text = $"[{srcLang.ToUpperInvariant()}] {original}",
            Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 158)),
            FontSize = 12,
            FontStyle = FontStyles.Italic,
            TextWrapping = TextWrapping.Wrap,
        });
        entry.Children.Add(new TextBlock
        {
            Text = translated,
            Foreground = new SolidColorBrush(Color.FromRgb(150, 230, 170)),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });
        entry.Children.Add(new TextBlock
        {
            Text = $"{DateTime.Now:HH:mm:ss} · {latencySec:F1}s",
            Foreground = new SolidColorBrush(Color.FromRgb(90, 90, 98)),
            FontSize = 10,
            Margin = new Thickness(0, 2, 0, 0),
        });
        entry.Children.Add(new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(40, 40, 44)), BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 8, 0, 0) });

        _historyPanel.Children.Add(entry);
        _historyScroll.ScrollToEnd();
    }

    private static TextBlock Label(string text, double topMargin = 0) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeights.Bold,
        Foreground = new SolidColorBrush(Color.FromRgb(120, 200, 255)),
        Margin = new Thickness(0, topMargin, 0, 0),
    };

    public void ShowAndFocus()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private async Task OnStartAsync()
    {
        _startButton.IsEnabled = false;
        string sourceHint = ((ComboBoxItem)_sourceLangCombo.SelectedItem).Tag as string ?? "auto";
        string targetCode = ((ComboBoxItem)_targetLangCombo.SelectedItem).Tag as string ?? "vi";
        _service.SourceLanguageHint = sourceHint;
        _service.TargetLanguage = targetCode;
        _service.ModelSize = (string)_modelCombo.SelectedItem;
        _service.AudioSource = ((ComboBoxItem)_audioSourceCombo.SelectedItem).Tag is AudioSourceKind src ? src : AudioSourceKind.SystemAudio;

        bool ok = await _service.StartAsync();
        if (!ok)
        {
            _startButton.IsEnabled = true;
            return;
        }

        _isRunning = true;
        _isPaused = false;
        _pauseButton.IsEnabled = true;
        _stopButton.IsEnabled = true;
        // Model và Audio Source bắt buộc khoá (đổi = phải dừng capture/load
        // lại STT), nhưng Từ/To chỉ là 1 chuỗi gửi kèm mỗi đoạn - đổi được
        // ngay cả khi đang Listening, không cần Stop/Start lại (người dùng đã
        // báo bị vướng chỗ này khi phát hiện chọn sai ngôn ngữ giữa chừng lúc
        // đang nghe).
        _modelCombo.IsEnabled = false;
        _audioSourceCombo.IsEnabled = false;
    }

    private void OnPauseToggle()
    {
        if (!_isRunning) return;
        _isPaused = !_isPaused;
        if (_isPaused) { _service.Pause(); _pauseButton.Content = "▶ Resume"; }
        else { _service.Resume(); _pauseButton.Content = "⏸ Pause"; }
    }

    private void OnStop()
    {
        _service.Stop();
        _overlay.HidePopup();
        _isRunning = false;
        _isPaused = false;
        _startButton.IsEnabled = true;
        _pauseButton.IsEnabled = false;
        _pauseButton.Content = "⏸ Pause";
        _stopButton.IsEnabled = false;
        _modelCombo.IsEnabled = true;
        _audioSourceCombo.IsEnabled = true;
        _sourceLangCombo.IsEnabled = true;
        _targetLangCombo.IsEnabled = true;
    }
}
