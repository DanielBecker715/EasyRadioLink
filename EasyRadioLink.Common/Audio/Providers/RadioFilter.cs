using EasyRadioLink.Common.Audio.Models;
using NAudio.Wave;

namespace EasyRadioLink.Common.Audio.Providers;

/// <summary>
///     Runs a source through the transmit chain of a radio model (no noise, no tones, no receive filter).
///     Used by the microphone preview so users can hear themselves through each radio model.
///     The source must be 48 kHz mono IEEE float.
/// </summary>
public class RadioFilter : ISampleProvider
{
    public static readonly float CLIPPING_MAX = 4000 / 32768f;
    public static readonly float CLIPPING_MIN = -CLIPPING_MAX;

    private readonly ISampleProvider _output;

    /// <param name="sampleProvider">48 kHz mono float source.</param>
    /// <param name="modelKey">Radio model key (see <see cref="RadioModelFactory.AvailableModels" />); unknown = "standard".</param>
    /// <param name="encrypted">Also apply the model's encryption colour (if it has one).</param>
    public RadioFilter(ISampleProvider sampleProvider, string modelKey = RadioModelFactory.DefaultModelKey,
        bool encrypted = false) : this(sampleProvider, modelKey, encrypted, RadioModelFactory.Instance)
    {
    }

    /// <summary>Same as the public constructor, with an explicit model factory (tools and tests).</summary>
    public RadioFilter(ISampleProvider sampleProvider, string modelKey, bool encrypted, RadioModelFactory factory)
    {
        factory ??= RadioModelFactory.Instance;
        ModelKey = factory.ResolveModelKey(modelKey);

        var model = factory.LoadTxOrDefaultRadio(ModelKey);
        model.TxSource.Source = sampleProvider;

        _output = encrypted && model.EncryptionProvider != null ? model.EncryptionProvider : model.TxEffectProvider;
    }

    /// <summary>The model key actually used (after fallback to "standard").</summary>
    public string ModelKey { get; }

    public WaveFormat WaveFormat => _output.WaveFormat;

    public int Read(float[] buffer, int offset, int sampleCount)
    {
        return _output.Read(buffer, offset, sampleCount);
    }
}
