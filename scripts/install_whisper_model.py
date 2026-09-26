"""One-time setup: pre-download the faster-whisper tiny + base models so
Model Manager can report an accurate status without needing to start
stt_server.py first. Run once: python install_whisper_model.py
"""
from faster_whisper import WhisperModel

for size in ["tiny", "base"]:
    print(f"Downloading Whisper '{size}'...")
    # Constructing WhisperModel triggers the Hugging Face download + local
    # cache (~/.cache/huggingface) if not already present, then loads it once
    # just to confirm it works before discarding.
    WhisperModel(size, device="cpu", compute_type="int8")
    print(f"Installed Whisper '{size}'")

print("Done.")
