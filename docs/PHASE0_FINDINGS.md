# Phase 0 — Feasibility Findings & Kiến trúc đề xuất

Ngày: 2026-09-18
Phạm vi: mục 22 + mục 32 của PRODUCT SPEC — kiểm chứng feasibility của "word-under-cursor" qua Windows UI Automation trước khi build full app.

## 1. Kết luận feasibility

**UI Automation `TextPattern.RangeFromPoint()` + `ExpandToEnclosingUnit(Word/Paragraph)` là khả thi** để lấy từ dưới con trỏ chuột + ngữ cảnh câu, mà không cần copy/paste hay OCR, miễn là control đích expose `TextPattern` (đúng hướng thiết kế ở mục 2 và mục 4 của spec).

Đã verify bằng automated test (6/6 pass) trên một harness cô lập, tự dựng (xem mục 3), mô phỏng hai họ control chính mà Notepad/Chrome/Word thực tế dùng:

| Control | ClassName thực tế | ControlType | Kết quả |
|---|---|---|---|
| WPF TextBox | `TextBox` | `Edit` | PASS — lấy đúng từ, nhưng "paragraph" trả về **toàn bộ** nội dung multi-line (xem Risk #1) |
| WinForms RichTextBox (native RICHEDIT control) | `WindowsForms10.RICHEDIT50W...` | `Document` | PASS — lấy đúng từ **và** đúng câu/dòng riêng lẻ |

RichTextBox ở trên dùng chung họ control Win32 RICHEDIT với Notepad hiện đại (đã xác nhận ClassName thực của Notepad là `RichEditD2DPT` khi kiểm tra qua UI Automation lúc setup môi trường — cùng gốc RichEdit, khác lớp render D2D). Đây là tín hiệu tốt cho khả năng tương thích thật với Notepad.

**Chưa test trực tiếp trên Notepad/Chrome thật** trong phiên này — xem mục 6 (quyết định an toàn) và mục 7 (cách bạn tự test).

## 2. Môi trường đã setup

- .NET 8 SDK 8.0.425 (cài qua winget: `Microsoft.DotNet.SDK.8`) — máy trước đó chưa có `dotnet`.
- Windows 11 (build 10.0.26200).
- Xác nhận: `notepad.exe` trên Windows 11 hiện tại chỉ là launcher/alias — tiến trình thật tên **`Notepad`** (không phải `notepad`), chạy single-instance kiểu tab (giống Edge/Chrome). Đây là điểm cần nhớ khi thiết kế module Windows Integration (không thể assume 1 process = 1 cửa sổ = 1 document).

## 3. Vì sao dùng harness cô lập thay vì Notepad/Chrome thật

Lần thử đầu tiên, script test đã vô tình gõ text mẫu vào một cửa sổ Notepad **đang mở sẵn** của bạn (do Notepad single-instance ở trên), cửa sổ đó chứa nội dung private key của một service account chưa lưu. Đã undo (Ctrl+Z) ngay, không có lệnh Save nào được gửi nên không mất/lộ dữ liệu — nhưng đây là lý do quyết định chuyển sang harness tự dựng, tự kiểm soát hoàn toàn (`poc/TestHarness`), không đụng vào bất kỳ ứng dụng thật nào của bạn nữa cho việc test tự động.

`poc/TestHarness` là một app WPF độc lập, tự tạo cửa sổ + nội dung mẫu riêng, rồi gọi `poc/HoverProbe.exe` (process con, đúng mô hình thật: app hover-translate sẽ luôn là process khác với app đang được hover) để verify kết quả UI Automation tại toạ độ màn hình tính toán được. Không có input/thao tác nào từ bên ngoài.

## 4. Rủi ro kỹ thuật phát hiện được

1. **Sentence/paragraph unit không đáng tin cậy trên control kiểu WPF Edit** — `ExpandToEnclosingUnit(TextUnit.Paragraph)` trả về toàn bộ nội dung multi-line thay vì tách đúng 1 dòng/câu. → Context Engine (mục 7 spec) **không thể** dựa hoàn toàn vào UIA text unit để tách câu; cần tự làm sentence segmentation (regex/NLP nhẹ) trên text lấy được từ UIA, ở tầng app.
2. **UIPI (User Interface Privilege Isolation)**: một app chạy quyền thường (non-elevated) **không** gọi được UI Automation vào cửa sổ của process chạy elevated (vd: Task Manager, cmd "Run as Administrator", một số installer). Nếu hover-translate chạy non-elevated (khuyến nghị, vì lý do UX/an toàn), sẽ có nhóm cửa sổ không đọc được text → cần fallback OCR đúng như spec đã thiết kế sẵn (mục 4, Phase 5), nhưng cần document rõ đây là **giới hạn theo thiết kế Windows**, không phải bug.
3. **`InvariantGlobalization=true` phá vỡ WPF text rendering** (đã tự phát hiện khi build harness — app crash ngay khi layout text). Ghi nhận: **không được bật InvariantGlobalization** ở bất kỳ project nào host WPF/popup UI.
4. **Notepad single-instance/tab model** (mục 2) — module Windows Integration cần enumerate theo `hwnd`/tab, không theo PID.
5. **Chrome/Electron**: theo tài liệu Chromium, cây UI Automation của Chrome chỉ kích hoạt "on-demand" khi phát hiện một AT (assistive technology) client gọi vào — có độ trễ ở lần hit đầu tiên (chưa đo thực tế, cần benchmark ở Phase 1). Một số nội dung trong Chrome (PDF viewer nội bộ render ra canvas, `<canvas>`, video có subtitle burn-in) sẽ **không** có TextPattern dù accessibility đã bật → đúng nhóm case OCR fallback mà spec đã liệt kê ở mục 15.
6. **Global mouse hook + hover 250-800ms polling**: hook procedure phải trả về cực nhanh (không block); nên tách việc lấy UIA text sang thread/task riêng khỏi hook callback để tránh Windows tự gỡ hook khi timeout.

## 5. Đề xuất kiến trúc & folder structure (cụ thể hoá mục 12 & mục 30 spec)

```
hover-translate/
  HoverTranslate.sln
  docs/
    PHASE0_FINDINGS.md          <- file này
  poc/                          <- giữ lại làm regression check cho mỗi phase sau
    HoverProbe/                 <- CLI probe: "probe x y" | "watch"
    TestHarness/                <- test cô lập, không đụng app thật
  src/
    HoverTranslate.App/                 # WPF host, system tray, DI composition root
    HoverTranslate.WindowsIntegration/  # mouse hook, hotkeys (Alt+Q), UIA text extraction
    HoverTranslate.Core/                # domain models, Context Engine, interfaces (IOcrEngine, ITranslationEngine, IExplanationEngine)
    HoverTranslate.Ocr/                 # OCR engine implementations (đổi được, theo mục 10 spec)
    HoverTranslate.Translation/         # Translation engine implementations
    HoverTranslate.Explanation/         # local LLM runtime adapter (Ollama/llama.cpp)
    HoverTranslate.Cache/               # local cache theo key (text, context, srcLang, tgtLang, modelVersion)
    HoverTranslate.ModelManager/        # download/track trạng thái model
    HoverTranslate.Popup/               # popup UI (Raycast/Spotlight style)
  tests/
    HoverTranslate.Core.Tests/
```

Mỗi engine (OCR, Translation, Explanation) là một interface trong `Core` + implementation riêng theo project — đúng nguyên tắc "không khoá architecture vào 1 engine" (mục 10, mục 14 spec).

## 6. Đề xuất công cụ (research-based, chưa benchmark thực nghiệm)

- **OCR MVP**: `Windows.Media.Ocr` (WinRT, có sẵn trong Windows 10/11, không cần cài thêm, hỗ trợ offline). Nâng cấp sau bằng RapidOCR (ONNX, nhẹ, độ chính xác tốt hơn với chữ nhỏ/CJK) nếu Windows OCR không đủ tốt cho text trong ảnh/game. Tesseract để dự phòng/so sánh baseline.
- **Translation MVP (EN→VI)**: Argos Translate (dựa trên OPUS-MT, chạy qua CTranslate2, có sẵn gói EN→VI, offline, license permissive) là lựa chọn plug-and-play nhanh nhất để test end-to-end. NLLB-200 distilled là phương án chất lượng cao hơn nhưng nặng hơn, cân nhắc ở bản sau.
- Cả hai mục này **chưa benchmark thực nghiệm** trong phiên này — thuộc phạm vi Phase 2 (Translation) và Phase 4 (OCR) theo đúng thứ tự spec đã định (mục 31: không build tất cả cùng lúc).

## 7. Cách bạn tự verify trên Notepad/Chrome thật

Đã build sẵn `poc/HoverProbe/bin/Release/net8.0-windows/HoverProbe.exe`. Chạy:

```
HoverProbe.exe watch
```

rồi rê chuột vào chữ trong Notepad hoặc Chrome thật — nó in ra JSON (word, sentence, class, controlType, elapsed ms) mỗi khi từ dưới con trỏ đổi. Không cần quyền admin. Ctrl+C để dừng.

## 8. Milestone tiếp theo (nhỏ, tuần tự — đúng mục 31 spec)

- **M0 (xong)**: POC UI Automation — 6/6 automated pass trên harness cô lập. Còn thiếu: bạn tự xác nhận bằng `watch` mode trên Notepad/Chrome thật (mục 7).
- **M1**: Global mouse hook + Alt+Hover trigger + hover delay config, nối vào logic lấy word/sentence thật (mở rộng từ HoverProbe), popup giả lập (chưa dịch thật).
- **M2**: Popup UI thật (Raycast-style, không cướp focus, dark/light).
- **M3**: Translation engine thật (Argos EN→VI) + Cache.
- **M4**: Sentence segmentation tự viết (khắc phục Risk #1) + Explanation engine (Ollama local).
- **M5**: OCR pipeline (Windows.Media.Ocr trước) + Alt+Q region overlay + intelligent fallback (Risk #2, #5).
- **M6**: Settings/tray, Model Manager, polish.
