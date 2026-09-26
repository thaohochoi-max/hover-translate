using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using TextBox = System.Windows.Controls.TextBox;
using Cursors = System.Windows.Input.Cursors;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;

namespace HoverTranslate.App;

// Spec-adjacent (không có trong spec gốc, người dùng yêu cầu thêm): khung
// dịch kiểu extension Google Dịch - gõ hoặc dán chữ vào, tự động dịch, không
// cần hover/OCR. Khác PopupWindow: cửa sổ này CẦN focus được (để gõ chữ), nên
// không dùng WS_EX_NOACTIVATE và không gắn với vòng đời Alt-hover của Tick().
internal sealed class QuickTranslateWindow : Window
{
    private readonly Border _border;
    private readonly TextBox _inputBox;
    private readonly TextBlock _statusBlock;
    private readonly TextBlock _suggestionLink;
    private readonly TextBlock _resultBlock;
    private readonly TextBlock _dictionaryBlock;
    private readonly Button _copyButton;
    private readonly DispatcherTimer _debounceTimer;
    private string _lastTranslatedText = "";

    public QuickTranslateWindow()
    {
        Title = "Hover Translate — Dịch nhanh";
        Width = 400;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(240, 32, 32, 36)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10, 14, 10),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.45 },
        };

        var stack = new StackPanel();

        var topRow = new Grid { Cursor = Cursors.SizeAll };
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topRow.MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { /* nút chuột đã thả trước khi kịp capture */ } };
        var titleBlock = new TextBlock { Text = "🔤 Dịch nhanh", FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Brushes.White };
        Grid.SetColumn(titleBlock, 0);
        topRow.Children.Add(titleBlock);
        var closeButton = new TextBlock { Text = "✕", FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 148)), Cursor = Cursors.Hand };
        closeButton.MouseLeftButtonDown += (_, e) => { e.Handled = true; Hide(); };
        Grid.SetColumn(closeButton, 1);
        topRow.Children.Add(closeButton);
        stack.Children.Add(topRow);

        stack.Children.Add(new TextBlock
        {
            Text = "Gõ hoặc dán chữ vào đây — tự động dịch.",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 158)),
            Margin = new Thickness(0, 6, 0, 6),
        });

        _inputBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 90,
            FontSize = 14,
            Background = new SolidColorBrush(Color.FromRgb(24, 24, 27)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 66)),
            Padding = new Thickness(8),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _inputBox.TextChanged += (_, _) => { _debounceTimer.Stop(); _debounceTimer.Start(); };
        stack.Children.Add(_inputBox);

        // Gõ sai chính tả ("transcrip") vẫn dịch ngay được (Google tự sửa lỗi
        // ngầm), gợi ý sửa chỉ để tham khảo - bấm vào để áp dụng, không bắt
        // buộc phải sửa trước mới dịch được.
        _suggestionLink = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(120, 200, 255)),
            Cursor = Cursors.Hand,
            TextDecorations = System.Windows.TextDecorations.Underline,
            Margin = new Thickness(0, 4, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        _suggestionLink.MouseLeftButtonDown += async (_, _) =>
        {
            if (_suggestionLink.Tag is not string corrected) return;
            _inputBox.Text = corrected;
            _inputBox.CaretIndex = corrected.Length;
            _debounceTimer.Stop();
            await TranslateCurrentTextAsync();
        };
        stack.Children.Add(_suggestionLink);

        var statusRow = new Grid { Margin = new Thickness(0, 8, 0, 2) };
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _statusBlock = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 158)),
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
        };
        Grid.SetColumn(_statusBlock, 0);
        statusRow.Children.Add(_statusBlock);
        _copyButton = new Button
        {
            Content = "📋 Copy",
            FontSize = 11,
            Padding = new Thickness(8, 2, 8, 2),
            Background = new SolidColorBrush(Color.FromRgb(48, 48, 54)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Visibility = Visibility.Collapsed,
        };
        _copyButton.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(StripEnginePrefix(_resultBlock.Text));
                _copyButton.Content = "✓ Đã copy";
            }
            catch { /* clipboard đang bị chiếm bởi tiến trình khác - không quan trọng bằng việc không crash */ }
        };
        Grid.SetColumn(_copyButton, 1);
        statusRow.Children.Add(_copyButton);
        stack.Children.Add(statusRow);

        _resultBlock = new TextBlock
        {
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(150, 230, 170)),
            TextWrapping = TextWrapping.Wrap,
        };
        var resultScroll = new ScrollViewer
        {
            MaxHeight = 200,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _resultBlock,
        };
        stack.Children.Add(resultScroll);

        // Chỉ có dữ liệu khi dịch 1 từ đơn (Google không trả từ điển cho câu
        // dài) - liệt kê các nghĩa khác theo loại từ, tránh chỉ tin 1 nghĩa
        // NMT chọn có thể lệch ngữ cảnh (vd "transcript" = bản ghi chép/bảng
        // điểm...).
        _dictionaryBlock = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 158)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        stack.Children.Add(_dictionaryBlock);

        _border.Child = stack;
        Content = _border;

        // Debounce: dịch 500ms sau khi người dùng ngừng gõ, không dịch từng
        // ký tự một (tốn tài nguyên, gây giật khi gõ nhanh).
        _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _debounceTimer.Tick += async (_, _) =>
        {
            _debounceTimer.Stop();
            await TranslateCurrentTextAsync();
        };

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
    }

    public void ShowAndFocus()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        _inputBox.Focus();
        Keyboard.Focus(_inputBox);
    }

    private async Task TranslateCurrentTextAsync()
    {
        string text = _inputBox.Text.Trim();
        if (text.Length == 0)
        {
            _statusBlock.Text = "";
            _resultBlock.Text = "";
            _copyButton.Visibility = Visibility.Collapsed;
            _suggestionLink.Visibility = Visibility.Collapsed;
            return;
        }
        if (text == _lastTranslatedText) return;
        _lastTranslatedText = text;

        string sourceLang = LanguageDetector.Detect(text);
        string targetLang = sourceLang == AppSettings.TargetLanguage
            ? LanguageDetector.DefaultTargetFor(sourceLang)
            : AppSettings.TargetLanguage;

        _statusBlock.Text = $"{LabelFor(sourceLang)} → {LabelFor(targetLang)} · Đang dịch...";
        _copyButton.Visibility = Visibility.Collapsed;
        _suggestionLink.Visibility = Visibility.Collapsed;
        _dictionaryBlock.Visibility = Visibility.Collapsed;

        // Gọi thẳng Google (đang là engine online chính) để lấy gợi ý sửa
        // chính tả + từ điển đa nghĩa trong CÙNG 1 lần gọi mạng - Google tự
        // dịch đúng ngay cả khi gõ sai (vd "transcrip" -> vẫn ra nghĩa của
        // "transcript"), gợi ý/từ điển chỉ để tham khảo, không chặn việc dịch
        // ngay. Lỗi mạng/bị chặn tạm thì rơi về pipeline đầy đủ cũ
        // (offline/MyMemory) như trước, không có gợi ý/từ điển ở lần đó.
        string? translated;
        string? suggestion;
        IReadOnlyList<DictionaryEntry> dictionary;
        (translated, suggestion, dictionary) = await GoogleTranslateOnline.TranslateWithDetailsAsync(text, sourceLang, targetLang);
        string enginePrefix = "[online] ";
        if (translated is null)
        {
            translated = await Program.TranslatePublicAsync(text, sourceLang, targetLang);
            enginePrefix = "";
            suggestion = null;
            dictionary = Array.Empty<DictionaryEntry>();
        }

        // Người dùng gõ tiếp trong lúc chờ - kết quả này đã cũ, bỏ qua.
        if (text != _lastTranslatedText) return;

        _statusBlock.Text = $"{LabelFor(sourceLang)} → {LabelFor(targetLang)}";
        _resultBlock.Text = translated is null ? "(không dịch được — kiểm tra mạng/model offline)" : enginePrefix + translated;
        _copyButton.Content = "📋 Copy";
        _copyButton.Visibility = translated is null ? Visibility.Collapsed : Visibility.Visible;

        if (!string.IsNullOrWhiteSpace(suggestion))
        {
            _suggestionLink.Text = $"✨ Có phải ý bạn là: {suggestion}?";
            _suggestionLink.Tag = suggestion;
            _suggestionLink.Visibility = Visibility.Visible;
        }

        if (dictionary.Count > 0)
        {
            var lines = dictionary.Select(e => $"{e.PartOfSpeech}: {string.Join(", ", e.Meanings.Take(5))}");
            _dictionaryBlock.Text = "Nghĩa khác: " + string.Join("  •  ", lines);
            _dictionaryBlock.Visibility = Visibility.Visible;
        }
    }

    // Bỏ nhãn nguồn dịch ("[online] "/"[offline] "/"[cache] ") - đó là thông
    // tin nội bộ để debug, người dùng copy đi dán vào chat không cần thấy nó.
    private static string StripEnginePrefix(string text)
    {
        foreach (var prefix in new[] { "[online] ", "[offline] ", "[cache] " })
            if (text.StartsWith(prefix)) return text[prefix.Length..];
        return text;
    }

    private static string LabelFor(string code) => code switch
    {
        "vi" => "Tiếng Việt",
        "en" => "English",
        "zh" => "中文",
        "ja" => "日本語",
        "ko" => "한국어",
        "ru" => "Русский",
        "hi" => "हिन्दी",
        "th" => "ภาษาไทย",
        "id" => "Bahasa Indonesia",
        "ms" => "Bahasa Melayu",
        "tl" => "Filipino",
        "fr" => "Français",
        "de" => "Deutsch",
        "es" => "Español",
        "pt" => "Português",
        "ar" => "العربية",
        _ => code,
    };
}
