using System;
using EasyRadioLink.Common.Audio.Models;
using EasyRadioLink.Common.Models.Player;
using NAudio.Wave;

namespace EasyRadioLink.Common.Audio.Providers;

/// <summary>Band character of the <see cref="VoiceDistortionProvider" />, from the band plan of the frequency.</summary>
public enum VoiceDistortionFlavour
{
    /// <summary>VHF / UHF bands: fading, crackle and dropouts.</summary>
    Standard,

    /// <summary>Bands below 30 MHz (MW, HF, CB): 1.5 x deeper fading (propagation QSB).</summary>
    Hf,

    /// <summary>
    ///     DIG band / DIGITAL modulation: no fading and no crackle; "digital breakup" (short mutes and robotic repeats
    ///     of the last 10-20 ms, like packet loss concealment) instead of the analogue dropouts.
    /// </summary>
    Digital
}

/// <summary>
///     What the <see cref="VoiceDistortionProvider" /> does at one amount (0..1). Every value is a smooth function of
///     the amount; the event rates are 0 up to 50 %.
/// </summary>
internal readonly record struct VoiceDistortionParameters(
    double Amount,
    double HighPassHz,
    double LowPassHz,
    double DriveDb,
    double Asymmetry,
    double Bits,
    double HoldRateHz,
    double FadingDepthDb,
    double Damage,
    double CrackleRate,
    double DropoutRate,
    double BreakupRate);

/// <summary>
///     "Voice distortion": makes the received voice itself sound like a bad radio link instead of only adding effects
///     on top of a clean voice. 48 kHz mono float in and out; one instance per received transmission (the state - filters,
///     fading, random events - carries over from one <see cref="Read" /> to the next, so there are no clicks at block
///     boundaries). Deterministic for a given seed.
///     <para>Chain, all scaled smoothly by <see cref="Amount" /> (0 = exact bypass, the output is the input):</para>
///     <list type="number">
///         <item>
///             channel (not for <see cref="VoiceDistortionFlavour.Digital" />): slow random fading (QSB, 0.3-2 Hz, up to
///             +-2 dB at 35 %, +-5 dB at 70 %, +-8 dB at 100 %, x1.5 below 30 MHz) with a little noise filling the fades,
///             and above 50 % sparse band-limited crackle;
///         </item>
///         <item>band narrowing: high-pass 200 -> 500 Hz, low-pass 3.8 -> 2.3 kHz (modulation-safe state variable filters);</item>
///         <item>
///             overdrive: slightly asymmetric soft clipping with up to +20 dB drive (between the two filters, so the
///             distortion stays inside the narrowed band), then make-up gain that keeps the level of the input (and a
///             peak limit of 0.9, so it never adds clipping);
///         </item>
///         <item>lo-fi: sample-and-hold decimation down to 8 kHz and bit-depth reduction down to 6 bits at 100 %;</item>
///         <item>
///             above 50 %: short dropouts (10-40 ms gaps with 2 ms ramps), or for digital the digital breakup
///             (20-60 ms mutes or repeats of the previous 10-20 ms frame).
///         </item>
///     </list>
/// </summary>
public sealed class VoiceDistortionProvider : ISampleProvider
{
    /// <summary>Default of <c>ProfileSettingsKeys.VoiceDistortion</c> in percent.</summary>
    public const float DefaultPercent = 35f;

    // = Constants.OUTPUT_SAMPLE_RATE (a constant here, so the ramp lengths below can be constants too)
    internal const int SampleRate = 48000;

    // Parameters, fading and make-up gain are updated every 32 samples (0.67 ms), independently of the block size.
    private const int ControlInterval = 32;

    // 2 ms ramps of dropouts / breakups, 1 ms cross-fade at the seam of a repeated frame.
    private const int RampSamples = SampleRate / 500;
    private const int LoopFadeSamples = SampleRate / 1000;

    // 20 ms fade between the dry and the distorted voice when the amount is switched on / off while playing.
    private const float MixStep = 1f / (SampleRate / 50);

    private const int HistorySize = 2048;
    private const double MaxOutputPeak = 0.9;
    private const double MaxMakeUp = 4.0;
    private const double MinMakeUp = 0.02;
    private const double PowerFloor = 1e-9;
    private const double ButterworthQ = 0.70710678118654752;
    private const double CrackleQ = 1.1;
    private const double FadeNoise = 0.15;
    private const int Never = int.MaxValue;

    private static readonly WaveFormat Format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1);

    // per sample: 80 ms power envelopes (make-up gain), 1 s input envelope (fade noise), 100 ms peak release
    private static readonly double EnvelopeCoefficient = 1 - Math.Exp(-1.0 / (0.08 * SampleRate));
    private static readonly double SlowEnvelopeCoefficient = 1 - Math.Exp(-1.0 / (1.0 * SampleRate));
    private static readonly double PeakRelease = Math.Exp(-1.0 / (0.1 * SampleRate));
    private static readonly double DcBlockPole = 1 - 2 * Math.PI * 20 / SampleRate;

    // per control tick: amount glides in 30 ms, fading rates wander in 0.8 s
    private static readonly double AmountCoefficient = 1 - Math.Exp(-ControlInterval / (0.03 * SampleRate));
    private static readonly double FadeRateCoefficient = 1 - Math.Exp(-ControlInterval / (0.8 * SampleRate));

    // amount: target (set from any thread), smoothed value, dry/wet mix (0 = exact bypass) and what it means
    private volatile float _targetAmount;
    private double _amount;
    private float _mix;
    private VoiceDistortionParameters _parameters;
    private int _controlCountdown;
    private ulong _rng;

    // channel: fading (two slow sines with wandering rates) and the noise that comes up in the fades
    private float _fadeGain = 1f;
    private float _fadeGainStep;
    private double _fadeCorrectionDb;
    private double _fadeCorrectionDepth = double.NaN;
    private double _fadeNoiseLevel;
    private double _fadePhase1;
    private double _fadePhase2;
    private double _fadeRate1;
    private double _fadeRate2;
    private double _fadeTarget1;
    private double _fadeTarget2;
    private int _fadeRetarget;
    private double _inputPower;

    // channel: crackle
    private readonly Svf _crackleFilter = new();
    private double _crackleAmplitude;
    private double _crackleNorm = 1;
    private int _crackleCountdown = Never;
    private int _crackleBurstLeft;
    private int _crackleBurstGap;

    // band narrowing and overdrive
    private readonly Svf _highPass = new();
    private readonly Svf _lowPass = new();
    private double _drive = 1;
    private double _bias;
    private double _biasTanh;
    private double _dcIn;
    private double _dcOut;

    // make-up gain and peak limit
    private double _refPower;
    private double _outPower;
    private double _makeUp = 1;
    private double _makeUpStep;
    private double _peak;

    // lo-fi
    private double _holdStep = 1;
    private double _holdPhase;
    private double _held;
    private double _levels;

    // dropouts / digital breakup (with the history the repeated frame is taken from)
    private int _eventCountdown = Never;
    private int _eventPos = -1;
    private int _eventHold;
    private int _eventLength;
    private bool _eventRepeat;
    private readonly float[] _history = new float[HistorySize];
    private int _historyPos;
    private readonly float[] _frame = new float[HistorySize];
    private int _frameLength;
    private int _framePos;
    private double _frameGain;
    private double _frameDecay;

    /// <param name="source">48 kHz mono float source (may be set later, see <see cref="Source" />).</param>
    /// <param name="flavour">band character, see <see cref="FlavourFor" /> / <see cref="FlavourForModel" /></param>
    /// <param name="seed">seed of the random fading and events (same seed = same output)</param>
    /// <param name="amount">0..1 (0 = off)</param>
    public VoiceDistortionProvider(ISampleProvider source, VoiceDistortionFlavour flavour, int seed, float amount)
    {
        Source = source;
        Flavour = flavour;

        // SplitMix64 of the seed: never 0 (xorshift must not start at 0)
        var z = unchecked((ulong)(uint)seed + 0x9E3779B97F4A7C15UL);
        z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
        _rng = (z ^ (z >> 31)) | 1UL;

        _targetAmount = Sanitise(amount);
        if (_targetAmount > 0f)
        {
            // a new transmission starts distorted - no fade in
            Activate(_targetAmount);
            _mix = 1f;
        }
    }

    /// <summary>
    ///     The source; the receive pipeline points it at the dry / wet mix of the received voice (the sender's radio model
    ///     and the dry share of the radio effect strength) for every block, the Audio Preview at the chosen radio model.
    /// </summary>
    public ISampleProvider Source { get; set; }

    public VoiceDistortionFlavour Flavour { get; }

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

    // diagnostics (tests)
    internal int CrackleCount { get; private set; }
    internal int DropoutCount { get; private set; }
    internal int BreakupCount { get; private set; }

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
    ///     Band character of a transmission: DIGITAL modulation or the DIG band = digital, bands below 30 MHz (MW, HF, CB)
    ///     = HF, everything else standard.
    /// </summary>
    public static VoiceDistortionFlavour FlavourFor(double frequency, Modulation modulation)
    {
        if (modulation == Modulation.DIGITAL) return VoiceDistortionFlavour.Digital;

        var band = BandPlan.GetBand(frequency);
        if (band.Modulation == Modulation.DIGITAL) return VoiceDistortionFlavour.Digital;

        return band.LastFrequency < RadioEffectRules.HfNoiseFrequencyCutoff
            ? VoiceDistortionFlavour.Hf
            : VoiceDistortionFlavour.Standard;
    }

    /// <summary>
    ///     Band character for a radio model (microphone preview, which has no frequency): the first band of the band plan
    ///     that uses the model ("cb" -> HF, "digital" -> digital, "walkie" -> standard); standard for other models.
    /// </summary>
    public static VoiceDistortionFlavour FlavourForModel(string modelKey)
    {
        var key = RadioModelFactory.NormaliseModelKey(modelKey);
        if (key.Length == 0) return VoiceDistortionFlavour.Standard;

        foreach (var band in BandPlan.Bands)
            if (band.Model == key)
                return FlavourFor(band.FirstFrequency, band.Modulation);

        return key == RadioModelFactory.DigitalModelKey ? VoiceDistortionFlavour.Digital : VoiceDistortionFlavour.Standard;
    }

    /// <summary>
    ///     Amount (0..1) for the profile settings: the voice distortion in percent, or 0 when the radio effect strength is 0
    ///     (radio effects off).
    /// </summary>
    public static float AmountFromSettings(float radioEffectsRatio, float distortionPercent)
    {
        if (!(radioEffectsRatio > 0f) || !float.IsFinite(distortionPercent)) return 0f;

        return Math.Clamp(distortionPercent, 0f, 100f) / 100f;
    }

    /// <summary>The processing at <paramref name="amount" /> (0..1) for <paramref name="flavour" />.</summary>
    internal static VoiceDistortionParameters ParametersFor(double amount, VoiceDistortionFlavour flavour)
    {
        var a = double.IsFinite(amount) ? Math.Clamp(amount, 0, 1) : 0;

        // band narrowing (log interpolation): 200 -> 500 Hz and 3.8 -> 2.3 kHz
        var highPass = 200 * Math.Pow(2.5, a);
        var lowPass = 3800 * Math.Pow(2300.0 / 3800.0, a);

        // overdrive: +9.6 dB at 35 %, +15.6 dB at 70 %, +20 dB at 100 %
        var drive = 20 * Math.Pow(a, 0.7);
        var asymmetry = 0.25 * a;

        // lo-fi from 10 %: 14 -> 6 bits (9 bits at 35 %, 8.4 at 40 %, 6.3 at 70 %) and sample and hold at
        // 48 -> 8 kHz (18.7 kHz at 35 %, 17 kHz at 40 %, 11 kHz at 70 %)
        var lofi = Math.Clamp((a - 0.1) / 0.9, 0, 1);
        var bits = lofi > 0 ? 6 + 8 * Math.Pow(1 - lofi, 3) : 0;
        var holdRate = SampleRate * Math.Pow(8000.0 / SampleRate, Math.Sqrt(lofi));

        // fading swing in dB: 2 at 35 %, 5 at 70 %, 8 at 100 %
        var fading = a <= 0.35 ? 2 * a / 0.35
            : a <= 0.7 ? 2 + 3 * (a - 0.35) / 0.35
            : 5 + 3 * (a - 0.7) / 0.3;

        // crackle, dropouts and breakups only above 50 %
        var damage = Math.Clamp((a - 0.5) / 0.5, 0, 1);
        var crackle = 8 * damage;
        var dropouts = 2 * damage;
        var breakups = 0.0;

        switch (flavour)
        {
            case VoiceDistortionFlavour.Hf:
                fading *= 1.5;
                break;
            case VoiceDistortionFlavour.Digital:
                fading = 0;
                crackle = 0;
                dropouts = 0;
                breakups = 2.5 * damage;
                break;
        }

        return new VoiceDistortionParameters(a, highPass, lowPass, drive, asymmetry, bits, holdRate, fading, damage,
            crackle, dropouts, breakups);
    }

    private static float Sanitise(float amount)
    {
        return float.IsFinite(amount) ? Math.Clamp(amount, 0f, 1f) : 0f;
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
        return _mix >= 1f ? wet : input + (wet - input) * _mix;
    }

    /// <summary>(Re)starts the processing from silence at <paramref name="amount" />.</summary>
    private void Activate(float amount)
    {
        _amount = amount;
        _highPass.Reset();
        _lowPass.Reset();
        _crackleFilter.Reset();
        _dcIn = _dcOut = 0;
        _refPower = _outPower = _peak = _inputPower = 0;
        _held = 0;
        _holdPhase = 0;
        Array.Clear(_history);
        _historyPos = 0;
        _eventPos = -1;
        _eventCountdown = Never;
        _crackleCountdown = Never;
        _crackleBurstLeft = 0;

        ApplyParameters(ParametersFor(_amount, Flavour));

        // start the make-up gain at its small-signal value
        _makeUp = Math.Clamp(1 / (_drive * (1 - _biasTanh * _biasTanh)), MinMakeUp, MaxMakeUp);
        _makeUpStep = 0;

        // every transmission fades differently
        _fadePhase1 = NextUniform() * 2 * Math.PI;
        _fadePhase2 = NextUniform() * 2 * Math.PI;
        _fadeRate1 = _fadeTarget1 = 0.3 + 0.9 * NextUniform();
        _fadeRate2 = _fadeTarget2 = 0.8 + 1.2 * NextUniform();
        _fadeRetarget = SampleRate;
        _fadeGain = (float)FadeGainTarget();
        _fadeGainStep = 0;

        _controlCountdown = 0;
    }

    private void ApplyParameters(VoiceDistortionParameters parameters)
    {
        _parameters = parameters;
        _highPass.Set(parameters.HighPassHz, ButterworthQ);
        _lowPass.Set(parameters.LowPassHz, ButterworthQ);
        _drive = Math.Pow(10, parameters.DriveDb / 20);
        _bias = parameters.Asymmetry;
        _biasTanh = Math.Tanh(_bias);
        _levels = parameters.Bits > 0 ? Math.Pow(2, parameters.Bits - 1) : 0;
        _holdStep = Math.Min(1.0, parameters.HoldRateHz / SampleRate);
        _crackleAmplitude = 0.05 + 0.15 * parameters.Damage;
    }

    /// <summary>Every <see cref="ControlInterval" /> samples: amount glide, fading, make-up gain, event rates.</summary>
    private void ControlTick()
    {
        var target = (double)_targetAmount;
        if (target > 0 && _amount != target)
        {
            _amount += (target - _amount) * AmountCoefficient;
            if (Math.Abs(target - _amount) < 1e-4) _amount = target;
            ApplyParameters(ParametersFor(_amount, Flavour));
        }

        // fading
        if (_parameters.FadingDepthDb > 0)
        {
            _fadeRetarget -= ControlInterval;
            if (_fadeRetarget <= 0)
            {
                _fadeRetarget = SampleRate + (int)(NextUniform() * SampleRate);
                _fadeTarget1 = 0.3 + 0.9 * NextUniform();
                _fadeTarget2 = 0.8 + 1.2 * NextUniform();
            }

            _fadeRate1 += (_fadeTarget1 - _fadeRate1) * FadeRateCoefficient;
            _fadeRate2 += (_fadeTarget2 - _fadeRate2) * FadeRateCoefficient;
            _fadePhase1 = (_fadePhase1 + 2 * Math.PI * _fadeRate1 * ControlInterval / SampleRate) % (2 * Math.PI);
            _fadePhase2 = (_fadePhase2 + 2 * Math.PI * _fadeRate2 * ControlInterval / SampleRate) % (2 * Math.PI);
        }

        var fadeTarget = FadeGainTarget();
        _fadeGainStep = (float)((fadeTarget - _fadeGain) / ControlInterval);
        _fadeNoiseLevel = FadeNoise * Math.Max(0, 1 - fadeTarget) * Math.Sqrt(_inputPower);

        // make-up gain: output level = level of the (faded) input
        var makeUp = Math.Clamp(Math.Sqrt((_refPower + PowerFloor) / (_outPower + PowerFloor)), MinMakeUp, MaxMakeUp);
        _makeUpStep = (makeUp - _makeUp) / ControlInterval;

        // random events
        if (Flavour != VoiceDistortionFlavour.Digital)
        {
            if (_parameters.CrackleRate <= 0) _crackleCountdown = Never;
            else if (_crackleCountdown == Never) _crackleCountdown = Schedule(_parameters.CrackleRate);
        }

        var eventRate = Flavour == VoiceDistortionFlavour.Digital ? _parameters.BreakupRate : _parameters.DropoutRate;
        if (eventRate <= 0) _eventCountdown = Never;
        else if (_eventCountdown == Never && _eventPos < 0) _eventCountdown = Schedule(eventRate);
    }

    /// <summary>Linear fading gain for the current fading phases (1 without fading).</summary>
    private double FadeGainTarget()
    {
        var depth = _parameters.FadingDepthDb;
        if (!(depth > 0)) return 1;

        if (depth != _fadeCorrectionDepth)
        {
            // mean power of 10^(depth * (0.7 sin p1 + 0.3 sin p2) / 10) = I0(0.7 k) I0(0.3 k) - remove it, so the
            // fading does not change the average level
            var k = depth * Math.Log(10) / 10;
            _fadeCorrectionDb = 10 * Math.Log10(BesselI0(0.7 * k) * BesselI0(0.3 * k));
            _fadeCorrectionDepth = depth;
        }

        var db = depth * (0.7 * Math.Sin(_fadePhase1) + 0.3 * Math.Sin(_fadePhase2)) - _fadeCorrectionDb;
        return Math.Pow(10, db / 20);
    }

    private float Process(float input)
    {
        if (--_controlCountdown <= 0)
        {
            _controlCountdown = ControlInterval;
            ControlTick();
        }

        double x = input;

        if (Flavour != VoiceDistortionFlavour.Digital)
        {
            // the channel: fading with a little noise coming up in the fades, and crackle
            _inputPower += (x * x - _inputPower) * SlowEnvelopeCoefficient;
            _fadeGain += _fadeGainStep;
            x *= _fadeGain;
            if (_fadeNoiseLevel > 0) x += (NextUniform() * 2 - 1) * 1.7320508075688772 * _fadeNoiseLevel;
            x += Crackle();
        }

        _refPower += (x * x - _refPower) * EnvelopeCoefficient;

        // band narrowing around an asymmetric soft clipper
        var driven = Math.Tanh(_drive * _highPass.HighPass(x) + _bias) - _biasTanh;
        _dcOut = driven - _dcIn + DcBlockPole * _dcOut;
        _dcIn = driven;
        var band = _lowPass.LowPass(_dcOut);
        _outPower += (band * band - _outPower) * EnvelopeCoefficient;

        // make-up gain, never above the peak limit
        var abs = Math.Abs(band);
        _peak = abs > _peak ? abs : _peak * PeakRelease;
        _makeUp += _makeUpStep;
        var gain = _makeUp;
        if (_peak * gain > MaxOutputPeak) gain = MaxOutputPeak / _peak;
        var y = band * gain;

        // lo-fi: sample and hold, then fewer bits
        _holdPhase += _holdStep;
        if (_holdPhase >= 1)
        {
            _holdPhase -= 1;
            _held = y;
        }

        y = _held;
        if (_levels > 0) y = Math.Round(y * _levels) / _levels;

        return (float)Events(y);
    }

    /// <summary>Crackle: sparse bursts of 1-4 band-limited ticks (only above 50 %, never for digital).</summary>
    private double Crackle()
    {
        var impulse = 0.0;
        if (_crackleBurstLeft > 0)
        {
            if (--_crackleBurstGap <= 0)
            {
                impulse = (NextUniform() < 0.5 ? -1 : 1) * _crackleAmplitude * (0.35 + 0.65 * NextUniform()) *
                          _crackleNorm;
                _crackleBurstLeft--;
                _crackleBurstGap = 12 + (int)(NextUniform() * 180); // 0.25 - 4 ms apart
            }
        }
        else if (_crackleCountdown != Never && --_crackleCountdown <= 0)
        {
            CrackleCount++;
            _crackleBurstLeft = 1 + (int)(NextUniform() * 4);
            _crackleBurstGap = 1;
            var frequency = 1200 + NextUniform() * 2400;
            _crackleFilter.Set(frequency, CrackleQ);
            _crackleNorm = 1 / Svf.BandPassImpulsePeak(frequency, CrackleQ);
            _crackleCountdown = _parameters.CrackleRate > 0 ? Schedule(_parameters.CrackleRate) : Never;
        }

        return _crackleFilter.BandPass(impulse);
    }

    /// <summary>Dropouts (analogue) or digital breakup (mutes / robotic repeats), only above 50 %.</summary>
    private double Events(double y)
    {
        var digital = Flavour == VoiceDistortionFlavour.Digital;
        if (digital)
        {
            _history[_historyPos] = (float)y;
            _historyPos = (_historyPos + 1) % HistorySize;
        }

        if (_eventPos < 0)
        {
            if (_eventCountdown == Never || --_eventCountdown > 0) return y;

            StartEvent(digital);
        }

        // 0 -> 1 in 2 ms, hold, 1 -> 0 in 2 ms (raised cosine)
        double weight;
        if (_eventPos < RampSamples) weight = 0.5 - 0.5 * Math.Cos(Math.PI * (_eventPos + 0.5) / RampSamples);
        else if (_eventPos < RampSamples + _eventHold) weight = 1;
        else weight = 0.5 + 0.5 * Math.Cos(Math.PI * (_eventPos - RampSamples - _eventHold + 0.5) / RampSamples);

        y = _eventRepeat ? y * (1 - weight) + RepeatSample() * weight : y * (1 - weight);

        if (++_eventPos >= _eventLength)
        {
            _eventPos = -1;
            var rate = digital ? _parameters.BreakupRate : _parameters.DropoutRate;
            _eventCountdown = rate > 0 ? Schedule(rate) : Never;
        }

        return y;
    }

    private void StartEvent(bool digital)
    {
        _eventPos = 0;
        _eventRepeat = false;

        if (digital)
        {
            BreakupCount++;
            _eventHold = Milliseconds(20 + 40 * NextUniform());

            if (NextUniform() < 0.6)
            {
                // robotic repeat of the last 10-20 ms (with 1 ms of pre-roll for a click-free loop seam)
                _eventRepeat = true;
                _frameLength = Milliseconds(10 + 10 * NextUniform());
                var start = _historyPos - _frameLength - LoopFadeSamples;
                for (var i = 0; i < _frameLength + LoopFadeSamples; i++)
                    _frame[i] = _history[((start + i) % HistorySize + HistorySize) % HistorySize];

                _framePos = 0;
                _frameGain = 1;
                _frameDecay = Math.Pow(0.8, 1.0 / _frameLength); // -2 dB per repeat
            }
        }
        else
        {
            DropoutCount++;
            _eventHold = Milliseconds(10 + (10 + 20 * _parameters.Damage) * NextUniform());
        }

        _eventLength = 2 * RampSamples + _eventHold;
    }

    private double RepeatSample()
    {
        // _frame[0 .. LoopFadeSamples) is the pre-roll, the frame itself follows
        double sample = _frame[LoopFadeSamples + _framePos];
        var seam = _frameLength - LoopFadeSamples;
        if (_framePos >= seam)
        {
            var w = (_framePos - seam + 0.5) / LoopFadeSamples;
            sample = sample * (1 - w) + _frame[_framePos - seam] * w;
        }

        if (++_framePos >= _frameLength) _framePos = 0;

        _frameGain *= _frameDecay;
        return sample * _frameGain;
    }

    private int Schedule(double ratePerSecond)
    {
        // exponential waiting time (Poisson events)
        var seconds = -Math.Log(1 - NextUniform()) / ratePerSecond;
        return (int)Math.Clamp(seconds * SampleRate, 1, 3600.0 * SampleRate);
    }

    private static int Milliseconds(double milliseconds)
    {
        return (int)(milliseconds * SampleRate / 1000);
    }

    /// <summary>xorshift64*: uniform in [0, 1).</summary>
    private double NextUniform()
    {
        _rng ^= _rng >> 12;
        _rng ^= _rng << 25;
        _rng ^= _rng >> 27;
        return (unchecked(_rng * 2685821657736338717UL) >> 11) * (1.0 / (1UL << 53));
    }

    private static double BesselI0(double x)
    {
        var sum = 1.0;
        var term = 1.0;
        var q = x * x / 4;
        for (var m = 1; m < 30; m++)
        {
            term *= q / (m * m);
            sum += term;
            if (term < 1e-12 * sum) break;
        }

        return sum;
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

        public void Set(double frequency, double q)
        {
            var g = Math.Tan(Math.PI * Math.Clamp(frequency, 10, SampleRate * 0.45) / SampleRate);
            _k = 1 / q;
            _a1 = 1 / (1 + g * (g + _k));
            _a2 = g * _a1;
            _a3 = g * _a2;
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

        public double BandPass(double x)
        {
            Tick(x, out var band, out _);
            return _k * band;
        }

        /// <summary>Peak of the (unity gain) band-pass response to a unit impulse (no allocation - audio thread).</summary>
        public static double BandPassImpulsePeak(double frequency, double q)
        {
            var g = Math.Tan(Math.PI * Math.Clamp(frequency, 10, SampleRate * 0.45) / SampleRate);
            var k = 1 / q;
            var a1 = 1 / (1 + g * (g + k));
            var a2 = g * a1;
            var a3 = g * a2;
            double ic1 = 0, ic2 = 0, peak = 0;
            for (var i = 0; i < 64; i++)
            {
                var v3 = (i == 0 ? 1.0 : 0.0) - ic2;
                var band = a1 * ic1 + a2 * v3;
                var low = ic2 + a2 * ic1 + a3 * v3;
                ic1 = 2 * band - ic1;
                ic2 = 2 * low - ic2;
                peak = Math.Max(peak, Math.Abs(k * band));
            }

            return Math.Max(peak, 1e-6);
        }
    }
}
