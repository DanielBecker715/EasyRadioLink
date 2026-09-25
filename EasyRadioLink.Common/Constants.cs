namespace EasyRadioLink.Common;

public class Constants
{
    public static readonly int MIC_SAMPLE_RATE = 16000;
    public static readonly int MIC_INPUT_AUDIO_LENGTH_MS = 40;
    public static readonly int MIC_SEGMENT_FRAMES = MIC_SAMPLE_RATE / 1000 * MIC_INPUT_AUDIO_LENGTH_MS;
    public static readonly int OUTPUT_SAMPLE_RATE = 48000;
    public static readonly int OUTPUT_AUDIO_LENGTH_MS = 40;
    public static readonly int OUTPUT_SEGMENT_FRAMES = OUTPUT_SAMPLE_RATE / 1000 * OUTPUT_AUDIO_LENGTH_MS;
    public static readonly int JITTER_BUFFER = 50; //in milliseconds

    // Length of every radio array (network + client state). Slot 0 is reserved: it is always
    // Modulation.DISABLED, never shown and never transmits. User radios are 1..10.
    public static readonly int MAX_RADIOS = 11;

    // Index of the first user radio (slot 0 is reserved).
    public const int FIRST_RADIO_INDEX = 1;

    // Number of user radios (slots 1..10).
    public const int RADIO_COUNT = 10;

    //no updates will be sent if there are no changes for this number of seconds
    public static readonly int CLIENT_UPDATE_INTERVAL_LIMIT = 180;
}
