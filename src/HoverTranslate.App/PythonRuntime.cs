using System.IO;

namespace HoverTranslate.App;

// Bản cài đặt đóng gói (installer) đi kèm 1 bản Python nhúng riêng
// (python-embed\python.exe, cùng cấp với scripts\/tessdata\) để người nhận
// không cần tự cài Python - xem installer/HoverTranslate.iss. Lúc đang dev
// (chạy thẳng từ bin/Debug|Release) thư mục này không tồn tại, tự rơi về
// "python" từ PATH hệ thống như trước giờ - code sidecar KHÔNG cần biết đang
// chạy ở môi trường nào.
internal static class PythonRuntime
{
    private static readonly Lazy<string> ExecutablePath = new(Resolve);

    public static string Executable => ExecutablePath.Value;

    private static string Resolve()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "python-embed", "python.exe");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return "python"; // dev machine - dựa vào PATH hệ thống
    }
}
