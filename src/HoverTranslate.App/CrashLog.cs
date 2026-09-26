using System.IO;

namespace HoverTranslate.App;

// Spec mục 28 "Polish": app chạy nền phải chịu lỗi tốt (crash handling), và
// khi có sự cố phải để lại dấu vết chẩn đoán được thay vì biến mất không dấu
// vết. File nhỏ, tự giới hạn dung lượng - không phải hệ thống log đầy đủ.
internal static class CrashLog
{
    private static readonly string LogPath = GetLogPath();
    private static readonly object Lock = new();

    public static string LogFilePath => LogPath;

    public static void Write(string context, Exception? ex)
    {
        try
        {
            lock (Lock)
            {
                string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}\n{ex}\n\n";
                File.AppendAllText(LogPath, entry);

                // Không để file phình vô hạn qua nhiều phiên chạy dài ngày.
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > 2 * 1024 * 1024)
                {
                    string[] lines = File.ReadAllLines(LogPath);
                    File.WriteAllLines(LogPath, lines[(lines.Length / 2)..]);
                }
            }
        }
        catch
        {
            // Ghi log mà cũng lỗi thì đành chịu - không được phép ném lỗi
            // tiếp từ trong chính handler xử lý lỗi.
        }
    }

    private static string GetLogPath()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HoverTranslate");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "crash.log");
    }
}
