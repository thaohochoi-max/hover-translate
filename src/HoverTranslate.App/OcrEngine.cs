using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace HoverTranslate.App;

// Spec mục 10/26: bench Windows OCR (built into Windows, zero install, fully
// offline) before reaching for PaddleOCR/RapidOCR/Tesseract. Kept behind this
// one static class on purpose - swapping the engine later (mục 10: "không
// khóa architecture vào 1 OCR engine") means replacing just this file.
internal static class OcrEngine
{
    // Windows.Media.Ocr.OcrEngine (aliased so it doesn't collide with this
    // class's own name).
    public static async Task<string?> RecognizeAsync(Bitmap bitmap)
    {
        try
        {
            SoftwareBitmap softwareBitmap = await ToSoftwareBitmapAsync(bitmap);

            var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages()
                ?? Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Language("en"));
            if (engine is null) return null;

            var result = await engine.RecognizeAsync(softwareBitmap);
            string text = result.Text;
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(Bitmap bitmap)
    {
        using var memoryStream = new MemoryStream();
        bitmap.Save(memoryStream, ImageFormat.Png);
        memoryStream.Position = 0;

        using var randomAccessStream = new InMemoryRandomAccessStream();
        using (var outputStream = randomAccessStream.GetOutputStreamAt(0))
        {
            var writer = new DataWriter(outputStream);
            writer.WriteBytes(memoryStream.ToArray());
            await writer.StoreAsync();
            await outputStream.FlushAsync();
            writer.DetachStream();
        }
        randomAccessStream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
        SoftwareBitmap softwareBitmap = await decoder.GetSoftwareBitmapAsync();

        // OcrEngine.RecognizeAsync requires Bgra8 + Premultiplied (or Straight).
        if (softwareBitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 ||
            softwareBitmap.BitmapAlphaMode == BitmapAlphaMode.Straight)
        {
            softwareBitmap = SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        }

        return softwareBitmap;
    }
}
