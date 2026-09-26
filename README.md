# EasyRadioLink

EasyRadioLink is a standalone digital radio for Windows. Users connect to an EasyRadioLink server, tune their radio
to a frequency and talk to everybody on that frequency, with the sound of real radios: band-pass filters, static,
squelch tails, key clicks, tones, half-duplex behaviour and interference.

It needs no game or other software: a group of friends, a club, a flight-sim squadron, an airsoft or role-play team
simply runs a server and connects.

## Features

**Radio**
- One radio, 1.000 - 999.999 MHz: as many channels as there are frequencies. The frequency decides the band,
  the modulation (AM, FM or digital) and the radio sound (see [Band plan](#band-plan)).
- Radio window with a seven-segment display, a tuning knob (drag, mouse wheel or arrow keys), step keys, STEP
  (1 kHz - 100 MHz), direct entry (double-click the display), volume knob, TX/RX indicators with the transmitter's
  name and the number of users on the frequency.
- The radio remembers its frequency and volume between sessions.

**Radio sound**
- Radio sound models per band (CB, walkie-talkie, airband, tactical, HF, vintage tube, digital). Customise them as
  JSON effect chains ([docs/radio-models.md](docs/radio-models.md)).
- Frequency-dependent static and HF noise, squelch tail, TX/RX clicks and FM tone.
- Optional background sound (jet, prop or helicopter) that the other stations hear behind your voice.

**Controls**
- Push-to-talk, frequency steps and volume on keyboard, mouse, joysticks/HOTAS (DirectInput) and gamepads (XInput).
- Radio window toggle hotkey; free choice of microphone and speaker devices, plus an optional "mic output" device that
  carries your own radio-processed voice (for streaming or recording software).
- Optional MP3 recording of radio traffic.

**Server**
- Server with a window for Windows, plus a command-line server for Windows and Linux (x64).
- Optional server password, radio check (echo) frequencies, clean frequencies without radio effects,
  half-duplex radios and interference of simultaneous transmissions.
- Mute/kick/ban, client list export, transmission log, UPnP port forwarding and an optional HTTP admin API.

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
4. The radio opens. Tune to the same frequency as the others (the frequency also sets the modulation), hold PTT
   and talk.

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
| `--show-tuned-count` | `SHOW_TUNED_COUNT` | `true` | Users see how many people are tuned to their frequency. |
| `--show-transmitter-name` | `SHOW_TRANSMITTER_NAME` | `false` | Users see who is transmitting. |
| `--client-export` | `CLIENT_EXPORT_ENABLED` | `false` | Write the connected clients to a JSON file every 5 seconds. |
| `--client-export-path` | `CLIENT_EXPORT_FILE_PATH` | `clients-list.json` | Full path of that file (default: next to `server.cfg`). |
| `--transmission-log` | `TRANSMISSION_LOG_ENABLED` | `false` | Log every transmission to a daily CSV file. |
| `--transmission-log-retention` | `TRANSMISSION_LOG_RETENTION` | `2` | Days of transmission logs to keep. |
| `--http-server` | `HTTP_SERVER_ENABLED` | `false` | Enable the HTTP admin API. |
| `--http-port` | `HTTP_SERVER_PORT` | `8080` | Port of the HTTP admin API. |
| `--http-address` | `HTTP_SERVER_ADDRESS` | `localhost` | Host name / address the HTTP admin API listens on. |
| `--console-logs` | – | `true` | Print connects/disconnects to the console; `--console-logs=false` turns it off (not saved to `server.cfg`). |

`EasyRadioLink.Server.Cli --help` always shows the options of your version.

### Linux: run as a service

The Linux download contains `easyradiolink.service`, a hardened systemd unit: the server runs as its own user without
a login shell, sees the file system read-only except for its data folder `/var/lib/easyradiolink`, has no privileges
and limited memory and processes.

```
sudo useradd --system --no-create-home --shell /usr/sbin/nologin easyradiolink
sudo install -D -m 0755 EasyRadioLink.Server.Cli /opt/easyradiolink/EasyRadioLink.Server.Cli
sudo install -m 0644 easyradiolink.service /etc/systemd/system/easyradiolink.service
sudo systemctl daemon-reload
sudo systemctl enable --now easyradiolink
journalctl -u easyradiolink -f
```

`server.cfg` is created in `/var/lib/easyradiolink` on the first start. To set a password, stop the service, set
`SERVER_PASSWORD` in the `[Server Settings]` section of that file and start it again (don't put the password on the
`ExecStart` command line - command lines are visible to other users of the machine).

### Security

The server only relays voice between authenticated clients:

- Clients send JSON over TCP and voice over UDP; nothing a client sends is ever executed, used as a file path or
  passed to native code (the server does not decode audio).
- With a password, only clients that passed the login receive or send voice, and only from the IP address they
  logged in from. Wrong passwords are slowed down and an address is locked out for 5 minutes after 10 failures.
- Connections that send invalid data, too many messages, or never finish the login are closed; each address can hold
  a limited number of connections, and voice packets are rate-limited per user.
- The password and the HTTP API key are never sent to clients or printed in logs.

What you should do as the operator:

- Open only TCP and UDP port 5010 (or your port). Keep the HTTP admin API off or on `localhost` (default) and reach
  it through an SSH tunnel.
- The server password is sent unencrypted - don't reuse a valuable password.
- A volumetric flood (hundreds of megabits of junk) has to be stopped by your firewall or hosting provider, like for
  any other internet service.

### Server configuration

By default `server.cfg` is kept next to the server program, whatever the working directory is; each server
(`Server\`, `CommandLine\`, Linux) has its own. All other server files are kept in the folder of `server.cfg`. The
server with window accepts `-cfg=<path to server.cfg>` as well, so both servers can share one configuration folder.
The server needs write access to that folder (a plain copy below `C:\Program Files` does not have it), otherwise
its settings cannot be saved.

| File | Location | Purpose |
|---|---|---|
| `server.cfg` | next to the server program, or the `--cfg` / `-cfg` path | All settings. `[General Settings]` are sent to every client; `[Server Settings]` (port, bind address, UPnP, HTTP API, password) never leave the server. |
| `banned.txt` | next to `server.cfg` | Banned IP addresses, one per line. |
| `serverlog.txt`, `*-transmissionlog.csv` | next to `server.cfg` | Server log and transmission logs. |
| `clients-list.json` | next to `server.cfg` | Client export (when enabled and no other path is set). |

To update a server, extract the new version and copy `server.cfg` (plus `banned.txt` if used) from the old folder,
or keep these files in a separate folder and start the server with `--cfg`. Settings in `server.cfg` that the running
version does not know (for example those of features removed in 1.1) are ignored and may be deleted.

Other `server.cfg` keys: `HTTP_SERVER_API_KEY` (generated on first start). Frequency lists always use a dot as
decimal separator, independent of the Windows language.

**HTTP admin API** (when `HTTP_SERVER_ENABLED = true`; send the key from `HTTP_SERVER_API_KEY` in the `X-API-KEY`
header):

| Request | Action |
|---|---|
| `GET /clients` | Connected clients as JSON. |
| `POST /client/kick/guid/<guid>` / `POST /client/kick/name/<name>` | Kick a client. |
| `POST /client/ban/guid/<guid>` / `POST /client/ban/name/<name>` | Ban a client's IP address (`banned.txt`) and disconnect it. |

## Radio, band plan and models

### The radio

After connecting, the radio window opens (*Open the radio when connected* on the **Settings** tab); **Show Radio** on
the **Radio** tab and the *Show / hide the radio* hotkey open it at any time. It stays on top of other windows.

- **Tune**: drag the big knob round with the mouse (30 notches per turn, one notch = one step), or turn the mouse
  wheel over the knob or the display. The ▲ / ▼ keys and the arrow keys Up / Down tune one step as well.
- **STEP** cycles the step: 1 kHz, 10 kHz, 100 kHz, 1 MHz, 10 MHz, 100 MHz (and back to 1 kHz). The arrow keys
  Left / Right choose a larger / smaller step. The digit that the step changes is underlined on the display.
- **Direct entry**: double-click the display (or press Enter) and type the frequency in MHz, e.g. `446.19375`
  (`446,19375` works too). Enter applies it, Esc cancels. The frequency is rounded to 10 Hz (what the display shows);
  a frequency outside 1.000 - 999.999 MHz is set to the nearest end of the range; text that is not a frequency makes
  the frame flash and the entry stays open.
- **VOL**: the small knob sets the volume (drag or mouse wheel).
- **Display**: the frequency in seven-segment digits (digits below 1 kHz appear small, e.g. the `75` of
  446.19375), the band, the modulation (AM / FM / DIG), `TX` while you transmit, `RX` while you receive, the
  transmitter's name and the number of users on the frequency (when the server allows them) and the step. Without a
  connection it shows `NO LINK` and the controls are disabled.
- Drag the radio by its case; the grip at the bottom right scales it. Position and size are remembered.

The **Controls** tab assigns keys or buttons to push-to-talk, frequency up / down (one binding per step, 100 MHz to
1 kHz), volume up / down and *Show / hide the radio*.

### Band plan

There is one radio. Its frequency (1.000 - 999.999 MHz, 1 kHz steps; a typed frequency may be finer, e.g.
446.19375) alone decides the modulation, the radio model (sound) and the band shown on the display. Everybody applies
the same plan, so everybody on a frequency uses the same modulation and hears the same radio sound.

| Frequency (MHz) | Band | Modulation | Radio model |
|---|---|---|---|
| 1.000 - 2.999 | MW | AM | `vintage` |
| 3.000 - 26.964 | HF | AM | `hf` |
| 26.965 - 27.405 | CB | AM | `cb` |
| 27.406 - 29.999 | HF | AM | `hf` |
| 30.000 - 87.999 | VHF | FM | `tactical` |
| 88.000 - 107.999 | FM | FM | `walkie` |
| 108.000 - 136.999 | AIR | AM | `airband` |
| 137.000 - 224.999 | VHF | FM | `walkie` |
| 225.000 - 399.999 | UHF | AM | `tactical` |
| 400.000 - 445.999 | UHF | FM | `walkie` |
| 446.000 - 446.199 | PMR | FM | `walkie` |
| 446.200 - 899.999 | UHF | FM | `walkie` |
| 900.000 - 999.999 | DIG | Digital (clean, no static) | `digital` |

Both ends of every range are included; a frequency between two kHz steps belongs to the band of the nearer step
(27.4054 MHz is CB, 446.19375 MHz is PMR). Where the modulation changes (30, 108, 137, 225, 400 and 900 MHz) a
frequency closer than 1 kHz to the band edge is rounded to the whole kHz (29.9996 MHz becomes 30.000 MHz, 29.9994 MHz
becomes 29.999 MHz), so two radios close enough to hear each other always use the same modulation. The radio starts on
27.185 MHz (CB channel 19) and remembers its last frequency and volume in `radio-state.json`.

### Radio models

Radio models are JSON effect chains that define how a radio sounds. Built-in models live in `RadioModels\*.json`
next to `EasyRadioLink.exe`; your own models go to `%AppData%\EasyRadioLink\RadioModels\*.json` (the file name is
the model key; a file with a built-in name replaces that model and so changes the sound of its band). The format
(`chain`, `filters`, `gain`, `saturation`, `compressor`, `sidechainCompressor`, `cvsd`; `lowpass`, `highpass`,
`peak` filters) is documented in [docs/radio-models.md](docs/radio-models.md).

## Files and folders

| Location | Contents |
|---|---|
| `%AppData%\EasyRadioLink` | Settings folder: client settings (`global.cfg`, profile `*.cfg`), `FavouriteServers.csv`, `radio-state.json` |
| `%AppData%\EasyRadioLink\RadioModels` | Custom radio models |
| `%AppData%\EasyRadioLink\Logs` | Client log (`clientlog.txt`, previous run in `clientlog.old.txt`); linked on the About tab |
| `Documents\EasyRadioLink\Recordings` | Recordings |
| Client folder (where `EasyRadioLink.exe` is) | Client program, built-in radio models and sounds |
| Server folder (where `server.cfg` is) | `server.cfg`, `banned.txt`, server logs, transmission logs, client export |

The client accepts `-cfg=<folder>` to use another settings folder: everything in the first row (`global.cfg`,
profiles, favourites, `radio-state.json`) then lives there, while logs, custom radio models and recordings stay
in their default places. `-host=<address:port>`, `-name=<name>` and
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
