using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using EasyRadioLink.Common;
using EasyRadioLink.Common.Models;
using EasyRadioLink.Common.Models.Player;

namespace EasyRadioLink.Client.Radios;

/// <summary>
///     The local user's radio. The network format keeps <see cref="Constants.MAX_RADIOS" /> slots, but only slot
///     <see cref="RadioId" /> (1) is ever used: slot 0 is reserved and slots 2..10 are always
///     <see cref="Modulation.DISABLED" />.
///     One instance lives for the whole application in <c>ClientStateSingleton.PlayerRadioInfo</c>; it is filled by
///     <see cref="RadioStateSyncService" /> after connecting and changed by the user through <c>RadioHelper</c>.
/// </summary>
public class PlayerRadioInfo
{
    /// <summary>Slot of THE radio.</summary>
    public const int RadioId = Constants.FIRST_RADIO_INDEX;

    // own background sound, sent to the server so other users hear it (abType is a name from
    // CachedAudioEffectProvider.AvailableBackgroundSounds, "" = none)
    public Ambient ambient = new()
    {
        vol = 0.0f,
        abType = ""
    };

    // RadioHelper replaces (never modifies) the radio in slot RadioId on every change, so other threads read it once
    // into a local and use that snapshot
    public Radio[] radios = new Radio[Constants.MAX_RADIOS];

    public PlayerRadioInfo()
    {
        for (var i = 0; i < radios.Length; i++) radios[i] = new Radio();
    }

    /// <summary>
    ///     True while connected and the radio is loaded (set by <see cref="RadioStateSyncService" />). The one
    ///     condition (together with the connection) that makes the radio usable - see <c>RadioHelper.RadiosAvailable</c>.
    /// </summary>
    [JsonIgnore]
    public volatile bool IsActive;

    /// <summary>THE radio (slot <see cref="RadioId" />).</summary>
    [JsonIgnore]
    public Radio Radio => radios[RadioId];

    public void Reset()
    {
        IsActive = false;
        ambient = new Ambient
        {
            vol = 0.0f,
            abType = ""
        };
        for (var i = 0; i < radios.Length; i++) radios[i] = new Radio();
    }

    /// <summary>Compares only the network relevant state (ambient + <see cref="Radio.Equals" /> of every slot).</summary>
    public override bool Equals(object compare)
    {
        if (compare == null || GetType() != compare.GetType()) return false;

        var other = (PlayerRadioInfo)compare;

        if (ambient == null || other.ambient == null)
        {
            if (!ReferenceEquals(ambient, other.ambient)) return false;
        }
        else if (!ambient.Equals(other.ambient))
        {
            return false;
        }

        if (radios.Length != other.radios.Length) return false;

        for (var i = 0; i < radios.Length; i++)
        {
            var radio1 = radios[i];
            var radio2 = other.radios[i];

            if (radio1 == null || radio2 == null)
            {
                if (!ReferenceEquals(radio1, radio2)) return false;
            }
            else if (!radio1.Equals(radio2))
            {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ambient);
        foreach (var radio in radios) hash.Add(radio);

        return hash.ToHashCode();
    }

    public PlayerRadioInfo DeepClone()
    {
        var clone = (PlayerRadioInfo)MemberwiseClone();

        clone.ambient = ambient?.Copy();
        clone.radios = new Radio[radios.Length];
        for (var i = 0; i < radios.Length; i++) clone.radios[i] = radios[i]?.DeepClone() ?? new Radio();

        return clone;
    }

    /// <summary>
    ///     The network view sent to the server (ambient + one <see cref="RadioBase" /> per slot). Every slot except
    ///     <see cref="RadioId" /> is disabled.
    /// </summary>
    public PlayerRadioInfoBase ConvertToRadioBase()
    {
        return ConvertToRadioBase(RadioId < radios.Length ? radios[RadioId] : null);
    }

    /// <param name="radio">
    ///     THE radio - read once by the caller: <c>RadioHelper</c> replaces it (never modifies it) when it is tuned.
    /// </param>
    private PlayerRadioInfoBase ConvertToRadioBase(Radio radio)
    {
        var radiosBase = new RadioBase[Constants.MAX_RADIOS];
        for (var i = 0; i < radiosBase.Length; i++)
            radiosBase[i] = (i == RadioId ? radio?.ToRadioBase() : null)
                            ?? new RadioBase { modulation = Modulation.DISABLED, secFreq = 0 };

        return new PlayerRadioInfoBase
        {
            ambient = ambient?.Copy() ?? new Ambient { vol = 0.0f, abType = "" },
            radios = radiosBase
        };
    }

    /// <summary>
    ///     Finds out whether the radio receives a transmission (same rules as the server's voice routing, see
    ///     <see cref="PlayerRadioInfoBase.CanHearTransmission" />). The radio never encrypts, so only unencrypted
    ///     transmissions are <paramref name="decryptable" />; an encrypted one (older clients) is still returned and
    ///     played garbled. A radio in <paramref name="blockedRadios" /> (half-duplex while transmitting) does not receive.
    /// </summary>
    /// <returns>The receiving radio or null.</returns>
    public Radio CanHearTransmission(double frequency,
        Modulation modulation,
        byte encryptionKey,
        List<int> blockedRadios,
        out RadioReceivingState receivingState,
        out bool decryptable)
    {
        // one snapshot: the radio may be retuned (replaced) by another thread at any time
        var radio = Radio;
        var networkView = ConvertToRadioBase(radio);

        var match = networkView.CanHearTransmission(frequency, modulation, encryptionKey, blockedRadios,
            out receivingState, out decryptable);

        if (match == null || receivingState == null
                          || receivingState.ReceivedOn != RadioId
                          || (blockedRadios != null && blockedRadios.Contains(RadioId)))
        {
            receivingState = null;
            decryptable = false;
            return null;
        }

        return radio;
    }
}
