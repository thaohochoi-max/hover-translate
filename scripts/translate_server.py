"""
Offline translation sidecar for HoverTranslate.App.

Loads the installed Argos Translate packages once, then serves translation
requests over localhost HTTP so the .NET app doesn't pay model-load latency
(1-3s) on every hover - only once, when this process starts.

Run: python translate_server.py [port]   (default port 5055)
GET /translate?text=...&from=en&to=vi -> {"translated": "...", "offline": true}
GET /health -> {"ok": true, "languages": ["en", "vi", ...]}
"""
import json
import sys
from http.server import BaseHTTPRequestHandler, HTTPServer
from urllib.parse import urlparse, parse_qs

import argostranslate.translate

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 5055


def get_installed_languages():
    return {lang.code: lang for lang in argostranslate.translate.get_installed_languages()}


LANGUAGES = get_installed_languages()


class Handler(BaseHTTPRequestHandler):
    def log_message(self, format, *args):
        pass  # keep stdout clean; .NET side doesn't need per-request logs

    def _send_json(self, status, payload):
        body = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        parsed = urlparse(self.path)
        qs = parse_qs(parsed.query)

        if parsed.path == "/health":
            self._send_json(200, {"ok": True, "languages": sorted(LANGUAGES.keys())})
            return

        if parsed.path == "/translate":
            text = qs.get("text", [""])[0]
            from_code = qs.get("from", ["en"])[0]
            to_code = qs.get("to", ["vi"])[0]

            if not text.strip():
                self._send_json(400, {"error": "empty text"})
                return

            from_lang = LANGUAGES.get(from_code)
            to_lang = LANGUAGES.get(to_code)
            if from_lang is None or to_lang is None:
                self._send_json(404, {"error": f"language pair not installed: {from_code} -> {to_code}"})
                return

            translation = from_lang.get_translation(to_lang)
            if translation is None:
                self._send_json(404, {"error": f"no installed route: {from_code} -> {to_code}"})
                return

            result = translation.translate(text)
            self._send_json(200, {"translated": result, "offline": True})
            return

        self._send_json(404, {"error": "not found"})


if __name__ == "__main__":
    print(f"Installed languages: {sorted(LANGUAGES.keys())}")
    server = HTTPServer(("127.0.0.1", PORT), Handler)
    print(f"HoverTranslate offline translation server on http://127.0.0.1:{PORT}")
    server.serve_forever()
