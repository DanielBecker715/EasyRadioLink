using EasyRadioLink.Common.Audio.Models;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Network.Singletons;
using EasyRadioLink.Common.Settings;
using EasyRadioLink.Common.Settings.Setting;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NLog;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;


namespace EasyRadioLink.Common.Audio.Providers
{
    /// <summary>
    ///     The "sound of the receiving radio": mixes all transmissions one radio receives (FM capture when radio
    ///     interference is enabled) and applies the receive filter. One instance per radio.
    /// </summary>
    public class ClientEffectsPipeline
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private float radioEffectRatio = 1.0f; // Default to 1.0 (full effect)
        private bool perRadioModelEffect;
        private bool clippingEnabled;

        private long lastRefresh = 0; //last refresh of settings

        private bool irlRadioRXInterference = false;

        // FM capture: the sender that currently holds this radio's FM channel (first to key up keeps it).
        private string _capturedFmGuid;

        public ClientEffectsPipeline()
        {
            RefreshSettings();
        }

        private IDictionary<string, RxRadioModel> RxRadioModels { get; } = new Dictionary<string, RxRadioModel>();
        private void RefreshSettings()
        {
            //only get settings every 3 seconds - and cache them - issues with performance
            long now = DateTime.Now.Ticks;

            if (TimeSpan.FromTicks(now - lastRefresh).TotalSeconds > 3) //3 seconds since last refresh
            {
                var profileSettings = GlobalSettingsStore.Instance.ProfileSettingsStore;
                var serverSettings = SyncedServerSettings.Instance;
                lastRefresh = now;

                perRadioModelEffect = profileSettings.GetClientSettingBool(ProfileSettingsKeys.PerRadioModelEffects);
                irlRadioRXInterference = serverSettings.GetSettingAsBool(ServerSettingsKeys.IRL_RADIO_RX_INTERFERENCE);
                clippingEnabled = profileSettings.GetClientSettingBool(ProfileSettingsKeys.RadioEffectsClipping);
                radioEffectRatio = Math.Clamp(profileSettings.GetClientSettingFloat(ProfileSettingsKeys.RadioEffectsRatio), 0f, 1f);
            }
        }

        private ISampleProvider BuildRXPipeline(ISampleProvider voiceProvider, RxRadioModel radioModel)
        {
            radioModel.RxSource.Source = voiceProvider;
            voiceProvider = radioModel.RxEffectProvider;
            return voiceProvider;
        }

        /// <param name="modelName">Radio model of the receiving radio (null = default receive filter).</param>
        public int ProcessSegments(Span<float> audioOut, IReadOnlyList<TransmissionSegment> segments, string modelName = null)
        {
            RefreshSettings();
            using var drySourceBuffer = new PooledArray<float>(audioOut.Length);
            var drySpan = drySourceBuffer.Array.AsSpan(0, drySourceBuffer.Length);
            drySpan.Clear();

            List<TransmissionSegment> fmSegments = null;
            foreach (var segment in segments)
            {
                if (irlRadioRXInterference && RadioEffectRules.TakesPartInFmCapture(segment.Modulation, segment.NoAudioEffects))
                {
                    // FM Capture effect: only one FM transmission can be heard at a time.
                    fmSegments ??= new List<TransmissionSegment>(segments.Count);
                    fmSegments.Add(segment);
                }
                else
                {
                    // Everything, just mix.
                    // Accumulate in destination buffer.
                    Mix(drySpan, segment.Audio.AsSpan());
                }
            }

            var capturedFMSegment = SelectCapturedFmSegment(fmSegments, _capturedFmGuid);
            _capturedFmGuid = capturedFMSegment?.OriginalClientGuid;

            if (capturedFMSegment != null)
            {
                Mix(drySpan, capturedFMSegment.Audio.AsSpan());
            }

            //Get desire RadioModel
            var desiredName = RadioModelFactory.Instance.ResolveModelKey(perRadioModelEffect ? modelName : null);
            if (!RxRadioModels.TryGetValue(desiredName, out var radioModel))
            {
                radioModel = RadioModelFactory.Instance.LoadRxOrDefault(desiredName);
                RxRadioModels[desiredName] = radioModel;
            }

            // Create dry and wet providers
            // Wet/effected provider: must use a separate buffer to avoid double-reading

            var dryProvider = new TransmissionProvider(drySourceBuffer.Array, 0, drySourceBuffer.Length);

            using var wetSourceBuffer = new PooledArray<float>(audioOut.Length);
            var wetSpan = wetSourceBuffer.Array.AsSpan(0, wetSourceBuffer.Length);
            drySpan.CopyTo(wetSpan);
            var wetProvider = BuildRXPipeline(new TransmissionProvider(wetSourceBuffer.Array, 0, wetSourceBuffer.Length), radioModel);

            // Set up volume providers for wet/dry mix
            var dryVolume = new VolumeSampleProvider(dryProvider) { Volume = 1.0f - radioEffectRatio };
            var wetVolume = new VolumeSampleProvider(wetProvider) { Volume = radioEffectRatio };

            // Mix dry and wet
            var mixer = new MixingSampleProvider(new[] { dryVolume, wetVolume });

            // Wrap with ClippingProvider if needed
            ISampleProvider finalProvider = mixer;
            if (clippingEnabled && radioEffectRatio > 0f)
                finalProvider = new ClippingProvider(mixer, -1f, 1f);

            using var mixerBuffer = new PooledArray<float>(audioOut.Length);
            int samplesRead = 0;
            try
            {
                samplesRead = finalProvider.Read(mixerBuffer.Array, 0, mixerBuffer.Length);
                mixerBuffer.Array.AsSpan(0, samplesRead).CopyTo(audioOut);
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failed to process segments: ");
                // Something borked - reset for the next packet(s).
                RxRadioModels.Clear();
            }

            return samplesRead;
        }

        /// <summary>
        ///     FM capture without positions: the station that holds the channel (<paramref name="capturedGuid" />) keeps
        ///     it while it is transmitting; otherwise the first FM transmission captures it. Null if there is none.
        /// </summary>
        internal static TransmissionSegment SelectCapturedFmSegment(IReadOnlyList<TransmissionSegment> fmSegments,
            string capturedGuid)
        {
            if (fmSegments == null || fmSegments.Count == 0) return null;

            if (capturedGuid != null)
                foreach (var segment in fmSegments)
                    if (segment.OriginalClientGuid == capturedGuid)
                        return segment;

            return fmSegments[0];
        }

        internal void Mix(Span<float> target, ReadOnlySpan<float> source)
        {
            var vectorSize = Vector<float>.Count;
            var remainder = source.Length % vectorSize;


            for (var i = 0; i < source.Length - remainder; i += vectorSize)
            {
                var v_source = Vector.LoadUnsafe(ref MemoryMarshal.GetReference(source), (nuint)i);
                var v_current = Vector.LoadUnsafe(ref MemoryMarshal.GetReference(target), (nuint)i);

                (v_current + v_source).CopyTo(target.Slice(i, vectorSize));
            }

            for (var i = source.Length - remainder; i < source.Length; ++i)
            {
                target[i] += source[i];
            }
        }
    }
}
