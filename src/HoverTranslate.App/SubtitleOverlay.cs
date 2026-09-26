using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;

namespace HoverTranslate.App;

// Phụ đề nổi cho Live Audio Translate - theo mẫu PopupWindow.cs (kéo, resize
// qua grip tự vẽ, luôn-trên-cùng qua SetWindowPos, WS_EX_NOACTIVATE để không
// cướp focus của app đang phát video, đúng spec mục 7: "Không chiếm focus").
// Thêm: bật/tắt click-through (WS_EX_TRANSPARENT), chỉnh cỡ chữ, chỉnh độ mờ,
// hiện gốc+dịch hoặc chỉ dịch.
internal sealed class SubtitleOverlay : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private readonly Border _border;
    private readonly TextBlock _originalBlock;
    private readonly TextBlock _translatedBlock;
    private double _fontSize = 20;
    private double _opacityPct = 90;
    private bool _showOriginal = true;
    private bool _clickThrough;

    public SubtitleOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        Width = 700;
        SizeToContent = SizeToContent.Height;
        Left = (SystemParameters.WorkArea.Width - Width) / 2;
        Top = SystemParameters.WorkArea.Height - 160;

        _border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(230, 20, 20, 22)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 10, 16, 10),
            Cursor = Cursors.SizeAll,
        };
        _border.MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };

        var outer = new Grid();
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var toolRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        toolRow.Children.Add(ToolButton("A-", () => { _fontSize = Math.Max(12, _fontSize - 2); ApplyFontSize(); }));
        toolRow.Children.Add(ToolButton("A+", () => { _fontSize = Math.Min(48, _fontSize + 2); ApplyFontSize(); }));
        toolRow.Children.Add(ToolButton("◐", () => { _opacityPct = _opacityPct <= 40 ? 90 : _opacityPct - 20; ApplyOpacity(); }));
        toolRow.Children.Add(ToolButton("EN", () => { _showOriginal = !_showOriginal; ApplyShowOriginal(); }));
        toolRow.Children.Add(ToolButton("🖱", () => SetClickThrough(!_clickThrough)));
        toolRow.Children.Add(ToolButton("✕", HidePopup));
        Grid.SetRow(toolRow, 0);
        outer.Children.Add(toolRow);

        var textStack = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        _originalBlock = new TextBlock
        {
            FontSize = _fontSize * 0.7,
            Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 188)),
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
        _translatedBlock = new TextBlock
        {
            FontSize = _fontSize,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        };
        textStack.Children.Add(_originalBlock);
        textStack.Children.Add(_translatedBlock);
        Grid.SetRow(textStack, 1);
        outer.Children.Add(textStack);

        _border.Child = outer;
        Content = _border;
    }

    private static Button ToolButton(string text, Action onClick)
    {
        var b = new Button
        {
            Content = text,
            FontSize = 11,
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(3, 0, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(48, 48, 54)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    public void ShowSegment(string original, string translated)
    {
        _originalBlock.Text = original;
        _translatedBlock.Text = translated;
        if (!IsVisible) Show();
        ForceTopmost();
    }

    public void ShowStatus(string status)
    {
        _originalBlock.Text = "";
        _translatedBlock.Text = status;
        if (!IsVisible) Show();
        ForceTopmost();
    }

    public void HidePopup()
    {
        if (IsVisible) Hide();
    }

    private void SetClickThrough(bool enabled)
    {
        _clickThrough = enabled;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, enabled ? exStyle | WS_EX_TRANSPARENT : exStyle & ~WS_EX_TRANSPARENT);
    }

    private void ApplyFontSize()
    {
        _translatedBlock.FontSize = _fontSize;
        _originalBlock.FontSize = _fontSize * 0.7;
    }

    private void ApplyOpacity() => _border.Opacity = _opacityPct / 100.0;

    private void ApplyShowOriginal() => _originalBlock.Visibility = _showOriginal ? Visibility.Visible : Visibility.Collapsed;

    private void ForceTopmost()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }
}
