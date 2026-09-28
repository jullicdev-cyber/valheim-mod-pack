"""Create a deterministic MP3 regression fixture: 220 / 880 / 1760 Hz, 6 s each.
Usage: python Generate-SeekProbe.py /path/to/ffmpeg /path/to/output.mp3
"""
import math
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import wave

ffmpeg, output = sys.argv[1:]
Path(output).parent.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(prefix="nordic-seek-") as temporary:
    wav = Path(temporary) / "tones.wav"
    with wave.open(str(wav), "wb") as writer:
        writer.setnchannels(1)
        writer.setsampwidth(2)
        writer.setframerate(44100)
        for frequency in (220, 880, 1760):
            writer.writeframes(b"".join(struct.pack("<h", int(6000 * math.sin(2 * math.pi * frequency * i / 44100))) for i in range(6 * 44100)))
    subprocess.run([ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-i", str(wav), "-codec:a", "libmp3lame", "-b:a", "128k", output], check=True)
print(output)
