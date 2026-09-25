EasyRadioLink
=============

EasyRadioLink is a digital radio for your PC. Connect to a server, tune your
radios to a frequency and talk to everybody on the same frequency - with the
sound of a real radio: filters, static, squelch, clicks, tones and scrambled
encryption.


CONTENTS OF THIS PACKAGE
------------------------
  EasyRadioLink-Setup.exe      Installs, updates and uninstalls EasyRadioLink
  Client\                      The EasyRadioLink program (EasyRadioLink.exe)
  Server\                      The server with a window (EasyRadioLink.Server.exe)
  ServerCommandLine-Windows\   The server without a window, for Windows
  ServerCommandLine-Linux\     The server without a window, for Linux (x64)
  VC_redist.x64.exe            Microsoft Visual C++ Runtime (installed by the setup)
  README.txt                   This file
  LICENSE.txt                  GNU General Public License v3.0


REQUIREMENTS
------------
  - Windows 10 or Windows 11, 64-bit
  - Microsoft .NET 10 Desktop Runtime (x64) for EasyRadioLink, the server
    with a window and the setup. If Windows reports that .NET is missing,
    install it from https://dotnet.microsoft.com/download/dotnet/10.0
    ("Desktop Runtime", x64). The command-line servers need no .NET install.
  - A microphone and headphones or speakers.


1. INSTALLATION
---------------
  1. Extract the WHOLE zip file into a folder (right-click the zip file >
     "Extract All..."). The setup does not work from inside the zip file.
  2. Run EasyRadioLink-Setup.exe and allow it to make changes.
     If Windows SmartScreen shows "Windows protected your PC", click
     "More info" and then "Run anyway".
  3. Keep the install folder (C:\Program Files\EasyRadioLink) or choose
     another one, choose the shortcuts you want and click "Install / Update".
  4. Start EasyRadioLink from the Start menu (folder "EasyRadioLink").

  Update: run the setup of the new version and click "Install / Update".
  Your settings are kept.

  Uninstall: Windows Settings > Apps > Installed apps > EasyRadioLink >
  Uninstall, or Start menu > EasyRadioLink > Uninstall EasyRadioLink.
  Your settings and recordings are kept (see "Files and folders").

  Without the setup: you can also run Client\EasyRadioLink.exe directly from
  the extracted folder (install VC_redist.x64.exe once if there is no sound).


2. FIRST START
--------------
  1. Radio tab > Audio Devices: choose your microphone and your speakers or
     headset. "Preview" lets you hear your own voice with the radio sound.
  2. Controls tab: assign a key, mouse button or joystick button to
     "Push To Talk - PTT".
  3. Radio tab > Connection: enter your name and the server address, for
     example 203.0.113.10:5010 (5010 is the default port), and the server
     password if the server has one. Click "Connect".
  4. The Radio Panel opens. Tune a radio to the same frequency as the people
     you want to talk to, hold your PTT button and talk.


3. USING THE RADIOS
-------------------
  Every radio in the Radio Panel shows its name, its frequency and its
  modulation (AM, FM or DIG = digital). Two radios only hear each other when
  both frequency and modulation match.

  - Change the frequency with the arrow buttons or type it in MHz, always
    with a dot as decimal separator (27.185, not 27,185).
  - Click a radio to select it. PTT transmits on the selected radio.
  - G: also listen on the guard (emergency) frequency of that radio.
  - Channel: pick a preset channel (see "Preset channels" below).
  - Encryption (only on radios that support it): radios with encryption on
    and the same key (1-252) understand each other; everybody else hears
    scrambled noise.
  - Sound: the radio model that shapes how the others hear you
    (CB, walkie-talkie, airband, tactical, HF, vintage, digital, ...).
  - ST: transmit on this radio at the same time as on the selected radio.
  - The counter shows how many users are tuned to that frequency (if the
    server allows it).

  Default radios:
    Radio 1  CB              AM    26.965 - 27.405 MHz  (starts on channel 19)
    Radio 2  PMR446          FM    446.00625 - 446.19375 MHz
    Radio 3  VHF Airband     AM    118 - 137 MHz        (guard 121.5 MHz)
    Radio 4  UHF Tactical    AM    225 - 400 MHz        (guard 243.0 MHz, encryption)
    Radio 5  HF Long Range   AM    3 - 30 MHz
    Radio 6  Digital         DIG   100 - 199.999 MHz    (clean digital sound, encryption)
  EasyRadioLink remembers the frequencies and settings of your radios.

  Radio check: on a server with default settings, whatever you transmit on
  27.405 MHz (CB channel 40) or 446.19375 MHz (PMR channel 16) is sent back
  to you, so you can hear how you sound.

  Preset channels: create a text file named like the radio (for example
  "CB.txt" or "PMR446.txt") in %AppData%\EasyRadioLink\Presets (or in the
  presets folder chosen on the Radio tab) with one channel per line, either
  "Name|Frequency in MHz" or only the frequency:
      Channel 9|27.065
      Channel 19|27.185
      446.00625
  The server can also provide preset channels for everybody.

  Your own radio layout: copy Client\radios.json from the install folder to
  %AppData%\EasyRadioLink\radios-custom.json and edit it (frequencies in Hz).
  Delete the file to go back to the default radios.


4. HOSTING A SERVER
-------------------
  Server with a window: Start menu > EasyRadioLink > EasyRadioLink Server
  (or Server\EasyRadioLink.Server.exe). The server starts immediately.

  - Port: 5010, TCP AND UDP. Allow the server in the Windows firewall
    (Windows asks on the first start). For users on the internet, forward
    TCP and UDP port 5010 in your router to this PC; the server also tries to
    open the port automatically (UPnP).
  - Users connect to <public IP address of the server>:5010. Users in the
    same network use the local address, for example 192.168.1.20:5010.
  - Password: type a server password in the server window. Users must enter
    the same password to connect. Leave it empty for an open server.
    Note: the password keeps strangers out, but it is sent unencrypted.
  - The server window also sets: radio check (echo) frequencies, clean
    frequencies (played without radio effects), half-duplex radios,
    interference of simultaneous transmissions, encryption rules, preset
    channels and the server radio layout, transmission logging. Connected
    users can be muted, kicked or banned in the client list.

  Command-line server (no window, for a dedicated PC or a Linux server):
    Windows:  ServerCommandLine-Windows\EasyRadioLink.Server.Cli.exe --password=secret
    Linux:    chmod +x EasyRadioLink.Server.Cli
              ./EasyRadioLink.Server.Cli --password=secret
  Run it with --help to see all options (on/off options take a value, for
  example --half-duplex=true). Options are saved to server.cfg. Stop the
  server with Ctrl+C.

  Server files: server.cfg (all settings), Presets\*.txt (preset channels),
  server-radios.json (server radio layout), banned.txt and the log files.
  When the server is started from the Start menu they are all kept in
  C:\Program Files\EasyRadioLink\Server. Start the server with
  -cfg=<path to a server.cfg> to keep server.cfg, Presets and
  server-radios.json somewhere else (banned.txt and the logs always stay
  next to the server program).


5. FILES AND FOLDERS
--------------------
  %AppData%\EasyRadioLink             Settings, profiles, favourite servers,
                                      saved radio state, radios-custom.json
  %AppData%\EasyRadioLink\Presets     Preset channel files (*.txt)
  %AppData%\EasyRadioLink\RadioModels Your own radio models (*.json)
  Documents\EasyRadioLink\Recordings  Recordings
  <install folder>\Client             Program files, built-in radio models
  <install folder>\Server             Server program, server.cfg, logs

  Tip: type %AppData%\EasyRadioLink into the address bar of the File
  Explorer to open your settings folder.


6. TROUBLESHOOTING
------------------
  - "You must install .NET" when starting: install the .NET 10 Desktop
    Runtime (x64), see REQUIREMENTS.
  - Cannot connect: check the address and the port, the password, the
    firewall and the port forwarding (TCP and UDP 5010) on the server PC.
  - "Version mismatch": the client and the server must be compatible
    EasyRadioLink versions. Update both.
  - Nobody hears you: same frequency AND same modulation? Encryption on both
    sides with the same key, or off on both? PTT assigned? Correct
    microphone selected (watch the level meter)?
  - You only hear scrambled noise: the other station uses encryption with a
    different key.
  - No sound at all or errors about missing DLLs: run VC_redist.x64.exe from
    this package.


7. LICENSE AND CREDITS
----------------------
  EasyRadioLink is free software: you can redistribute it and/or modify it
  under the terms of the GNU General Public License version 3 (see
  LICENSE.txt). It comes with ABSOLUTELY NO WARRANTY.

  Based on DCS-SimpleRadio Standalone by Ciribob and contributors (GPL-3.0).
  The source code of EasyRadioLink is published together with this release.
