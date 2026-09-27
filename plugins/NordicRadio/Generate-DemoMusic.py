"""Synthesize original demo scores locally. Requires Python 3, NumPy and FFmpeg.

No samples, existing recordings, music services or game libraries are used.
The output directory is explicit and existing files are never overwritten.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import wave

import numpy as np

RATE = 44100


def frequency(midi: float) -> float:
    return 440.0 * 2.0 ** ((midi - 69.0) / 12.0)


def gate(t: np.ndarray, duration: float, attack: float, release: float) -> np.ndarray:
    attack_curve = np.minimum(1.0, t / attack)
    release_curve = np.minimum(1.0, np.maximum(0.0, duration - t) / release)
    return np.sin(attack_curve * math.pi / 2) ** 2 * np.sin(release_curve * math.pi / 2) ** 2


def voice(kind: str, note: int, duration: float, rng: np.random.Generator) -> np.ndarray:
    t = np.arange(max(1, int(RATE * duration)), dtype=np.float64) / RATE
    f = frequency(note)
    sound = np.zeros_like(t)
    if kind == 'pluck':
        # Additive string with independently decaying partials and a soft pick transient.
        detune = rng.uniform(-0.001, 0.001)
        for harmonic in range(1, 15):
            decay = (0.8 + 95.0 / f) / (1 + harmonic * 0.15)
            sound += np.sin(2 * math.pi * f * harmonic * (1 + detune) * t) * np.exp(-t / decay) / harmonic ** 1.45
        sound += rng.normal(0, 0.02, len(t)) * np.exp(-t * 85)
        sound *= gate(t, duration, 0.004, min(0.12, duration / 4))
    elif kind == 'flute':
        vibrato = 0.0025 * np.minimum(1, t / 0.35) * np.sin(2 * math.pi * 4.8 * t)
        phase = 2 * math.pi * f * np.cumsum(1 + vibrato) / RATE
        sound = np.sin(phase) + 0.12 * np.sin(2 * phase + 0.15) + 0.035 * np.sin(3 * phase)
        breath = np.convolve(rng.normal(0, 0.055, len(t)), np.ones(7) / 7, mode='same')
        sound = (sound + breath) * gate(t, duration, min(0.10, duration / 5), min(0.24, duration / 3))
    elif kind in ('bow', 'bass'):
        phase = 2 * math.pi * f * t + 0.07 * np.sin(2 * math.pi * 4.3 * t)
        for harmonic in range(1, 10):
            sound += np.sin(phase * harmonic + rng.uniform(-0.12, 0.12)) / harmonic ** 1.65
        sound += 0.10 * np.sin(phase * 1.002)
        sound *= gate(t, duration, min(0.65, duration / 3), min(0.9, duration / 3))
    elif kind == 'drum':
        phase = 2 * math.pi * (63 * t + (56 / 35) * (1 - np.exp(-35 * t)))
        sound = np.sin(phase) * np.exp(-t * 8)
        noise = np.convolve(rng.normal(0, 0.35, len(t)), np.ones(12) / 12, mode='same')
        sound = (sound + noise * np.exp(-t * 26)) * gate(t, duration, 0.003, 0.1)
    else:
        raise ValueError('Unknown voice: ' + kind)
    return sound.astype(np.float32)


def mix_note(bus, kind, note, start, duration, gain, pan, rng):
    signal = voice(kind, note, duration, rng)
    offset = max(0, int(start * RATE))
    count = min(len(signal), len(bus) - offset)
    if count <= 0:
        return
    pan = min(1, max(-1, pan))
    bus[offset:offset + count, 0] += signal[:count] * gain * math.sqrt((1 - pan) / 2)
    bus[offset:offset + count, 1] += signal[:count] * gain * math.sqrt((1 + pan) / 2)


def synthesize(score: dict, seed: int) -> np.ndarray:
    rng = np.random.default_rng(seed)
    beat = 60.0 / score['bpm']
    intro = 2 * beat
    length = intro + score['bars'] * 4 * beat + 3.0
    mix = np.zeros((int(length * RATE), 2), dtype=np.float32)
    style = score['style']
    calm = style == 'aurora'
    for event in score['melody']:
        kind = event.get('voice', 'bow' if calm else 'flute' if style == 'journey' else 'pluck')
        start = intro + event['beat'] * beat + rng.uniform(-0.009, 0.009)
        duration = event['duration'] * beat
        if kind == 'pluck':
            duration = max(duration, 1.7)
        mix_note(mix, kind, event['note'], start, duration,
                 0.21 * event.get('velocity', 0.7), -0.10, rng)
    for chord in score['chords']:
        start = intro + chord['beat'] * beat
        duration = chord['duration'] * beat
        notes = chord['notes']
        # Understated sustained harmony and a root one octave below the voicing.
        for index, note in enumerate(notes):
            mix_note(mix, 'bow', note, start, duration + 0.25,
                     0.035 if calm else 0.016, (index - 1) * 0.2, rng)
        bass = notes[0] - 12 if notes[0] >= 45 else notes[0]
        mix_note(mix, 'bass', bass, start, duration, 0.035, 0, rng)
        step = 1.0 if calm else 0.5
        count = int(chord['duration'] / step)
        pattern = (0, 1, 2, 1, 0, 2, 1, 2)
        for i in range(count):
            note = notes[pattern[i % len(pattern)]] + (12 if calm else 0)
            mix_note(mix, 'pluck', note, start + i * step * beat + rng.uniform(-0.012, 0.012),
                     1.8, (0.028 if calm else 0.049) * rng.uniform(0.85, 1.08), 0.25, rng)
    if not calm:
        for bar in range(score['bars'] - 1):
            for position, gain in ((0, 0.11), (2, 0.075)):
                mix_note(mix, 'drum', 36, intro + (bar * 4 + position) * beat,
                         0.8, gain * (1 if style == 'journey' else 0.6), 0, rng)
    # Quiet, irregular early reflections; the direct signal stays centred for 3D radio playback.
    dry = mix.copy()
    for delay, gain in ((0.071, 0.13), (0.113, 0.11), (0.179, 0.085), (0.271, 0.06), (0.419, 0.04)):
        for channel in range(2):
            offset = int((delay + channel * 0.009) * RATE)
            mix[offset:, channel] += dry[:-offset, 1 - channel] * gain
    mix = np.tanh(mix * 1.1) / 1.1
    fade_in = int(0.45 * RATE)
    fade_out = int(2.3 * RATE)
    mix[:fade_in] *= np.linspace(0, 1, fade_in, dtype=np.float32)[:, None]
    mix[-fade_out:] *= np.linspace(1, 0, fade_out, dtype=np.float32)[:, None] ** 2
    peak = float(np.max(np.abs(mix)))
    if peak <= 0 or not np.isfinite(mix).all():
        raise ValueError('Invalid synthesized audio')
    mix *= 0.78 / peak
    return mix


def validate_scores(scores):
    seen = set()
    for score in scores:
        slug = score['slug']
        if not slug or any(c not in 'abcdefghijklmnopqrstuvwxyz0123456789-_' for c in slug) or slug in seen:
            raise ValueError('Invalid or duplicate score slug')
        seen.add(slug)
        title = score['title']
        if not title or len(title) > 90 or any(ord(c) < 32 or c in '<>:"/\\|?*' for c in title) or title[-1] in ' .':
            raise ValueError('Title is not a safe filename')
        if not 40 <= score['bpm'] <= 180 or not 1 <= score['bars'] <= 64:
            raise ValueError('Score duration out of range')
        end = score['bars'] * 4
        for event in score['melody'] + score['chords']:
            if not 0 <= event['beat'] < end or not 0 < event['duration'] <= end - event['beat']:
                raise ValueError('Event outside score')
            for note in event.get('notes', [event.get('note')]):
                if not isinstance(note, int) or not 24 <= note <= 96:
                    raise ValueError('Invalid MIDI note')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True, help='Explicit output directory; existing files are preserved.')
    parser.add_argument('--ffmpeg', default='ffmpeg', help='FFmpeg executable path, or ffmpeg on PATH.')
    args = parser.parse_args()
    scores = json.loads(Path(__file__).with_name('demo-scores.json').read_text(encoding='utf-8-sig'))
    validate_scores(scores)
    executable = shutil.which(args.ffmpeg)
    if not executable:
        raise SystemExit('FFmpeg not found; specify its executable with --ffmpeg.')
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    targets = [output / ('%02d - %s.mp3' % (i + 1, score['title'])) for i, score in enumerate(scores)]
    if any(p.exists() for p in targets):
        raise SystemExit('Demo filenames already exist. Choose another output directory; no files overwritten.')
    for index, (score, target) in enumerate(zip(scores, targets)):
        samples = synthesize(score, 20260927 + index)
        with tempfile.TemporaryDirectory(prefix='NordicRadio-synthesis-') as temporary:
            wav = Path(temporary) / 'source.wav'
            with wave.open(str(wav), 'wb') as stream:
                stream.setnchannels(2)
                stream.setsampwidth(2)
                stream.setframerate(RATE)
                stream.writeframes(np.round(samples * 32767).astype('<i2').tobytes())
            handle, partial_name = tempfile.mkstemp(prefix='.NordicRadio-demo-', suffix='.part', dir=output)
            os.close(handle)
            partial = Path(partial_name)
            try:
                subprocess.run([executable, '-hide_banner', '-loglevel', 'error', '-y', '-i', str(wav),
                                '-af', 'loudnorm=I=-19:TP=-2:LRA=7', '-ar', str(RATE), '-ac', '2',
                                '-codec:a', 'libmp3lame', '-b:a', '128k', '-map_metadata', '-1',
                                '-metadata', 'title=' + score['title'], '-metadata', 'artist=NordicRadio original demo',
                                '-f', 'mp3', str(partial)], check=True)
                # A hard link publishes the complete file atomically and fails if another
                # process created this name meanwhile. Both entries are on the same volume.
                os.link(partial, target)
            finally:
                partial.unlink(missing_ok=True)
        print(json.dumps({'file': str(target), 'title': score['title'],
                          'seconds': round(len(samples) / RATE, 2), 'bytes': target.stat().st_size,
                          'sha256': hashlib.sha256(target.read_bytes()).hexdigest()}, ensure_ascii=True))


if __name__ == '__main__':
    main()
