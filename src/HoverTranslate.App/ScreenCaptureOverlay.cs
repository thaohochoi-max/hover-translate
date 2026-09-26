using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Rectangle = System.Windows.Shapes.Rectangle;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using Cursors = System.Windows.Input.Cursors;

namespace HoverTranslate.App;

// Spec mục 9: Alt+Q -> "screen overlay -> user chọn vùng -> OCR -> dịch".
// Full-screen semi-transparent window the user drags a rectangle on; returns
// that rectangle in PHYSICAL screen pixels (matching Graphics.CopyFromScreen
// and UI Automation's own coordinate space) so the caller never has to think
// about DPI again.
internal sealed class ScreenCaptureOverlay : Window
{
    // WPF's Topmost=true không phải lúc nào cũng thắng được z-order của app
    // khác (y hệt lý do PopupWindow/SubtitleOverlay đã phải ép qua Win32) -
    // bug thật đã gặp: bấm Alt+Q chạy đúng code nhưng overlay bị khuất phía
    // sau, người dùng tưởng phím không ăn. Khác PopupWindow (cố ý không lấy
    // focus), overlay này CẦN nhận focus thật vì người dùng phải kéo chuột +
    // có thể bấm Esc trên chính nó.
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private readonly Rectangle _selectionBox;
    private System.Windows.Point? _dragStart;
    private double _dpiScale = 1.0;

    public System.Drawing.Rectangle? SelectedRegion { get; private set; }

    public ScreenCaptureOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0));
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Cursor = Cursors.Cross;
        WindowStartupLocation = WindowStartupLocation.Manual;

        // Phủ toàn bộ virtual screen (mọi màn hình), không chỉ màn hình chính.
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        var canvas = new System.Windows.Controls.Canvas();
        _selectionBox = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(255, 190, 90)),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(40, 255, 190, 90)),
            Visibility = Visibility.Collapsed,
        };
        canvas.Children.Add(_selectionBox);

        var hint = new System.Windows.Controls.TextBlock
        {
            Text = "Kéo để chọn vùng cần dịch — Esc để hủy",
            Foreground = Brushes.White,
            FontSize = 16,
            Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
            Padding = new Thickness(10, 5, 10, 5),
        };
        System.Windows.Controls.Canvas.SetLeft(hint, 20);
        System.Windows.Controls.Canvas.SetTop(hint, 20);
        canvas.Children.Add(hint);

        Content = canvas;

        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { SelectedRegion = null; Close(); } };
        Loaded += (_, _) =>
        {
            _dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            ForceTopmostAndActivate();
        };
        ContentRendered += (_, _) => ForceTopmostAndActivate();
    }

    private void ForceTopmostAndActivate()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        NativeMethods.SetForegroundWindow(hwnd);
        Activate();
        Keyboard.Focus(this);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _selectionBox.Visibility = Visibility.Visible;
        System.Windows.Controls.Canvas.SetLeft(_selectionBox, _dragStart.Value.X);
        System.Windows.Controls.Canvas.SetTop(_selectionBox, _dragStart.Value.Y);
        _selectionBox.Width = 0;
        _selectionBox.Height = 0;
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is null) return;
        var cur = e.GetPosition(this);
        double x = Math.Min(_dragStart.Value.X, cur.X);
        double y = Math.Min(_dragStart.Value.Y, cur.Y);
        double w = Math.Abs(cur.X - _dragStart.Value.X);
        double h = Math.Abs(cur.Y - _dragStart.Value.Y);

        System.Windows.Controls.Canvas.SetLeft(_selectionBox, x);
        System.Windows.Controls.Canvas.SetTop(_selectionBox, y);
        _selectionBox.Width = w;
        _selectionBox.Height = h;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is null) return;
        ReleaseMouseCapture();
        var cur = e.GetPosition(this);

        double dipX = Math.Min(_dragStart.Value.X, cur.X) + Left;
        double dipY = Math.Min(_dragStart.Value.Y, cur.Y) + Top;
        double dipW = Math.Abs(cur.X - _dragStart.Value.X);
        double dipH = Math.Abs(cur.Y - _dragStart.Value.Y);
        _dragStart = null;

        if (dipW < 4 || dipH < 4)
        {
            // Coi như bấm nhầm/click đơn, không phải kéo chọn vùng thật.
            SelectedRegion = null;
        }
        else
        {
            SelectedRegion = new System.Drawing.Rectangle(
                (int)Math.Round(dipX * _dpiScale),
                (int)Math.Round(dipY * _dpiScale),
                (int)Math.Round(dipW * _dpiScale),
                (int)Math.Round(dipH * _dpiScale));
        }
        Close();
    }
}
