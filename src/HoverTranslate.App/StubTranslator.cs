namespace HoverTranslate.App;

// Phase 1 stand-in for the real Translation Engine (Phase 2 will wire up
// Argos Translate / CTranslate2 per docs/PHASE0_FINDINGS.md). Spec section 23
// explicitly calls for "dùng translation giả để test" the hover + popup
// mechanics before the real engine is built - this is that fake.
internal static class StubTranslator
{
    private static readonly Dictionary<string, string> Dictionary = new(StringComparer.OrdinalIgnoreCase)
    {
        ["we"] = "chúng tôi / chúng ta",
        ["need"] = "cần",
        ["a"] = "một",
        ["different"] = "khác",
        ["approach"] = "cách tiếp cận / phương pháp",
        ["they"] = "họ",
        ["sat"] = "ngồi (quá khứ của sit)",
        ["sit"] = "ngồi",
        ["on"] = "trên",
        ["the"] = "(mạo từ xác định)",
        ["river"] = "dòng sông",
        ["bank"] = "ngân hàng / bờ sông (tùy ngữ cảnh)",
        ["i"] = "tôi",
        ["deposited"] = "đã gửi (tiền)",
        ["deposit"] = "gửi (tiền), tiền đặt cọc",
        ["money"] = "tiền",
        ["in"] = "trong / vào",
        ["problem"] = "vấn đề",
        ["solve"] = "giải quyết",
        ["solution"] = "giải pháp",
        ["work"] = "làm việc / công việc",
        ["time"] = "thời gian",
        ["people"] = "người / mọi người",
        ["new"] = "mới",
        ["good"] = "tốt",
        ["make"] = "làm / tạo ra",
        ["use"] = "sử dụng",
        ["think"] = "nghĩ",
        ["know"] = "biết",
        ["want"] = "muốn",
        ["give"] = "cho / đưa",
        ["find"] = "tìm thấy",
        ["tell"] = "nói / kể",
        ["ask"] = "hỏi",
        ["seem"] = "có vẻ",
        ["feel"] = "cảm thấy",
        ["leave"] = "rời đi / để lại",
        ["call"] = "gọi",
        ["marketing"] = "tiếp thị",
        ["business"] = "kinh doanh",
        ["company"] = "công ty",
        ["customer"] = "khách hàng",
        ["product"] = "sản phẩm",
        ["service"] = "dịch vụ",
        ["market"] = "thị trường",
        ["team"] = "đội / nhóm",
        ["project"] = "dự án",
        ["change"] = "thay đổi",
        ["important"] = "quan trọng",
        ["understand"] = "hiểu",
        ["language"] = "ngôn ngữ",
        ["word"] = "từ",
        ["sentence"] = "câu",
        ["meaning"] = "nghĩa",
        ["context"] = "ngữ cảnh",
        ["example"] = "ví dụ",
        ["translate"] = "dịch",
        ["translation"] = "bản dịch",
        ["hover"] = "rê chuột / di chuột",
        ["mouse"] = "chuột",
        ["screen"] = "màn hình",
        ["window"] = "cửa sổ",
        ["text"] = "văn bản / chữ",
        ["file"] = "tệp",
        ["computer"] = "máy tính",
        ["software"] = "phần mềm",
        ["application"] = "ứng dụng",
        ["system"] = "hệ thống",
    };

    // null = not in the ~70-word demo dictionary; caller falls back to the
    // real translation engine (TranslateWithFallbackAsync) for the word itself.
    public static string? TryTranslate(string word)
    {
        return Dictionary.TryGetValue(word, out var meaning) ? meaning : null;
    }
}
