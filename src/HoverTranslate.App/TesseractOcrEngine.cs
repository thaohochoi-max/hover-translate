using System.Drawing;
using System.IO;
using Tesseract;
using ImageFormat = System.Drawing.Imaging.ImageFormat;

namespace HoverTranslate.App;

// Windows.Media.Ocr (OcrEngine.cs) doesn't support Vietnamese at all on this
// machine even with the Vietnamese language pack installed - confirmed via
// OcrEngine.AvailableRecognizerLanguages (only en/zh listed). Per spec mục 10
// ("không khóa vào 1 OCR engine"), Tesseract + tessdata/{eng,vie}.traineddata
// is the swap-in that actually covers the app's main target languages.
internal static class TesseractOcrEngine
{
    private static TesseractEngine? _engine;
    private static bool _initFailed;

    public static Task<string?> RecognizeAsync(Bitmap bitmap) => Task.Run(() =>
    {
        try
        {
            var engine = GetEngine();
            if (engine is null) return null;

            using var memoryStream = new MemoryStream();
            bitmap.Save(memoryStream, ImageFormat.Png);
            using var pix = Pix.LoadFromMemory(memoryStream.ToArray());
            using var page = engine.Process(pix);
            string text = page.GetText();
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch
        {
            return null;
        }
    });

    // Dùng cho OCR-hover-fallback (Program.cs): dịch nguyên khối OCR ra hết cả
    // vùng chụp (RecognizeAsync) hợp lý cho Alt+Q chọn thủ công, nhưng khi tự
    // động chụp quanh con trỏ lại dễ dính chữ của dòng/cột bên cạnh - người
    // dùng chỉ muốn dịch ĐÚNG chữ đang trỏ tới, không thêm ký tự thừa. Tesseract
    // tự tách được bounding box từng từ (PageIteratorLevel.Word) - chọn từ có
    // box gần điểm hover nhất, kèm dòng chứa nó để làm ngữ cảnh câu.
    public static Task<(string Word, string Line)?> RecognizeWordAtPointAsync(Bitmap bitmap, Point point) => Task.Run(() =>
    {
        try
        {
            var engine = GetEngine();
            if (engine is null) return ((string, string)?)null;

            using var memoryStream = new MemoryStream();
            bitmap.Save(memoryStream, ImageFormat.Png);
            using var pix = Pix.LoadFromMemory(memoryStream.ToArray());
            using var page = engine.Process(pix);
            using var iter = page.GetIterator();
            iter.Begin();

            string? bestWord = null;
            string? bestLine = null;
            long bestDistSq = long.MaxValue;

            do
            {
                if (!iter.TryGetBoundingBox(PageIteratorLevel.Word, out var box)) continue;
                string word = iter.GetText(PageIteratorLevel.Word)?.Trim() ?? "";
                if (word.Length == 0) continue;

                // Khoảng cách từ điểm hover tới bounding box - 0 nếu điểm nằm
                // trong box (con trỏ đang đúng trên chữ), > 0 nếu ở gần đó
                // (hover lệch vài px so với glyph thật vẫn cần bắt được từ).
                long dx = Math.Max(0, Math.Max(box.X1 - point.X, point.X - box.X2));
                long dy = Math.Max(0, Math.Max(box.Y1 - point.Y, point.Y - box.Y2));
                long distSq = dx * dx + dy * dy;

                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestWord = word;
                    bestLine = iter.GetText(PageIteratorLevel.TextLine)?.Trim();
                }
            } while (iter.Next(PageIteratorLevel.Word));

            if (bestWord is null) return ((string, string)?)null;
            return (bestWord, string.IsNullOrWhiteSpace(bestLine) ? bestWord : bestLine!);
        }
        catch
        {
            return ((string, string)?)null;
        }
    });

    private static TesseractEngine? GetEngine()
    {
        if (_engine is not null) return _engine;
        if (_initFailed) return null;

        string? tessDataDir = FindTessDataDir();
        if (tessDataDir is null) { _initFailed = true; return null; }

        try
        {
            _engine = new TesseractEngine(tessDataDir, "eng+vie", EngineMode.Default);
            return _engine;
        }
        catch
        {
            _initFailed = true;
            return null;
        }
    }

    private static string? FindTessDataDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "tessdata");
            if (File.Exists(Path.Combine(candidate, "eng.traineddata"))) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
