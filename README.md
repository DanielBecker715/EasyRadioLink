# EasyRadioLink

EasyRadioLink is a standalone digital radio for Windows. Users connect to an EasyRadioLink server, tune their radio
to a frequency and talk to everybody on that frequency, with the sound and the rules of real radios: band-pass
filters, static, squelch tails, key clicks, tones, one speaker per frequency, half-duplex behaviour and interference.

It needs no game or other software: a group of friends, a club, a flight-sim squadron, an airsoft or role-play team
simply runs a server and connects.

## Features

**Radio**
- One radio, 1.000 - 999.999 MHz: as many channels as there are frequencies. The frequency decides the band,
  the modulation (AM, FM or digital) and the radio sound (see [Band plan](#band-plan)).
- Radio window with a seven-segment display, a tuning knob (drag, mouse wheel or arrow keys), step keys, STEP
  (1 kHz - 100 MHz), direct entry (double-click the display), volume knob, BUSY/TX/RX indicators with the
  transmitter's name and the number of users on the frequency.
- One speaker per frequency, like real radios with busy channel lockout: while somebody is talking, the others can't
  (a short busy tone tells you the frequency is in use; see [The radio](#the-radio)).
- The radio remembers its frequency and volume between sessions.

**Radio sound**
- Radio sound models per band (CB, walkie-talkie, airband, tactical, HF, vintage tube, digital). Customise them as
  JSON effect chains ([docs/radio-models.md](docs/radio-models.md)).
- Frequency-dependent static and HF noise, FM tone and an optional squelch tail (off by default).
- *Distance (weak signal)* (0 - 100 %, default 35 %): other stations sound far away, like a long-distance or field
  radio link - fading that swirls through the voice, static that breathes up in the fades (FM: hiss that surges up),
  a narrower, harsher voice; slow deep fading and a faint whistle below 30 MHz, gritty "secure voice" coding on DIG.
  No crackle, no dropouts.
- Radio sounds of your choice when you press / release push-to-talk and when someone starts / stops talking: clicks,
  chirp, beeps, roger beep and more - or none (see [Settings](#settings)).
- Optional background sound (jet, prop or helicopter) that the other stations hear behind your voice.

**Controls**
- Push-to-talk, frequency steps and volume on keyboard, mouse, joysticks/HOTAS (DirectInput) and gamepads (XInput).
- Radio window toggle hotkey; free choice of microphone and speaker devices, plus an optional "mic output" device that
  carries your own radio-processed voice (for streaming or recording software).
- Optional MP3 recording of radio traffic.

**Privacy**
- Encrypted connection to the server (TLS) with a pinned server identity, like SSH.
- End-to-end encrypted voice: only the stations tuned to the frequency can hear a transmission - not even the server
  (see [Security](#security)).

**Server**
- Server with a window for Windows, plus a command-line server for Windows and Linux (x64).
- Optional server password, radio check (echo) frequencies, clean frequencies without radio effects,
  one speaker per frequency (busy channel lockout, on by default), half-duplex radios and interference of
  simultaneous transmissions.
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

On the first connection to a server EasyRadioLink remembers the server's identity (its fingerprint is shown under
**Server Info**). If that identity ever changes, EasyRadioLink warns you before it sends anything; see
[Security](#security).

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

The password travels over the encrypted connection and is compared on the server; a wrong password is rejected. It
keeps strangers out - anybody who can connect can tune to any frequency and listen.

On the first start the server creates its identity `server-identity.pfx` and shows its **fingerprint** in the
*Server identity* box (the command-line servers print it and write it to `serverlog.txt`). Share the fingerprint with
your users, keep `server-identity.pfx` private and copy it along with `server.cfg` when you update or move the server.

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
| `--busy-lockout` | `BUSY_CHANNEL_LOCKOUT` | `true` | One speaker per frequency (busy channel lockout): while a station transmits on a frequency, nobody else can transmit on it until 0.3 s after its last transmission. |
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

`server.cfg` and the server identity `server-identity.pfx` (readable by the service user only) are created in
`/var/lib/easyradiolink` on the first start; `journalctl -u easyradiolink` shows the identity's fingerprint. To set a
password, stop the service, set
`SERVER_PASSWORD` in the `[Server Settings]` section of that file and start it again (don't put the password on the
`ExecStart` command line - command lines are visible to other users of the machine).

### Security

EasyRadioLink 1.1 encrypts the connection to the server and the voice between the stations:

- **Encrypted connection.** Everything a client and the server say to each other travels over TLS 1.2/1.3: the
  password, names, frequencies and the user list can't be read or changed on the way (Wi-Fi, internet provider).
  Voice packets (UDP) are encrypted and authenticated per user with AES-256-GCM, with a new key for every connection;
  replayed, altered or forged packets are dropped. Forged packets can't mute anybody: packets from the address and port
  a user's authenticated packets come from are always checked, and the server's limit on packets that fail the check
  applies per sender address and port (plus a total limit), never per user.
- **End-to-end encrypted voice.** Every transmission (each press of PTT, on one frequency) gets its own random key.
  That key is sent only to the stations whose radio can hear the frequency, encrypted separately for each of them (every
  client creates an ECDH P-256 key pair when it starts; the private key never leaves the PC and is never stored). The
  server forwards the voice but can't decrypt it, and it can't move it to another frequency without breaking it.
  Stations that tune in during a transmission get the key too and hear the rest of it - also when they come back
  (reconnected, or tuned away and back) while it lasts. If the key of a transmission doesn't reach a station in time,
  it hears the scrambled sound of an encrypted radio instead. Recordings (made from your own audio) and the radio check
  echo keep working: on a radio check frequency your client keeps the key of its own transmission to play the echo; on
  every other frequency it doesn't keep it.
- **Server identity, trust on first use.** Each server creates its identity (`server-identity.pfx`, a self-signed
  certificate) on its first start and shows its fingerprint (SHA-256 of the public key, `AB:CD:...`) in the server
  window, on the console and in `serverlog.txt`. On the first connection the client remembers it (`known-servers.json`
  in the settings folder, like SSH does) and shows it under **Server Info**. If the server later presents another
  identity, the client stops before sending anything - not even the password - and shows both fingerprints:
  *Connect anyway and trust the new identity* or *Cancel*. Only continue if the server admin confirms the new
  fingerprint. The check fails closed: if the saved identity of a server is unreadable (the file was edited), the
  client shows the server's fingerprint and asks the same way instead of trusting it silently; if `known-servers.json`
  can't be read at all (damaged, or locked by another program), it connects to no server and says so. A damaged file is
  never overwritten - repair it, or rename or delete it (then every server counts as new again).

What the server can and can't see:

| The server sees | The server can't see |
|---|---|
| Who is connected (name, IP address) and which frequency each radio is tuned to | What anybody says - the voice is end-to-end encrypted |
| Who transmits when, on which frequency and for how long | The transmission keys: it only forwards copies that are encrypted for each listener |
| The server password (it checks it) | The users' private keys |

Threat model:

- Protected against: anybody on the network path (they see only encrypted traffic); a curious server operator (logs,
  network captures and the server process itself never hold a voice key); voice packets injected or replayed on the
  network (every packet is authenticated, with a replay window per connection and direction); a server replaying a
  recorded transmission to you - frames you already played are never played again while the app runs (the last 256
  transmissions per sender are remembered), and the key of a transmission without activity for 5 minutes is refused;
  users on other frequencies.
- Not protected against: a server that is deliberately modified to fake listeners - clients trust the server's user
  list, and there is no manual key comparison between users (such a server could also hand you parts of a recent
  transmission that you did not receive, a little late); anybody who can connect and tunes to your frequency - that is
  how radio works, so set a password to keep strangers out; traffic analysis (who talks when, on which frequency).
- Anybody who can connect can also take over a connected user's client id (the ids are part of the user list): the
  server closes that user's old connection - they see it drop - and from then on voice keys for that id go to the new
  connection. On an open server that is anybody on the internet: set a password so only people you trust can
  connect.
- The server identity is trusted on first use: an impostor on the very first connection can't be told apart. If in
  doubt, compare the fingerprint under **Server Info** with the one your server admin shares.
- EasyRadioLink 1.1 and 1.0 can't connect to each other: a 1.0 client gets "incompatible server", a 1.1 client
  connecting to a 1.0 server reports that servers older than 1.1 can't be used.

The server only relays voice between authenticated clients:

- Clients send JSON over the encrypted connection and voice over UDP; nothing a client sends is ever executed, used as a
  file path or passed to native code (the server neither decrypts nor decodes audio).
- With a password, only clients that passed the login receive or send voice, and only from the IP address they
  logged in from. Wrong passwords are slowed down and an address is locked out for 5 minutes after 10 failures.
- Connections that send invalid data, too many messages, or don't finish the TLS handshake and login within 15
  seconds are closed; each address may open a limited number of connections (32 at once, 8 unfinished handshakes,
  about one new connection per second after a burst), voice packets are rate-limited per user and voice keys to a
  burst of 20, then 10 per second per user. Refused and unfinished connections are logged at debug level each and
  summarised in `serverlog.txt` once a minute, so a flood doesn't flood the log. The client closes the connection if
  the server sends a message longer than 512 KB.
- The password, the HTTP API key, UDP keys and voice keys are never printed in logs; the password and the HTTP API key
  are never sent to clients.

What you should do as the operator:

- Open only TCP and UDP port 5010 (or your port). Keep the HTTP admin API off or on `localhost` (default) and reach
  it through an SSH tunnel.
- Keep `server-identity.pfx` private and in your backups, and copy it with `server.cfg` when you update or move the
  server. It is created readable only by the server's account (on Linux mode 0600; on Windows with an ACL of its own:
  the server's account, SYSTEM and Administrators, nothing inherited from the folder). On every start the server warns
  (log, console, server window) if other Windows accounts can read it - for example a copy that picked up the folder's
  permissions. A lost or replaced identity makes every client warn about a changed identity once. Share the
  fingerprint with your users so they can compare it.
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
| `server-identity.pfx` | next to `server.cfg` | The server identity (TLS certificate with its private key), created on the first start. Clients pin its fingerprint - keep it private, back it up and keep it when updating. |
| `banned.txt` | next to `server.cfg` | Banned IP addresses, one per line. |
| `serverlog.txt`, `*-transmissionlog.csv` | next to `server.cfg` | Server log and transmission logs. |
| `clients-list.json` | next to `server.cfg` | Client export (when enabled and no other path is set). |

To update a server, extract the new version and copy `server.cfg` and `server-identity.pfx` (plus `banned.txt` if
used) from the old folder, or keep these files in a separate folder and start the server with `--cfg`. Without the old
`server-identity.pfx` the server creates a new identity and every user is warned about a changed identity once.
Settings in `server.cfg` that the running version does not know (for example those of features removed in 1.1) are
ignored and may be deleted.

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
  446.19375), the band, the modulation (AM / FM / DIG), `BUSY` while another station uses the frequency, `TX` while
  you transmit, `RX` while you receive, the transmitter's name and the number of users on the frequency (when the
  server allows them) and the step. Without a connection it shows `NO LINK` and the controls are disabled.
- **One speaker per frequency** (busy channel lockout; the server has it on unless its admin switched it off - see
  **Server Info**): while somebody is talking on your frequency, `BUSY` is lit and you can't transmit. If you press
  push-to-talk then, you hear a short busy tone (two low beeps, only you hear it), `BUSY` flashes and nothing is sent
  for the whole press - release and press again once the frequency is free (0.3 s after the other station stopped).
  If two stations press at the same moment, the server lets the first one through; the other one hears the busy tone
  and stops transmitting. With voice activation (VOX) nothing is sent while the frequency is busy; you hear the busy
  tone at most once per second. The radio check echo of your own voice never counts as busy.
- Drag the radio by its case; the grip at the bottom right scales it. Position and size are remembered.

The **Controls** tab assigns keys or buttons to push-to-talk, frequency up / down (one binding per step, 100 MHz to
1 kHz), volume up / down and *Show / hide the radio*.

### Settings

The **Settings** tab shows the everyday settings first:

- **Radio Sounds**: the sound *When I press push-to-talk*, *When I release push-to-talk*, *When someone starts
  talking* and *When someone stops talking* - *Click*, *Soft click*, *Chirp* and *Key-up beep* (start sounds),
  *Roger beep*, *Double beep* and *Three-tone beep* (end sounds), *Fancy Release* and *Almost Fancy* (both), or
  *Off* (default: *Fancy Release* when a transmission starts, *Almost Fancy* when it ends). The ▶ button next to
  each plays the chosen sound on your speakers, also without a connection. Only you hear
  your push-to-talk sounds; the others hear what they chose for someone starting / stopping to talk. A start sound
  is played before the received voice, so a long one delays the voice by its length. Below: squelch tail (the
  short "kssht" when an AM / FM transmission ends; off by default, profiles that already have the setting keep it),
  radio static, FM tone, your background sound and its volume, the radio effect strength and the distance.
- **Distance (weak signal)** (0 - 100 %, default 35 %) makes the stations you receive sound far away - the sound of a
  long-distance or field radio link rather than a clean voice with effects on top:
  - *Multipath*: the signal also arrives over other paths, a fraction of a millisecond later and with a slowly
    drifting phase, so notches swim through the voice - the watery, swirling sound of distant radio.
  - *Weak signal*: the signal fades, static comes up, and the receiver's automatic gain control pulls the static up
    in the fades, so it breathes. The voice never cuts out - there is no crackle and there are no dropouts.
  - *Field radio voice*: narrower and harsher, like a military handset (about 450 Hz - 2.6 kHz at 100 %, a honky mid
    resonance, soft overdrive - at the same loudness).
  - *The band sets the character*: below 30 MHz (HF, CB, MW) slow, deep fading, the strongest swirl and soft static,
    above 50 % also a faint whistle of a distant station drifting in and out; AM on VHF / UHF (AIR, UHF AM) a fast
    flutter; FM (VHF, FM, PMR, UHF FM) keeps the voice level, but a bright hiss surges up whenever the signal fades
    below the FM threshold - the typical tactical radio sound; on the DIG band no static and no fading, the voice gets
    the gritty, buzzy sound of CVSD "secure voice" coding instead.

  Voice to static is about 24 dB at 35 % (clearly far away, easy to understand), 14 dB at 70 % and 7 dB at 100 %
  (very far away: the voice swims in the static, but stays understandable). It applies to everything you receive
  (also the radio check echo), to the whole voice at any radio effect strength (the clean share of that mix
  included), but not on clean frequencies and not when the radio effect strength is 0 %; the squelch tail and the
  start / end sounds stay clean, and so does your own voice on the mic output device. The **Audio Preview** on the
  Radio tab uses it too, so you can tune it while you hear yourself.
- **General**: open the radio when connected, show who is transmitting, minimise to the system tray, start
  minimised, connect / disconnect sounds and voice activation (VOX).

Everything else is under **Advanced settings** (closed until you open it; EasyRadioLink remembers whether it is
open): microphone and incoming audio (noise suppression, automatic gain control), voice activation details,
recording, radio effect details (clipping, the radio sound of the band, static levels, FM tone volume, the
background sounds of other users, radio balance), push-to-talk delays and controllers, the radio window, profiles
and *Run as administrator*. A profile holds your key bindings and the radio settings (radio sounds and effects,
background sound, push-to-talk delays, rotary tuning, radio balance); all other settings apply to every profile.

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
| `%AppData%\EasyRadioLink` | Settings folder: client settings (`global.cfg`, profile `*.cfg`), `FavouriteServers.csv`, `radio-state.json`, `known-servers.json` (the pinned server identities) |
| `%AppData%\EasyRadioLink\RadioModels` | Custom radio models |
| `%AppData%\EasyRadioLink\Logs` | Client log (`clientlog.txt`, previous run in `clientlog.old.txt`); linked on the About tab |
| `Documents\EasyRadioLink\Recordings` | Recordings |
| Client folder (where `EasyRadioLink.exe` is) | Client program, built-in radio models and sounds |
| Server folder (where `server.cfg` is) | `server.cfg`, `server-identity.pfx`, `banned.txt`, server logs, transmission logs, client export |

The client accepts `-cfg=<folder>` to use another settings folder: everything in the first row (`global.cfg`,
profiles, favourites, `radio-state.json`, `known-servers.json`) then lives there, while logs, custom radio models and
recordings stay in their default places. `-host=<address:port>`, `-name=<name>` and
`-password=<password>` pre-fill the connection.

## Building from source

Requirements: Windows, [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (see `global.json`).

```
dotnet restore EasyRadioLink.sln
dotnet build EasyRadioLink.sln -c Release
dotnet test EasyRadioLink.Tests/EasyRadioLink.Tests.csproj -c Release
```

The synthesised push-to-talk sounds (*Chirp*, *Key-up beep*, *Roger beep*, *Double beep*, *Three-tone beep*) are
generated by `python tools/generate-sounds.py` (Python 3, standard library only; the output is reproducible byte for
byte). `EasyRadioLink.Client/AudioEffects/SOURCES.txt` lists the origin of every shipped sound.

To add your own push-to-talk sound, convert it with `python tools/convert-sound.py <input.wav>
EasyRadioLink.Client/AudioEffects/<Name>.wav` (needs numpy): EasyRadioLink only loads 16-bit, 48 kHz, mono WAV files, and
the script also matches the loudness of the other sounds. Keep the original in `tools/sound-sources`, list the sound in
`SOURCES.txt` and register it with the start/end sounds in `EasyRadioLink.Common/Audio/Models/CachedAudioEffect.cs`.

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
