#!/usr/bin/env python3
"""
Generates the synthesised push-to-talk sounds and the busy tone of EasyRadioLink (no third-party material).

    python tools/generate-sounds.py [output folder]

The default output folder is EasyRadioLink.Client/AudioEffects next to this script's parent folder. The output is
deterministic: running the script again produces byte-identical files.

Format: 16-bit PCM, 48000 Hz, mono (the only format the radio pipeline accepts, see CachedAudioEffect).

Loudness: every tone is scaled so that its RMS over the sounding parts (gaps excluded) is TARGET_RMS_DBFS, the
RMS of the existing RADIO_TRANS_START.wav / RADIO_TRANS_END.wav clicks (-24.3 dBFS over the file). A sine has a
crest factor of 3 dB, so the peaks end up near -21 dBFS - below the -11 dBFS peaks of the clicks, which are much
shorter and therefore sound quieter at the same RMS. Every note has raised-cosine fades so it starts and ends
without a click; frequency changes inside a note keep the phase continuous.

File names decide where the sounds appear (CachedAudioEffectProvider): RADIO_TRANS_START_*.wav are the push-to-talk
press / "someone starts talking" sounds, RADIO_TRANS_END_*.wav the release / "someone stops talking" sounds. The
friendly names ("Chirp", "Roger beep", ...) are defined in CachedAudioEffect.BuildDisplayName. BUSY_TONE.wav is not
selectable: only the sender hears it when push-to-talk is refused because another station uses the frequency (busy
channel lockout, "one speaker per frequency").
"""

import math
import os
import struct
import sys
import wave

SAMPLE_RATE = 48000
TARGET_RMS_DBFS = -24.0
FADE_IN_MS = 5.0
FADE_OUT_MS = 8.0
GLIDE_MS = 3.0  # frequency glide between two notes of one continuous tone


def ms_to_samples(ms):
    return int(round(ms * SAMPLE_RATE / 1000.0))


def tone(segments, fade_in_ms=FADE_IN_MS, fade_out_ms=FADE_OUT_MS):
    """
    One continuous tone. segments: list of (start_hz, end_hz, duration_ms); the frequency moves exponentially from
    start_hz to end_hz within a segment. The first GLIDE_MS of every following segment glide from the previous
    segment's end frequency to its start frequency. The phase is continuous.
    """
    freqs = []
    previous_end_hz = None
    for start_hz, end_hz, duration_ms in segments:
        count = ms_to_samples(duration_ms)
        glide = ms_to_samples(GLIDE_MS) if previous_end_hz is not None else 0
        for i in range(count):
            if i < glide:
                freqs.append(previous_end_hz + (start_hz - previous_end_hz) * (i + 1) / (glide + 1))
            else:
                position = (i - glide) / max(count - glide - 1, 1)
                freqs.append(start_hz * (end_hz / start_hz) ** position)
        previous_end_hz = end_hz

    samples = []
    phase = 0.0
    for freq in freqs:
        samples.append(math.sin(phase))
        phase += 2.0 * math.pi * freq / SAMPLE_RATE

    apply_fades(samples, fade_in_ms, fade_out_ms)
    return samples


def silence(duration_ms):
    return [0.0] * ms_to_samples(duration_ms)


def apply_fades(samples, fade_in_ms, fade_out_ms):
    fade_in = min(ms_to_samples(fade_in_ms), len(samples) // 2)
    fade_out = min(ms_to_samples(fade_out_ms), len(samples) // 2)
    for i in range(fade_in):
        samples[i] *= 0.5 - 0.5 * math.cos(math.pi * i / fade_in)
    for i in range(fade_out):
        samples[len(samples) - 1 - i] *= 0.5 - 0.5 * math.cos(math.pi * i / fade_out)


def sequence(*parts):
    """Concatenates tones and gaps. Returns (samples, mask) - mask marks the sounding samples for the RMS."""
    samples, mask = [], []
    for kind, part in parts:
        samples.extend(part)
        mask.extend([kind == "tone"] * len(part))
    return samples, mask


def normalise(samples, mask):
    sounding = [s for s, m in zip(samples, mask) if m]
    rms = math.sqrt(sum(s * s for s in sounding) / len(sounding))
    gain = 10.0 ** (TARGET_RMS_DBFS / 20.0) / rms
    return [s * gain for s in samples]


def write_wav(path, samples):
    frames = b"".join(struct.pack("<h", max(-32768, min(32767, int(round(s * 32767.0))))) for s in samples)
    with wave.open(path, "wb") as out:
        out.setnchannels(1)
        out.setsampwidth(2)
        out.setframerate(SAMPLE_RATE)
        out.writeframes(frames)


def sounds():
    return {
        # press: a short rising tone
        "RADIO_TRANS_START_CHIRP.wav": sequence(("tone", tone([(700.0, 1700.0, 120.0)]))),

        # press: a short 1 kHz blip
        "RADIO_TRANS_START_KEY_UP_BEEP.wav": sequence(("tone", tone([(1000.0, 1000.0, 80.0)]))),

        # release: the classic CB roger beep, two notes (high, low) in one tone
        "RADIO_TRANS_END_ROGER_BEEP.wav": sequence(
            ("tone", tone([(1200.0, 1200.0, 70.0), (800.0, 800.0, 90.0)]))),

        # release: two short beeps
        "RADIO_TRANS_END_DOUBLE_BEEP.wav": sequence(
            ("tone", tone([(1400.0, 1400.0, 55.0)])),
            ("gap", silence(50.0)),
            ("tone", tone([(1400.0, 1400.0, 55.0)]))),

        # release: three falling notes (E6, C6, G5)
        "RADIO_TRANS_END_THREE_TONE_BEEP.wav": sequence(
            ("tone", tone([(1318.5, 1318.5, 50.0)])),
            ("gap", silence(15.0)),
            ("tone", tone([(1046.5, 1046.5, 50.0)])),
            ("gap", silence(15.0)),
            ("tone", tone([(784.0, 784.0, 50.0)]))),

        # push-to-talk refused, the frequency is busy: two short low beeps (A4)
        "BUSY_TONE.wav": sequence(
            ("tone", tone([(440.0, 440.0, 80.0)])),
            ("gap", silence(60.0)),
            ("tone", tone([(440.0, 440.0, 80.0)]))),
    }


def levels(path):
    with wave.open(path, "rb") as source:
        data = source.readframes(source.getnframes())
        rate = source.getframerate()
    values = struct.unpack("<%dh" % (len(data) // 2), data)
    peak = max(abs(v) for v in values) / 32768.0
    rms = math.sqrt(sum(v * v for v in values) / len(values)) / 32768.0
    return len(values) * 1000.0 / rate, 20.0 * math.log10(peak), 20.0 * math.log10(rms)


def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    folder = sys.argv[1] if len(sys.argv) > 1 else os.path.join(root, "EasyRadioLink.Client", "AudioEffects")
    os.makedirs(folder, exist_ok=True)

    for name, (samples, mask) in sounds().items():
        write_wav(os.path.join(folder, name), normalise(samples, mask))

    print("%-36s %8s %10s %9s" % ("file", "length", "peak", "RMS"))
    for name in sorted(os.listdir(folder)):
        if (name.upper().startswith("RADIO_TRANS_") or name.upper() == "BUSY_TONE.WAV") and name.lower().endswith(".wav"):
            length, peak, rms = levels(os.path.join(folder, name))
            print("%-36s %6.0f ms %6.1f dBFS %5.1f dBFS" % (name, length, peak, rms))


if __name__ == "__main__":
    main()
