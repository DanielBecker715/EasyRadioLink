using System;
using System.Collections.Generic;
using System.Text.Json;

namespace EasyRadioLink.Common.Models.Player;

/// <summary>
///     One entry of a radio layout file (<c>radios.json</c>, <c>radios-custom.json</c>, the server's
///     <c>server-radios.json</c> / <c>SERVER_RADIO_PRESET</c> setting). Frequencies are in Hz.
///     A layout always has <see cref="Constants.MAX_RADIOS" /> entries; entry 0 is reserved and disabled.
///     The field names are the JSON names (matched case-insensitively) - keep them in sync with the client's radio model.
/// </summary>
public class RadioDefinition
{
    public const byte MinEncryptionKey = 1;
    public const byte MaxEncryptionKey = 252;

    /// <summary>Tolerant options for reading layout files: case-insensitive names, comments, trailing commas.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    // Display name; also the key for preset channel files ("<normalised name>.txt")
    public string name = "";

    // Radio model (sound character) of this radio, see RadioModels/*.json. Empty = default model.
    public string model = "";

    public Modulation modulation = Modulation.DISABLED;

    public double freq = 1;
    public double freqMin = 1;
    public double freqMax = 1;

    // Guard / secondary receive frequency, 0 = the radio has no guard receiver
    public double guardFreq;

    // The radio may encrypt (scrambler controls are shown)
    public bool encCapable;
    public bool enc;
    public byte encKey = MinEncryptionKey;

    // Preselected preset channel (1-based), -1 = none
    public int channel = -1;

    // Receive-only radio
    public bool rxOnly;

    // Part of simultaneous transmission
    public bool simul;

    public RadioDefinition Clone()
    {
        return (RadioDefinition)MemberwiseClone();
    }

    /// <summary>A disabled placeholder for <paramref name="slot" /> (slot 0 = "Reserved").</summary>
    public static RadioDefinition CreateDisabled(int slot)
    {
        return new RadioDefinition
        {
            name = slot == 0 ? "Reserved" : $"Radio {slot}",
            modulation = Modulation.DISABLED
        };
    }

    /// <summary>Modulations a radio layout may use: AM (0), FM (1), DISABLED (3), DIGITAL (5).</summary>
    public static bool IsSupportedModulation(Modulation modulation)
    {
        // numeric values are the stable wire contract
        return (int)modulation is 0 or 1 or 3 or 5;
    }

    /// <summary>Parses a JSON array of radio definitions. Throws <see cref="JsonException" /> for invalid JSON.</summary>
    public static List<RadioDefinition> ParseList(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<RadioDefinition>();

        return JsonSerializer.Deserialize<List<RadioDefinition>>(json, JsonOptions) ?? new List<RadioDefinition>();
    }

    /// <summary>
    ///     Returns a validated layout with exactly <see cref="Constants.MAX_RADIOS" /> entries:
    ///     <list type="bullet">
    ///         <item>a list of up to <see cref="Constants.RADIO_COUNT" /> entries whose first entry is enabled is treated as
    ///             radios 1..n (the reserved slot 0 is prepended); otherwise the list is padded/trimmed to 11 entries,</item>
    ///         <item>slot 0 is forced DISABLED; unknown modulations become DISABLED,</item>
    ///         <item>frequencies are clamped into [freqMin, freqMax] (no range given = fixed frequency),</item>
    ///         <item>encKey is clamped to 1..252; enc is cleared when the radio is not encCapable.</item>
    ///     </list>
    ///     Never returns null and never modifies the input entries.
    /// </summary>
    public static List<RadioDefinition> Normalise(IReadOnlyList<RadioDefinition> source)
    {
        var entries = new List<RadioDefinition>();
        if (source != null)
        {
            if (source.Count > 0 && source.Count <= Constants.RADIO_COUNT
                                 && source[0] != null && IsEnabled(source[0].modulation))
                // the file only lists the user radios - keep them in slots 1..n
                entries.Add(null);

            foreach (var entry in source) entries.Add(entry);
        }

        var result = new List<RadioDefinition>(Constants.MAX_RADIOS);
        for (var slot = 0; slot < Constants.MAX_RADIOS; slot++)
        {
            var entry = slot < entries.Count ? entries[slot] : null;

            result.Add(entry == null ? CreateDisabled(slot) : entry.Clone().Sanitised(slot));
        }

        result[0] = CreateDisabled(0);

        return result;
    }

    private static bool IsEnabled(Modulation modulation)
    {
        return modulation != Modulation.DISABLED && IsSupportedModulation(modulation);
    }

    private RadioDefinition Sanitised(int slot)
    {
        name = string.IsNullOrWhiteSpace(name) ? $"Radio {slot}" : name.Trim();
        model = model?.Trim() ?? "";

        if (!IsSupportedModulation(modulation)) modulation = Modulation.DISABLED;

        freq = FiniteOrZero(freq);
        freqMin = FiniteOrZero(freqMin);
        freqMax = FiniteOrZero(freqMax);
        guardFreq = Math.Max(0, FiniteOrZero(guardFreq));

        if (freqMin > freqMax) (freqMin, freqMax) = (freqMax, freqMin);

        // no range given (defaults are 1 Hz) - the radio is fixed to its frequency
        if (freqMax <= 1 && freqMin <= 1)
        {
            freqMin = freq;
            freqMax = freq;
        }

        freq = Math.Clamp(freq, freqMin, freqMax);

        encKey = Math.Clamp(encKey, MinEncryptionKey, MaxEncryptionKey);
        if (!encCapable) enc = false;

        if (channel < 1) channel = -1;

        return this;
    }

    private static double FiniteOrZero(double value)
    {
        return double.IsFinite(value) ? value : 0;
    }
}
