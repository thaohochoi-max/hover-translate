namespace HoverTranslate.App;

internal enum TranslationMode { Auto, OfflineOnly, OnlineOnly }

// Shared, in-memory settings read by Tick() and written by both the tray menu
// and SettingsWindow. A user reported the tray icon being unreachable because
// their IDE window covers the whole screen (khay hệ thống không hiện ra) - the
// settings window is the fallback path that doesn't depend on the tray at all.
internal static class AppSettings
{
    public static string TargetLanguage = "vi";

    // Mặc định Chỉ online (Google Translate) - người dùng phản ánh bản dịch
    // offline (Argos) dịch sai nhiều, trong khi Google chính xác hơn hẳn cho
    // câu tiếng Việt kiểu chat/casual. Vẫn chừa lựa chọn Tự động/Chỉ offline
    // trong Settings (Alt+Shift+S) cho lúc không có mạng.
    public static TranslationMode Mode = TranslationMode.OnlineOnly;

    public static readonly (string Code, string Label)[] SupportedTargetLanguages =
    {
        ("vi", "Tiếng Việt"),
        ("en", "English"),
        ("zh", "中文"),
        ("ja", "日本語"),
        ("ko", "한국어"),
        ("ru", "Русский"),
        ("hi", "हिन्दी"),
        ("th", "ภาษาไทย"),
        ("id", "Bahasa Indonesia"),
        ("ms", "Bahasa Melayu"),
        ("tl", "Filipino"),
        ("fr", "Français"),
        ("de", "Deutsch"),
        ("es", "Español"),
        ("pt", "Português"),
        ("ar", "العربية"),
    };
}
