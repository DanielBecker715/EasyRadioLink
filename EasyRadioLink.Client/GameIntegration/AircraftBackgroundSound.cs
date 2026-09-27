using System;

namespace EasyRadioLink.Client.GameIntegration;

/// <summary>
///     Chooses the background sound (<c>AudioEffects\Background\&lt;name&gt;.wav</c>) that fits an aircraft of the
///     simulator: helicopter, jet or prop.
/// </summary>
public static class AircraftBackgroundSound
{
    public const string Helicopter = "helicopter";
    public const string Jet = "jet";
    public const string Prop = "prop";

    // SimConnect "ENGINE TYPE"
    public const int EngineTypePiston = 0;
    public const int EngineTypeJet = 1;
    public const int EngineTypeNone = 2;
    public const int EngineTypeHeloTurbine = 3;
    public const int EngineTypeUnsupported = 4;
    public const int EngineTypeTurboprop = 5;

    /// <summary>
    ///     The background sound for an aircraft of SimConnect <c>CATEGORY</c> <paramref name="category" /> ("Airplane",
    ///     "Helicopter", ...) with <c>ENGINE TYPE</c> <paramref name="engineType" />, or null if none fits (gliders,
    ///     unknown engines, vehicles that aren't aircraft) - then the profile's background sound is used.
    /// </summary>
    public static string ForAircraft(string category, int engineType)
    {
        if (string.Equals(category?.Trim(), "Helicopter", StringComparison.OrdinalIgnoreCase)
            || engineType == EngineTypeHeloTurbine)
            return Helicopter;

        if (category != null && category.Trim().Length > 0
                             && !string.Equals(category.Trim(), "Airplane", StringComparison.OrdinalIgnoreCase))
            return null;

        return engineType switch
        {
            EngineTypeJet => Jet,
            EngineTypePiston or EngineTypeTurboprop => Prop,
            _ => null
        };
    }
}
