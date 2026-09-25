using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace EasyRadioLink.Common.Models.Player;

/// <summary>
///     Network view of a user's radios: <c>{"ambient":{...},"radios":[11 x RadioBase]}</c>.
///     Slot 0 is reserved and always <see cref="Modulation.DISABLED" />; user radios are 1..10.
/// </summary>
public class PlayerRadioInfoBase
{
    // Sender's background sound (abType + volume), mixed in by receivers.
    public Ambient ambient = new()
    {
        vol = 0.0f,
        abType = ""
    };

    public RadioBase[] radios = new RadioBase[Constants.MAX_RADIOS];


    public PlayerRadioInfoBase()
    {
        for (var i = 0; i < radios.Length; i++) radios[i] = new RadioBase();
    }

    [JsonIgnore] public long LastUpdate { get; set; }


    public void Reset()
    {
        ambient = new Ambient
        {
            vol = 0.0f,
            abType = ""
        };
        radios = new RadioBase[Constants.MAX_RADIOS];
        for (var i = 0; i < radios.Length; i++) radios[i] = new RadioBase();
    }

    /// <summary>
    ///     Repairs data received from the network: exactly <see cref="Constants.MAX_RADIOS" /> non-null radios,
    ///     reserved slot 0 disabled, non-null ambient.
    /// </summary>
    public void EnsureValid()
    {
        ambient ??= new Ambient { vol = 0.0f, abType = "" };
        ambient.abType ??= "";

        if (radios == null || radios.Length != Constants.MAX_RADIOS)
        {
            var fixedRadios = new RadioBase[Constants.MAX_RADIOS];
            for (var i = 0; i < fixedRadios.Length; i++)
                fixedRadios[i] = radios != null && i < radios.Length ? radios[i] : null;

            radios = fixedRadios;
        }

        for (var i = 0; i < radios.Length; i++) radios[i] ??= new RadioBase();

        radios[0].modulation = Modulation.DISABLED;
    }

    // override object.Equals
    public override bool Equals(object compare)
    {
        if (compare == null || GetType() != compare.GetType()) return false;

        var compareRadio = (PlayerRadioInfoBase)compare;

        if (ambient == null || compareRadio.ambient == null)
        {
            if (!ReferenceEquals(ambient, compareRadio.ambient)) return false;
        }
        else if (!ambient.Equals(compareRadio.ambient))
        {
            return false;
        }

        if (radios == null || compareRadio.radios == null) return ReferenceEquals(radios, compareRadio.radios);

        if (radios.Length != compareRadio.radios.Length) return false;

        for (var i = 0; i < radios.Length; i++)
        {
            var radio1 = radios[i];
            var radio2 = compareRadio.radios[i];

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
        if (radios != null)
            foreach (var radio in radios)
                hash.Add(radio);

        return hash.ToHashCode();
    }


    public PlayerRadioInfoBase DeepClone()
    {
        var clone = (PlayerRadioInfoBase)MemberwiseClone();

        clone.ambient = ambient?.Copy();

        clone.radios = new RadioBase[Constants.MAX_RADIOS];

        for (var i = 0; i < clone.radios.Length; i++)
            clone.radios[i] = radios != null && i < radios.Length && radios[i] != null
                ? radios[i].DeepClone()
                : new RadioBase();

        return clone;
    }

    /// <summary>
    ///     Voice routing predicate (server) and "users on frequency" predicate (client).
    ///     A radio matches when it is enabled and either its main frequency or its guard/secondary frequency
    ///     (<see cref="RadioBase.secFreq" />) is within <see cref="RadioBase.FreqCloseEnough" /> of
    ///     <paramref name="frequency" /> with the same modulation.
    ///     A matching radio that cannot decrypt the transmission is still returned (with
    ///     <paramref name="decryptable" /> = false) so the receiver can play scrambled audio; a decryptable match on a
    ///     radio that is not blocked always wins.
    /// </summary>
    /// <returns>The receiving radio or null if no radio is tuned to the transmission.</returns>
    public RadioBase CanHearTransmission(double frequency,
        Modulation modulation,
        byte encryptionKey,
        bool strictEncryption,
        List<int> blockedRadios,
        out RadioReceivingState receivingState,
        out bool decryptable)
    {
        RadioBase bestMatchingRadio = null;
        RadioReceivingState bestMatchingRadioState = null;
        var bestMatchingDecryptable = false;

        if (radios == null || modulation == Modulation.DISABLED)
        {
            receivingState = null;
            decryptable = false;
            return null;
        }

        for (var i = 0; i < radios.Length; i++)
        {
            var receivingRadio = radios[i];

            if (receivingRadio == null
                || receivingRadio.modulation == Modulation.DISABLED
                || receivingRadio.modulation != modulation)
                continue;

            var isDecryptable = (receivingRadio.enc ? receivingRadio.encKey : 0) == encryptionKey ||
                                (!strictEncryption && encryptionKey == 0);

            var isBlocked = blockedRadios != null && blockedRadios.Contains(i);

            //within tolerance on the main frequency
            if (RadioBase.FreqCloseEnough(receivingRadio.freq, frequency)
                && receivingRadio.freq > 10000)
            {
                if (isDecryptable && !isBlocked)
                {
                    receivingState = new RadioReceivingState
                    {
                        IsSecondary = false,
                        LastReceivedAt = DateTime.Now.Ticks,
                        ReceivedOn = i
                    };
                    decryptable = true;
                    return receivingRadio;
                }

                bestMatchingRadio = receivingRadio;
                bestMatchingRadioState = new RadioReceivingState
                {
                    IsSecondary = false,
                    LastReceivedAt = DateTime.Now.Ticks,
                    ReceivedOn = i
                };
                bestMatchingDecryptable = isDecryptable;
            }

            //within tolerance on the guard / secondary frequency
            if (RadioBase.FreqCloseEnough(receivingRadio.secFreq, frequency)
                && receivingRadio.secFreq > 10000)
            {
                if (isDecryptable && !isBlocked)
                {
                    receivingState = new RadioReceivingState
                    {
                        IsSecondary = true,
                        LastReceivedAt = DateTime.Now.Ticks,
                        ReceivedOn = i
                    };
                    decryptable = true;
                    return receivingRadio;
                }

                bestMatchingRadio = receivingRadio;
                bestMatchingRadioState = new RadioReceivingState
                {
                    IsSecondary = true,
                    LastReceivedAt = DateTime.Now.Ticks,
                    ReceivedOn = i
                };
                bestMatchingDecryptable = isDecryptable;
            }
        }

        decryptable = bestMatchingDecryptable;
        receivingState = bestMatchingRadioState;
        return bestMatchingRadio;
    }
}
