using System;
using EasyRadioLink.Common.Audio.Models;
using EasyRadioLink.Common.Models.Player;
using NAudio.Wave;

namespace EasyRadioLink.Common.Audio.Providers;

/// <summary>
///     Band character of the <see cref="WeakSignalChannelProvider" />: the propagation comes from the frequency (below or
///     above 30 MHz), the receiver behaviour from the modulation (AM, FM, digital).
/// </summary>
public enum WeakSignalBand
{
    /// <summary>
    ///     Below 30 MHz (MW, HF, CB - all AM): slow, deep ionospheric fading and the strongest multipath swirl, soft
    ///     pink-ish hiss with a little rumble that breathes with the receiver AGC, above 50 % a faint heterodyne whistle.
    /// </summary>
    Hf,

    /// <summary>AM above 30 MHz (AIR, UHF-AM): fast, shallow flutter, little comb, mid hiss, receiver AGC.</summary>
    VhfAm,

    /// <summary>
    ///     FM (VHF, FM, PMR, UHF-FM): flutter like a moving station and little comb; the FM limiter keeps the voice level
    ///     while a bright hiss surges up whenever the signal fades below the FM threshold.
    /// </summary>
    VhfFm,

    /// <summary>DIG band / DIGITAL modulation: no noise, fading or whistle - CVSD "secure voice" coding instead.</summary>
    Digital
}

/// <summary>
///     What the <see cref="WeakSignalChannelProvider" /> does at one amount (0..1) on one band. Every value is a smooth,
///     monotonic function of the amount.
/// </summary>
/// <param name="Amount">0..1</param>
/// <param name="HighPassHz">handset / transmitter high-pass (3rd order in total)</param>
/// <param name="LowPassHz">handset / transmitter low-pass (4th order Butterworth)</param>
/// <param name="PeakHz">mid resonance ("honk")</param>
/// <param name="PeakDb">gain of the mid resonance</param>
/// <param name="DriveDb">soft saturation drive (the level is made up again)</param>
/// <param name="SnrDb">voice to noise at the nominal (unfaded) signal; +infinity = no noise</param>
/// <param name="RiceKDb">flat fading: Rice K-factor (steady / scattered power); +infinity = no fading</param>
/// <param name="FadeFloorDb">deepest flat fade (soft floor)</param>
/// <param name="FadeRateHz">bandwidth of the flat fading (smooth Gaussian processes)</param>
/// <param name="ShadowDb">slow shadowing (standard deviation, VHF / UHF only)</param>
/// <param name="EchoGain">largest gain of the first multipath echo relative to the direct signal</param>
/// <param name="EchoDopplerHz">rms drift rate of the echo phases (moves the notches through the voice)</param>
/// <param name="AgcReleaseMs">AM receiver AGC release (attack 10 ms); 0 = no AGC (FM limiter, digital)</param>
/// <param name="FmThresholdDb">FM: fade depth where the threshold hiss surges up; +infinity = never (AM, digital)</param>
/// <param name="HeterodyneDb">peak level of the heterodyne whistle relative to the voice; -infinity = none</param>
/// <param name="CodecMix">digital: share of the CVSD coded voice (0 for the analogue bands)</param>
/// <param name="CodecStepMin">digital: smallest CVSD step (relative to the speech level)</param>
/// <param name="CodecStepMax">digital: largest CVSD step (relative to the speech level)</param>
/// <param name="CodecSyllabicMs">digital: CVSD step adaptation time constant</param>
internal readonly record struct WeakSignalParameters(
    double Amount,
    double HighPassHz,
    double LowPassHz,
    double PeakHz,
    double PeakDb,
    double DriveDb,
    double SnrDb,
    double RiceKDb,
    double FadeFloorDb,
    double FadeRateHz,
    double ShadowDb,
    double EchoGain,
    double EchoDopplerHz,
    double AgcReleaseMs,
    double FmThresholdDb,
    double HeterodyneDb,
    double CodecMix,
    double CodecStepMin,
    double CodecStepMax,
    double CodecSyllabicMs);

/// <summary>
///     "Distance (weak signal)": makes a received voice sound like a station far away - the typical sound of a long
///     distance / war zone radio link. 48 kHz mono float in and out; one instance per received transmission (all state
///     carries over from one <see cref="Read" /> to the next, the output does not depend on the block size).
///     Deterministic for a given seed. Nothing in here is impulsive or stepped: no crackle, no dropouts, no bit or rate
///     reduction; every modulation is a smooth random process, computed every 32 samples (0.67 ms) and interpolated
///     sample by sample.
///     <para>The chain, all scaled smoothly by <see cref="Amount" /> (0 = exact bypass, the output is the input):</para>
///     <list type="number">
///         <item>
///             handset / transmitter: high-pass 150 -> 450 Hz, a mid resonance (1.3-1.7 kHz, up to +8 dB), smooth soft
///             saturation (up to +12 dB drive, relative to the speech level, slightly asymmetric) and a low-pass
///             4.2 -> 2.6 kHz, then make-up gain back to the input level;
///         </item>
///         <item>
///             multipath (analogue bands): the direct signal plus 2-3 echoes 0.35-3 ms later with slowly wandering
///             delays (cubic fractional-delay interpolation), smooth random gains and a slowly drifting phase (the
///             Doppler difference, applied with an IIR Hilbert pair) - notches that swim through the voice: the watery
///             swirl of long distance radio;
///         </item>
///         <item>
///             flat fading: a Rician envelope from smooth Gaussian processes (slow and deep below 30 MHz, fast flutter
///             above) with a soft floor, so it never goes silent;
///         </item>
///         <item>
///             weak signal: filtered Gaussian noise (band dependent colour) at an SNR relative to the tracked speech
///             level; AM: a receiver AGC (attack 10 ms, release 350-600 ms, driven by the carrier, not by the speech)
///             pulls the noise up in the fades; FM: the limiter keeps the voice level and the hiss surges up smoothly
///             when the fade crosses the FM threshold;
///         </item>
///         <item>HF above 50 %: a faint, slowly drifting heterodyne whistle (below -32 dB) that fades in and out;</item>
///         <item>
///             digital (DIG band / DIGITAL): none of the above channel effects; instead 16 kHz CVSD (continuously
///             variable slope delta modulation, syllabic step adaptation) blended in - buzzy and gritty but continuous;
///         </item>
///         <item>a smooth peak limiter (0.85) and a soft clip that never exceeds 0.9.</item>
///     </list>
/// </summary>
public sealed class WeakSignalChannelProvider : ISampleProvider
{
    /// <summary>Default of <c>ProfileSettingsKeys.VoiceDistortion</c> in percent.</summary>
    public const float DefaultPercent = 35f;

    // = Constants.OUTPUT_SAMPLE_RATE (a constant here, so the time constants below can be constants too)
    internal const int SampleRate = 48000;

    // The random processes, the channel gains and the make-up gains are computed every 32 samples (0.67 ms) and
    // interpolated linearly in between - smooth, and independent of the block size.
    private const int ControlInterval = 32;
    private const double ControlSeconds = (double)ControlInterval / SampleRate;
    private const double InverseControlInterval = 1.0 / ControlInterval;

    // 20 ms fade between the dry input and the processed voice when the amount is switched on / off while playing.
    private const float MixStep = 1f / (SampleRate / 50);

    // multipath: up to 3 echoes, delay lines of 256 samples (5.3 ms > 3.3 ms + interpolation)
    private const int EchoCount = 3;
    private const int DelaySize = 256;
    private const int DelayMask = DelaySize - 1;
    private const double MinDelaySamples = 8;
    private const double MaxDelaySamples = DelaySize - 8;

    // speech level tracking: starts at -18 dBFS RMS, never below -46 dBFS (a long pause must not leave the noise and the
    // saturation relative to nothing)
    private const double InitialSpeechLevel = 0.126;
    private const double MinSpeechLevel = 0.005;

    // handset: how hard the speech level hits the saturation before the drive (0.1 -> 0.3 with the amount)
    private const double SaturationScale0 = 0.1;
    private const double SaturationScale1 = 0.2;
    private const double AsymmetryMax = 0.15;

    private const double MinMakeUp = 1e-4;
    private const double MaxMakeUp = 1e4;
    private const double SilentPower = 1e-12;

    // output safety
    private const double LimiterThreshold = 0.85;
    private const double MaxOutputPeak = 0.9;
    private const double SoftClipKnee = 0.7;

    // FM threshold: the hiss rises 3 dB per dB of fade below the threshold (soft knee 1.5 dB) up to 4 dB above the voice
    private const double FmSurgeSlope = 3;
    private const double FmSurgeKneeDb = 1.5;
    private const double FmNoiseCeilingDb = 4;
    private const double FmCeilingKneeDb = 2;

    // heterodyne (HF only): 600-1400 Hz; peak amplitude -34 dB relative to the tracked speech level, which keeps its
    // rms (50 ms windows) below -32 dB relative to the active voice (measured worst case about -33.7 dB)
    private const double HeterodyneMaxDb = -34;
    private const double HeterodyneCentreHz = 1000;
    private const double HeterodyneSwingHz = 400;

    // digital: CVSD at 16 kHz; the clean branch is delayed by the latency of the coded branch
    private const int CodecDecimation = 3;
    private const int CodecCleanDelay = 4;
    private const int CodecDelaySize = 8;
    private const double CodecRate = (double)SampleRate / CodecDecimation;
    private const double CodecFilterHz = 3600;

    // random processes (indices)
    private const int ProcessFade1 = 0;
    private const int ProcessFade2 = 1;
    private const int ProcessShadow = 2;
    private const int ProcessEchoGain = 3; // .. 5
    private const int ProcessEchoWander = 6; // .. 8
    private const int ProcessEchoDoppler = 9; // .. 11
    private const int ProcessHeterodyneFrequency = 12;
    private const int ProcessHeterodyneLevel = 13;
    private const int ProcessCount = 14;

    private const double Sqrt6 = 2.449489742783178;
    private const double InverseSqrt2 = 0.70710678118654752;
    private const double ButterworthQ = 0.70710678118654752;

    private static readonly WaveFormat Format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1);

    // echo layout per band: base delay (ms), wander (ms) and relative weight
    private static readonly double[] HfEchoDelayMs = { 0.7, 1.5, 2.5 };
    private static readonly double[] HfEchoWanderMs = { 0.25, 0.4, 0.5 };
    private static readonly double[] HfEchoWeight = { 1.0, 0.8, 0.6 };
    private static readonly double[] VhfEchoDelayMs = { 0.35, 0.8, 0.8 };
    private static readonly double[] VhfEchoWanderMs = { 0.1, 0.18, 0.18 };
    private static readonly double[] VhfEchoWeight = { 1.0, 0.6, 0.0 };

    // IIR Hilbert pair (O. Niemitalo): 90 +- 0.7 degrees from 50 Hz to 20 kHz at 48 kHz
    private static readonly double[] HilbertCoefficientsI =
        Squares(0.6923878, 0.9360654322959, 0.9882295226860, 0.9987488452737);

    private static readonly double[] HilbertCoefficientsQ =
        Squares(0.4021921162426, 0.8561710882420, 0.9722909545651, 0.9952884791278);

    // unit-variance scale of the coloured noise of every band (white noise of variance 1 in)
    private static readonly double[] NoiseNorms = ComputeNoiseNorms();

    // per sample
    private static readonly double Power20Coefficient = 1 - Math.Exp(-1.0 / (0.02 * SampleRate));
    private static readonly double MakeUpCoefficient = 1 - Math.Exp(-1.0 / (1.5 * SampleRate));
    private static readonly double PeakRelease = Math.Exp(-1.0 / (0.1 * SampleRate));
    private static readonly double LimiterAttack = 1 - Math.Exp(-1.0 / (0.0005 * SampleRate));
    private static readonly double LimiterRelease = 1 - Math.Exp(-1.0 / (0.15 * SampleRate));
    private static readonly double CodecLeak = Math.Exp(-1.0 / (0.001 * CodecRate));

    // per control tick
    private static readonly double AmountCoefficient = 1 - Math.Exp(-ControlSeconds / 0.03);
    private static readonly double SpeechAttack = 1 - Math.Exp(-ControlSeconds / 0.3);
    private static readonly double SpeechRelease = 1 - Math.Exp(-ControlSeconds / 5.0);
    private static readonly double AgcAttack = 1 - Math.Exp(-ControlSeconds / 0.01);
    private static readonly double AgcTrimCoefficient = 1 - Math.Exp(-ControlSeconds / 4.0);
    private static readonly double FmSmoothing = 1 - Math.Exp(-ControlSeconds / 0.004);

    // amount: target (set from any thread), smoothed value, dry / processed mix (0 = exact bypass) and what it means
    private volatile float _targetAmount;
    private double _amount;
    private float _mix;
    private WeakSignalParameters _parameters;
    private int _controlCountdown;
    private ulong _channelRng;
    private ulong _noiseRng;

    // speech level: 20 ms power, then a slow follower (attack 0.3 s, release 5 s)
    private double _power20;
    private double _speechPower;
    private double _speechLevel = InitialSpeechLevel;

    // handset / transmitter
    private readonly OnePoleHighPass _handsetHighPass1 = new();
    private readonly Svf _handsetPeak = new();
    private readonly Svf _handsetHighPass2 = new();
    private readonly Svf _handsetLowPass1 = new();
    private readonly Svf _handsetLowPass2 = new();
    private double _saturationScale = SaturationScale0;
    private double _drive = 1;
    private double _bias;
    private double _biasOffset;
    private double _saturationGain;
    private double _saturationGainStep;
    private double _handsetInPower;
    private double _handsetOutPower;
    private double _makeUp = 1;
    private double _makeUpStep;

    // random processes: two cascaded one-poles of white noise each (smooth, unit variance)
    private readonly double[] _process1 = new double[ProcessCount];
    private readonly double[] _process2 = new double[ProcessCount];
    private readonly double[] _processCoefficient = new double[ProcessCount];
    private readonly double[] _processScale = new double[ProcessCount];

    // multipath: analytic signal, delay lines and the echoes
    private readonly AllpassChain _hilbertI = new(HilbertCoefficientsI);
    private readonly AllpassChain _hilbertQ = new(HilbertCoefficientsQ);
    private double _hilbertDelay;
    private readonly double[] _delayI = new double[DelaySize];
    private readonly double[] _delayQ = new double[DelaySize];
    private int _write;
    private readonly double[] _echoDelayMs;
    private readonly double[] _echoWanderMs;
    private readonly double[] _echoWeight;
    private readonly int _echoes;
    private readonly double[] _echoGain = new double[EchoCount];
    private readonly double[] _echoGainStep = new double[EchoCount];
    private readonly double[] _echoDelay = new double[EchoCount];
    private readonly double[] _echoDelayStep = new double[EchoCount];
    private readonly double[] _echoRe = new double[EchoCount];
    private readonly double[] _echoIm = new double[EchoCount];
    private readonly double[] _echoRotationCos = new double[EchoCount];
    private readonly double[] _echoRotationSin = new double[EchoCount];

    // flat fading, noise, AGC / FM threshold
    private double _riceSpecular = 1;
    private double _riceScattered;
    private double _fadeFloorPower;
    private double _shadowNeper;
    private double _noiseAmplitude;
    private double _agcRelease;
    private double _agc = 1;
    private double _agcTrimPower = 1;
    private double _fmNoiseDb;
    private double _voiceGain = 1;
    private double _voiceGainStep;
    private double _noiseGain;
    private double _noiseGainStep;

    // noise colour
    private readonly bool _pinkNoise;
    private double _pink0;
    private double _pink1;
    private double _pink2;
    private readonly Svf _noiseHighPass = new();
    private readonly Svf _noiseLowPass = new();
    private readonly double _noiseNorm;

    // heterodyne
    private double _heterodynePresence;
    private double _heterodyneRe = 1;
    private double _heterodyneIm;
    private double _heterodyneCos = 1;
    private double _heterodyneSin;
    private double _heterodyneAmplitude;
    private double _heterodyneAmplitudeStep;

    // digital: CVSD
    private readonly Svf _codecAntiAlias = new();
    private readonly Svf _codecReconstruction1 = new();
    private readonly Svf _codecReconstruction2 = new();
    private readonly Svf _cleanAntiAlias = new();
    private readonly Svf _cleanReconstruction1 = new();
    private readonly Svf _cleanReconstruction2 = new();
    private readonly double[] _cleanDelay = new double[CodecDelaySize];
    private int _cleanWrite;
    private int _codecPhase;
    private double _codecPrevious;
    private double _codecCurrent;
    private double _codecEstimate;
    private double _codecStep;
    private int _codecBits;
    private double _codecStepMin;
    private double _codecStepMax;
    private double _codecSyllabic;
    private double _codecMix;
    private double _codecMixStep;
    private double _codecInPower;
    private double _codecOutPower;
    private double _codecMakeUp = 1;
    private double _codecMakeUpStep;

    // output limiter
    private double _peak;
    private double _limiterGain = 1;

    /// <param name="source">48 kHz mono float source (may be set later, see <see cref="Source" />).</param>
    /// <param name="band">band character, see <see cref="BandFor" /> / <see cref="BandForModel" /></param>
    /// <param name="seed">seed of the random processes (same seed = same output)</param>
    /// <param name="amount">0..1 (0 = off)</param>
    public WeakSignalChannelProvider(ISampleProvider source, WeakSignalBand band, int seed, float amount)
    {
        Source = source;
        Band = band;

        // SplitMix64 of the seed, one stream for the channel, one for the noise (so muting the noise in the tests
        // leaves the channel as it is); never 0 (xorshift must not start at 0)
        _channelRng = SplitMix((ulong)(uint)seed);
        _noiseRng = SplitMix((ulong)(uint)seed ^ 0x5DEECE66DUL);

        var vhf = band != WeakSignalBand.Hf;
        _echoDelayMs = vhf ? VhfEchoDelayMs : HfEchoDelayMs;
        _echoWanderMs = vhf ? VhfEchoWanderMs : HfEchoWanderMs;
        _echoWeight = vhf ? VhfEchoWeight : HfEchoWeight;
        _echoes = band == WeakSignalBand.Digital ? 0 : vhf ? 2 : 3;

        _pinkNoise = band == WeakSignalBand.Hf;
        ConfigureNoise(band, _noiseHighPass, _noiseLowPass);
        _noiseNorm = NoiseNorms[(int)band];

        _targetAmount = Sanitise(amount);
        if (_targetAmount > 0f)
        {
            // a new transmission starts processed - no fade in
            Activate(_targetAmount);
            _mix = 1f;
        }
    }

    /// <summary>
    ///     The source; the receive pipeline points it at the dry / wet mix of the received voice (the sender's radio model
    ///     and the dry share of the radio effect strength) for every block, the Audio Preview at the chosen radio model.
    /// </summary>
    public ISampleProvider Source { get; set; }

    public WeakSignalBand Band { get; }

    /// <summary>
    ///     Amount 0..1 (NaN = 0). Changes glide smoothly; switching to 0 fades to the dry input in 20 ms and then
    ///     bypasses exactly.
    /// </summary>
    public float Amount
    {
        get => _targetAmount;
        set => _targetAmount = Sanitise(value);
    }

    /// <summary>True while the output is exactly the input.</summary>
    public bool IsBypassed => _mix <= 0f && _targetAmount <= 0f;

    /// <summary>Tests: leaves out the noise and the heterodyne (everything else stays exactly the same).</summary>
    internal bool NoiseMuted { get; set; }

    /// <summary>Tests (digital): leaves out the CVSD coding - the clean share only, same filters and latency.</summary>
    internal bool CodecMuted { get; set; }

    /// <summary>Tests: the processing at the current (smoothed) amount.</summary>
    internal WeakSignalParameters Parameters => _parameters;

    public WaveFormat WaveFormat => Source?.WaveFormat ?? Format;

    public int Read(float[] buffer, int offset, int count)
    {
        var source = Source;
        if (source == null)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        var read = source.Read(buffer, offset, count);

        // exact bypass: not a single operation on the samples
        if (IsBypassed) return read;

        for (var i = offset; i < offset + read; i++) buffer[i] = Next(buffer[i]);

        return read;
    }

    /// <summary>
    ///     Band character of a transmission: DIGITAL modulation or the DIG band = digital; below 30 MHz (MW, HF, CB) = HF;
    ///     above: FM or AM by the modulation of the transmission (the band plan's modulation if it is neither).
    /// </summary>
    public static WeakSignalBand BandFor(double frequency, Modulation modulation)
    {
        if (modulation == Modulation.DIGITAL) return WeakSignalBand.Digital;

        var band = BandPlan.GetBand(frequency);
        if (band.Modulation == Modulation.DIGITAL) return WeakSignalBand.Digital;

        if (band.LastFrequency < RadioEffectRules.HfNoiseFrequencyCutoff) return WeakSignalBand.Hf;

        var fm = modulation == Modulation.FM || (modulation != Modulation.AM && band.Modulation == Modulation.FM);
        return fm ? WeakSignalBand.VhfFm : WeakSignalBand.VhfAm;
    }

    /// <summary>
    ///     Band character for a radio model (microphone preview, which has no frequency): the first band of the band plan
    ///     that uses the model ("cb" -> HF, "airband" -> VHF AM, "walkie" -> VHF FM, "digital" -> digital); VHF FM for
    ///     other models.
    /// </summary>
    public static WeakSignalBand BandForModel(string modelKey)
    {
        var key = RadioModelFactory.NormaliseModelKey(modelKey);
        if (key.Length == 0) return WeakSignalBand.VhfFm;

        foreach (var band in BandPlan.Bands)
            if (band.Model == key)
                return BandFor(band.FirstFrequency, band.Modulation);

        return key == RadioModelFactory.DigitalModelKey ? WeakSignalBand.Digital : WeakSignalBand.VhfFm;
    }

    /// <summary>
    ///     Amount (0..1) for the profile settings: the distance in percent, or 0 when the radio effect strength is 0
    ///     (radio effects off).
    /// </summary>
    public static float AmountFromSettings(float radioEffectsRatio, float distancePercent)
    {
        if (!(radioEffectsRatio > 0f) || !float.IsFinite(distancePercent)) return 0f;

        return Math.Clamp(distancePercent, 0f, 100f) / 100f;
    }

    /// <summary>The processing at <paramref name="amount" /> (0..1) on <paramref name="band" />.</summary>
    internal static WeakSignalParameters ParametersFor(double amount, WeakSignalBand band)
    {
        var a = double.IsFinite(amount) ? Math.Clamp(amount, 0, 1) : 0;

        // handset / transmitter, the same on every band (h: 0.48 at 35 %, 0.84 at 70 %)
        var h = HandsetShape(a);
        var highPass = 150 * Math.Pow(3, h); // 150 -> 450 Hz
        var lowPass = 4200 * Math.Pow(2600.0 / 4200.0, h); // 4.2 -> 2.6 kHz
        var peakDb = 8 * h; // 0 -> +8 dB
        var driveDb = 12 * h; // 0 -> +12 dB
        var peakHz = band switch
        {
            WeakSignalBand.Hf => 1300.0,
            WeakSignalBand.VhfAm => 1500.0,
            WeakSignalBand.VhfFm => 1700.0,
            _ => 1600.0
        };

        if (band == WeakSignalBand.Digital)
        {
            // no channel; CVSD blended in, grittier with the amount
            return new WeakSignalParameters(a, highPass, lowPass, peakHz, peakDb, driveDb,
                SnrDb: double.PositiveInfinity,
                RiceKDb: double.PositiveInfinity,
                FadeFloorDb: 0,
                FadeRateHz: 0,
                ShadowDb: 0,
                EchoGain: 0,
                EchoDopplerHz: 0,
                AgcReleaseMs: 0,
                FmThresholdDb: double.PositiveInfinity,
                HeterodyneDb: double.NegativeInfinity,
                CodecMix: Math.Pow(a, 0.6), // 0.53 at 35 %, 0.81 at 70 %, 1 at 100 %
                CodecStepMin: 0.02 + 0.04 * a,
                CodecStepMax: 1.0 - 0.4 * a,
                CodecSyllabicMs: 5 + 5 * a);
        }

        // voice to noise at the nominal (unfaded) signal: 60 dB -> 24 dB at 35 %, 13.5 dB at 70 %, 7 dB at 100 %, plus a
        // band offset that compensates the noise the fades add (AGC / FM threshold), so that the voice to noise
        // measured over active speech comes out at about 24 / 14 / 7 dB (HF about 1 dB noisier)
        var snr = 60 - 53 * Math.Pow(a, 0.368);

        if (band == WeakSignalBand.Hf)
        {
            // slow, deep ionospheric fading and the strongest swirl; heterodyne whistle from 50 % (full from 85 %)
            var presence = SmoothStep((a - 0.5) / 0.35);
            return new WeakSignalParameters(a, highPass, lowPass, peakHz, peakDb, driveDb,
                SnrDb: snr + 2.5 + 0.5 * a,
                RiceKDb: 30 * Math.Pow(1 - a, 2.79), // 9 dB at 35 %, 1 dB at 70 %, 0 dB at 100 %
                FadeFloorDb: -22,
                FadeRateHz: 0.12 + 0.2 * a,
                ShadowDb: 0,
                EchoGain: 0.92 * Math.Pow(a, 0.6),
                EchoDopplerHz: 0.06 + 0.3 * a,
                AgcReleaseMs: 600,
                FmThresholdDb: double.PositiveInfinity,
                HeterodyneDb: presence > 0 ? HeterodyneMaxDb + 20 * Math.Log10(presence) : double.NegativeInfinity,
                CodecMix: 0, CodecStepMin: 0, CodecStepMax: 0, CodecSyllabicMs: 0);
        }

        // VHF / UHF: faster, shallower flutter like a moving station, slow shadowing, less comb (FM: half of that
        // again - the limiter keeps the voice steady, the fades show up as threshold hiss instead).
        // AM: the flutter is too fast for the AGC, so it reaches the voice directly - a strong steady component
        // (Rice K 12 dB at 35 %, 7.3 dB at 70 %, 7 dB at 100 %) and a -14 dB floor keep it a warble, clearly shallower
        // than the HF fading (which the AGC turns into breathing static instead), never a dropout.
        // FM: Rice K 12 dB at 35 %, 5 dB at 70 %, 4 dB at 100 % - deeper, but only the threshold hiss follows it.
        var fm = band == WeakSignalBand.VhfFm;
        return new WeakSignalParameters(a, highPass, lowPass, peakHz, peakDb, driveDb,
            SnrDb: snr + (fm ? 2 + 2 * a : 2 + 1.2 * a),
            RiceKDb: fm ? 4 + 26 * Math.Pow(1 - a, 2.73) : 7 + 23 * Math.Pow(1 - a, 3.55),
            FadeFloorDb: fm ? -20 : -14,
            FadeRateHz: 1 + 6 * a,
            ShadowDb: 3 * a,
            EchoGain: (fm ? 0.22 : 0.45) * Math.Pow(a, 0.6),
            EchoDopplerHz: 0.4 + 2.6 * a,
            AgcReleaseMs: fm ? 0 : 350,
            FmThresholdDb: fm ? 24 - 17 * a : double.PositiveInfinity, // 18 dB at 35 %, 12 dB at 70 %, 7 dB at 100 %
            HeterodyneDb: double.NegativeInfinity,
            CodecMix: 0, CodecStepMin: 0, CodecStepMax: 0, CodecSyllabicMs: 0);
    }

    private static float Sanitise(float amount)
    {
        return float.IsFinite(amount) ? Math.Clamp(amount, 0f, 1f) : 0f;
    }

    /// <summary>How far the handset character is developed: 1 - (1 - a)^1.5 (quick at first, finite slope at 0).</summary>
    private static double HandsetShape(double amount)
    {
        return 1 - Math.Pow(1 - amount, 1.5);
    }

    private static double SmoothStep(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * (3 - 2 * x);
    }

    private float Next(float input)
    {
        var target = _targetAmount;
        if (target > 0f)
        {
            if (_mix <= 0f) Activate(target);
            _mix = Math.Min(1f, _mix + MixStep);
        }
        else
        {
            _mix = Math.Max(0f, _mix - MixStep);
            if (_mix <= 0f) return input; // faded out: exact bypass from here on
        }

        var wet = Process(input);
        return _mix >= 1f ? wet : (float)(input + (wet - input) * _mix);
    }

    /// <summary>(Re)starts the processing at <paramref name="amount" />, at a random point of the fading.</summary>
    private void Activate(float amount)
    {
        _amount = amount;

        _handsetHighPass1.Reset();
        _handsetPeak.Reset();
        _handsetHighPass2.Reset();
        _handsetLowPass1.Reset();
        _handsetLowPass2.Reset();
        _hilbertI.Reset();
        _hilbertQ.Reset();
        _hilbertDelay = 0;
        Array.Clear(_delayI);
        Array.Clear(_delayQ);
        _write = 0;
        _noiseHighPass.Reset();
        _noiseLowPass.Reset();
        _pink0 = _pink1 = _pink2 = 0;
        _codecAntiAlias.Reset();
        _codecReconstruction1.Reset();
        _codecReconstruction2.Reset();
        _cleanAntiAlias.Reset();
        _cleanReconstruction1.Reset();
        _cleanReconstruction2.Reset();
        Array.Clear(_cleanDelay);
        _cleanWrite = 0;
        _codecPhase = 0;
        _codecPrevious = _codecCurrent = _codecEstimate = 0;
        _codecBits = 0;

        _power20 = 0;
        _speechPower = InitialSpeechLevel * InitialSpeechLevel;
        _speechLevel = InitialSpeechLevel;
        _handsetInPower = _handsetOutPower = 0;
        _codecInPower = _codecOutPower = 0;
        _peak = 0;
        _limiterGain = 1;

        ApplyParameters(ParametersFor(_amount, Band));
        _codecStep = _codecStepMin;

        // every transmission starts at another point of the fading
        for (var i = 0; i < ProcessCount; i++) _process1[i] = _process2[i] = NextGaussian(ref _channelRng);

        for (var k = 0; k < EchoCount; k++)
        {
            var phase = 2 * Math.PI * NextUniform(ref _channelRng);
            _echoRe[k] = Math.Cos(phase);
            _echoIm[k] = Math.Sin(phase);
        }

        var heterodynePhase = 2 * Math.PI * NextUniform(ref _channelRng);
        _heterodyneRe = Math.Cos(heterodynePhase);
        _heterodyneIm = Math.Sin(heterodynePhase);

        // start settled: all gains at their values for this point of the fading
        _saturationGain = SaturationGainTarget();
        _makeUp = Math.Clamp(1 / _saturationGain, MinMakeUp, MaxMakeUp);
        _codecMakeUp = 1;
        _codecMix = _parameters.CodecMix;
        if (Band != WeakSignalBand.Digital) UpdateChannel(true);

        _controlCountdown = 0;
    }

    private void ApplyParameters(WeakSignalParameters parameters)
    {
        _parameters = parameters;
        var h = HandsetShape(parameters.Amount);

        _handsetHighPass1.Set(parameters.HighPassHz);
        _handsetPeak.SetBell(parameters.PeakHz, 1.2, parameters.PeakDb);
        _handsetHighPass2.Set(parameters.HighPassHz, ButterworthQ);
        _handsetLowPass1.Set(parameters.LowPassHz, 0.54119610014619698); // 4th order Butterworth
        _handsetLowPass2.Set(parameters.LowPassHz, 1.3065629648763766);
        _saturationScale = SaturationScale0 + SaturationScale1 * h;
        _drive = Math.Pow(10, parameters.DriveDb / 20);
        _bias = AsymmetryMax * h;
        _biasOffset = SoftSaturate(_bias);

        if (Band == WeakSignalBand.Digital)
        {
            _codecAntiAlias.Set(CodecFilterHz, ButterworthQ);
            _cleanAntiAlias.Set(CodecFilterHz, ButterworthQ);
            _codecReconstruction1.Set(CodecFilterHz, 0.54119610014619698);
            _codecReconstruction2.Set(CodecFilterHz, 1.3065629648763766);
            _cleanReconstruction1.Set(CodecFilterHz, 0.54119610014619698);
            _cleanReconstruction2.Set(CodecFilterHz, 1.3065629648763766);
            _codecStepMin = parameters.CodecStepMin;
            _codecStepMax = parameters.CodecStepMax;
            _codecSyllabic = Math.Exp(-1.0 / (parameters.CodecSyllabicMs / 1000 * CodecRate));
            return;
        }

        // flat fading: Rice envelope with unit mean power, soft floor, (VHF) log-normal shadowing
        var k = Math.Pow(10, parameters.RiceKDb / 10);
        _riceSpecular = Math.Sqrt(k / (k + 1));
        _riceScattered = Math.Sqrt(1 / (k + 1));
        _fadeFloorPower = Math.Pow(10, parameters.FadeFloorDb / 10);
        _shadowNeper = parameters.ShadowDb * Math.Log(10) / 10;
        _noiseAmplitude = Math.Pow(10, -parameters.SnrDb / 20);
        _agcRelease = parameters.AgcReleaseMs > 0 ? 1 - Math.Exp(-ControlSeconds / (parameters.AgcReleaseMs / 1000)) : 0;
        _heterodynePresence = double.IsFinite(parameters.HeterodyneDb)
            ? Math.Pow(10, (parameters.HeterodyneDb - HeterodyneMaxDb) / 20)
            : 0;

        var hf = Band == WeakSignalBand.Hf;
        SetProcess(ProcessFade1, parameters.FadeRateHz);
        SetProcess(ProcessFade2, parameters.FadeRateHz);
        SetProcess(ProcessShadow, hf ? 0 : 0.15);
        for (var e = 0; e < EchoCount; e++)
        {
            var active = e < _echoes;
            SetProcess(ProcessEchoGain + e, !active ? 0 : hf ? 0.15 + 0.15 * parameters.Amount : 0.5 + parameters.Amount);
            SetProcess(ProcessEchoWander + e, !active ? 0 : hf ? 0.08 : 0.2);
            SetProcess(ProcessEchoDoppler + e, !active ? 0 : hf ? 0.1 : 0.3);
        }

        SetProcess(ProcessHeterodyneFrequency, hf ? 0.06 : 0);
        SetProcess(ProcessHeterodyneLevel, hf ? 0.05 : 0);
    }

    /// <summary>
    ///     A smooth random process with bandwidth <paramref name="hertz" /> (0 = frozen): white noise through two
    ///     cascaded one-poles, scaled to unit variance.
    /// </summary>
    private void SetProcess(int index, double hertz)
    {
        if (!(hertz > 0))
        {
            _processCoefficient[index] = 0;
            return;
        }

        var p = Math.Exp(-2 * Math.PI * hertz * ControlSeconds);
        var c = 1 - p;
        // variance of (1-p)^2 / (1 - p z^-1)^2 for unit white noise
        var variance = c * c * c * c * (1 + p * p) / Math.Pow(1 - p * p, 3);
        _processCoefficient[index] = c;
        _processScale[index] = 1 / Math.Sqrt(variance);
    }

    private double SaturationGainTarget()
    {
        return _saturationScale * _drive / _speechLevel;
    }

    /// <summary>Every <see cref="ControlInterval" /> samples: amount glide, levels, random processes, channel gains.</summary>
    private void ControlTick()
    {
        var target = (double)_targetAmount;
        if (target > 0 && _amount != target)
        {
            _amount += (target - _amount) * AmountCoefficient;
            if (Math.Abs(target - _amount) < 1e-4) _amount = target;
            ApplyParameters(ParametersFor(_amount, Band));
        }

        // speech level
        _speechPower += (_power20 - _speechPower) * (_power20 > _speechPower ? SpeechAttack : SpeechRelease);
        _speechLevel = Math.Max(Math.Sqrt(_speechPower), MinSpeechLevel);

        // handset: saturation relative to the speech level, make-up gain back to the input level (energy weighted
        // over 1.5 s, so speech sets it, not the static in the pauses; kept while it is silent)
        _saturationGainStep = (SaturationGainTarget() - _saturationGain) * InverseControlInterval;
        if (_handsetInPower > SilentPower && _handsetOutPower > SilentPower)
        {
            var makeUp = Math.Clamp(Math.Sqrt(_handsetInPower / _handsetOutPower), MinMakeUp, MaxMakeUp);
            _makeUpStep = (makeUp - _makeUp) * InverseControlInterval;
        }
        else
        {
            _makeUpStep = 0;
        }

        if (Band == WeakSignalBand.Digital)
        {
            _codecMixStep = (_parameters.CodecMix - _codecMix) * InverseControlInterval;
            if (_codecInPower > SilentPower && _codecOutPower > SilentPower)
            {
                var makeUp = Math.Clamp(Math.Sqrt(_codecInPower / _codecOutPower), 0.25, 4);
                _codecMakeUpStep = (makeUp - _codecMakeUp) * InverseControlInterval;
            }
            else
            {
                _codecMakeUpStep = 0;
            }

            return;
        }

        for (var i = 0; i < ProcessCount; i++)
        {
            var c = _processCoefficient[i];
            if (c <= 0) continue;

            var white = (NextUniform(ref _channelRng) + NextUniform(ref _channelRng) - 1) * Sqrt6;
            _process1[i] += c * (_processScale[i] * white - _process1[i]);
            _process2[i] += c * (_process1[i] - _process2[i]);
        }

        UpdateChannel(false);
    }

    /// <summary>
    ///     The channel gains for the current state of the random processes: echoes, flat fading, AGC or FM threshold,
    ///     heterodyne. <paramref name="settle" /> sets them at once (start), otherwise they are interpolated over the next
    ///     <see cref="ControlInterval" /> samples.
    /// </summary>
    private void UpdateChannel(bool settle)
    {
        var parameters = _parameters;
        var hf = Band == WeakSignalBand.Hf;

        // multipath: gains 35-100 % of their maximum, wandering delays, drifting phases
        var echoPower = 0.0;
        for (var e = 0; e < _echoes; e++)
        {
            var share = 0.35 + 0.65 * (0.5 + 0.5 * Math.Tanh(1.2 * _process2[ProcessEchoGain + e]));
            var gain = parameters.EchoGain * _echoWeight[e] * share;
            echoPower += gain * gain;

            var delay = Math.Clamp(
                (_echoDelayMs[e] + _echoWanderMs[e] * Math.Tanh(_process2[ProcessEchoWander + e])) * SampleRate / 1000,
                MinDelaySamples, MaxDelaySamples);

            // phase increment per sample (at most a few Hz: small angle)
            var omega = 2 * Math.PI * parameters.EchoDopplerHz * _process2[ProcessEchoDoppler + e] / SampleRate;
            _echoRotationCos[e] = Math.Cos(omega);
            _echoRotationSin[e] = Math.Sin(omega);
            var norm = 1 / Math.Sqrt(_echoRe[e] * _echoRe[e] + _echoIm[e] * _echoIm[e]);
            _echoRe[e] *= norm;
            _echoIm[e] *= norm;

            if (settle)
            {
                _echoGain[e] = gain;
                _echoDelay[e] = delay;
                _echoGainStep[e] = _echoDelayStep[e] = 0;
            }
            else
            {
                _echoGainStep[e] = (gain - _echoGain[e]) * InverseControlInterval;
                _echoDelayStep[e] = (delay - _echoDelay[e]) * InverseControlInterval;
            }
        }

        var multipathNorm = 1 / Math.Sqrt(1 + echoPower);

        // flat fading: Rice envelope (unit mean power), soft floor, shadowing
        var x1 = _process2[ProcessFade1] * InverseSqrt2;
        var x2 = _process2[ProcessFade2] * InverseSqrt2;
        var re = _riceSpecular + _riceScattered * x1;
        var im = _riceScattered * x2;
        var fade = (re * re + im * im + _fadeFloorPower) / (1 + _fadeFloorPower);
        if (_shadowNeper > 0)
            fade *= Math.Exp(_shadowNeper * _process2[ProcessShadow] - 0.5 * _shadowNeper * _shadowNeper);

        double voiceGain, noiseGain;
        var noise = _noiseAmplitude;
        if (Band == WeakSignalBand.VhfFm)
        {
            // FM limiter: the voice keeps its level; the hiss follows 1 / carrier and surges up below the threshold
            var fadeDb = 10 * Math.Log10(fade);
            var noiseDb = -parameters.SnrDb - fadeDb +
                          FmSurgeSlope * SoftPlus(-parameters.FmThresholdDb - fadeDb, FmSurgeKneeDb);
            noiseDb = FmNoiseCeilingDb - SoftPlus(FmNoiseCeilingDb - noiseDb, FmCeilingKneeDb);
            _fmNoiseDb = settle ? noiseDb : _fmNoiseDb + (noiseDb - _fmNoiseDb) * FmSmoothing;

            var n = Math.Pow(10, _fmNoiseDb / 20);
            var total = 1 / Math.Sqrt(1 + n * n);
            voiceGain = total * multipathNorm;
            noiseGain = n * total * _speechLevel;
        }
        else
        {
            // AM receiver AGC on the carrier (signal + noise power): fast attack, slow release - the noise comes up
            // in the fades; a slow trim keeps the long-term level
            var power = fade + noise * noise;
            var level = Math.Sqrt(power);
            if (settle)
            {
                _agc = level;
                _agcTrimPower = 1;
            }
            else
            {
                _agc += (level - _agc) * (level > _agc ? AgcAttack : _agcRelease);
                _agcTrimPower += (power / (_agc * _agc) - _agcTrimPower) * AgcTrimCoefficient;
            }

            var gain = Math.Clamp(1 / Math.Sqrt(_agcTrimPower), 0.5, 2) / _agc;
            voiceGain = Math.Sqrt(fade) * gain * multipathNorm;
            noiseGain = noise * gain * _speechLevel;
        }

        // heterodyne: drifting 600-1400 Hz, fading in and out
        var heterodyne = 0.0;
        if (hf && _heterodynePresence > 0)
        {
            var hertz = HeterodyneCentreHz + HeterodyneSwingHz * Math.Tanh(0.8 * _process2[ProcessHeterodyneFrequency]);
            var omega = 2 * Math.PI * hertz / SampleRate;
            _heterodyneCos = Math.Cos(omega);
            _heterodyneSin = Math.Sin(omega);
            var norm = 1 / Math.Sqrt(_heterodyneRe * _heterodyneRe + _heterodyneIm * _heterodyneIm);
            _heterodyneRe *= norm;
            _heterodyneIm *= norm;

            var envelope = 0.5 + 0.5 * Math.Tanh(2 * _process2[ProcessHeterodyneLevel]);
            heterodyne = _speechLevel * Math.Pow(10, HeterodyneMaxDb / 20) * _heterodynePresence * envelope;
        }

        if (settle)
        {
            _voiceGain = voiceGain;
            _noiseGain = noiseGain;
            _heterodyneAmplitude = heterodyne;
            _voiceGainStep = _noiseGainStep = _heterodyneAmplitudeStep = 0;
        }
        else
        {
            _voiceGainStep = (voiceGain - _voiceGain) * InverseControlInterval;
            _noiseGainStep = (noiseGain - _noiseGain) * InverseControlInterval;
            _heterodyneAmplitudeStep = (heterodyne - _heterodyneAmplitude) * InverseControlInterval;
        }
    }

    private float Process(float input)
    {
        if (--_controlCountdown <= 0)
        {
            _controlCountdown = ControlInterval;
            ControlTick();
        }

        double x = input;
        _power20 += (x * x - _power20) * Power20Coefficient;

        var voice = Handset(x);
        var y = Band == WeakSignalBand.Digital ? Codec(voice) : Channel(voice);

        return (float)Limit(y);
    }

    /// <summary>Handset / transmitter: high-pass, honk, soft saturation, low-pass, make-up gain.</summary>
    private double Handset(double x)
    {
        _handsetInPower += (x * x - _handsetInPower) * MakeUpCoefficient;

        var y = _handsetPeak.Bell(_handsetHighPass1.Process(x));
        _saturationGain += _saturationGainStep;
        y = SoftSaturate(_saturationGain * y + _bias) - _biasOffset;
        y = _handsetLowPass2.LowPass(_handsetLowPass1.LowPass(_handsetHighPass2.HighPass(y)));

        _handsetOutPower += (y * y - _handsetOutPower) * MakeUpCoefficient;
        _makeUp += _makeUpStep;
        return y * _makeUp;
    }

    /// <summary>The analogue channel: multipath, flat fading, noise and AGC / FM threshold, heterodyne.</summary>
    private double Channel(double voice)
    {
        // analytic signal (I: direct path, Q: 90 degrees for the echo phase rotation)
        var i = _hilbertDelay;
        _hilbertDelay = _hilbertI.Process(voice);
        var q = _hilbertQ.Process(voice);
        _delayI[_write] = i;
        _delayQ[_write] = q;

        var sum = i;
        for (var e = 0; e < _echoes; e++)
        {
            var delay = _echoDelay[e] += _echoDelayStep[e];
            var gain = _echoGain[e] += _echoGainStep[e];

            // cubic (Catmull-Rom) interpolation between the samples delay - 1 and delay (from the write position)
            var whole = (int)delay;
            var t = 1 - (delay - whole);
            var index = _write - whole - 2;
            var iv = Cubic(_delayI, index, t);
            var qv = Cubic(_delayQ, index, t);

            var cos = _echoRotationCos[e];
            var sin = _echoRotationSin[e];
            var er = _echoRe[e];
            var ei = _echoIm[e];
            _echoRe[e] = er * cos - ei * sin;
            _echoIm[e] = er * sin + ei * cos;

            sum += gain * (iv * er - qv * ei);
        }

        _write = (_write + 1) & DelayMask;

        _voiceGain += _voiceGainStep;
        _noiseGain += _noiseGainStep;
        _heterodyneAmplitude += _heterodyneAmplitudeStep;

        var y = _voiceGain * sum;
        if (NoiseMuted) return y;

        y += _noiseGain * Noise();

        if (_heterodyneAmplitude > 0)
        {
            var hr = _heterodyneRe;
            _heterodyneRe = hr * _heterodyneCos - _heterodyneIm * _heterodyneSin;
            _heterodyneIm = hr * _heterodyneSin + _heterodyneIm * _heterodyneCos;
            y += _heterodyneAmplitude * _heterodyneIm;
        }

        return y;
    }

    /// <summary>Filtered Gaussian noise of unit variance in the colour of the band.</summary>
    private double Noise()
    {
        // two uniforms = triangular white noise; the colouring filters make it Gaussian
        var white = (NextUniform(ref _noiseRng) + NextUniform(ref _noiseRng) - 1) * Sqrt6;
        return ColourNoise(white, _pinkNoise, ref _pink0, ref _pink1, ref _pink2, _noiseHighPass, _noiseLowPass) *
               _noiseNorm;
    }

    private static double ColourNoise(double white, bool pink, ref double b0, ref double b1, ref double b2,
        Svf highPass, Svf lowPass)
    {
        var s = white;
        if (pink)
        {
            // P. Kellet's economy pink filter (-3 dB / octave)
            b0 = 0.99765 * b0 + white * 0.0990460;
            b1 = 0.96300 * b1 + white * 0.2965164;
            b2 = 0.57000 * b2 + white * 1.0526913;
            s = b0 + b1 + b2 + white * 0.1848;
        }

        return lowPass.LowPass(highPass.HighPass(s));
    }

    private static void ConfigureNoise(WeakSignalBand band, Svf highPass, Svf lowPass)
    {
        switch (band)
        {
            case WeakSignalBand.Hf:
                // soft pink-ish band hiss with a little rumble
                highPass.Set(150, 0.5);
                lowPass.Set(2700, ButterworthQ);
                break;
            case WeakSignalBand.VhfAm:
                // mid hiss
                highPass.Set(350, ButterworthQ);
                lowPass.Set(3000, ButterworthQ);
                break;
            default:
                // FM: bright hiss above 1 kHz
                highPass.Set(1000, ButterworthQ);
                lowPass.Set(5000, ButterworthQ);
                break;
        }
    }

    private static double[] ComputeNoiseNorms()
    {
        var bands = Enum.GetValues<WeakSignalBand>();
        var norms = new double[bands.Length];
        foreach (var band in bands)
        {
            var highPass = new Svf();
            var lowPass = new Svf();
            ConfigureNoise(band, highPass, lowPass);
            double b0 = 0, b1 = 0, b2 = 0, energy = 0;
            for (var n = 0; n < 1 << 16; n++)
            {
                var y = ColourNoise(n == 0 ? 1 : 0, band == WeakSignalBand.Hf, ref b0, ref b1, ref b2, highPass, lowPass);
                energy += y * y;
            }

            norms[(int)band] = 1 / Math.Sqrt(energy);
        }

        return norms;
    }

    /// <summary>
    ///     Digital: 16 kHz CVSD of the handset voice, blended in with the amount; the clean share runs through the same
    ///     filters and latency, so the blend does not comb.
    /// </summary>
    private double Codec(double voice)
    {
        _codecInPower += (voice * voice - _codecInPower) * MakeUpCoefficient;

        var coded = _codecAntiAlias.LowPass(voice);
        if (_codecPhase == 0)
        {
            _codecPrevious = _codecCurrent;
            _codecCurrent = Cvsd(coded / _speechLevel) * _speechLevel;
        }

        coded = _codecPrevious + (_codecCurrent - _codecPrevious) * (_codecPhase * (1.0 / CodecDecimation));
        if (++_codecPhase >= CodecDecimation) _codecPhase = 0;
        coded = _codecReconstruction2.LowPass(_codecReconstruction1.LowPass(coded));

        _cleanDelay[_cleanWrite] = voice;
        var clean = _cleanDelay[(_cleanWrite - CodecCleanDelay) & (CodecDelaySize - 1)];
        _cleanWrite = (_cleanWrite + 1) & (CodecDelaySize - 1);
        clean = _cleanReconstruction2.LowPass(_cleanReconstruction1.LowPass(_cleanAntiAlias.LowPass(clean)));

        _codecMix += _codecMixStep;
        var y = CodecMuted ? clean : clean + (coded - clean) * _codecMix;

        _codecOutPower += (y * y - _codecOutPower) * MakeUpCoefficient;
        _codecMakeUp += _codecMakeUpStep;
        return y * _codecMakeUp;
    }

    /// <summary>
    ///     One CVSD step (encoder and decoder in one): 1 bit per sample, the step grows while 3 equal bits in a row show
    ///     slope overload (syllabic filter) and shrinks back otherwise; leaky principal integrator (1 ms).
    /// </summary>
    private double Cvsd(double x)
    {
        var bit = x >= _codecEstimate ? 1 : 0;
        _codecBits = ((_codecBits << 1) | bit) & 7;
        var run = _codecBits == 0 || _codecBits == 7;
        _codecStep = _codecStep * _codecSyllabic + (1 - _codecSyllabic) * (run ? _codecStepMax : _codecStepMin);
        _codecEstimate = Math.Clamp(_codecEstimate * CodecLeak + (bit != 0 ? _codecStep : -_codecStep), -8, 8);
        return _codecEstimate;
    }

    /// <summary>Smooth peak limiter (0.85, 0.5 ms attack, 150 ms release), then a soft clip that stays below 0.9.</summary>
    private double Limit(double y)
    {
        var abs = Math.Abs(y);
        _peak = abs > _peak ? abs : _peak * PeakRelease;
        var target = _peak > LimiterThreshold ? LimiterThreshold / _peak : 1.0;
        _limiterGain += (target - _limiterGain) * (target < _limiterGain ? LimiterAttack : LimiterRelease);
        y *= _limiterGain;

        abs = Math.Abs(y);
        if (abs <= SoftClipKnee) return y;

        var clipped = SoftClipKnee + (MaxOutputPeak - SoftClipKnee) *
            SoftSaturate((abs - SoftClipKnee) / (MaxOutputPeak - SoftClipKnee));
        return y < 0 ? -clipped : clipped;
    }

    /// <summary>tanh-like soft saturation (Pade 3/2, C1-smooth into +-1 at +-3).</summary>
    private static double SoftSaturate(double x)
    {
        if (x >= 3) return 1;
        if (x <= -3) return -1;

        var x2 = x * x;
        return x * (27 + x2) / (27 + 9 * x2);
    }

    private static double SoftPlus(double x, double knee)
    {
        var z = x / knee;
        return z > 30 ? x : knee * Math.Log(1 + Math.Exp(z));
    }

    /// <summary>Catmull-Rom interpolation between <c>buffer[index + 1]</c> and <c>buffer[index + 2]</c> at t (0..1).</summary>
    private static double Cubic(double[] buffer, int index, double t)
    {
        var ym1 = buffer[index & DelayMask];
        var y0 = buffer[(index + 1) & DelayMask];
        var y1 = buffer[(index + 2) & DelayMask];
        var y2 = buffer[(index + 3) & DelayMask];
        var c1 = 0.5 * (y1 - ym1);
        var c2 = ym1 - 2.5 * y0 + 2 * y1 - 0.5 * y2;
        var c3 = 0.5 * (y2 - ym1) + 1.5 * (y0 - y1);
        return ((c3 * t + c2) * t + c1) * t + y0;
    }

    private static ulong SplitMix(ulong seed)
    {
        var z = unchecked(seed + 0x9E3779B97F4A7C15UL);
        z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
        return (z ^ (z >> 31)) | 1UL;
    }

    /// <summary>xorshift64*: uniform in [0, 1).</summary>
    private static double NextUniform(ref ulong state)
    {
        state ^= state >> 12;
        state ^= state << 25;
        state ^= state >> 27;
        return (unchecked(state * 2685821657736338717UL) >> 11) * (1.0 / (1UL << 53));
    }

    /// <summary>Box-Muller (only for the start values of the random processes).</summary>
    private static double NextGaussian(ref ulong state)
    {
        var u1 = 1 - NextUniform(ref state);
        var u2 = NextUniform(ref state);
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    private static double[] Squares(params double[] values)
    {
        var squares = new double[values.Length];
        for (var i = 0; i < values.Length; i++) squares[i] = values[i] * values[i];

        return squares;
    }

    /// <summary>Topology-preserving state variable filter (Simper / Zavalishin): stays smooth while it is retuned.</summary>
    private sealed class Svf
    {
        private double _a1;
        private double _a2;
        private double _a3;
        private double _ic1;
        private double _ic2;
        private double _k;
        private double _m1;

        public void Set(double frequency, double q)
        {
            var g = Math.Tan(Math.PI * Math.Clamp(frequency, 10, SampleRate * 0.45) / SampleRate);
            _k = 1 / q;
            _a1 = 1 / (1 + g * (g + _k));
            _a2 = g * _a1;
            _a3 = g * _a2;
            _m1 = 0;
        }

        /// <summary>Peaking ("bell") equaliser.</summary>
        public void SetBell(double frequency, double q, double gainDb)
        {
            var a = Math.Pow(10, gainDb / 40);
            var g = Math.Tan(Math.PI * Math.Clamp(frequency, 10, SampleRate * 0.45) / SampleRate);
            _k = 1 / (q * a);
            _a1 = 1 / (1 + g * (g + _k));
            _a2 = g * _a1;
            _a3 = g * _a2;
            _m1 = _k * (a * a - 1);
        }

        public void Reset()
        {
            _ic1 = _ic2 = 0;
        }

        private void Tick(double x, out double band, out double low)
        {
            var v3 = x - _ic2;
            band = _a1 * _ic1 + _a2 * v3;
            low = _ic2 + _a2 * _ic1 + _a3 * v3;
            _ic1 = 2 * band - _ic1;
            _ic2 = 2 * low - _ic2;
        }

        public double LowPass(double x)
        {
            Tick(x, out _, out var low);
            return low;
        }

        public double HighPass(double x)
        {
            Tick(x, out var band, out var low);
            return x - _k * band - low;
        }

        public double Bell(double x)
        {
            Tick(x, out var band, out _);
            return x + _m1 * band;
        }
    }

    /// <summary>Topology-preserving one-pole high-pass.</summary>
    private sealed class OnePoleHighPass
    {
        private double _g;
        private double _s;

        public void Set(double frequency)
        {
            var g = Math.Tan(Math.PI * Math.Clamp(frequency, 10, SampleRate * 0.45) / SampleRate);
            _g = g / (1 + g);
        }

        public void Reset()
        {
            _s = 0;
        }

        public double Process(double x)
        {
            var v = (x - _s) * _g;
            var low = v + _s;
            _s = low + v;
            return x - low;
        }
    }

    /// <summary>Four second-order allpass sections in z^-2 (half of the IIR Hilbert pair).</summary>
    private sealed class AllpassChain
    {
        private readonly double _c0;
        private readonly double _c1;
        private readonly double _c2;
        private readonly double _c3;
        private readonly double[] _state = new double[16];

        public AllpassChain(double[] squaredCoefficients)
        {
            _c0 = squaredCoefficients[0];
            _c1 = squaredCoefficients[1];
            _c2 = squaredCoefficients[2];
            _c3 = squaredCoefficients[3];
        }

        public void Reset()
        {
            Array.Clear(_state);
        }

        public double Process(double x)
        {
            var s = _state;
            x = Section(s, 0, _c0, x);
            x = Section(s, 4, _c1, x);
            x = Section(s, 8, _c2, x);
            return Section(s, 12, _c3, x);
        }

        // y[n] = c (x[n] + y[n-2]) - x[n-2]; state: x[n-1], x[n-2], y[n-1], y[n-2]
        private static double Section(double[] s, int i, double c, double x)
        {
            var y = c * (x + s[i + 3]) - s[i + 1];
            s[i + 1] = s[i];
            s[i] = x;
            s[i + 3] = s[i + 2];
            s[i + 2] = y;
            return y;
        }
    }
}
