using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using EasyRadioLink.Client.Network.Models;
using EasyRadioLink.Client.Radios;
using EasyRadioLink.Client.Singletons;
using EasyRadioLink.Client.Utils;
using EasyRadioLink.Common.Audio.Models;
using EasyRadioLink.Common.Audio.Utility;
using EasyRadioLink.Common.Models;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Network.Client;
using EasyRadioLink.Common.Network.Singletons;
using EasyRadioLink.Common.Settings;
using EasyRadioLink.Common.Settings.Input;
using EasyRadioLink.Common.Settings.Setting;
using NLog;
using ConnectedClientsSingleton =
    EasyRadioLink.Common.Network.Singletons.ConnectedClientsSingleton;
using RadioReceivingState = EasyRadioLink.Common.Models.RadioReceivingState;

namespace EasyRadioLink.Client.Audio.Managers;

/// <summary>
///     Voice transmit and receive decisions of the client.
///     <list type="bullet">
///         <item>
///             TX (<see cref="Send" />, capture thread): keyed by PTT (hotkey or radio switch as PTT) or VOX; transmits
///             on the selected radio plus every <c>simul</c> radio when simultaneous transmission is on.
///         </item>
///         <item>
///             RX (<see cref="UdpAudioDecode" />, own thread): finds the local radio that hears a packet (frequency,
///             modulation, guard, encryption, half-duplex) and hands the audio to the <see cref="AudioManager" />.
///         </item>
///         <item>PTT state machine (<see cref="PTTHandler" />, input thread every 40 ms).</item>
///     </list>
/// </summary>
public class UDPClientAudioProcessor : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly AudioInputSingleton _audioInputSingleton = AudioInputSingleton.Instance;
    private readonly AudioManager _audioManager;
    private readonly ConnectedClientsSingleton _clients = ConnectedClientsSingleton.Instance;
    private readonly ClientStateSingleton _clientStateSingleton = ClientStateSingleton.Instance;
    private readonly GlobalSettingsStore _globalSettings = GlobalSettingsStore.Instance;
    private readonly string _guid;
    private readonly byte[] _guidAsciiBytes;
    private readonly RadioReceivingState[] _radioReceivingState;
    private readonly SyncedServerSettings _serverSettings = SyncedServerSettings.Instance;
    private readonly UDPVoiceHandler _udpClient;
    private readonly object lockObj = new();
    private long _firstPTTPress; // to delay start PTT time
    private long _lastPTTPress; // to handle dodgy PTT - release time

    // last time VOX detected speech (hang time VOXMinimumTime)
    private long _lastVOXSend;

    private ulong _packetNumber = 1;

    private volatile bool _ptt;
    private CancellationTokenSource _stopFlag;

    // true while VOX keys the radios (read by CurrentlyBlockedRadios for half-duplex)
    private volatile bool _voxActive;

    public UDPClientAudioProcessor(UDPVoiceHandler udpClient, AudioManager audioManager, string guid)
    {
        _udpClient = udpClient;
        _audioManager = audioManager;
        _guid = guid;
        _guidAsciiBytes = Encoding.ASCII.GetBytes(guid);

        _radioReceivingState = _clientStateSingleton.RadioReceivingState;
    }

    // PTT key/radio switch or VOX is keying the radios
    private bool IsTransmitKeyed => _ptt || _voxActive;

    public void Dispose()
    {
        _ptt = false;
        _voxActive = false;
        _stopFlag?.Dispose();
    }

    public void Start()
    {
        _ptt = false;
        _voxActive = false;
        _packetNumber = 1;

        // created before the thread starts so Stop() always reaches it
        _stopFlag = new CancellationTokenSource();
        var token = _stopFlag.Token;

        var decoderThread = new Thread(() => UdpAudioDecode(token)) { IsBackground = true, Name = "Voice decode" };
        decoderThread.Start();
        InputDeviceManager.Instance.StartPTTListening(PTTHandler);
    }

    private ClientInfo IsClientMetaDataValid(string clientGuid)
    {
        if (_clients.TryGetValue(clientGuid, out var client)) return client;

        return null;
    }

    /// <summary>
    ///     Half-duplex (server IRL_RADIO_TX): radios that are transmitting right now cannot receive.
    /// </summary>
    private List<int> CurrentlyBlockedRadios()
    {
        var transmitting = new List<int>();
        if (!_serverSettings.GetSettingAsBool(ServerSettingsKeys.IRL_RADIO_TX)) return transmitting;

        if (!IsTransmitKeyed) return transmitting;

        //Currently transmitting - figure out which radios can't hear
        var radioInfo = _clientStateSingleton.PlayerRadioInfo;
        var selected = radioInfo.selected;

        if (selected >= PlayerRadioInfo.FirstUserRadio && selected < radioInfo.radios.Length)
        {
            var currentRadio = radioInfo.radios[selected];

            // every modulation is half-duplex (AM, FM and DIGITAL)
            if (currentRadio != null && currentRadio.IsEnabled && !currentRadio.rxOnly) transmitting.Add(selected);
        }

        if (radioInfo.simultaneousTransmission)
            for (var i = PlayerRadioInfo.FirstUserRadio; i < radioInfo.radios.Length; i++)
            {
                var radio = radioInfo.radios[i];
                if (radio != null && radio.IsEnabled && radio.simul && !radio.rxOnly && i != selected)
                    transmitting.Add(i);
            }

        return transmitting;
    }

    private int SortRadioReceivingPriorities(RadioReceivingPriority x, RadioReceivingPriority y)
    {
        var xScore = 0;
        var yScore = 0;

        if (x.ReceivingRadio == null || x.ReceivingState == null) return 1;

        if (y.ReceivingRadio == null || y.ReceivingState == null) return -1;

        if (x.Decryptable) xScore += 16;

        if (y.Decryptable) yScore += 16;

        var selected = _clientStateSingleton.PlayerRadioInfo.selected;

        if (selected == x.ReceivingState.ReceivedOn) xScore += 8;

        if (selected == y.ReceivingState.ReceivedOn) yScore += 8;

        if (x.ReceivingRadio.volume > 0) xScore += 4;

        if (y.ReceivingRadio.volume > 0) yScore += 4;

        return yScore - xScore;
    }

    /// <summary>
    ///     Decides which radios transmit this mic frame.
    /// </summary>
    /// <param name="sendingOn">the radio shown as transmitting (selected radio, or the first simul radio)</param>
    /// <param name="voice">
    ///     VOX result of the frame. Always true when VOX is disabled - the VOX setting is checked here.
    /// </param>
    private List<Radio> PTTPressed(out int sendingOn, bool voice)
    {
        sendingOn = -1;

        // VOX keys the selected radio (plus simul radios) while speech is detected and for VOXMinimumTime after it
        var voxActive = false;
        if (_globalSettings.GetClientSettingBool(GlobalSettingsKeys.VOX))
        {
            if (voice)
            {
                _lastVOXSend = DateTime.Now.Ticks;
                voxActive = true;
            }
            else
            {
                voxActive = new TimeSpan(DateTime.Now.Ticks - _lastVOXSend).TotalMilliseconds <
                            _globalSettings.GetClientSettingInt(GlobalSettingsKeys.VOXMinimumTime);
            }
        }

        _voxActive = voxActive;

        var transmittingRadios = new List<Radio>();
        if (!_ptt && !voxActive) return transmittingRadios;

        var radioInfo = _clientStateSingleton.PlayerRadioInfo;

        // Always add currently selected radio (if valid)
        var currentSelected = radioInfo.selected;
        if (currentSelected >= PlayerRadioInfo.FirstUserRadio && currentSelected < radioInfo.radios.Length)
        {
            var currentlySelectedRadio = radioInfo.radios[currentSelected];

            if (CanTransmit(currentlySelectedRadio))
            {
                sendingOn = currentSelected;
                transmittingRadios.Add(currentlySelectedRadio);
            }
        }

        // Add all radios toggled for simultaneous transmission if the global flag has been set
        if (radioInfo.simultaneousTransmission)
            for (var i = PlayerRadioInfo.FirstUserRadio; i < radioInfo.radios.Length; i++)
            {
                var radio = radioInfo.radios[i];
                if (radio != null && radio.simul && CanTransmit(radio)
                    && !transmittingRadios.Contains(radio)) // Make sure we don't add the selected radio twice
                {
                    if (sendingOn == -1) sendingOn = i;
                    transmittingRadios.Add(radio);
                }
            }

        return transmittingRadios;
    }

    private static bool CanTransmit(Radio radio)
    {
        return radio != null && radio.IsEnabled && radio.freq > 100 && !radio.rxOnly;
    }

    public ClientAudio Send(byte[] bytes, int len, bool voice)
    {
        //can only send if the voice connection is up, the radios are loaded and a microphone is available
        var ready = _udpClient.Ready
                    && RadioHelper.RadiosAvailable()
                    && _audioInputSingleton.MicrophoneAvailable
                    && bytes != null;

        // List of radios the transmission is sent to (can me multiple if simultaneous transmission is enabled)
        var sendingOn = -1;
        var transmittingRadios = ready ? PTTPressed(out sendingOn, voice) : null;

        if (!ready) _voxActive = false;

        if (transmittingRadios != null && transmittingRadios.Count > 0)
        {
            try
            {
                var frequencies = new List<double>(transmittingRadios.Count);
                var encryptions = new List<byte>(transmittingRadios.Count);
                var modulations = new List<byte>(transmittingRadios.Count);

                for (var i = 0; i < transmittingRadios.Count; i++)
                {
                    var radio = transmittingRadios[i];
                    var encryption = radio.enc ? radio.encKey : (byte)0;

                    // Further deduplicate transmitted frequencies if they have the same freq./modulation/encryption (caused by differently named radios)
                    var alreadyIncluded = false;
                    for (var j = 0; j < frequencies.Count; j++)
                        if (frequencies[j] == radio.freq
                            && modulations[j] == (byte)radio.modulation
                            && encryptions[j] == encryption)
                        {
                            alreadyIncluded = true;
                            break;
                        }

                    if (alreadyIncluded) continue;

                    frequencies.Add(radio.freq);
                    encryptions.Add(encryption);
                    modulations.Add((byte)radio.modulation);
                }

                //generate packet - UnitId and RetransmissionCount are reserved (always 0)
                var udpVoicePacket = new UDPVoicePacket
                {
                    GuidBytes = _guidAsciiBytes,
                    AudioPart1Bytes = bytes,
                    AudioPart1Length = (ushort)bytes.Length,
                    Frequencies = frequencies.ToArray(),
                    UnitId = 0,
                    Encryptions = encryptions.ToArray(),
                    Modulations = modulations.ToArray(),
                    PacketNumber = _packetNumber++,
                    OriginalClientGuidBytes = _guidAsciiBytes,
                    RetransmissionCount = 0
                };

                _udpClient.Send(udpVoicePacket);

                var sendingRadio = _clientStateSingleton.PlayerRadioInfo.radios[sendingOn];

                //not sending or really quickly switched sending
                if (sendingRadio != null &&
                    (!_clientStateSingleton.RadioSendingState.IsSending ||
                     _clientStateSingleton.RadioSendingState.SendingOn != sendingOn))
                    _audioManager.PlaySoundEffectStartTransmit(sendingOn,
                        sendingRadio.enc && sendingRadio.encKey > 0);

                // radio panel: transmitting indicator
                _clientStateSingleton.RadioSendingState = new RadioSendingState
                {
                    IsSending = true,
                    LastSentAt = DateTime.Now.Ticks,
                    SendingOn = sendingOn
                };

                // local passthrough (mic output device / recording) through the own radio model
                return new ClientAudio
                {
                    Frequency = frequencies[0],
                    Modulation = modulations[0],
                    EncodedAudio = bytes,
                    Encryption = encryptions[0],
                    Volume = 1,
                    Decryptable = true,
                    ReceivedRadio = sendingOn,
                    PacketNumber = _packetNumber,
                    ReceiveTime = DateTime.Now.Ticks,
                    OriginalClientGuid = _guid,
                    Ambient = _clientStateSingleton.PlayerRadioInfo.ambient
                };
            }
            catch (Exception e)
            {
                Logger.Error(e, "Exception Sending Audio Message " + e.Message);
            }
        }
        else
        {
            if (_clientStateSingleton.RadioSendingState.IsSending)
            {
                _clientStateSingleton.RadioSendingState.IsSending = false;

                var sentOn = _clientStateSingleton.RadioSendingState.SendingOn;
                if (sentOn >= PlayerRadioInfo.FirstUserRadio &&
                    sentOn < _clientStateSingleton.PlayerRadioInfo.radios.Length)
                    _audioManager.PlaySoundEffectEndTransmit(sentOn);
            }
        }

        return null;
    }


    private void UdpAudioDecode(CancellationToken token)
    {
        while (true)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var encodedOpusAudio = _udpClient.EncodedAudio.Take(token);

                if (encodedOpusAudio != null
                    && encodedOpusAudio.Length >=
                    UDPVoicePacket.PacketHeaderLength + UDPVoicePacket.FixedPacketLength +
                    UDPVoicePacket.FrequencySegmentLength)
                {
                    // only play audio once this client is registered and the radios are loaded
                    var myClient = IsClientMetaDataValid(_guid);

                    if (myClient != null && RadioHelper.RadiosAvailable())
                    {
                        //Decode bytes
                        var udpVoicePacket = UDPVoicePacket.DecodeVoicePacket(encodedOpusAudio);

                        if (udpVoicePacket != null) ProcessVoicePacket(udpVoicePacket);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Info("UDP Audio Decode - Stopping.");
                break;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    Logger.Error(ex, "Failed to decode audio from Packet");
            }
        }
    }

    private void ProcessVoicePacket(UDPVoicePacket udpVoicePacket)
    {
        var radioInfo = _clientStateSingleton.PlayerRadioInfo;
        var frequencyCount = udpVoicePacket.Frequencies.Length;

        var radioReceivingPriorities = new List<RadioReceivingPriority>(frequencyCount);
        var blockedRadios = CurrentlyBlockedRadios();

        var strictEncryption = _serverSettings.GetSettingAsBool(ServerSettingsKeys.STRICT_RADIO_ENCRYPTION);

        // Parse frequencies into receiving radio priority for selection below
        for (var i = 0; i < frequencyCount; i++)
        {
            var radio = radioInfo.CanHearTransmission(
                udpVoicePacket.Frequencies[i],
                (Modulation)udpVoicePacket.Modulations[i],
                udpVoicePacket.Encryptions[i],
                strictEncryption,
                blockedRadios,
                out var state,
                out var decryptable);

            // CanHearTransmission may return a blocked radio as a non-decryptable "best match" - drop it
            if (radio != null && state != null && !blockedRadios.Contains(state.ReceivedOn))
                radioReceivingPriorities.Add(new RadioReceivingPriority
                {
                    Decryptable = decryptable,
                    Encryption = udpVoicePacket.Encryptions[i],
                    Frequency = udpVoicePacket.Frequencies[i],
                    Modulation = udpVoicePacket.Modulations[i],
                    ReceivingRadio = radio,
                    ReceivingState = state
                });
        }

        if (radioReceivingPriorities.Count == 0) return;

        // Sort receiving radios to play audio on correct one
        radioReceivingPriorities.Sort(SortRadioReceivingPriorities);

        var interference = _serverSettings.GetSettingAsBool(ServerSettingsKeys.IRL_RADIO_RX_INTERFERENCE);

        for (var i = 0; i < radioReceivingPriorities.Count; i++)
        {
            var destinationRadio = radioReceivingPriorities[i];
            var isSimultaneousTransmission = radioReceivingPriorities.Count > 1 && i > 0;

            var audio = new ClientAudio
            {
                ClientGuid = udpVoicePacket.Guid,
                EncodedAudio = udpVoicePacket.AudioPart1Bytes,
                ReceiveTime = DateTime.Now.Ticks,
                Frequency = destinationRadio.Frequency,
                Modulation = destinationRadio.Modulation,
                Volume = VolumeConversionHelper.ConvertRadioVolumeSlider(destinationRadio.ReceivingRadio.volume),
                ReceivedRadio = destinationRadio.ReceivingState.ReceivedOn,
                Encryption = destinationRadio.Encryption,
                Decryptable = destinationRadio.Decryptable, // mark if we can decrypt it
                PacketNumber = udpVoicePacket.PacketNumber,
                OriginalClientGuid = udpVoicePacket.OriginalClientGuid
                // NoAudioEffects: the server's clean frequencies are applied by ClientAudioProvider
            };

            var transmitterName = "";
            if (_clients.TryGetValue(udpVoicePacket.Guid, out var transmittingClient))
            {
                //skip receiving this audio
                if (transmittingClient.Muted) continue;

                if (_serverSettings.GetSettingAsBool(ServerSettingsKeys.SHOW_TRANSMITTER_NAME)
                    && _globalSettings.GetClientSettingBool(GlobalSettingsKeys.ShowTransmitterName))
                    transmitterName = transmittingClient.Name;

                // background sound of the sender (may be null)
                audio.Ambient = transmittingClient.RadioInfo?.ambient;
            }

            _radioReceivingState[audio.ReceivedRadio] = new RadioReceivingState
            {
                IsSecondary = destinationRadio.ReceivingState.IsSecondary,
                IsSimultaneous = isSimultaneousTransmission,
                LastReceivedAt = DateTime.Now.Ticks,
                ReceivedOn = destinationRadio.ReceivingState.ReceivedOn,
                SentBy = transmitterName ?? ""
            };

            //we now WANT to duplicate through multiple pipelines ONLY if radio interference is on
            //this is a nice optimisation to save duplicated audio on servers without that setting
            if (i == 0 || interference) _audioManager.AddClientAudio(audio);
        }
    }

    private void PTTHandler(List<InputBindState> pressed)
    {
        var radios = _clientStateSingleton.PlayerRadioInfo;

        var radioSwitchPtt =
            _globalSettings.ProfileSettingsStore.GetClientSettingBool(ProfileSettingsKeys.RadioSwitchIsPTT);
        var radioSwitchPttWhenValid =
            _globalSettings.ProfileSettingsStore.GetClientSettingBool(ProfileSettingsKeys
                .RadioSwitchIsPTTOnlyWhenValid);

        //store the current PTT state and radios
        var currentRadioId = radios.selected;

        var ptt = false;
        foreach (var inputBindState in pressed)
            if (inputBindState.IsActive)
            {
                //radio switch? (SwitchN = 100 + radio index)
                if ((int)inputBindState.MainDevice.InputBind >= (int)InputBinding.Switch1 &&
                    (int)inputBindState.MainDevice.InputBind <= (int)InputBinding.Switch10)
                {
                    //gives you radio id if you minus 100
                    var radioId = (int)inputBindState.MainDevice.InputBind - 100;

                    if (radioId < radios.radios.Length)
                    {
                        if (RadioHelper.SelectRadio(radioId))
                        {
                            //turn on PTT
                            if (radioSwitchPttWhenValid || radioSwitchPtt)
                            {
                                _lastPTTPress = DateTime.Now.Ticks;
                                ptt = true;
                                //Store last release time
                            }
                        }
                        else
                        {
                            //turn on PTT even if not valid radio switch
                            if (radioSwitchPtt)
                            {
                                _lastPTTPress = DateTime.Now.Ticks;
                                ptt = true;
                            }
                        }
                    }
                }
                else if (inputBindState.MainDevice.InputBind == InputBinding.Ptt)
                {
                    _lastPTTPress = DateTime.Now.Ticks;
                    ptt = true;
                }
            }

        /**
         * Handle DELAYING PTT START
         */

        if (!ptt)
            //reset
            _firstPTTPress = -1;

        if (_firstPTTPress == -1 && ptt) _firstPTTPress = DateTime.Now.Ticks;

        if (ptt)
        {
            //should inhibit for a bit
            var startDiff = new TimeSpan(DateTime.Now.Ticks - _firstPTTPress);

            var startInhibit = _globalSettings.ProfileSettingsStore
                .GetClientSettingFloat(ProfileSettingsKeys.PTTStartDelay);

            if (startDiff.TotalMilliseconds < startInhibit)
            {
                _ptt = false;
                _lastPTTPress = -1;
                return;
            }
        }

        /**
         * End Handle DELAYING PTT START
         */


        /**
         * Start Handle PTT HOLD after release
         */

        //if length is zero - no keybinds or no PTT pressed set to false
        var diff = new TimeSpan(DateTime.Now.Ticks - _lastPTTPress);

        //Release the PTT ONLY if X ms have passed and we didnt switch radios to handle
        //shitty buttons
        var releaseTime = _globalSettings.ProfileSettingsStore
            .GetClientSettingFloat(ProfileSettingsKeys.PTTReleaseDelay);

        if (!ptt
            && releaseTime > 0
            && diff.TotalMilliseconds <= releaseTime
            && currentRadioId == radios.selected)
            ptt = true;

        /**
         * End Handle PTT HOLD after release
         */

        _ptt = ptt;
    }

    public void Stop()
    {
        lock (lockObj)
        {
            _stopFlag?.Cancel();
            _ptt = false;
            _voxActive = false;
            _clientStateSingleton.RadioSendingState.IsSending = false;
            InputDeviceManager.Instance.StopListening();
        }
    }
}
