EasyRadioLink
=============

EasyRadioLink is a digital radio for your PC. Connect to a server, tune the
radio to a frequency and talk to everybody on the same frequency - with the
sound of a real radio: filters, static, squelch, clicks and tones.

Website and source code: https://github.com/DanielBecker715/EasyRadioLink


DOWNLOADS
---------
  EasyRadioLink-Client-<version>.zip
      EasyRadioLink for everybody who wants to talk (EasyRadioLink.exe,
      Windows).

  EasyRadioLink-Server-<version>-Windows.zip
      Only needed to host a server on Windows:
        Server\EasyRadioLink.Server.exe            server with a window
        CommandLine\EasyRadioLink.Server.Cli.exe   server without a window

  EasyRadioLink-Server-<version>-Linux.tar.gz
      Only needed to host a server on Linux (x64): EasyRadioLink.Server.Cli,
      a server without a window.

  Every download also contains this README.txt, LICENSE.txt (GNU General
  Public License v3.0) and THIRD-PARTY-NOTICES.txt.


REQUIREMENTS
------------
  - Windows 10 or Windows 11, 64-bit (EasyRadioLink and the Windows servers).
  - Microsoft .NET 10 Desktop Runtime (x64) for EasyRadioLink and the server
    with a window. If Windows reports that .NET is missing, install it from
    https://dotnet.microsoft.com/download/dotnet/10.0 ("Desktop Runtime",
    x64). The servers without a window need no .NET installation.
  - Microsoft Visual C++ Redistributable (x64) for EasyRadioLink (its audio
    libraries need it). Most PCs already have it. If it is missing, install
    it from https://aka.ms/vs/17/release/vc_redist.x64.exe
  - Linux server: 64-bit (x64) Linux with glibc, for example Debian, Ubuntu
    or Fedora. No .NET installation needed.
  - A microphone and headphones or speakers.


1. GETTING STARTED
------------------
  1. Extract the WHOLE zip file into a folder of your choice (right-click the
     zip file > "Extract All..."). EasyRadioLink does not work from inside
     the zip file.
  2. Start EasyRadioLink.exe in the extracted folder.
     If Windows SmartScreen shows "Windows protected your PC", click
     "More info" and then "Run anyway".

  Update: extract the new version and use it instead of the old folder. Your
  settings are kept: they are stored in %AppData%\EasyRadioLink, not in the
  program folder (see "Files and folders").


2. FIRST START
--------------
  1. Radio tab > Audio Devices: choose your microphone and your speakers or
     headset. "Audio Preview" lets you hear your own voice with the radio
     sound.
  2. Controls tab: assign a key, mouse button or joystick button to
     "Push-To-Talk (PTT)".
  3. Radio tab > Connection: enter your name and the server address, for
     example 203.0.113.10:5010 (5010 is the default port), and the server
     password if the server has one. Click "Connect".
  4. The radio opens. Tune it to the same frequency as the people you want
     to talk to, hold your PTT button and talk.


3. USING THE RADIO
------------------
  There is one radio. Tune it to any frequency from 1.000 to 999.999 MHz -
  there are as many channels as there are frequencies. Everybody on the same
  frequency hears each other.

  - Turn the big knob: drag it round with the mouse, or use the mouse wheel
    over the knob or the display. The arrow keys Up / Down and the buttons
    with the arrows tune one step as well.
  - STEP (or the arrow keys Left / Right) chooses the step: 1 kHz, 10 kHz,
    100 kHz, 1 MHz, 10 MHz or 100 MHz. The digit that changes is underlined.
  - Double-click the display (or press Enter) to type a frequency in MHz,
    for example 446.19375 (446,19375 works too). Enter sets it, Esc cancels.
  - VOL: the small knob sets the volume (drag it up / down or use the wheel).
  - The display shows the band and the modulation, TX while you transmit, RX
    while you receive, the name of the speaker and the number of users on the
    frequency (if the server allows it).
  - Drag the radio by its case, resize it with the corner at the bottom
    right. Without a connection the display shows NO LINK.
  - Controls tab: besides Push-To-Talk you can assign keys or buttons to
    "Frequency up / down" (one for each step), the radio volume and
    "Show / hide the radio".

  The frequency decides the band, the modulation and the radio sound:
    1.000 -   2.999 MHz  MW    AM   vintage radio
    3.000 -  26.964 MHz  HF    AM   HF radio
   26.965 -  27.405 MHz  CB    AM   CB radio
   27.406 -  29.999 MHz  HF    AM   HF radio
   30.000 -  87.999 MHz  VHF   FM   tactical radio
   88.000 - 107.999 MHz  FM    FM   walkie-talkie
  108.000 - 136.999 MHz  AIR   AM   airband radio
  137.000 - 224.999 MHz  VHF   FM   walkie-talkie
  225.000 - 399.999 MHz  UHF   AM   tactical radio
  400.000 - 899.999 MHz  UHF   FM   walkie-talkie
                               (446.000 - 446.199 MHz is shown as PMR)
  900.000 - 999.999 MHz  DIG   digital, clean sound without static

  The radio starts on 27.185 MHz (CB channel 19) and remembers its frequency
  and volume.

  Radio check: on a server with default settings, whatever you transmit on
  27.405 MHz (CB channel 40) or 446.19375 MHz (PMR channel 16) is sent back
  to you, so you can hear how you sound.


4. HOSTING A SERVER
-------------------
  Extract EasyRadioLink-Server-<version>-Windows.zip (Windows) or
  EasyRadioLink-Server-<version>-Linux.tar.gz (Linux) into a folder where
  you may write files, for example C:\EasyRadioLink-Server or your home
  folder. The server keeps its settings and logs in its own folder.

  Server with a window (Windows): start Server\EasyRadioLink.Server.exe.
  The server starts immediately.

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
    interference of simultaneous transmissions, whether users see the
    number of users on their frequency and the name of the speaker, the
    client list export and transmission logging. Connected users can be
    muted, kicked or banned in the client list.

  Server without a window (for a dedicated PC or a Linux server):
    Windows (Command Prompt):
      cd <extracted folder>\CommandLine
      EasyRadioLink.Server.Cli.exe --password=secret
    Linux:
      tar xzf EasyRadioLink-Server-<version>-Linux.tar.gz
      cd EasyRadioLink-Server-<version>
      ./EasyRadioLink.Server.Cli --password=secret
  Run it with --help to see all options (on/off options take a value, for
  example --half-duplex=true). The options you give are saved to server.cfg,
  so later starts without options keep them. To remove the password again,
  start it once with --password "" (a space, not "="). Stop the server with
  Ctrl+C.

  On a Linux server, run it as a service with the included
  easyradiolink.service (own user, no privileges, writes only to
  /var/lib/easyradiolink). The installation commands are at the top of that
  file.

  Server files: server.cfg (all settings) is kept next to the server
  program; Server\ and CommandLine\ each have their own. banned.txt and
  the log files are kept in the same folder as server.cfg. Start the
  server with --cfg=<path to a server.cfg> to keep all of them in another
  folder.

  Update a server: extract the new version, then copy server.cfg (and
  banned.txt if you use it) from the old server folder into the new one.
  Settings in server.cfg that the new version does not know any more are
  ignored; you may delete them.


5. FILES AND FOLDERS
--------------------
  %AppData%\EasyRadioLink             Settings folder: settings, profiles,
                                      favourite servers, saved radio state
  %AppData%\EasyRadioLink\RadioModels Your own radio models (*.json)
  %AppData%\EasyRadioLink\Logs        Log files (clientlog.txt)
  Documents\EasyRadioLink\Recordings  Recordings
  EasyRadioLink folder                Program, built-in radio models and
                                      sounds
  Server folder                       Server program, server.cfg,
                                      banned.txt, log files

  Tip: type %AppData%\EasyRadioLink into the address bar of the File
  Explorer to open your settings folder. The About tab has a link to the
  log folder.


6. TROUBLESHOOTING
------------------
  - "You must install .NET" when starting: install the .NET 10 Desktop
    Runtime (x64), see REQUIREMENTS.
  - No sound at all, or an error about opus.dll or another missing DLL:
    install the Microsoft Visual C++ Redistributable (x64), see
    REQUIREMENTS.
  - Cannot connect: check the address and the port, the password, the
    firewall and the port forwarding (TCP and UDP 5010) on the server PC.
  - "Incompatible server": the client and the server must be compatible
    EasyRadioLink versions. Update both.
  - Nobody hears you: exactly the same frequency? PTT assigned? Correct
    microphone selected (watch the level meter)?
  - You only hear scrambled noise: the other station uses an older
    EasyRadioLink version with encryption switched on.
  - Anything else: the log files usually tell what went wrong - clientlog.txt
    in %AppData%\EasyRadioLink\Logs (link on the About tab) and serverlog.txt
    in the server folder. Please include them when you report a problem.


7. LICENSE AND CREDITS
----------------------
  EasyRadioLink is free software: you can redistribute it and/or modify it
  under the terms of the GNU General Public License version 3 (see
  LICENSE.txt). It comes with ABSOLUTELY NO WARRANTY.

  Source code: https://github.com/DanielBecker715/EasyRadioLink
  The source code of every release is available there under its version
  tag (for example v1.0.0).

  Based on DCS-SimpleRadio Standalone by Ciribob and contributors (GPL-3.0).

  EasyRadioLink uses third-party components under their own licenses, see
  THIRD-PARTY-NOTICES.txt.
