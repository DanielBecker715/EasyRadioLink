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
///     Voice transmit and receive decisions of the client (one radio, slot <see cref="PlayerRadioInfo.RadioId" />).
///     <list type="bullet">
///         <item>
///             TX (<see cref="Send" />, capture thread): keyed by the PTT hotkey or VOX; transmits on the radio's
///             frequency and modulation, never encrypted.
///         </item>
///         <item>
///             RX (<see cref="UdpAudioDecode" />, own thread): checks whether the radio hears a packet (frequency,
///             modulation, half-duplex) and hands the audio to the <see cref="AudioManager" />.
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

    // PTT key or VOX is keying the radio
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
    ///     Half-duplex (server IRL_RADIO_TX): the radio cannot receive while it is transmitting.
    /// </summary>
    private List<int> CurrentlyBlockedRadios()
    {
        var transmitting = new List<int>();
        if (!_serverSettings.GetSettingAsBool(ServerSettingsKeys.IRL_RADIO_TX)) return transmitting;

        if (!IsTransmitKeyed) return transmitting;

        // every modulation is half-duplex (AM, FM and DIGITAL)
        if (CanTransmit(_clientStateSingleton.PlayerRadioInfo.Radio)) transmitting.Add(PlayerRadioInfo.RadioId);

        return transmitting;
    }

    /// <summary>
    ///     Decides whether this mic frame is transmitted.
    /// </summary>
    /// <param name="voice">
    ///     VOX result of the frame. Always true when VOX is disabled - the VOX setting is checked here.
    /// </param>
    /// <returns>The transmitting radio, or null.</returns>
    private Radio PTTPressed(bool voice)
    {
        // VOX keys the radio while speech is detected and for VOXMinimumTime after it
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

        if (!_ptt && !voxActive) return null;

        var radio = _clientStateSingleton.PlayerRadioInfo.Radio;
        return CanTransmit(radio) ? radio : null;
    }

    private static bool CanTransmit(Radio radio)
    {
        return radio != null && radio.IsEnabled && radio.freq > 100;
    }

    public ClientAudio Send(byte[] bytes, int len, bool voice)
    {
        //can only send if the voice connection is up, the radios are loaded and a microphone is available
        var ready = _udpClient.Ready
                    && RadioHelper.RadiosAvailable()
                    && _audioInputSingleton.MicrophoneAvailable
                    && bytes != null;

        var sendingRadio = ready ? PTTPressed(voice) : null;

        if (!ready) _voxActive = false;

        const int sendingOn = PlayerRadioInfo.RadioId;

        if (sendingRadio != null)
        {
            try
            {
                var frequency = sendingRadio.freq;
                var modulation = (byte)sendingRadio.modulation;

                //generate packet - UnitId and RetransmissionCount are reserved (always 0), never encrypted
                var udpVoicePacket = new UDPVoicePacket
                {
                    GuidBytes = _guidAsciiBytes,
                    AudioPart1Bytes = bytes,
                    AudioPart1Length = (ushort)bytes.Length,
                    Frequencies = new[] { frequency },
                    UnitId = 0,
                    Encryptions = new byte[] { 0 },
                    Modulations = new[] { modulation },
                    PacketNumber = _packetNumber++,
                    OriginalClientGuidBytes = _guidAsciiBytes,
                    RetransmissionCount = 0
                };

                _udpClient.Send(udpVoicePacket);

                //not sending yet
                if (!_clientStateSingleton.RadioSendingState.IsSending)
                    _audioManager.PlaySoundEffectStartTransmit(sendingOn);

                // radio window: transmitting indicator
                _clientStateSingleton.RadioSendingState = new RadioSendingState
                {
                    IsSending = true,
                    LastSentAt = DateTime.Now.Ticks,
                    SendingOn = sendingOn
                };

                // local passthrough (mic output device / recording) through the own radio model
                return new ClientAudio
                {
                    Frequency = frequency,
                    Modulation = modulation,
                    EncodedAudio = bytes,
                    Encryption = 0,
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

                _audioManager.PlaySoundEffectEndTransmit(sendingOn);
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
        var blockedRadios = CurrentlyBlockedRadios();

        // the first frequency of the packet the radio hears - an unencrypted one wins
        Radio receivingRadio = null;
        RadioReceivingState receivingState = null;
        var receivedFrequency = 0d;
        byte receivedModulation = 0;
        byte receivedEncryption = 0;
        var decryptable = false;

        for (var i = 0; i < udpVoicePacket.Frequencies.Length; i++)
        {
            var radio = radioInfo.CanHearTransmission(
                udpVoicePacket.Frequencies[i],
                (Modulation)udpVoicePacket.Modulations[i],
                udpVoicePacket.Encryptions[i],
                blockedRadios,
                out var state,
                out var canDecrypt);

            if (radio == null || state == null) continue;

            if (receivingRadio == null || (canDecrypt && !decryptable))
            {
                receivingRadio = radio;
                receivingState = state;
                receivedFrequency = udpVoicePacket.Frequencies[i];
                receivedModulation = udpVoicePacket.Modulations[i];
                receivedEncryption = udpVoicePacket.Encryptions[i];
                decryptable = canDecrypt;
            }

            if (decryptable) break;
        }

        if (receivingRadio == null) return;

        var audio = new ClientAudio
        {
            ClientGuid = udpVoicePacket.Guid,
            EncodedAudio = udpVoicePacket.AudioPart1Bytes,
            ReceiveTime = DateTime.Now.Ticks,
            Frequency = receivedFrequency,
            Modulation = receivedModulation,
            Volume = VolumeConversionHelper.ConvertRadioVolumeSlider(receivingRadio.volume),
            ReceivedRadio = receivingState.ReceivedOn,
            Encryption = receivedEncryption,
            Decryptable = decryptable,
            PacketNumber = udpVoicePacket.PacketNumber,
            OriginalClientGuid = udpVoicePacket.OriginalClientGuid
            // NoAudioEffects: the server's clean frequencies are applied by ClientAudioProvider
        };

        var transmitterName = "";
        if (_clients.TryGetValue(udpVoicePacket.Guid, out var transmittingClient))
        {
            //skip receiving this audio
            if (transmittingClient.Muted) return;

            if (_serverSettings.GetSettingAsBool(ServerSettingsKeys.SHOW_TRANSMITTER_NAME)
                && _globalSettings.GetClientSettingBool(GlobalSettingsKeys.ShowTransmitterName))
                transmitterName = transmittingClient.Name;

            // background sound of the sender (may be null)
            audio.Ambient = transmittingClient.RadioInfo?.ambient;
        }

        _radioReceivingState[audio.ReceivedRadio] = new RadioReceivingState
        {
            IsSecondary = false,
            IsSimultaneous = false,
            LastReceivedAt = DateTime.Now.Ticks,
            ReceivedOn = receivingState.ReceivedOn,
            SentBy = transmitterName ?? ""
        };

        _audioManager.AddClientAudio(audio);
    }

    private void PTTHandler(List<InputBindState> pressed)
    {
        var ptt = false;
        foreach (var inputBindState in pressed)
            if (inputBindState.IsActive && inputBindState.MainDevice.InputBind == InputBinding.Ptt)
            {
                _lastPTTPress = DateTime.Now.Ticks;
                ptt = true;
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

        //Release the PTT ONLY if X ms have passed, to handle bouncing buttons
        var releaseTime = _globalSettings.ProfileSettingsStore
            .GetClientSettingFloat(ProfileSettingsKeys.PTTReleaseDelay);

        if (!ptt
            && releaseTime > 0
            && diff.TotalMilliseconds <= releaseTime)
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
