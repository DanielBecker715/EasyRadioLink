using EasyRadioLink.Common.Models.Player;
using System;

namespace EasyRadioLink.Common.Audio.Models
{
    // https://stackoverflow.com/questions/538060/proper-use-of-the-idisposable-interface#538238
    public class TransmissionSegment
    {
        // Non-owning - belongs to a pool!
        public float[] Audio { get; }
        public bool HasEncryption { get; }
        public bool Decryptable { get; }
        public string OriginalClientGuid { get; }
        public Modulation Modulation { get; }
        public bool NoAudioEffects { get; }

        // Linear volume of the receiving radio (already applied to Audio).
        public float Volume { get; }

        public TransmissionSegment(DeJitteredTransmission transmission)
        {
            Audio = new float[transmission.PCMAudioLength];

            transmission.PCMMonoAudio.AsSpan(0, transmission.PCMAudioLength).CopyTo(Audio);
            
            HasEncryption = transmission.Encryption > 0;
            Decryptable = transmission.Decryptable;
            OriginalClientGuid = transmission.OriginalClientGuid;
            Modulation = transmission.Modulation;
            NoAudioEffects = transmission.NoAudioEffects;
            Volume = transmission.Volume;
        }
    }
}
