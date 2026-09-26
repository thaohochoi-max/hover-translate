using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace HoverTranslate.App;

// Raycast/Spotlight-style popup: borderless, always-on-top, never steals focus,
// not in taskbar/alt-tab (spec section 11: "không lấy focus", "không làm gián đoạn").
internal sealed class PopupWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private readonly TextBlock _wordBlock;
    private readonly TextBlock _meaningBlock;
    private readonly TextBlock _sentenceBlock;
    private readonly TextBlock _translationBlock;
    private readonly TextBlock _explainLink;
    private readonly TextBlock _explanationBlock;
    private readonly Border _border;
    private TextBlock _pinButton = null!;
    private ScrollViewer _scrollViewer = null!;

    // Set once the user drags the resize grip - from then on ShowAt() keeps
    // their chosen size instead of resetting back to the auto-fit default.
    private bool _userResized;
    private double _userWidth;
    private double _userHeight;

    private string _currentSentence = "";
    private string _currentSourceLang = "en";
    private string _currentTargetLang = "vi";

    // Guards against a slow online-translation response landing after the user
    // has already moved on to hovering a different word.
    public string? CurrentWord { get; private set; }

    // While the user is dragging the popup by hand, Tick() must not treat
    // ordinary cursor movement as "hover moved to a new word" or Alt release
    // as "hide the popup" - both would fight the drag.
    public bool IsDragging { get; private set; }

    // Set once the user clicks "Explain deeper" (spec mục 6): Tick() must stop
    // hiding the popup on Alt release while they're reading it - Deep Explain
    // is allowed to be slower and shouldn't vanish mid-read (mục 20).
    public bool IsPinned { get; private set; }

    public PopupWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;

        _border = new Border
        {
            // Hơi trong (alpha ~235/255) thay vì nền đặc hoàn toàn - vẫn đủ
            // tương phản để đọc chữ nhưng thấy lờ mờ nội dung phía sau.
            Background = new SolidColorBrush(Color.FromArgb(235, 32, 32, 36)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10, 14, 10),
            MaxWidth = 360,
            Cursor = System.Windows.Input.Cursors.SizeAll,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 16,
                ShadowDepth = 2,
                Opacity = 0.4
            }
        };
        // Kéo để di chuyển popup: WindowStyle=None nên không có title bar để kéo,
        // bấm-giữ-kéo bất kỳ đâu trên popup đóng vai trò title bar.
        _border.MouseLeftButtonDown += (_, _) =>
        {
            IsDragging = true;
            try { DragMove(); }
            finally { IsDragging = false; }
        };
        var border = _border;

        // Nội dung dài (nhất là sau khi bấm Explain deeper) không được tràn ra
        // ngoài màn hình - topRow (ghim/đóng) đứng yên phía trên, phần còn lại
        // nằm trong ScrollViewer cuộn được, giới hạn chiều cao. Grid (thay vì
        // DockPanel) để có thể chèn tay cầm resize đè lên góc dưới-phải.
        var outer = new Grid();
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var stack = new StackPanel();

        var topRow = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _pinButton = new TextBlock
        {
            Text = "📌",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 148)),
            Cursor = System.Windows.Input.Cursors.Hand,
            Margin = new Thickness(0, 0, 10, 0),
            ToolTip = "Ghim popup (không tự ẩn khi thả Alt)",
        };
        _pinButton.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            IsPinned = !IsPinned;
            UpdatePinButtonVisual();
        };
        Grid.SetColumn(_pinButton, 1);
        topRow.Children.Add(_pinButton);

        var closeButton = new TextBlock
        {
            Text = "✕",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 148)),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        closeButton.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            IsPinned = false;
            HidePopup();
        };
        Grid.SetColumn(closeButton, 2);
        topRow.Children.Add(closeButton);
        Grid.SetRow(topRow, 0);
        outer.Children.Add(topRow);

        _wordBlock = new TextBlock
        {
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap
        };
        _meaningBlock = new TextBlock
        {
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(120, 200, 255)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0)
        };
        _sentenceBlock = new TextBlock
        {
            FontSize = 12,
            FontStyle = FontStyles.Italic,
            Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 178)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };

        _translationBlock = new TextBlock
        {
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(150, 230, 170)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };

        _explainLink = new TextBlock
        {
            Text = "Explain deeper ▸",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(255, 190, 90)),
            Cursor = System.Windows.Input.Cursors.Hand,
            TextDecorations = System.Windows.TextDecorations.Underline,
            Margin = new Thickness(0, 10, 0, 0),
        };
        _explainLink.MouseLeftButtonDown += async (_, e) =>
        {
            e.Handled = true;
            await OnExplainClickedAsync();
        };

        _explanationBlock = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 226)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed,
        };

        stack.Children.Add(_wordBlock);
        stack.Children.Add(_meaningBlock);
        stack.Children.Add(_sentenceBlock);
        stack.Children.Add(_translationBlock);
        stack.Children.Add(_explainLink);
        stack.Children.Add(_explanationBlock);

        _scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 480,
            Content = stack,
        };
        Grid.SetRow(_scrollViewer, 1);
        outer.Children.Add(_scrollViewer);

        // Tay cầm phóng to/thu nhỏ: đè lên góc dưới-phải, kéo để chỉnh cả
        // chiều rộng (border) lẫn chiều cao vùng cuộn (_scrollViewer).
        var resizeGrip = new TextBlock
        {
            Text = "◢",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(110, 110, 118)),
            Cursor = System.Windows.Input.Cursors.SizeNWSE,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            ToolTip = "Kéo để phóng to/thu nhỏ",
        };
        Grid.SetRow(resizeGrip, 1);
        outer.Children.Add(resizeGrip);

        System.Windows.Input.MouseEventHandler? resizeMoveHandler = null;
        System.Windows.Input.MouseButtonEventHandler? resizeUpHandler = null;
        resizeGrip.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            var startPos = e.GetPosition(this);
            double startWidth = border.ActualWidth;
            double startHeight = _scrollViewer.ActualHeight;

            resizeMoveHandler = (_, me) =>
            {
                if (me.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
                var cur = me.GetPosition(this);
                _userWidth = Math.Clamp(startWidth + (cur.X - startPos.X), 220, 700);
                _userHeight = Math.Clamp(startHeight + (cur.Y - startPos.Y), 60, 800);
                _userResized = true;
                ApplyUserSize();
            };
            resizeUpHandler = (_, _) =>
            {
                resizeGrip.ReleaseMouseCapture();
                if (resizeMoveHandler is not null) resizeGrip.MouseMove -= resizeMoveHandler;
                if (resizeUpHandler is not null) resizeGrip.MouseLeftButtonUp -= resizeUpHandler;
            };
            resizeGrip.MouseMove += resizeMoveHandler;
            resizeGrip.MouseLeftButtonUp += resizeUpHandler;
            resizeGrip.CaptureMouse();
        };

        border.Child = outer;
        Content = border;
    }

    private void ApplyUserSize()
    {
        _border.MaxWidth = double.PositiveInfinity;
        _border.Width = _userWidth;
        _scrollViewer.MaxHeight = double.PositiveInfinity;
        _scrollViewer.Height = _userHeight;
        ForceTopmost();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    public void ShowAt(double screenX, double screenY, string word, string meaning, string sentence, string sourceLang, string targetLang)
    {
        CurrentWord = word;
        _currentSentence = sentence;
        _currentSourceLang = sourceLang;
        _currentTargetLang = targetLang;
        // Ghim là "đừng tự tắt", không phải "đừng đổi nội dung" - rê sang chữ
        // khác vẫn phải cập nhật bản dịch mới, trạng thái ghim giữ nguyên qua
        // các lần cập nhật cho tới khi người dùng tự bỏ ghim/đóng popup.

        _wordBlock.Text = word;
        _meaningBlock.Text = meaning;
        _sentenceBlock.Text = string.IsNullOrWhiteSpace(sentence) ? "" : $"“{sentence}”";
        _sentenceBlock.Visibility = string.IsNullOrWhiteSpace(sentence) ? Visibility.Collapsed : Visibility.Visible;
        _translationBlock.Text = string.IsNullOrWhiteSpace(sentence) ? "" : "Đang dịch cả câu...";
        _translationBlock.Visibility = string.IsNullOrWhiteSpace(sentence) ? Visibility.Collapsed : Visibility.Visible;

        _explainLink.Text = "Explain deeper ▸";
        _explainLink.IsEnabled = true;
        _explainLink.Visibility = Visibility.Visible;
        _explanationBlock.Visibility = Visibility.Collapsed;
        _explanationBlock.Text = "";
        UpdatePinButtonVisual();
        if (_userResized) ApplyUserSize();

        Left = screenX + 16;
        Top = screenY + 22;

        if (!IsVisible) Show();
        ForceTopmost();

        // Keep the popup on-screen if it would spill past the right/bottom edge.
        Dispatcher.InvokeAsync(() =>
        {
            var workArea = SystemParameters.WorkArea;
            if (Left + ActualWidth > workArea.Right) Left = screenX - ActualWidth - 16;
            if (Top + ActualHeight > workArea.Bottom) Top = screenY - ActualHeight - 12;
            ForceTopmost();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // WPF's Topmost=true isn't always enough to beat every other topmost/
    // fullscreen window (spec mục 11: "không che nội dung" implies the popup
    // itself must not BE the thing getting hidden either) - re-assert z-order
    // via Win32 directly each time the popup is (re)shown.
    private void ForceTopmost()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    // Called once the async sentence-translation call resolves. Dropped silently
    // if the user has already moved on to a different word.
    public void SetSentenceTranslation(string forWord, string? translated)
    {
        if (!string.Equals(CurrentWord, forWord, StringComparison.OrdinalIgnoreCase)) return;
        _translationBlock.Text = translated is null
            ? "(không dịch được câu này — kiểm tra mạng)"
            : $"Dịch: {translated}";
    }

    // Called once the async single-word translation resolves (real engine,
    // not just the ~70-word demo dictionary). Dropped silently if the user
    // already moved on to a different word.
    public void SetWordMeaning(string forWord, string? translated)
    {
        if (!string.Equals(CurrentWord, forWord, StringComparison.OrdinalIgnoreCase)) return;
        _meaningBlock.Text = translated ?? "(không dịch được từ này)";
    }

    public void HidePopup()
    {
        CurrentWord = null;
        IsPinned = false;
        if (IsVisible) Hide();
    }

    // True while the cursor is anywhere over the popup's own rectangle - Tick()
    // uses this to stop treating "moving the mouse to click a popup button" as
    // "hovering new text", which was hiding the popup / misreading the popup's
    // own Vietnamese labels as the thing to translate before this existed.
    public bool ContainsScreenPoint(double x, double y)
        => IsVisible && x >= Left && x <= Left + ActualWidth && y >= Top && y <= Top + ActualHeight;

    // Dùng cho các kết quả không gắn với vòng đời hover-theo-Alt (OCR: Alt
    // thường đã thả ra từ lúc kéo chọn vùng, trước khi OCR/dịch xong) - ghim
    // sẵn để Tick() không tự ẩn theo việc thả Alt.
    public void SetPinned(bool pinned)
    {
        IsPinned = pinned;
        UpdatePinButtonVisual();
    }

    private void UpdatePinButtonVisual()
    {
        _pinButton.Foreground = IsPinned
            ? new SolidColorBrush(Color.FromRgb(255, 190, 90))
            : new SolidColorBrush(Color.FromRgb(140, 140, 148));
    }

    private async Task OnExplainClickedAsync()
    {
        if (CurrentWord is null) return;
        string word = CurrentWord;
        IsPinned = true;
        UpdatePinButtonVisual();
        _explainLink.Text = "Đang giải thích...";
        _explainLink.IsEnabled = false;

        string? result = await ExplanationEngine.ExplainAsync(word, _currentSentence, _currentSourceLang, _currentTargetLang);

        // User already moved on to a different word while we were waiting.
        if (!string.Equals(CurrentWord, word, StringComparison.OrdinalIgnoreCase)) return;

        _explanationBlock.Text = result ?? "(không giải thích được — kiểm tra Ollama đã chạy và đã tải model chưa)";
        _explanationBlock.Visibility = Visibility.Visible;
        _explainLink.Visibility = Visibility.Collapsed;

        _ = Dispatcher.InvokeAsync(ForceTopmost, System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
