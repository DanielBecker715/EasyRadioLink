using System.Text.Json.Serialization;

namespace EasyRadioLink.Common.Audio.Dsp
{
    internal class BiQuadFilter : IFilter
    {
        public NAudio.Dsp.BiQuadFilter Filter { get; set; }
        public float Transform(float input)
        {
            return Filter.Transform(input);
        }
    }
}
