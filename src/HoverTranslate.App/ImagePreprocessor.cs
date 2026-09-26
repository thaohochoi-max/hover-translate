using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace HoverTranslate.App;

// Spec mục 10 architecture: Screen Capture -> Image Preprocessing -> OCR
// Engine. Screen-rendered text (ClearType-antialiased, often small) loses
// fine detail - diacritics especially - when fed to OCR at native resolution;
// upscaling gives both Tesseract and Windows OCR meaningfully more pixels per
// glyph to work with.
internal static class ImagePreprocessor
{
    public static Bitmap Upscale(Bitmap source, float factor = 3f)
    {
        int newWidth = Math.Max(1, (int)(source.Width * factor));
        int newHeight = Math.Max(1, (int)(source.Height * factor));

        var result = new Bitmap(newWidth, newHeight, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(result);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(source, 0, 0, newWidth, newHeight);
        return result;
    }
}
