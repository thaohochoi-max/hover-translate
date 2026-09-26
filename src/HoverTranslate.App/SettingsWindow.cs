using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RadioButton = System.Windows.Controls.RadioButton;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace HoverTranslate.App;

// Regular top-level window (Alt+Shift+S to open) - the tray-menu-free way to
// change settings, for setups where the system tray isn't reachable (e.g. a
// fullscreen/maximized host app covering the whole screen and taskbar).
internal sealed class SettingsWindow : Window
{
    private readonly List<(string Code, RadioButton Button)> _targetLangButtons = new();
    private readonly List<(TranslationMode Mode, RadioButton Button)> _modeButtons = new();

    private sealed class ModelRow
    {
        public required TextBlock NameBlock;
        public required TextBlock StatusBlock;
        public required Button InstallButton;
    }

    private ModelRow _translationRow = null!;
    private ModelRow _explanationRow = null!;
    private ModelRow _ocrRow = null!;
    private ModelRow _whisperRow = null!;

    public SettingsWindow()
    {
        Title = "Hover Translate — Cài đặt";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(24, 24, 27));

        var root = new StackPanel { Margin = new Thickness(20) };

        // Cửa sổ dùng chrome mặc định của Windows (có nút X thật) - nút đó
        // theo default WPF sẽ ĐÓNG HẲN window object, không phải chỉ ẩn. Gọi
        // lại Show() trên 1 window đã Close() ném InvalidOperationException
        // (bug thật đã gặp: đóng Settings 1 lần rồi giữ Alt+Shift+S là app
        // crash liên tục). Chặn Closing, Hide() thay vì để đóng thật, đúng
        // pattern LiveTranslateWindow/QuickTranslateWindow đã dùng.
        Closing += (_, e) => { e.Cancel = true; Hide(); };

        root.Children.Add(Header("Hover Translate — Cài đặt"));
        root.Children.Add(Hint("Giữ Alt + rê chuột vào chữ để dịch. Alt+Shift+S mở lại cửa sổ này bất cứ lúc nào."));

        root.Children.Add(SectionLabel("Ngôn ngữ đích"));
        foreach (var (code, label) in AppSettings.SupportedTargetLanguages)
        {
            var rb = new RadioButton
            {
                Content = label,
                GroupName = "targetLang",
                IsChecked = AppSettings.TargetLanguage == code,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 4, 0, 0)
            };
            rb.Checked += (_, _) => AppSettings.TargetLanguage = code;
            _targetLangButtons.Add((code, rb));
            root.Children.Add(rb);
        }

        root.Children.Add(SectionLabel("Chế độ dịch"));
        AddModeOption(root, TranslationMode.Auto, "Tự động (offline trước, online dự phòng)");
        AddModeOption(root, TranslationMode.OfflineOnly, "Chỉ offline");
        AddModeOption(root, TranslationMode.OnlineOnly, "Chỉ online");

        root.Children.Add(SectionLabel("Model Manager"));
        _translationRow = AddModelRow(root, ModelManager.InstallTranslation);
        _explanationRow = AddModelRow(root, ModelManager.InstallExplanation);
        _ocrRow = AddModelRow(root, ModelManager.InstallOcr);
        _whisperRow = AddModelRow(root, ModelManager.InstallWhisper);
        var refreshLink = new TextBlock
        {
            Text = "↻ Làm mới trạng thái",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 148)),
            Cursor = System.Windows.Input.Cursors.Hand,
            Margin = new Thickness(0, 4, 0, 0),
        };
        refreshLink.MouseLeftButtonDown += async (_, _) => await RefreshModelManagerAsync();
        root.Children.Add(refreshLink);

        var closeButton = new Button
        {
            Content = "Đóng",
            Margin = new Thickness(0, 20, 0, 0),
            Padding = new Thickness(16, 6, 16, 6),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        closeButton.Click += (_, _) => Hide();
        root.Children.Add(closeButton);

        // Nội dung đã dài hơn nhiều (thêm Model Manager) - cần cuộn được thay
        // vì để cửa sổ tràn ra ngoài màn hình (SizeToContent không tự giới
        // hạn theo chiều cao màn hình, phần dưới sẽ nằm ngoài tầm với).
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = SystemParameters.WorkArea.Height - 80,
            Content = root,
        };
    }

    private ModelRow AddModelRow(StackPanel root, Action installAction)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textStack = new StackPanel();
        var nameBlock = new TextBlock { FontSize = 12, Foreground = Brushes.White };
        var statusBlock = new TextBlock { FontSize = 11, Margin = new Thickness(0, 1, 0, 0) };
        textStack.Children.Add(nameBlock);
        textStack.Children.Add(statusBlock);
        Grid.SetColumn(textStack, 0);
        grid.Children.Add(textStack);

        var installButton = new Button
        {
            Content = "Cài đặt",
            Padding = new Thickness(10, 2, 10, 2),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        installButton.Click += async (_, _) =>
        {
            installAction();
            await RefreshModelManagerAsync();
        };
        Grid.SetColumn(installButton, 1);
        grid.Children.Add(installButton);

        root.Children.Add(grid);
        return new ModelRow { NameBlock = nameBlock, StatusBlock = statusBlock, InstallButton = installButton };
    }

    private async Task RefreshModelManagerAsync()
    {
        var translationInfo = await ModelManager.CheckTranslationAsync();
        var explanationInfo = await ModelManager.CheckExplanationAsync();
        var ocrInfo = ModelManager.CheckOcr();
        var whisperInfo = ModelManager.CheckWhisper();
        await ApplyModelInfoAsync(_translationRow, translationInfo);
        await ApplyModelInfoAsync(_explanationRow, explanationInfo);
        await ApplyModelInfoAsync(_ocrRow, ocrInfo);
        await ApplyModelInfoAsync(_whisperRow, whisperInfo);

        // Model đang tải, hoặc đã cài nhưng server sidecar chưa kịp khởi
        // động xong - tự làm mới lại sau vài giây để cập nhật trạng thái mà
        // không cần người dùng tự bấm "Làm mới" liên tục.
        bool stillSettling = ModelManager.TranslationDownloading || ModelManager.ExplanationDownloading || ModelManager.OcrDownloading || ModelManager.WhisperDownloading
            || translationInfo.Status == ModelStatus.InstalledNotReady
            || explanationInfo.Status == ModelStatus.InstalledNotReady;
        if (stillSettling)
        {
            await Task.Delay(4000);
            if (IsVisible) await RefreshModelManagerAsync();
        }
    }

    private static Task ApplyModelInfoAsync(ModelRow row, ModelInfo info)
    {
        row.NameBlock.Text = info.Name;
        (string icon, Color color) = info.Status switch
        {
            ModelStatus.Ready => ("● Sẵn sàng", Color.FromRgb(120, 220, 140)),
            ModelStatus.Downloading => ("◐ Đang tải...", Color.FromRgb(255, 190, 90)),
            ModelStatus.InstalledNotReady => ("◐ Đã cài", Color.FromRgb(150, 200, 255)),
            _ => ("○ Chưa cài", Color.FromRgb(160, 160, 168)),
        };
        row.StatusBlock.Text = $"{icon} — {info.Detail}";
        row.StatusBlock.Foreground = new SolidColorBrush(color);
        row.InstallButton.Visibility = info.Status == ModelStatus.NotInstalled ? Visibility.Visible : Visibility.Collapsed;
        return Task.CompletedTask;
    }

    private void AddModeOption(StackPanel root, TranslationMode mode, string label)
    {
        var rb = new RadioButton
        {
            Content = label,
            GroupName = "mode",
            IsChecked = AppSettings.Mode == mode,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 4, 0, 0)
        };
        rb.Checked += (_, _) => AppSettings.Mode = mode;
        _modeButtons.Add((mode, rb));
        root.Children.Add(rb);
    }

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        FontSize = 16,
        FontWeight = FontWeights.Bold,
        Foreground = Brushes.White,
        Margin = new Thickness(0, 0, 0, 8)
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 168)),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 16)
    };

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        FontSize = 13,
        FontWeight = FontWeights.Bold,
        Foreground = new SolidColorBrush(Color.FromRgb(120, 200, 255)),
        Margin = new Thickness(0, 16, 0, 4)
    };

    public void ShowAndActivate()
    {
        // Re-sync in case the tray menu changed a setting while this window
        // was hidden - avoids showing stale selections.
        foreach (var (code, button) in _targetLangButtons) button.IsChecked = code == AppSettings.TargetLanguage;
        foreach (var (mode, button) in _modeButtons) button.IsChecked = mode == AppSettings.Mode;

        Show();
        WindowState = WindowState.Normal;
        Activate();
        Focus();
        _ = RefreshModelManagerAsync();
    }
}
