"""
Speech-to-text sidecar for Live Audio Translate, cùng khuôn mẫu với
translate_server.py: HTTP localhost, load model 1 lần lúc khởi động, xử lý
theo request.

Khác translate_server.py ở chỗ sidecar này CHỈ được .NET khởi động khi bấm
START LIVE TRANSLATE (không chạy nền suốt đời app), vì model Whisper tốn RAM
hơn hẳn và chỉ cần khi thực sự dùng tính năng.

Run: python stt_server.py [port] [model_size]   (default port 5056, model base)
GET  /health -> {"ok": true, "model": "base"}
POST /transcribe?lang=auto  (body = WAV bytes, 16kHz mono PCM16 khuyến nghị)
     -> {"text": "...", "language": "en"}
     lang=auto (mặc định) để Whisper tự nhận diện; lang=en/vi/... để ép cứng -
     hữu ích khi biết trước ngôn ngữ nguồn, vì model "tiny" tự nhận diện đôi
     khi không đáng tin cậy với giọng tổng hợp/audio chất lượng thấp.

model_size hỗ trợ thêm "phowhisper-tiny"/"phowhisper-base"/... (PhoWhisper,
VinAI Research luyện riêng cho tiếng Việt trên 844h audio đa giọng vùng miền -
demo so sánh trực tiếp với Whisper gốc trên cùng câu tiếng Việt cho thấy chính
xác hơn hẳn, ví dụ "nghe"/"đoạn" bị Whisper gốc nghe nhầm "ngay"/"loạn" thì
PhoWhisper nghe đúng). Cùng định dạng CTranslate2 nên dùng chung
faster-whisper, không cần thư viện/pipeline nào khác - chỉ khác chỗ trỏ tới
model nào.
"""
import io
import json
import os
import sys
from http.server import BaseHTTPRequestHandler, HTTPServer
from urllib.parse import urlparse, parse_qs

from faster_whisper import WhisperModel

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 5056
MODEL_SIZE = sys.argv[2] if len(sys.argv) > 2 else "base"

PHOWHISPER_PREFIX = "phowhisper-"
PHOWHISPER_REPO = "quocphu/PhoWhisper-ct2-FasterWhisper"


def resolve_model_path(model_size: str) -> str:
    if not model_size.startswith(PHOWHISPER_PREFIX):
        return model_size

    size = model_size[len(PHOWHISPER_PREFIX):]  # "tiny", "base", ...
    subfolder = f"PhoWhisper-{size}-ct2-fasterWhisper"
    from huggingface_hub import snapshot_download

    local_dir = snapshot_download(repo_id=PHOWHISPER_REPO, allow_patterns=f"{subfolder}/*")
    return os.path.join(local_dir, subfolder)


print(f"Loading Whisper model '{MODEL_SIZE}'...")
model = WhisperModel(resolve_model_path(MODEL_SIZE), device="cpu", compute_type="int8")
print("Model loaded.")


class Handler(BaseHTTPRequestHandler):
    def log_message(self, format, *args):
        pass

    def _send_json(self, status, payload):
        body = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path == "/health":
            self._send_json(200, {"ok": True, "model": MODEL_SIZE})
            return
        self._send_json(404, {"error": "not found"})

    def do_POST(self):
        parsed = urlparse(self.path)
        if not parsed.path.startswith("/transcribe"):
            self._send_json(404, {"error": "not found"})
            return

        qs = parse_qs(parsed.query)
        lang_param = qs.get("lang", ["auto"])[0]
        language = None if lang_param in ("auto", "") else lang_param

        length = int(self.headers.get("Content-Length", 0))
        if length <= 0:
            self._send_json(400, {"error": "empty body"})
            return
        wav_bytes = self.rfile.read(length)

        try:
            # Audio chỉ tồn tại trong bộ nhớ - không ghi ra đĩa (spec mục 13:
            # không lưu audio tạm lâu dài).
            segments, info = model.transcribe(
                io.BytesIO(wav_bytes),
                beam_size=1,          # ưu tiên tốc độ hơn độ chính xác tuyệt đối cho realtime
                vad_filter=True,      # Whisper tự lọc khoảng lặng/nhiễu, không cần model VAD riêng
                language=language,    # None = auto-detect, hoặc ép cứng theo ?lang=
            )
            text = "".join(seg.text for seg in segments).strip()
            self._send_json(200, {"text": text, "language": info.language})
        except Exception as e:
            self._send_json(500, {"error": str(e)})


if __name__ == "__main__":
    server = HTTPServer(("127.0.0.1", PORT), Handler)
    print(f"STT server on http://127.0.0.1:{PORT}")
    server.serve_forever()
