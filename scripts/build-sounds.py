#!/usr/bin/env python3
"""Synthesize the desktop pet's original, royalty-free cute-monster sounds.

All sounds are generated deterministically from sine/noise primitives.  They are
short, mono, 44.1 kHz, 16-bit PCM WAV files so Windows' MediaPlayer can play
them without any codec pack.

Usage:
  python scripts/build-sounds.py [output-dir]
"""

from __future__ import annotations

import math
import os
import struct
import sys
import wave

import numpy as np

SR = 44100


def envelope(n: int, attack: float = 0.008, release: float = 0.12) -> np.ndarray:
    env = np.ones(n, dtype=np.float64)
    a = max(1, int(attack * SR))
    r = max(1, int(release * SR))
    env[:a] = np.linspace(0.0, 1.0, a) ** 0.6
    env[-r:] = np.linspace(1.0, 0.0, r) ** 1.6
    return env


def sine_sweep(duration: float, start: float, end: float, vibrato: float = 0.0, vibrato_hz: float = 0.0) -> np.ndarray:
    n = int(duration * SR)
    t = np.arange(n) / SR
    freq = np.linspace(start, end, n)
    if vibrato > 0:
        freq = freq * (1.0 + vibrato * np.sin(2 * math.pi * vibrato_hz * t))
    phase = 2 * math.pi * np.cumsum(freq) / SR
    return np.sin(phase)


def chirp(duration: float, start: float, end: float, harmonic: float = 0.35) -> np.ndarray:
    fundamental = sine_sweep(duration, start, end)
    upper = sine_sweep(duration, start * 2.01, end * 2.01)
    return (fundamental + harmonic * upper) / (1.0 + harmonic)


def soft_noise(duration: float) -> np.ndarray:
    n = int(duration * SR)
    noise = np.random.default_rng(7).normal(0.0, 1.0, n)
    # Very light smoothing: a low-passed noise burst sounds like a chew, not a
    # hiss.  A 5-sample moving average is enough at 44.1 kHz.
    kernel = np.ones(5) / 5.0
    return np.convolve(noise, kernel, mode="same")


def mix(*parts: np.ndarray) -> np.ndarray:
    length = max(len(part) for part in parts)
    out = np.zeros(length, dtype=np.float64)
    for part in parts:
        out[: len(part)] += part
    return out


def write_wav(path: str, samples: np.ndarray) -> None:
    samples = np.asarray(samples, dtype=np.float64)
    peak = float(np.max(np.abs(samples))) if samples.size else 0.0
    if peak > 0:
        samples = samples / peak * 0.82  # about -1.7 dBFS
    pcm = np.clip(samples * 32767.0, -32768, 32767).astype("<i2")
    with wave.open(path, "wb") as handle:
        handle.setnchannels(1)
        handle.setsampwidth(2)
        handle.setframerate(SR)
        handle.writeframes(pcm.tobytes())


def complete() -> np.ndarray:
    """A bright two-note 'yay!' with a tiny springy tail."""
    first = chirp(0.14, 620, 880, 0.25) * envelope(int(0.14 * SR), 0.004, 0.05)
    gap = np.zeros(int(0.03 * SR))
    second = chirp(0.22, 880, 1320, 0.3) * envelope(int(0.22 * SR), 0.004, 0.16)
    sparkle = sine_sweep(0.12, 1760, 2200, 0.02, 18) * envelope(int(0.12 * SR), 0.002, 0.1) * 0.18
    body = mix(first, gap, second)
    body[: len(sparkle)] += sparkle
    return body


def eat() -> np.ndarray:
    """Three soft 'nom' bites plus a satisfied low coo."""
    out = np.zeros(int(0.62 * SR))
    for index, offset in enumerate((0.0, 0.155, 0.31)):
        n = int(0.12 * SR)
        bite = soft_noise(0.12) * 0.45 + sine_sweep(0.12, 180 - index * 15, 110 - index * 10) * 0.55
        bite *= envelope(n, 0.003, 0.09)
        start = int(offset * SR)
        out[start : start + n] += bite
    coo = chirp(0.2, 260, 190, 0.2) * envelope(int(0.2 * SR), 0.01, 0.15) * 0.45
    out[-len(coo) :] += coo
    return out


def click() -> np.ndarray:
    """A short question-mark 'boop' that invites another poke."""
    out = chirp(0.11, 720, 1080, 0.3) * envelope(int(0.11 * SR), 0.002, 0.09)
    out *= 0.85
    return out


def pickup() -> np.ndarray:
    """A rubbery upward boing for the long-press lift."""
    duration = 0.28
    n = int(duration * SR)
    tone = sine_sweep(duration, 240, 560, 0.08, 12)
    bounce = sine_sweep(duration, 480, 1120, 0.03, 18) * 0.2
    return (tone + bounce) * envelope(n, 0.004, 0.18)


def error() -> np.ndarray:
    """A gentle descending 'awww' — never a harsh buzzer."""
    out = chirp(0.34, 520, 260, 0.22) * envelope(int(0.34 * SR), 0.01, 0.22)
    return out * 0.8


def drop() -> np.ndarray:
    """A soft landing thump after a drag."""
    n = int(0.16 * SR)
    thump = sine_sweep(0.16, 150, 70) * envelope(n, 0.002, 0.12)
    dust = soft_noise(0.16) * 0.12 * envelope(n, 0.001, 0.12)
    return thump + dust


def main() -> int:
    here = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    out_dir = sys.argv[1] if len(sys.argv) > 1 else os.path.join(here, "pet", "assets", "sounds")
    os.makedirs(out_dir, exist_ok=True)
    sounds = {
        "complete.wav": complete(),
        "eat.wav": eat(),
        "click.wav": click(),
        "pickup.wav": pickup(),
        "error.wav": error(),
        "drop.wav": drop(),
    }
    for name, samples in sounds.items():
        path = os.path.join(out_dir, name)
        write_wav(path, samples)
        print(f"{name:12s} {os.path.getsize(path):7d} bytes")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
