"""Converts a WAV file into the format EasyRadioLink loads for radio sounds and matches its loudness.

EasyRadioLink only loads 16-bit PCM, 48000 Hz, mono WAV files from EasyRadioLink.Client/AudioEffects (anything else
is ignored). This script converts any 8/16/24/32-bit PCM WAV (mono or stereo, any sample rate) into that format,
resamples with a windowed-sinc filter, adds 2 ms fades so the sound never starts or stops with a click, and scales it
to the same loudness as the built-in beeps (-24 dBFS RMS over the audible part).

    python tools/convert-sound.py tools/sound-sources/MySound.wav EasyRadioLink.Client/AudioEffects/MySound.wav

Options: --rms <dBFS> to choose another loudness, --keep-level to keep the original level.
Needs numpy (pip install numpy).
"""
import argparse
import wave

import numpy as np

TARGET_RATE = 48000
DEFAULT_RMS_DBFS = -24.0
FADE_SECONDS = 0.002


def read_wav(path):
    with wave.open(path, "rb") as w:
        channels, width, rate, frames = w.getnchannels(), w.getsampwidth(), w.getframerate(), w.getnframes()
        raw = w.readframes(frames)
    if width == 1:
        data = (np.frombuffer(raw, dtype=np.uint8).astype(np.float64) - 128) / 128
    elif width == 2:
        data = np.frombuffer(raw, dtype="<i2").astype(np.float64) / 32768
    elif width == 3:
        b = np.frombuffer(raw, dtype=np.uint8).reshape(-1, 3).astype(np.int32)
        v = b[:, 0] | (b[:, 1] << 8) | (b[:, 2] << 16)
        data = np.where(v >= 1 << 23, v - (1 << 24), v).astype(np.float64) / (1 << 23)
    elif width == 4:
        data = np.frombuffer(raw, dtype="<i4").astype(np.float64) / 2 ** 31
    else:
        raise SystemExit(f"{path}: unsupported sample width {width}")
    return data.reshape(-1, channels).mean(axis=1), rate


def resample(x, src, dst, half_taps=48, beta=8.6):
    """Windowed-sinc (Kaiser) interpolation with the cutoff just below the lower Nyquist frequency."""
    if src == dst:
        return x.copy()
    cutoff = 0.5 * min(src, dst) / src * 0.95
    n_out = int(round(len(x) * dst / src))
    t = np.arange(n_out) * src / dst
    k = np.arange(-half_taps + 1, half_taps + 1)
    idx = np.floor(t).astype(int)[:, None] + k[None, :]
    h = 2 * cutoff * np.sinc(2 * cutoff * (t[:, None] - idx)) * np.kaiser(2 * half_taps, beta)[None, :]
    valid = (idx >= 0) & (idx < len(x))
    return np.sum(np.where(valid, x[np.clip(idx, 0, len(x) - 1)], 0.0) * h, axis=1)


def audible_rms(x, floor_dbfs=-60.0):
    """RMS between the first and the last sample above the floor (leading/trailing silence doesn't count)."""
    loud = np.where(np.abs(x) > 10 ** (floor_dbfs / 20))[0]
    part = x[loud[0]:loud[-1] + 1] if len(loud) else x
    return np.sqrt(np.mean(part ** 2)) if len(part) else 0.0


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("input")
    parser.add_argument("output")
    parser.add_argument("--rms", type=float, default=DEFAULT_RMS_DBFS, help="target loudness in dBFS RMS")
    parser.add_argument("--keep-level", action="store_true", help="do not change the loudness")
    args = parser.parse_args()

    mono, rate = read_wav(args.input)
    out = resample(mono, rate, TARGET_RATE)

    fade = int(FADE_SECONDS * TARGET_RATE)
    if len(out) > 2 * fade:
        ramp = 0.5 - 0.5 * np.cos(np.linspace(0, np.pi, fade))
        out[:fade] *= ramp
        out[-fade:] *= ramp[::-1]

    before = audible_rms(out)
    if not args.keep_level and before > 0:
        out *= 10 ** (args.rms / 20) / before
    peak = np.max(np.abs(out)) if len(out) else 0.0
    if peak > 0.98:  # never clip - rather be a little quieter than the target
        out *= 0.98 / peak

    pcm = np.clip(np.round(out * 32767), -32768, 32767).astype("<i2")
    with wave.open(args.output, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(TARGET_RATE)
        w.writeframes(pcm.tobytes())

    after = audible_rms(pcm / 32768)
    print(f"{args.output}: {len(pcm) / TARGET_RATE * 1000:.0f} ms, 48 kHz mono 16-bit, "
          f"loudness {20 * np.log10(before + 1e-12):.1f} -> {20 * np.log10(after + 1e-12):.1f} dBFS RMS, "
          f"peak {20 * np.log10(np.max(np.abs(pcm)) / 32768 + 1e-12):.1f} dBFS")


if __name__ == "__main__":
    main()
