namespace EasyRadioLink.Common.Models.Player;

/// <summary>
///     Radio modulation. The numeric values are part of the wire contract (UDP voice packet byte, TCP JSON integer)
///     and must never change. The client's radio takes its modulation from the <see cref="BandPlan" />.
///     Values 2, 4, 6 and 7 are retired and must never be reused.
/// </summary>
public enum Modulation
{
    /// <summary>Amplitude modulation: CB / aviation style. Simultaneous transmissions overlap (heterodyne).</summary>
    AM = 0,

    /// <summary>Frequency modulation: optional FM tone, FM capture when radio interference is enabled.</summary>
    FM = 1,

    // 2 is retired

    /// <summary>Radio switched off / slot unused.</summary>
    DISABLED = 3,

    // 4 is retired

    /// <summary>Digital voice: clean audio path - no static, no FM tone, no FM capture, no squelch tail.</summary>
    DIGITAL = 5

    // 6 and 7 are retired
}
