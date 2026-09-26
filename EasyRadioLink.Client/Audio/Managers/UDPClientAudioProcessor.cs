using System;
using System.Collections.Generic;
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
using EasyRadioLink.Common.Network.Crypto;
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
///             frequency and modulation. Every frame is end-to-end encrypted (<see cref="E2EVoiceSession" />): each
///             PTT press is a new transmission whose key only the listeners on the frequency receive.
///         </item>
///         <item>
///             RX (<see cref="UdpAudioDecode" />, own thread): checks whether the radio hears a packet (frequency,
///             modulation, half-duplex), decrypts it with the transmission key (a packet whose key has not arrived yet
///             waits up to <see cref="E2EVoiceReceiver.HoldTime" />; without a key it is played scrambled) and hands the
///             audio to the <see cref="AudioManager" />.
///         </item>
///         <item>PTT state machine (<see cref="PTTHandler" />, input thread every 40 ms).</item>
///     </list>
/// </summary>
public class UDPClientAudioProcessor : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    // the decode thread wakes up at least this often to release packets that waited for their key
    private const int ReceivePollIntervalMs = 20;

    private readonly AudioInputSingleton _audioInputSingleton = AudioInputSingleton.Instance;
    private readonly AudioManager _audioManager;
    private readonly ConnectedClientsSingleton _clients = ConnectedClientsSingleton.Instance;
    private readonly ClientStateSingleton _clientStateSingleton = ClientStateSingleton.Instance;
    private readonly GlobalSettingsStore _globalSettings = GlobalSettingsStore.Instance;
    private readonly string _guid;
    private readonly RadioReceivingState[] _radioReceivingState;
    private readonly SyncedServerSettings _serverSettings = SyncedServerSettings.Instance;
    private readonly UDPVoiceHandler _udpClient;
    private readonly E2EVoiceSession _voiceSession;
    private readonly object lockObj = new();
    private bool _encryptionFailureLogged;
    private long _firstPTTPress; // to delay start PTT time
    private long _lastPTTPress; // to handle dodgy PTT - release time

    // last time VOX detected speech (hang time VOXMinimumTime)
    private long _lastVOXSend;

    private ulong _packetNumber = 1;

    private volatile bool _ptt;
    private CancellationTokenSource _stopFlag;

    // true while VOX keys the radios (read by CurrentlyBlockedRadios for half-duplex)
    private volatile bool _voxActive;

    public UDPClientAudioProcessor(UDPVoiceHandler udpClient, AudioManager audioManager, string guid,
        E2EVoiceSession voiceSession)
    {
        _udpClient = udpClient;
        _audioManager = audioManager;
        _guid = guid;
        _voiceSession = voiceSession ?? throw new ArgumentNullException(nameof(voiceSession));

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

                // end-to-end encrypted with the key of this transmission (UnitId, hop count and the legacy encryption
                // byte are 0); only listeners on the frequency receive the key
                var udpVoicePacket = _voiceSession.CreateVoicePacket(bytes, _packetNumber++, frequency,
                    sendingRadio.modulation);

                if (udpVoicePacket == null)
                {
                    // fail closed: never send a frame that is not encrypted
                    if (!_encryptionFailureLogged)
                        Logger.Warn("Voice frame not sent: the voice encryption of this connection is closed");
                    _encryptionFailureLogged = true;
                    return null;
                }

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
            // the next PTT press is a new transmission with a new key
            _voiceSession.EndTransmission();

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
        var frames = new List<ReceivedVoiceFrame>();

        while (true)
        {
            try
            {
                token.ThrowIfCancellationRequested();

                // wait for the next packet, but wake up regularly: packets that wait for their key are released by
                // the clock (decrypted once the key arrived, scrambled after E2EVoiceReceiver.HoldTime)
                if (_udpClient.EncodedAudio.TryTake(out var encodedOpusAudio, ReceivePollIntervalMs, token))
                    ReceivePacket(encodedOpusAudio, frames);

                _voiceSession.Receiver.Poll(DateTime.UtcNow, frames);

                foreach (var frame in frames) ProcessVoicePacket(frame.Packet, frame.Opus);
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
            finally
            {
                frames.Clear();
            }
        }
    }

    /// <summary>A decrypted (hop layer) datagram body: decoded, checked and handed to the end-to-end decryption.</summary>
    private void ReceivePacket(byte[] encodedOpusAudio, List<ReceivedVoiceFrame> frames)
    {
        if (encodedOpusAudio == null
            || encodedOpusAudio.Length <
            UDPVoicePacket.PacketHeaderLength + UDPVoicePacket.FixedPacketLength +
            UDPVoicePacket.FrequencySegmentLength)
            return;

        // only play audio once this client is registered and the radios are loaded
        if (IsClientMetaDataValid(_guid) == null || !RadioHelper.RadiosAvailable()) return;

        // strict checks (the body was authenticated by the UDPVoiceHandler)
        if (!UDPVoicePacket.TryDecode(encodedOpusAudio, true, out var udpVoicePacket)) return;

        // nothing is decrypted or held for a frequency this radio does not hear
        if (FindReceivingRadio(udpVoicePacket, out _, out _, out _) == null) return;

        _voiceSession.Receiver.Receive(udpVoicePacket, DateTime.UtcNow, frames);
    }

    /// <summary>The radio that hears the packet: its first frequency the radio is tuned to.</summary>
    private Radio FindReceivingRadio(UDPVoicePacket udpVoicePacket, out RadioReceivingState receivingState,
        out double receivedFrequency, out byte receivedModulation)
    {
        var radioInfo = _clientStateSingleton.PlayerRadioInfo;
        var blockedRadios = CurrentlyBlockedRadios();

        receivingState = null;
        receivedFrequency = 0d;
        receivedModulation = 0;

        for (var i = 0; i < udpVoicePacket.Frequencies.Length; i++)
        {
            // the legacy encryption byte is always 0 (the end-to-end receiver drops anything else)
            var radio = radioInfo.CanHearTransmission(
                udpVoicePacket.Frequencies[i],
                (Modulation)udpVoicePacket.Modulations[i],
                0,
                blockedRadios,
                out var state,
                out _);

            if (radio == null || state == null) continue;

            receivingState = state;
            receivedFrequency = udpVoicePacket.Frequencies[i];
            receivedModulation = udpVoicePacket.Modulations[i];
            return radio;
        }

        return null;
    }

    /// <param name="opus">The decrypted Opus frame; null = its key never arrived: play one frame of scrambled audio.</param>
    private void ProcessVoicePacket(UDPVoicePacket udpVoicePacket, byte[] opus)
    {
        var receivingRadio = FindReceivingRadio(udpVoicePacket, out var receivingState, out var receivedFrequency,
            out var receivedModulation);

        if (receivingRadio == null) return;

        var scrambled = opus == null;

        var audio = new ClientAudio
        {
            ClientGuid = udpVoicePacket.Guid,
            // scrambled: no audio - ClientAudioProvider plays one frame of the scrambled radio effect instead
            EncodedAudio = opus,
            ReceiveTime = DateTime.Now.Ticks,
            Frequency = receivedFrequency,
            Modulation = receivedModulation,
            Volume = VolumeConversionHelper.ConvertRadioVolumeSlider(receivingRadio.volume),
            ReceivedRadio = receivingState.ReceivedOn,
            Encryption = scrambled ? (short)1 : (short)0,
            Decryptable = !scrambled,
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
            _voiceSession.EndTransmission();
            _clientStateSingleton.RadioSendingState.IsSending = false;
            InputDeviceManager.Instance.StopListening();
        }
    }
}
