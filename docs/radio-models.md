# Radio models

A *radio model* describes how a radio sounds: the band-pass of the microphone and transmitter, distortion,
compression, the amount of static and the effect of encryption. Every radio has a model (the **Sound** selector in
the Radio Panel, the `model` field in `radios.json`). When you transmit, your model is sent along with your voice
and the receivers render your transmission through it, so a CB radio sounds like a CB radio on every PC.
(Listeners can turn this off with the *Use the sender's radio sound* setting; transmissions are then played with
`standard`, or with `digital` on digital (DIG) radios.)

## Built-in models

The built-in models are JSON files in the `RadioModels` folder next to `EasyRadioLink.exe` (in the folder you
extracted EasyRadioLink to). Do not edit them - an update replaces them.

| Key | Character |
|---|---|
| `standard` | Default military VHF/UHF sound. Also the fallback for unknown model names. |
| `cb` | 27 MHz citizens band, AM: narrow, "power mic" bark, lots of static. |
| `walkie` | PMR446 / walkie-talkie, FM: thin small-speaker sound, quiet background. |
| `airband` | VHF aviation band, AM. |
| `tactical` | Harsh, gritty military set. |
| `hf` | HF long range (SSB-like): very narrow, very noisy. |
| `vintage` | 1940s tube radio: honky, saturated. |
| `digital` | Clean, wide digital voice with almost no noise. |
| `hfnoise` | Internal: shapes the background noise below 30 MHz. Not selectable. |

## Your own models

Put your own model files into **`%AppData%\EasyRadioLink\RadioModels`** and restart EasyRadioLink.

- The file name (without `.json`) is the model key, e.g. `mycb.json` → `mycb`. Keys are lower case; use only
  `a-z` and `0-9`.
- A file with the same name as a built-in model (e.g. `cb.json`) replaces the built-in model.
- Other users only hear your model if **they** have a model with the same key. Unknown keys are played with
  `standard`. Share your JSON file with your group.
- Invalid files are skipped and reported in the client log.
- The easiest start is a copy of a built-in file.

## File format

```json
{
  "version": 1,
  "displayName": "My CB",
  "description": "Old AM CB with a hot power microphone.",
  "sortOrder": 100,
  "noiseGain": -22,
  "txEffect": { "$type": "chain", "effects": [ ... ] },
  "rxEffect": { "$type": "filters", "filters": [ ... ] },
  "encryptionEffect": { "$type": "cvsd" }
}
```

| Field | Required | Meaning |
|---|---|---|
| `version` | yes | Format version, always `1`. |
| `displayName` | no | Name shown in the Sound selector (default: the key). |
| `description` | no | Short description shown as tool tip. |
| `sortOrder` | no | Position in the Sound selector (lower first, default 1000; `standard` is always first). |
| `noiseGain` | yes | Level of the background static in dB, added to the frequency-dependent base level (lower frequencies are noisier). Above 30 MHz the static stays subtle. At or below 30 MHz (CB/HF) it is much stronger and rises steeply: `-22` is heavy (`cb`), `-33` is light (`standard`), `-60` is practically silent. Above about `-18` the static drowns the voice on CB/HF. |
| `txEffect` | yes | Effect applied to a transmission made with this model (microphone + transmitter sound). |
| `rxEffect` | yes | Effect applied to received audio (receiver + speaker). A gentle `highpass 270` / `lowpass 4500` is typical. |
| `encryptionEffect` | no | Applied after `txEffect` when the transmission is encrypted. Usually `cvsd`. |

Rules of the JSON reader:

- Property names are not case sensitive; camelCase as shown is recommended (`noiseGain`, `txEffect`, `makeUp`,
  `sidechainEffect`, ...).
- `"$type"` may appear anywhere in an effect or filter object (first is recommended). Its value (`chain`,
  `sidechainCompressor`, `highpass`, ...) must be spelled exactly as documented.
- Comments (`// ...`) and trailing commas are allowed.
- Unknown properties of the root object and of effects are ignored; unknown properties of **filters** make the
  file invalid.
- Frequencies are in Hz, gains and thresholds in dB, times in seconds.

## Effects

### `chain`

Runs effects one after another, top to bottom.

```json
{
  "$type": "chain",
  "effects": [
    { "$type": "saturation", "gain": 11, "threshold": -30 },
    { "$type": "gain", "gain": -3 }
  ]
}
```

### `filters`

Runs a list of filters (see [Filters](#filters)).

```json
{
  "$type": "filters",
  "filters": [
    { "$type": "highpass", "frequency": 300 },
    { "$type": "lowpass", "frequency": 3200, "q": 0.5 }
  ]
}
```

### `gain`

Amplifies (positive) or attenuates (negative) the signal by `gain` dB.

```json
{ "$type": "gain", "gain": 12 }
```

### `saturation`

Soft clipping / overdrive. `gain` (dB) drives the signal into the curve, `threshold` (dB) is where saturation starts.

```json
{ "$type": "saturation", "gain": 9, "threshold": -23 }
```

### `compressor`

Dynamic range compressor.

| Field | Meaning |
|---|---|
| `attack` | Attack time in seconds. |
| `release` | Release time in seconds. |
| `threshold` | Threshold in dB. |
| `ratio` | Compression ratio. |
| `makeUp` | Make-up gain in dB. |

```json
{ "$type": "compressor", "attack": 0.01, "makeUp": 6, "release": 0.2, "threshold": -33, "ratio": 1.18 }
```

### `sidechainCompressor`

A compressor whose gain reduction is driven by a filtered copy of the same signal (`sidechainEffect`, any effect).
Same fields as `compressor` plus `sidechainEffect`.

```json
{
  "$type": "sidechainCompressor",
  "attack": 0.01,
  "makeUp": 6,
  "release": 0.2,
  "threshold": -33,
  "ratio": 1.18,
  "sidechainEffect": {
    "$type": "filters",
    "filters": [ { "$type": "highpass", "frequency": 709 } ]
  }
}
```

### `cvsd`

Continuously variable slope delta modulation - the typical "digital secure voice" artefacts. Mostly used as
`encryptionEffect`.

```json
{ "$type": "cvsd" }
```

## Filters

A filter object has only these properties:

| Field | Meaning |
|---|---|
| `$type` | `lowpass`, `highpass` or `peak`. |
| `frequency` | Cut-off or centre frequency in Hz, > 0. |
| `q` | Optional for `lowpass` / `highpass`, required for `peak`. > 0. |
| `gain` | Only for `peak`: boost (positive) or cut (negative) in dB. |

- `lowpass` / `highpass` **without** `q` are gentle first-order filters (6 dB/octave).
- `lowpass` / `highpass` **with** `q` are steeper second-order (biquad) filters; a higher `q` gives a resonant peak at
  the cut-off frequency.
- `peak` is a peaking equaliser: it boosts or cuts a band around `frequency`; `q` sets the width.

## Complete example

`%AppData%\EasyRadioLink\RadioModels\mycb.json`:

```json
{
  "version": 1,
  "displayName": "My CB",
  "description": "Classic AM citizens band with power-mic bark and heavy static.",
  "txEffect": {
    "$type": "chain",
    "effects": [
      {
        "$type": "filters",
        "filters": [
          { "$type": "highpass", "frequency": 954, "q": 0.09 },
          { "$type": "peak", "frequency": 2302, "q": 0.63, "gain": 13 },
          { "$type": "lowpass", "frequency": 5165, "q": 0.4 }
        ]
      },
      { "$type": "saturation", "gain": 11, "threshold": -30 },
      {
        "$type": "sidechainCompressor",
        "attack": 0.01, "makeUp": 5, "release": 0.2, "threshold": -35, "ratio": 2.63,
        "sidechainEffect": { "$type": "filters", "filters": [ { "$type": "highpass", "frequency": 252 } ] }
      },
      {
        "$type": "filters",
        "filters": [
          { "$type": "highpass", "frequency": 829 },
          { "$type": "lowpass", "frequency": 3200, "q": 0.5 }
        ]
      },
      { "$type": "gain", "gain": 12 }
    ]
  },
  "rxEffect": {
    "$type": "filters",
    "filters": [
      { "$type": "highpass", "frequency": 270 },
      { "$type": "lowpass", "frequency": 4500 }
    ]
  },
  "encryptionEffect": { "$type": "cvsd" },
  "noiseGain": -22
}
```

Then select *My CB* in the Sound selector of a radio (or set `"model": "mycb"` in `radios-custom.json`).

## Tips

- Change one thing at a time and compare with the **Audio Preview** button on the Radio tab. The preview plays no
  static, so judge `noiseGain` with a real transmission (for example on a radio check frequency) on the band the
  model is meant for.
- Keep the final `gain` so that the model is about as loud as `standard`; very loud models clip.
- Radio voice lives between roughly 300 Hz and 3.5 kHz; narrower sounds more "radio", wider sounds cleaner.
- The static level also depends on the frequency (lower frequencies are noisier) and on the listener's noise
  settings, so test on the band the model is meant for.
