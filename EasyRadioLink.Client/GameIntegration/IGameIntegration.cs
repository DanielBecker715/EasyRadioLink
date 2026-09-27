using System.Collections.Generic;

namespace EasyRadioLink.Client.GameIntegration;

/// <summary>
///     The integration of one game. <see cref="GameIntegrationManager" /> starts it when one of its
///     <see cref="ProcessNames" /> is running (and it <see cref="IsEnabled" />) and stops it when the game exits.
///     Start and Stop are called from one thread, never at the same time.
/// </summary>
public interface IGameIntegration
{
    /// <summary>Name shown to the user, e.g. "Microsoft Flight Simulator 2024".</summary>
    string GameName { get; }

    /// <summary>Process names of the game, without ".exe".</summary>
    IReadOnlyList<string> ProcessNames { get; }

    /// <summary>At least one feature of the integration is switched on in the settings.</summary>
    bool IsEnabled { get; }

    /// <summary>What the integration is doing - shown in the settings while the game is running.</summary>
    string Status { get; }

    /// <summary>The game was started (or the integration was switched on while it runs).</summary>
    void Start();

    /// <summary>The game exited, the integration was switched off or EasyRadioLink is closing. Undo every change.</summary>
    void Stop();
}
