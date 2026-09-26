using System.Drawing;
using System.Drawing.Imaging;

namespace HoverTranslate.App;

// Spec mục 17: "capture -> OCR -> discard", không lưu screenshot. Callers
// phải Dispose() bitmap ngay sau khi OCR xong - không có đường ghi ra đĩa nào
// trong file này.
internal static class ScreenCapture
{
    public static Bitmap Capture(Rectangle region)
    {
        var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.CopyFromScreen(region.Left, region.Top, 0, 0, region.Size);
        return bitmap;
    }
}
