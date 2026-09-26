# EasyRadioLink

EasyRadioLink is a standalone digital radio for Windows. Users connect to an EasyRadioLink server, tune their radios
to a frequency and talk to everybody on that frequency, with the sound of real radios: band-pass filters, static,
squelch tails, key clicks, tones, half-duplex behaviour, interference and scrambled encryption.

It needs no game or other software: a group of friends, a club, a flight-sim squadron, an airsoft or role-play team
simply runs a server and connects.

## Features

**Radios**
- Up to 10 radios per user, each with its own frequency, modulation (AM, FM or digital), volume, preset channels,
  guard receiver and, where supported, encryption (keys 1-252).
- Transmit on the selected radio or on several radios at once; receive on all radios at the same time.
- Radio Panel window with frequency step buttons and direct entry, channel list, guard toggle, encryption,
  volume, TX/RX indicators with the transmitter's name and the number of users on the frequency.
- Default radio set: CB (27 MHz AM), PMR446 (FM), VHF airband, UHF tactical, HF long range and a clean digital radio.
  Your own layout via `radios-custom.json`; the server can push a common layout.
- Radios remember their frequencies and settings between sessions.

**Radio sound**
- Per-radio sound models (CB, walkie-talkie, airband, tactical, HF, vintage tube, digital, ...). Receivers hear you
  through *your* radio model. Add your own models as JSON effect chains ([docs/radio-models.md](docs/radio-models.md)).
- Frequency-dependent static and HF noise, squelch tail, TX/RX clicks, FM tone, encryption tones and CVSD scramble.
- Optional background sound (jet, prop or helicopter) that the other stations hear behind your voice.

**Controls**
- Push-to-talk and radio selection on keyboard, mouse, joysticks/HOTAS (DirectInput) and gamepads (XInput).
- Radio Panel toggle hotkey; free choice of microphone and speaker devices, plus an optional "mic output" device that
  carries your own radio-processed voice (for streaming or recording software).
- Optional MP3 recording of radio traffic.

**Server**
- Server with a window for Windows, plus a command-line server for Windows and Linux (x64).
- Optional server password, radio check (echo) frequencies, clean frequencies without radio effects,
  half-duplex radios, interference of simultaneous transmissions, encryption rules.
- Server preset channels and server radio layout, mute/kick/ban, client list export, transmission log,
  UPnP port forwarding and an optional HTTP admin API.

## Download

The [GitHub releases](https://github.com/DanielBecker715/EasyRadioLink/releases) offer three downloads. Nothing
needs to be installed: extract the archive and run the program.

| Download | Who needs it | Contents |
|---|---|---|
| `EasyRadioLink-Client-<version>.zip` | Everybody who wants to talk | `EasyRadioLink.exe` (Windows) |
| `EasyRadioLink-Server-<version>-Windows.zip` | Whoever hosts a server on Windows | `Server\EasyRadioLink.Server.exe` (server with window), `CommandLine\EasyRadioLink.Server.Cli.exe` (command-line server) |
| `EasyRadioLink-Server-<version>-Linux.tar.gz` | Whoever hosts a server on Linux (x64) | `EasyRadioLink.Server.Cli` (command-line server) |

Every download also contains `README.txt` (the end-user guide, [packaging/README.txt](packaging/README.txt)),
`LICENSE.txt` and `THIRD-PARTY-NOTICES.txt`.

**Requirements**

- Client and server with window: Windows 10/11 x64 and the
  [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (x64).
- Client: the Microsoft Visual C++ Redistributable (x64), which the audio libraries (`opus.dll`, `WebRtcVad.dll`)
  need. Most PCs already have it; otherwise install it from
  [aka.ms/vs/17/release/vc_redist.x64.exe](https://aka.ms/vs/17/release/vc_redist.x64.exe).
- Command-line servers: self-contained, no .NET installation needed. Linux: x64 with glibc (e.g. Debian, Ubuntu,
  Fedora).

## Quick start

### Users

1. Download `EasyRadioLink-Client-<version>.zip`, extract it completely and start `EasyRadioLink.exe`.
   (Windows SmartScreen may warn about the unsigned program: *More info*, then *Run anyway*.)
2. **Radio** tab: choose microphone and speakers. **Controls** tab: assign *Push-To-Talk (PTT)*.
3. **Radio** tab: enter your name, the server address (`host:5010`) and the password if the server has one,
   then **Connect**.
4. The Radio Panel opens. Tune to the same frequency and modulation as the others, hold PTT and talk.

To update, extract the new version and use it instead of the old folder; the settings live in
`%AppData%\EasyRadioLink` and are kept.

Radio check: with default server settings, transmissions on **27.405 MHz** (CB channel 40) and **446.19375 MHz**
(PMR channel 16) are echoed back to you.

### Hosting a server (Windows, with window)

1. Download `EasyRadioLink-Server-<version>-Windows.zip` and extract it into a folder where you may write files
   (for example `C:\EasyRadioLink-Server`; the server keeps its settings and logs next to the program).
2. Start `Server\EasyRadioLink.Server.exe`. It starts listening immediately.
3. Open **TCP and UDP port 5010** in the Windows firewall and forward both in your router (the server also tries UPnP).
4. Optionally set a **server password**. Leave it empty for an open server.
5. Users connect to `<your public IP>:5010`.

The password is compared on the server and a wrong password is rejected, but it is sent **unencrypted** over TCP.
It keeps strangers out; do not reuse a valuable password.

### Command-line server (Windows and Linux)

Windows (`CommandLine` folder of the Windows server download):

```
cd C:\EasyRadioLink-Server\CommandLine
EasyRadioLink.Server.Cli.exe --port=5010 --password=secret
```

Linux:

```
tar xzf EasyRadioLink-Server-<version>-Linux.tar.gz
cd EasyRadioLink-Server-<version>
./EasyRadioLink.Server.Cli --port=5010 --password=secret --half-duplex=true
```

The command-line servers are self-contained (no .NET installation needed). Stop them with `Ctrl+C` (SIGTERM is
handled too, so they run fine under systemd). Every option that is given is also saved to `server.cfg`, so later
starts without options keep the settings. On/off options take a value: `--half-duplex=true`, `--upnp=false`.

| Option | server.cfg key | Default | Meaning |
|---|---|---|---|
| `-c`, `--cfg` | – | `server.cfg` next to the program | Configuration file. All other server files are kept in the same folder (see below). |
| `-p`, `--port` | `SERVER_PORT` | `5010` | TCP and UDP port. |
| `--bind-ip` | `SERVER_IP` | `0.0.0.0` | IP address to listen on (all interfaces). |
| `--upnp` | `UPNP_ENABLED` | `true` | Open the port on the router automatically (UPnP / NAT-PMP). |
| `--password` | `SERVER_PASSWORD` | empty | Server password. `--password ""` (with a space, not `=`) makes the server open again. |
| `--test-frequencies` | `TEST_FREQUENCIES` | `27.405,446.19375` | Radio check (echo) frequencies in MHz, comma separated. |
| `--clean-frequencies` | `CLEAN_FREQUENCIES` | empty | Frequencies in MHz that are played without radio effects. |
| `--half-duplex` | `IRL_RADIO_TX` | `false` | Half-duplex radios: a radio cannot receive while it transmits. |
| `--radio-interference` | `IRL_RADIO_RX_INTERFERENCE` | `false` | Simultaneous transmissions on one frequency interfere. |
| `--allow-encryption` | `ALLOW_RADIO_ENCRYPTION` | `true` | Radios that support it may encrypt (scramble). |
| `--strict-encryption` | `STRICT_RADIO_ENCRYPTION` | `false` | Encrypted radios only understand transmissions with the same key; clear transmissions are scrambled too. |
| `--show-tuned-count` | `SHOW_TUNED_COUNT` | `true` | Users see how many people are tuned to each frequency. |
| `--show-transmitter-name` | `SHOW_TRANSMITTER_NAME` | `false` | Users see who is transmitting. |
| `--server-presets` | `SERVER_PRESETS_ENABLED` | `false` | Offer the preset channels from `Presets/*.txt` to the clients. |
| `--server-radio-layout` | `SERVER_RADIO_PRESET_ENABLED` | `false` | Offer the radio layout from `server-radios.json` to the clients. |
| `--client-export` | `CLIENT_EXPORT_ENABLED` | `false` | Write the connected clients to a JSON file every 5 seconds. |
| `--client-export-path` | `CLIENT_EXPORT_FILE_PATH` | `clients-list.json` | Full path of that file (default: next to `server.cfg`). |
| `--transmission-log` | `TRANSMISSION_LOG_ENABLED` | `false` | Log every transmission to a daily CSV file. |
| `--transmission-log-retention` | `TRANSMISSION_LOG_RETENTION` | `2` | Days of transmission logs to keep. |
| `--http-server` | `HTTP_SERVER_ENABLED` | `false` | Enable the HTTP admin API. |
| `--http-port` | `HTTP_SERVER_PORT` | `8080` | Port of the HTTP admin API. |
| `--http-address` | `HTTP_SERVER_ADDRESS` | `localhost` | Host name / address the HTTP admin API listens on. |
| `--console-logs` | – | `true` | Print connects/disconnects to the console; `--console-logs=false` turns it off (not saved to `server.cfg`). |

`EasyRadioLink.Server.Cli --help` always shows the options of your version.

### Server configuration

By default `server.cfg` is kept next to the server program, whatever the working directory is; each server
(`Server\`, `CommandLine\`, Linux) has its own. All other server files are kept in the folder of `server.cfg`. The
server with window accepts `-cfg=<path to server.cfg>` as well, so both servers can share one configuration folder.
The server needs write access to that folder (a plain copy below `C:\Program Files` does not have it), otherwise
its settings cannot be saved.

| File | Location | Purpose |
|---|---|---|
| `server.cfg` | next to the server program, or the `--cfg` / `-cfg` path | All settings. `[General Settings]` are sent to every client; `[Server Settings]` (port, bind address, UPnP, HTTP API, password) never leave the server. |
| `Presets/<radio>.txt` | next to `server.cfg` | Server preset channels (same format as the client preset files, see below). |
| `server-radios.json` | next to `server.cfg` | Server radio layout (same format as `radios.json`), used when `SERVER_RADIO_PRESET_ENABLED` is on and the user allows server layouts. |
| `banned.txt` | next to `server.cfg` | Banned IP addresses, one per line. |
| `serverlog.txt`, `*-transmissionlog.csv` | next to `server.cfg` | Server log and transmission logs. |
| `clients-list.json` | next to `server.cfg` | Client export (when enabled and no other path is set). |

To update a server, extract the new version and copy `server.cfg` (plus `Presets/`, `server-radios.json` and
`banned.txt` if used) from the old folder, or keep these files in a separate folder and start the server with `--cfg`.

Other `server.cfg` keys: `HTTP_SERVER_API_KEY` (generated on first start). Frequency lists always use a dot as
decimal separator, independent of the Windows language.

**HTTP admin API** (when `HTTP_SERVER_ENABLED = true`; send the key from `HTTP_SERVER_API_KEY` in the `X-API-KEY`
header):

| Request | Action |
|---|---|
| `GET /clients` | Connected clients as JSON. |
| `POST /client/kick/guid/<guid>` / `POST /client/kick/name/<name>` | Kick a client. |
| `POST /client/ban/guid/<guid>` / `POST /client/ban/name/<name>` | Ban a client's IP address (`banned.txt`) and disconnect it. |

## Radios, models and presets

### Radio layout: `radios.json` and `radios-custom.json`

The radios are defined by `radios.json` next to `EasyRadioLink.exe`. To change them, copy it to the settings folder
as `radios-custom.json` (`%AppData%\EasyRadioLink\radios-custom.json` by default) and edit the copy. Load order:
server radio layout (if the server provides one and the user allows it) → `radios-custom.json` → `radios.json`.

The file is a JSON array with either 11 entries (entry 0 is reserved and always disabled, entries 1-10 are the user
radios) or just the user radios (up to 10, the first one enabled). Frequencies are in **Hz**. Comments and trailing
commas are allowed, names are case-insensitive.

```json
[
  { "name": "Reserved", "modulation": 3 },
  { "name": "CB", "model": "cb", "modulation": 0, "freq": 27185000, "freqMin": 26965000, "freqMax": 27405000 },
  { "name": "PMR446", "model": "walkie", "modulation": 1, "freq": 446006250, "freqMin": 446006250,
    "freqMax": 446193750 },
  { "name": "UHF Tactical", "model": "tactical", "modulation": 0, "freq": 251000000, "freqMin": 225000000,
    "freqMax": 400000000, "guardFreq": 243000000, "encCapable": true, "encKey": 12 }
]
```

| Field | Default | Meaning |
|---|---|---|
| `name` | `Radio <n>` | Radio name, also used to find its preset channel file. |
| `model` | empty (= `standard`) | Radio model key, see below. |
| `modulation` | `3` | `0` = AM, `1` = FM, `3` = disabled, `5` = digital (clean, no static). |
| `freq` | – | Start frequency in Hz. |
| `freqMin`, `freqMax` | = `freq` | Allowed range in Hz. Without a range the radio is fixed to `freq`. |
| `guardFreq` | `0` | Guard receiver frequency in Hz (`0` = no guard receiver). |
| `encCapable` | `false` | The radio can encrypt. |
| `enc`, `encKey` | `false`, `1` | Encryption on at start, and the key (1-252). |
| `channel` | `-1` | Preset channel selected at start (1-based, `-1` = none). |
| `rxOnly` | `false` | Receive-only radio. |
| `simul` | `false` | Transmit together with the selected radio. |

Invalid values are corrected when the file is loaded (frequency clamped into the range, unknown modulation =
disabled, missing entries = disabled).

### Radio models

Radio models are JSON effect chains that define how a radio sounds. Built-in models live in `RadioModels\*.json`
next to `EasyRadioLink.exe`; your own models go to `%AppData%\EasyRadioLink\RadioModels\*.json` (the file name is
the model key; a file with a built-in name replaces that model). Select a model per radio in the Radio Panel or with the
`model` field. The format (`chain`, `filters`, `gain`, `saturation`, `compressor`, `sidechainCompressor`, `cvsd`;
`lowpass`, `highpass`, `peak` filters) is documented in [docs/radio-models.md](docs/radio-models.md).

### Preset channels

Preset channel files are plain text files named after the radio (`CB.txt`, `PMR446.txt`, ...; letters and digits of
the radio name, case-insensitive) in `%AppData%\EasyRadioLink\Presets` or the presets folder chosen on the Radio tab.
One channel per line, `Name|Frequency in MHz` or just the frequency:

```
Channel 9|27.065
Channel 19|27.185
446.00625
```

A server can provide the same files for everybody in its `Presets` folder (`SERVER_PRESETS_ENABLED`).

## Files and folders

| Location | Contents |
|---|---|
| `%AppData%\EasyRadioLink` | Settings folder: client settings (`global.cfg`, profile `*.cfg`), `FavouriteServers.csv`, `radios-custom.json`, `radio-state.json` |
| `%AppData%\EasyRadioLink\Presets` | Preset channel files |
| `%AppData%\EasyRadioLink\RadioModels` | Custom radio models |
| `%AppData%\EasyRadioLink\Logs` | Client log (`clientlog.txt`, previous run in `clientlog.old.txt`); linked on the About tab |
| `Documents\EasyRadioLink\Recordings` | Recordings |
| Client folder (where `EasyRadioLink.exe` is) | Client program, `radios.json`, built-in radio models and sounds |
| Server folder (where `server.cfg` is) | `server.cfg`, `Presets`, `server-radios.json`, `banned.txt`, server logs |

The client accepts `-cfg=<folder>` to use another settings folder: everything in the first row (`global.cfg`,
profiles, favourites, `radios-custom.json`, `radio-state.json`) then lives there, while logs, presets, custom radio
models and recordings stay in their default places. `-host=<address:port>`, `-name=<name>` and
`-password=<password>` pre-fill the connection.

## Building from source

Requirements: Windows, [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (see `global.json`).

```
dotnet restore EasyRadioLink.sln
dotnet build EasyRadioLink.sln -c Release
dotnet test EasyRadioLink.Tests/EasyRadioLink.Tests.csproj -c Release
```

| Project | Output |
|---|---|
| `EasyRadioLink.Client` | `EasyRadioLink.exe` - the client (WPF) |
| `EasyRadioLink.Server` | `EasyRadioLink.Server.exe` - server with window (WPF) |
| `EasyRadioLink.Server.Cli` | `EasyRadioLink.Server.Cli(.exe)` - command-line server (Windows and Linux) |
| `EasyRadioLink.Common` | Shared models, settings, networking and audio processing |
| `EasyRadioLink.SharedAudio` | Native Opus and LAME libraries (Windows and Linux) |
| `EasyRadioLink.Installer` | `EasyRadioLink-Setup.exe` - installer / uninstaller |
| `EasyRadioLink.Tests` | Unit tests (MSTest) |

### Release downloads

```powershell
.\publish.ps1 -Zip
```

creates the three release downloads in `dist\`: `EasyRadioLink-Client-<version>.zip`,
`EasyRadioLink-Server-<version>-Windows.zip` and `EasyRadioLink-Server-<version>-Linux.tar.gz` (next to the unpacked
package folders). The version comes from `Directory.Build.props`, the only place where it is set. The client and the
server with window are published as framework-dependent single files for win-x64, the command-line servers as
self-contained builds for win-x64 and linux-x64; `packaging/README.txt`, `LICENSE` (as `LICENSE.txt`) and
`THIRD-PARTY-NOTICES.txt` are added to every download. The script runs in Windows PowerShell 5.1 and PowerShell 7.
The `.tar.gz`, which keeps the executable bit of the Linux server, needs PowerShell 7.3 or newer; Windows PowerShell
5.1 creates `EasyRadioLink-Server-<version>-Linux.zip` instead. Pushing a `v*` tag runs the same build on GitHub
Actions (`.github/workflows/release.yml`) and publishes the release with the notes from `docs/release-notes/<tag>.md`.

| Parameter | Meaning |
|---|---|
| `-Zip` | Also create the release downloads (`.zip` / `.tar.gz`). |
| `-Installer` | Also build the installer package `dist\EasyRadioLink-<version>\` with `EasyRadioLink-Setup.exe` (not part of the release). |
| `-Sign -CertSubject "<name>"` | Sign all executables with `signtool` (Windows SDK). Default: unsigned. |
| `-TimestampUrl <url>` | RFC 3161 timestamp server (default DigiCert). |
| `-SignToolPath <path>` | Use a specific `signtool.exe`. |
| `-NoVcRedist` | With `-Installer`: do not download `VC_redist.x64.exe` (offline builds). |

Unsigned executables trigger Windows SmartScreen warnings on first start.

### Installer (optional)

`publish.ps1 -Installer` builds `dist\EasyRadioLink-<version>\` with `EasyRadioLink-Setup.exe`, `Client\`,
`Server\`, `ServerCommandLine-Windows\`, `ServerCommandLine-Linux\` and the Microsoft Visual C++ Redistributable.
The setup installs to `C:\Program Files\EasyRadioLink` (changeable; a folder that holds files of another program is
refused), creates a Start menu folder (EasyRadioLink, EasyRadioLink Server, Uninstall EasyRadioLink) and optionally a
desktop shortcut, registers EasyRadioLink in *Apps & Features* and installs the Visual C++ runtime. Running
EasyRadioLink programs are closed first. Every installed file is listed in `install-manifest.txt`; updates and the
uninstaller remove exactly those files, so server settings and logs next to the servers survive an update (the
uninstaller asks before deleting them). The installing user gets write access to the `Server` and
`ServerCommandLine-Windows` folders so the servers can save their files; the client folder stays write-protected.
The setup never deletes the files in `%AppData%\EasyRadioLink` or the recordings. `EasyRadioLink-Setup.exe -uninstall`
uninstalls without the setup window.

## License

EasyRadioLink is free software, licensed under the [GNU General Public License v3.0](LICENSE). The source code is
available at <https://github.com/DanielBecker715/EasyRadioLink>; the source of every release is tagged there
(for example `v1.0.0`).

Based on [DCS-SimpleRadio Standalone](https://github.com/ciribob/DCS-SimpleRadioStandalone) by Ciribob and
contributors (GPL-3.0). EasyRadioLink is an independent, game-independent derivative and is not affiliated with or
endorsed by the original authors. Third-party components keep their own licenses; they are listed with their license
texts in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt), which ships in every download.
