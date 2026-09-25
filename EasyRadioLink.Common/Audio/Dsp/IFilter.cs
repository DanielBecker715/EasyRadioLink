using System.Text.Json.Serialization;

namespace EasyRadioLink.Common.Audio.Dsp
{
    internal interface IFilter
    {
        float Transform(float input);
    }
}
