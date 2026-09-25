using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using EasyRadioLink.Common;
using EasyRadioLink.Common.Models;
using EasyRadioLink.Common.Models.Player;

namespace EasyRadioLink.Client.Radios;

/// <summary>
///     The local user's radios (<see cref="Constants.MAX_RADIOS" /> slots; slot 0 is reserved, always
///     <see cref="Modulation.DISABLED" />, never shown and never transmits; user radios are 1..10).
///     One instance lives for the whole application in <c>ClientStateSingleton.PlayerRadioInfo</c>; it is filled by
///     <see cref="RadioStateSyncService" /> after connecting and changed by the user through <c>RadioHelper</c>.
/// </summary>
public class PlayerRadioInfo
{
    public const int FirstUserRadio = Constants.FIRST_RADIO_INDEX;

    // own background sound, sent to the server so other users hear it (abType is a name from
    // CachedAudioEffectProvider.AvailableBackgroundSounds, "" = none)
    public Ambient ambient = new()
    {
        vol = 0.0f,
        abType = ""
    };

    public Radio[] radios = new Radio[Constants.MAX_RADIOS];

    // selected radio (1..10), used by PTT, hotkeys and the radio panel
    public short selected = FirstUserRadio;

    // global simultaneous transmission toggle: PTT also transmits on every radio with Radio.simul
    public bool simultaneousTransmission;

    public PlayerRadioInfo()
    {
        for (var i = 0; i < radios.Length; i++) radios[i] = new Radio();
    }

    /// <summary>
    ///     True while connected and the radios are loaded (set by <see cref="RadioStateSyncService" />). The one
    ///     condition (together with the connection) that makes radios usable - see <c>RadioHelper.RadiosAvailable</c>.
    /// </summary>
    [JsonIgnore]
    public volatile bool IsActive;

    public void Reset()
    {
        IsActive = false;
        ambient = new Ambient
        {
            vol = 0.0f,
            abType = ""
        };
        selected = FirstUserRadio;
        simultaneousTransmission = false;
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

    /// <summary>The network view sent to the server (ambient + one <see cref="RadioBase" /> per slot).</summary>
    public PlayerRadioInfoBase ConvertToRadioBase()
    {
        var radiosBase = new RadioBase[Constants.MAX_RADIOS];
        for (var i = 0; i < radiosBase.Length; i++)
        {
            var radio = i < radios.Length ? radios[i] : null;
            radiosBase[i] = radio?.ToRadioBase() ?? new RadioBase();
        }

        // reserved slot
        radiosBase[0].modulation = Modulation.DISABLED;

        return new PlayerRadioInfoBase
        {
            ambient = ambient?.Copy() ?? new Ambient { vol = 0.0f, abType = "" },
            radios = radiosBase
        };
    }

    /// <summary>
    ///     Finds the local radio that receives a transmission (same rules as the server's voice routing, see
    ///     <see cref="PlayerRadioInfoBase.CanHearTransmission" />). A matching radio that cannot decrypt, or that is
    ///     blocked, may still be returned with <paramref name="decryptable" /> = false - callers must check
    ///     <paramref name="blockedRadios" /> themselves.
    /// </summary>
    /// <returns>The receiving radio or null.</returns>
    public Radio CanHearTransmission(double frequency,
        Modulation modulation,
        byte encryptionKey,
        bool strictEncryption,
        List<int> blockedRadios,
        out RadioReceivingState receivingState,
        out bool decryptable)
    {
        var networkView = ConvertToRadioBase();

        var match = networkView.CanHearTransmission(frequency, modulation, encryptionKey, strictEncryption,
            blockedRadios, out receivingState, out decryptable);

        if (match == null || receivingState == null
                          || receivingState.ReceivedOn <= 0
                          || receivingState.ReceivedOn >= radios.Length)
        {
            receivingState = null;
            decryptable = false;
            return null;
        }

        return radios[receivingState.ReceivedOn];
    }
}
