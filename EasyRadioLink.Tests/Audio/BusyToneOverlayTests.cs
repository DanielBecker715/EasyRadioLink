using System.Linq;
using EasyRadioLink.Common.Audio.Providers;
using EasyRadioLink.Common.Audio.Utility;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EasyRadioLink.Common.Tests.Audio;

/// <summary>
///     The busy tone is mixed over the radio's output (<see cref="RadioMixingProvider.MixOverlay" />): it plays on
///     silence as well as on top of a received voice, which goes on without being delayed.
/// </summary>
[TestClass]
public class BusyToneOverlayTests
{
    private static CircularFloatBuffer Overlay(int samples, float value)
    {
        var overlay = new CircularFloatBuffer(48000);
        overlay.Write(Enumerable.Repeat(value, samples).ToArray(), 0, samples);
        return overlay;
    }

    [TestMethod]
    public void TheToneFillsSilence()
    {
        var overlay = Overlay(100, 0.25f);
        var buffer = new float[480];

        var valid = RadioMixingProvider.MixOverlay(overlay, buffer, 0, buffer.Length);

        Assert.AreEqual(100, valid, "the radio outputs the tone although nothing is received");
        Assert.IsTrue(buffer.Take(100).All(s => s == 0.25f));
        Assert.IsTrue(buffer.Skip(100).All(s => s == 0f));
        Assert.AreEqual(0, overlay.Count);
    }

    [TestMethod]
    public void TheToneIsMixedOverTheReceivedVoice()
    {
        var overlay = Overlay(1000, 0.25f);
        var buffer = Enumerable.Repeat(0.5f, 480).ToArray();

        var valid = RadioMixingProvider.MixOverlay(overlay, buffer, 480, buffer.Length);

        Assert.AreEqual(480, valid, "the voice is not held back");
        Assert.IsTrue(buffer.All(s => s == 0.75f));
        Assert.AreEqual(520, overlay.Count, "the rest of the tone follows with the next read");

        // loud voice: clipped, never above full scale
        var loud = Enumerable.Repeat(0.9f, 480).ToArray();
        RadioMixingProvider.MixOverlay(overlay, loud, 480, loud.Length);
        Assert.IsTrue(loud.All(s => s <= 1f));
        Assert.AreEqual(40, overlay.Count);
    }

    [TestMethod]
    public void WithoutAToneNothingChanges()
    {
        var overlay = new CircularFloatBuffer(48000);
        var buffer = Enumerable.Repeat(0.5f, 480).ToArray();

        Assert.AreEqual(200, RadioMixingProvider.MixOverlay(overlay, buffer, 200, buffer.Length));
        Assert.IsTrue(buffer.All(s => s == 0.5f));
    }
}
