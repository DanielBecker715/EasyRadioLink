# Radio models

Each JSON file in this folder is a radio model: how a transmission made with that radio sounds on the receiving
side. The file name without `.json` is the model key (`cb.json` -> `cb`). The band plan picks the model from the
frequency (CB -> `cb`, PMR and FM -> `walkie`, AIR -> `airband`, ...). Keys are lower case letters and digits only
(max 32 characters).

- Built-in models live here, next to `EasyRadioLink.exe`. Do not edit them - updates overwrite them.
- Your own models go into `%AppData%\EasyRadioLink\RadioModels`. A file with the same name as a built-in model
  replaces it. Restart EasyRadioLink after adding or changing a file.
- `standard` is the default and the fallback for unknown keys. `hfnoise` is internal (it shapes the static below
  30 MHz) and is not offered as preview sound.
- Invalid files are skipped and reported in the client log.
- Other users only hear your model if they have a model with the same key.

## File format

```json
{
  "version": 1,
  "displayName": "My CB",
  "description": "Old AM CB with a hot power microphone.",
  "sortOrder": 100,
  "txEffect": { "$type": "chain", "effects": [ ... ] },
  "rxEffect": { "$type": "filters", "filters": [ ... ] },
  "encryptionEffect": { "$type": "cvsd" },
  "noiseGain": -8
}
```

| Field | Required | Meaning |
|---|---|---|
| `version` | yes | Always `1`. |
| `displayName` | no | Name shown in the Preview sound list (default: the key). |
| `description` | no | Short description (tool tip). |
| `sortOrder` | no | Position in the Preview sound list (lower first, default 1000; `standard` is always first). |
| `txEffect` | yes | Effect applied to transmissions made with this model (microphone + transmitter). |
| `rxEffect` | yes | Receive filter (receiver + speaker). A gentle highpass 270 / lowpass 4500 is typical. |
| `encryptionEffect` | no | Applied after `txEffect` to encrypted transmissions (only older versions encrypt), usually `cvsd`. |
| `noiseGain` | yes | Static level in dB, added to the frequency dependent base level (lower frequencies are noisier). Above 30 MHz the static is always subtle. Shipped values: `-33` (`standard`, subtle), `-24`/`-23` (`airband`, `tactical`, `walkie`), `-8` (`cb`), `-12` (`hf`, heavy). Every +6 dB doubles the static. |

Property names are camelCase (not case sensitive). `"$type"` may appear anywhere in an object. Comments (`// ...`)
and trailing commas are allowed. Unknown properties of filters
make the file invalid. Frequencies are in Hz, gains and thresholds in dB, times in seconds.

## Effects

| `$type` | Fields | Meaning |
|---|---|---|
| `chain` | `effects` | Runs the listed effects one after another, top to bottom. |
| `filters` | `filters` | Runs the listed filters (see below). |
| `gain` | `gain` | Amplifies (positive) or attenuates (negative) by `gain` dB. |
| `saturation` | `gain`, `threshold` | Soft clipping / overdrive. |
| `compressor` | `attack`, `release`, `threshold`, `ratio`, `makeUp` | Dynamic range compressor (`ratio` 4 = 4:1). |
| `sidechainCompressor` | as `compressor` + `sidechainEffect` | Compressor driven by a filtered copy of the signal. |
| `cvsd` | - | CVSD vocoder artefacts ("digital secure voice"), mostly used as `encryptionEffect`. |

## Filters

| Field | Meaning |
|---|---|
| `$type` | `lowpass`, `highpass` or `peak`. |
| `frequency` | Cut-off / centre frequency in Hz (> 0). |
| `q` | Optional for `lowpass`/`highpass` (without `q`: gentle first-order filter), required for `peak`. |
| `gain` | `peak` only: boost or cut in dB. |

Example:

```json
{
  "$type": "filters",
  "filters": [
    { "$type": "highpass", "frequency": 300 },
    { "$type": "peak", "frequency": 2300, "q": 0.6, "gain": 8 },
    { "$type": "lowpass", "frequency": 3200, "q": 0.5 }
  ]
}
```

Tip: start from a copy of a built-in model, change one thing at a time and compare with the Audio Preview button.
The preview plays no static, so judge `noiseGain` with a real transmission on the band the model is meant for.
Keep the final `gain` so the model is about as loud as `standard`.
