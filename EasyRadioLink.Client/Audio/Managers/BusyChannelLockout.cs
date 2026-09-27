using System;
using EasyRadioLink.Common.Models;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Network.Server;

namespace EasyRadioLink.Client.Audio.Managers;

/// <summary>
///     Client side of the busy channel lockout ("one speaker per frequency", server setting BUSY_CHANNEL_LOCKOUT). The
///     server decides who talks (<see cref="BusyChannelArbiter" />); the client makes it audible and visible, like a
///     real radio with busy channel lockout:
///     <list type="bullet">
///         <item>
///             Pre-check: a push-to-talk press that starts while another station was heard on the radio's channel
///             within the hang time (<see cref="HangTimeMilliseconds" />, the server's) does not transmit for the whole
///             press - release and press again. The busy tone plays once and BUSY flashes.
///         </item>
///         <item>
///             VOX does not transmit while the channel is busy; the busy tone plays at most once per
///             <see cref="VoxToneIntervalMilliseconds" />.
///         </item>
///         <item>
///             Race (both pressed at once): while transmitting, voice of another station on the own channel means the
///             server gave the channel to them (it only forwards the holder). The transmission stops for the rest of
///             the press, busy tone once. The own radio check echo is not another station
///             (<see cref="IsOtherStationOnChannel" />), and voice handled within <see cref="RaceGraceMilliseconds" />
///             after the transmission started does not count (it was on its way before the key-up).
///         </item>
///     </list>
///     Pure logic: the caller passes the time (<see cref="NowMilliseconds" />, the server's clock of the lockout) and
///     whether the lockout is on. Thread-safe: the decode thread reports voice (<see cref="OnVoiceHeard" />), the
///     capture thread decides every mic frame (<see cref="Decide" />), the radio window reads the indicator.
/// </summary>
public sealed class BusyChannelLockout
{
    /// <summary>What happens with a mic frame.</summary>
    public enum Decision
    {
        /// <summary>Nothing keys the radio.</summary>
        Idle,

        /// <summary>Transmit the frame.</summary>
        Transmit,

        /// <summary>Keyed, but the channel is busy (refused press, lost race, VOX while busy): do not transmit.</summary>
        Blocked
    }

    /// <summary>A channel is busy this long after the last voice of another station (the server's hang time).</summary>
    public const int HangTimeMilliseconds = BusyChannelArbiter.HangTimeMilliseconds;

    /// <summary>The clock to pass in: milliseconds, high resolution, monotonic (<see cref="BusyChannelArbiter.NowMilliseconds" />).</summary>
    public static long NowMilliseconds => BusyChannelArbiter.NowMilliseconds;

    /// <summary>
    ///     Voice of another station handled this soon after the own transmission started is not a lost race: it left
    ///     the other station before the key-up (the pre-check had not seen it yet).
    /// </summary>
    public const int RaceGraceMilliseconds = 60;

    /// <summary>VOX: at most one busy tone per this interval.</summary>
    public const int VoxToneIntervalMilliseconds = 1000;

    /// <summary>BUSY flashes this long after a refused press or a lost race.</summary>
    public const int RefusedFlashMilliseconds = 1200;

    /// <summary>On / off time of the flashing BUSY.</summary>
    public const int FlashPeriodMilliseconds = 160;

    private readonly object _lock = new();

    // last voice of another station on the radio's channel
    private bool _heard;
    private long _heardAt;
    private double _heardFrequency;
    private Modulation _heardModulation;

    // the current key-up (PTT press or VOX)
    private bool _pttHeld;
    private bool _pressRefused;
    private bool _transmitting;
    private long _transmitStartedAt;
    private double _transmitFrequency;
    private Modulation _transmitModulation;
    private bool _raceLost;

    private bool _toneEver;
    private long _lastToneAt;
    private bool _refusedEver;
    private long _lastRefusedAt;

    /// <summary>
    ///     True if <paramref name="packet" /> is voice of ANOTHER station (not this client's own radio check echo) on
    ///     the channel <paramref name="frequency" /> / <paramref name="modulation" /> (one of its frequencies within
    ///     <see cref="RadioBase.FreqCloseEnough" />, same modulation).
    /// </summary>
    public static bool IsOtherStationOnChannel(UDPVoicePacket packet, string ownGuid, double frequency,
        Modulation modulation)
    {
        if (packet?.Frequencies == null || packet.Modulations == null) return false;

        // the radio check echo of the own transmission
        if (string.Equals(packet.Guid, ownGuid, StringComparison.Ordinal) ||
            string.Equals(packet.OriginalClientGuid, ownGuid, StringComparison.Ordinal))
            return false;

        var count = Math.Min(packet.Frequencies.Length, packet.Modulations.Length);
        for (var i = 0; i < count; i++)
            if (packet.Modulations[i] == (byte)modulation && RadioBase.FreqCloseEnough(packet.Frequencies[i], frequency))
                return true;

        return false;
    }

    /// <summary>Forgets everything (new connection).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _heard = false;
            _pttHeld = false;
            _pressRefused = false;
            _transmitting = false;
            _raceLost = false;
            _toneEver = false;
            _refusedEver = false;
        }
    }

    /// <summary>
    ///     Decode thread: voice of another station arrived on the radio's channel (see
    ///     <see cref="IsOtherStationOnChannel" />) - before the half-duplex check, a transmitting radio notices it too.
    /// </summary>
    public void OnVoiceHeard(double frequency, Modulation modulation, long nowMilliseconds)
    {
        lock (_lock)
        {
            _heard = true;
            _heardAt = nowMilliseconds;
            _heardFrequency = frequency;
            _heardModulation = modulation;

            // the server forwards only the holder: while we transmit on this channel, somebody else got it
            if (_transmitting && nowMilliseconds - _transmitStartedAt >= RaceGraceMilliseconds &&
                SameChannel(_transmitFrequency, _transmitModulation, frequency, modulation))
                _raceLost = true;
        }
    }

    /// <summary>
    ///     Capture thread, every mic frame: decides whether the frame is transmitted.
    /// </summary>
    /// <param name="enabled">The server's lockout is on (<c>SyncedServerSettings.BusyChannelLockout</c>).</param>
    /// <param name="ptt">Push-to-talk is pressed (after the start / release delays).</param>
    /// <param name="vox">VOX keys the radio.</param>
    /// <param name="frequency">The radio's frequency (Hz).</param>
    /// <param name="modulation">The radio's modulation.</param>
    /// <param name="nowMilliseconds">Monotonic clock.</param>
    /// <param name="playBusyTone">True: play the busy tone now (a press was refused, a race lost, VOX blocked).</param>
    public Decision Decide(bool enabled, bool ptt, bool vox, double frequency, Modulation modulation,
        long nowMilliseconds, out bool playBusyTone)
    {
        playBusyTone = false;

        lock (_lock)
        {
            if (!ptt && !vox)
            {
                EndKeyUp();
                return Decision.Idle;
            }

            if (!enabled)
            {
                // as before 1.3: whoever keys, transmits
                _pttHeld = ptt;
                _pressRefused = false;
                _raceLost = false;
                StartTransmitting(frequency, modulation, nowMilliseconds);
                return Decision.Transmit;
            }

            if (ptt)
            {
                if (!_pttHeld)
                {
                    // a new push-to-talk press: refused for the whole press if the channel is busy
                    _pttHeld = true;
                    _pressRefused = false;
                    _raceLost = false;

                    if (IsBusyLocked(frequency, modulation, nowMilliseconds))
                    {
                        Refuse(nowMilliseconds);
                        playBusyTone = true;
                        return Decision.Blocked;
                    }
                }

                if (_pressRefused) return Decision.Blocked;

                if (_raceLost)
                {
                    // somebody else got the channel: silent for the rest of this press
                    Refuse(nowMilliseconds);
                    playBusyTone = true;
                    return Decision.Blocked;
                }

                StartTransmitting(frequency, modulation, nowMilliseconds);
                return Decision.Transmit;
            }

            // VOX only: no transmission while the channel is busy (a lost race leaves it busy, too)
            _pttHeld = false;
            _pressRefused = false;

            if (_raceLost || IsBusyLocked(frequency, modulation, nowMilliseconds))
            {
                _raceLost = false;
                _transmitting = false;

                if (!_toneEver || nowMilliseconds - _lastToneAt >= VoxToneIntervalMilliseconds)
                {
                    playBusyTone = true;
                    _toneEver = true;
                    _lastToneAt = nowMilliseconds;
                    _refusedEver = true;
                    _lastRefusedAt = nowMilliseconds;
                }

                return Decision.Blocked;
            }

            StartTransmitting(frequency, modulation, nowMilliseconds);
            return Decision.Transmit;
        }
    }

    /// <summary>
    ///     True while another station was heard on the channel within the hang time (the BUSY indicator is lit; a
    ///     push-to-talk press now would be refused).
    /// </summary>
    public bool IsChannelBusy(double frequency, Modulation modulation, long nowMilliseconds)
    {
        lock (_lock)
        {
            return IsBusyLocked(frequency, modulation, nowMilliseconds);
        }
    }

    /// <summary>
    ///     Radio window: BUSY is lit. It flashes for <see cref="RefusedFlashMilliseconds" /> after a refused press or a
    ///     lost race and is lit steadily while the channel is busy.
    /// </summary>
    public bool IsIndicatorLit(double frequency, Modulation modulation, long nowMilliseconds)
    {
        lock (_lock)
        {
            if (_refusedEver)
            {
                var sinceRefused = nowMilliseconds - _lastRefusedAt;
                if (sinceRefused >= 0 && sinceRefused < RefusedFlashMilliseconds)
                    return sinceRefused / FlashPeriodMilliseconds % 2 == 0;
            }

            return IsBusyLocked(frequency, modulation, nowMilliseconds);
        }
    }

    /// <summary>True while the current push-to-talk press is refused (it transmits nothing until it is released).</summary>
    public bool IsPressRefused
    {
        get
        {
            lock (_lock)
            {
                return _pressRefused;
            }
        }
    }

    /// <summary>
    ///     Half-duplex (decode thread): true if a key-up that is not transmitting yet meets a busy channel - the next
    ///     mic frame refuses it (<see cref="Decide" />), so the radio should keep receiving the other station in the
    ///     meantime instead of dropping up to a frame of it. False while the radio transmits.
    /// </summary>
    public bool WillRefuseKeyUp(double frequency, Modulation modulation, long nowMilliseconds)
    {
        lock (_lock)
        {
            return !_transmitting && IsBusyLocked(frequency, modulation, nowMilliseconds);
        }
    }

    /// <summary>
    ///     Capture thread: still keyed, but nothing can be sent right now (voice link not ready, radio off or not
    ///     tuned). Nothing is transmitted, but the key-up goes on: a refused push-to-talk press stays refused until it is
    ///     released (<see cref="Decide" /> with neither PTT nor VOX ends it).
    /// </summary>
    public void KeyedWithoutTransmitter()
    {
        lock (_lock)
        {
            _transmitting = false;
            _raceLost = false;
        }
    }

    private bool IsBusyLocked(double frequency, Modulation modulation, long nowMilliseconds)
    {
        return _heard && nowMilliseconds - _heardAt < HangTimeMilliseconds &&
               SameChannel(_heardFrequency, _heardModulation, frequency, modulation);
    }

    private static bool SameChannel(double frequency, Modulation modulation, double otherFrequency,
        Modulation otherModulation)
    {
        return modulation == otherModulation && RadioBase.FreqCloseEnough(frequency, otherFrequency);
    }

    private void Refuse(long nowMilliseconds)
    {
        _pressRefused = true;
        _raceLost = false;
        _transmitting = false;
        _toneEver = true;
        _lastToneAt = nowMilliseconds;
        _refusedEver = true;
        _lastRefusedAt = nowMilliseconds;
    }

    private void StartTransmitting(double frequency, Modulation modulation, long nowMilliseconds)
    {
        // a new transmission, or retuned while transmitting: the grace for voice that was on its way starts again
        if (!_transmitting || !SameChannel(_transmitFrequency, _transmitModulation, frequency, modulation))
        {
            _transmitting = true;
            _transmitStartedAt = nowMilliseconds;
            _raceLost = false;
        }

        _transmitFrequency = frequency;
        _transmitModulation = modulation;
    }

    private void EndKeyUp()
    {
        _pttHeld = false;
        _pressRefused = false;
        _transmitting = false;
        _raceLost = false;
    }
}
