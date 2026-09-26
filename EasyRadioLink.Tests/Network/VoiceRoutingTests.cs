using System.Collections.Generic;
using System.Net;
using EasyRadioLink.Common.Models;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Network.Server;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EasyRadioLink.Common.Tests.Network;

[TestClass]
public class VoiceRoutingTests
{
    private const double Cb19 = 27185000;
    private const double Pmr1 = 446006250;
    private const double AirbandGuard = 121500000;

    private static readonly IReadOnlyList<double> TestFrequencies = new[] { 27405000d, 446193750d };

    private static ClientInfo Client(string guid, int port, params RadioBase[] radios)
    {
        var info = new PlayerRadioInfoBase();
        for (var i = 0; i < radios.Length; i++) info.radios[i + 1] = radios[i];

        return new ClientInfo
        {
            ClientGuid = guid,
            Name = guid,
            RadioInfo = info,
            VoipPort = new IPEndPoint(IPAddress.Loopback, port)
        };
    }

    private static RadioBase Radio(double freq, Modulation modulation, double secFreq = 1, bool enc = false,
        byte encKey = 0)
    {
        return new RadioBase { freq = freq, modulation = modulation, secFreq = secFreq, enc = enc, encKey = encKey };
    }

    private static UDPVoicePacket Packet(double freq, Modulation modulation, byte encryption = 0)
    {
        return new UDPVoicePacket
        {
            Frequencies = new[] { freq },
            Modulations = new[] { (byte)modulation },
            Encryptions = new[] { encryption }
        };
    }

    private static HashSet<IPEndPoint> Route(ClientInfo sender, UDPVoicePacket packet, bool strict,
        params ClientInfo[] others)
    {
        var all = new List<ClientInfo> { sender };
        all.AddRange(others);
        return VoiceRouting.SelectRecipients(all, sender, packet, strict, TestFrequencies);
    }

    [TestMethod]
    public void SameFrequencyAndModulationIsDelivered()
    {
        var sender = Client("sender_______________1", 1000, Radio(Cb19, Modulation.AM));
        var receiver = Client("receiver_____________1", 1001, Radio(Cb19, Modulation.AM));

        var recipients = Route(sender, Packet(Cb19, Modulation.AM), false, receiver);

        Assert.HasCount(1, recipients);
        Assert.Contains(receiver.VoipPort, recipients);
    }

    [TestMethod]
    public void FrequencyWithinToleranceIsDelivered()
    {
        var sender = Client("sender_______________1", 1000);
        var close = Client("receiver_____________1", 1001, Radio(Cb19 + 400, Modulation.AM));
        var far = Client("receiver_____________2", 1002, Radio(Cb19 + 10000, Modulation.AM));

        var recipients = Route(sender, Packet(Cb19, Modulation.AM), false, close, far);

        Assert.Contains(close.VoipPort, recipients);
        Assert.DoesNotContain(far.VoipPort, recipients);
    }

    [TestMethod]
    public void OtherModulationIsNotDelivered()
    {
        var sender = Client("sender_______________1", 1000);
        var fmReceiver = Client("receiver_____________1", 1001, Radio(Pmr1, Modulation.FM));

        Assert.IsEmpty(Route(sender, Packet(Pmr1, Modulation.AM), false, fmReceiver));
        Assert.HasCount(1, Route(sender, Packet(Pmr1, Modulation.FM), false, fmReceiver));
    }

    [TestMethod]
    public void DisabledRadioOrDisabledTransmissionIsNotDelivered()
    {
        var sender = Client("sender_______________1", 1000);
        var disabled = Client("receiver_____________1", 1001, Radio(Cb19, Modulation.DISABLED));
        var enabled = Client("receiver_____________2", 1002, Radio(Cb19, Modulation.AM));

        Assert.IsEmpty(Route(sender, Packet(Cb19, Modulation.AM), false, disabled));
        Assert.IsEmpty(Route(sender, Packet(Cb19, Modulation.DISABLED), false, enabled));
    }

    [TestMethod]
    public void GuardFrequencyIsDeliveredAsSecondary()
    {
        var sender = Client("sender_______________1", 1000);
        var receiver = Client("receiver_____________1", 1001,
            Radio(124800000, Modulation.AM, AirbandGuard));

        var recipients = Route(sender, Packet(AirbandGuard, Modulation.AM), false, receiver);
        Assert.HasCount(1, recipients);

        var radio = receiver.RadioInfo.CanHearTransmission(AirbandGuard, Modulation.AM, 0, false, new List<int>(),
            out var state, out var decryptable);
        Assert.IsNotNull(radio);
        Assert.IsNotNull(state);
        Assert.IsTrue(state.IsSecondary);
        Assert.AreEqual(1, state.ReceivedOn);
        Assert.IsTrue(decryptable);
    }

    [TestMethod]
    public void GuardFrequencyRequiresSameModulation()
    {
        var sender = Client("sender_______________1", 1000);
        var receiver = Client("receiver_____________1", 1001,
            Radio(124800000, Modulation.AM, AirbandGuard));

        Assert.IsEmpty(Route(sender, Packet(AirbandGuard, Modulation.FM), false, receiver));
    }

    [TestMethod]
    public void GuardDisabledWhenSecFreqNotSet()
    {
        var sender = Client("sender_______________1", 1000);
        var receiver = Client("receiver_____________1", 1001, Radio(124800000, Modulation.AM, 0));

        Assert.IsEmpty(Route(sender, Packet(AirbandGuard, Modulation.AM), false, receiver));
    }

    [TestMethod]
    public void EncryptedTransmissionIsDeliveredButOnlyDecryptableWithMatchingKey()
    {
        var sender = Client("sender_______________1", 1000);
        var clear = Client("receiver_____________1", 1001, Radio(Cb19, Modulation.AM));
        var sameKey = Client("receiver_____________2", 1002, Radio(Cb19, Modulation.AM, enc: true, encKey: 5));
        var otherKey = Client("receiver_____________3", 1003, Radio(Cb19, Modulation.AM, enc: true, encKey: 6));

        foreach (var strict in new[] { false, true })
        {
            // everybody tuned in gets the packet - non-matching keys play scrambled audio
            var recipients = Route(sender, Packet(Cb19, Modulation.AM, 5), strict, clear, sameKey, otherKey);
            Assert.HasCount(3, recipients);

            Assert.IsNotNull(clear.RadioInfo.CanHearTransmission(Cb19, Modulation.AM, 5, strict, null, out _,
                out var clearDecryptable));
            Assert.IsFalse(clearDecryptable);

            Assert.IsNotNull(sameKey.RadioInfo.CanHearTransmission(Cb19, Modulation.AM, 5, strict, null, out _,
                out var sameKeyDecryptable));
            Assert.IsTrue(sameKeyDecryptable);

            Assert.IsNotNull(otherKey.RadioInfo.CanHearTransmission(Cb19, Modulation.AM, 5, strict, null, out _,
                out var otherKeyDecryptable));
            Assert.IsFalse(otherKeyDecryptable);
        }
    }

    [TestMethod]
    public void ClearTransmissionOnEncryptingRadioDependsOnStrictEncryption()
    {
        var receiver = Client("receiver_____________1", 1001, Radio(Cb19, Modulation.AM, enc: true, encKey: 5));

        // non-strict: an encrypting radio still understands clear transmissions
        Assert.IsNotNull(receiver.RadioInfo.CanHearTransmission(Cb19, Modulation.AM, 0, false, null, out _,
            out var nonStrictDecryptable));
        Assert.IsTrue(nonStrictDecryptable);

        // strict: only the same key is understood
        Assert.IsNotNull(receiver.RadioInfo.CanHearTransmission(Cb19, Modulation.AM, 0, true, null, out _,
            out var strictDecryptable));
        Assert.IsFalse(strictDecryptable);

        // delivered in both modes
        var sender = Client("sender_______________1", 1000);
        Assert.HasCount(1, Route(sender, Packet(Cb19, Modulation.AM), false, receiver));
        Assert.HasCount(1, Route(sender, Packet(Cb19, Modulation.AM), true, receiver));
    }

    [TestMethod]
    public void DecryptableRadioIsPreferred()
    {
        var info = new PlayerRadioInfoBase();
        info.radios[1] = Radio(Cb19, Modulation.AM, enc: true, encKey: 9);
        info.radios[2] = Radio(Cb19, Modulation.AM, enc: true, encKey: 5);

        var radio = info.CanHearTransmission(Cb19, Modulation.AM, 5, true, new List<int>(), out var state,
            out var decryptable);

        Assert.AreSame(info.radios[2], radio);
        Assert.AreEqual(2, state.ReceivedOn);
        Assert.IsTrue(decryptable);
    }

    [TestMethod]
    public void SenderIsEchoedOnlyOnTestFrequencies()
    {
        var sender = Client("sender_______________1", 1000, Radio(27405000, Modulation.AM));

        Assert.IsEmpty(Route(sender, Packet(Cb19, Modulation.AM), false));

        var echo = Route(sender, Packet(27405000, Modulation.AM), false);
        Assert.HasCount(1, echo);
        Assert.Contains(sender.VoipPort, echo);
    }

    [TestMethod]
    public void MutedSenderIsDropped()
    {
        var sender = Client("sender_______________1", 1000);
        sender.Muted = true;
        var receiver = Client("receiver_____________1", 1001, Radio(Cb19, Modulation.AM));

        Assert.IsEmpty(Route(sender, Packet(Cb19, Modulation.AM), false, receiver));
    }

    [TestMethod]
    public void ClientsWithoutVoiceEndpointOrRadiosAreSkipped()
    {
        var sender = Client("sender_______________1", 1000);
        var noEndpoint = Client("receiver_____________1", 1001, Radio(Cb19, Modulation.AM));
        noEndpoint.VoipPort = null;
        var noRadios = Client("receiver_____________2", 1002);
        noRadios.RadioInfo = null;
        var brokenRadios = Client("receiver_____________3", 1003);
        brokenRadios.RadioInfo.radios = null;

        Assert.IsEmpty(Route(sender, Packet(Cb19, Modulation.AM), false, noEndpoint, noRadios, brokenRadios));
    }

    [TestMethod]
    public void AnyOfSeveralFrequenciesIsEnough()
    {
        var sender = Client("sender_______________1", 1000);
        var receiver = Client("receiver_____________1", 1001, Radio(Pmr1, Modulation.FM));

        var packet = new UDPVoicePacket
        {
            Frequencies = new[] { Cb19, Pmr1 },
            Modulations = new[] { (byte)Modulation.AM, (byte)Modulation.FM },
            Encryptions = new byte[] { 0, 0 }
        };

        Assert.HasCount(1, Route(sender, packet, false, receiver));
    }

    [TestMethod]
    public void ReservedSlotZeroIsForcedDisabled()
    {
        var info = new PlayerRadioInfoBase();
        info.radios[0] = Radio(Cb19, Modulation.AM);
        info.radios = new[] { info.radios[0], Radio(Pmr1, Modulation.FM) };

        info.EnsureValid();

        Assert.HasCount(Constants.MAX_RADIOS, info.radios);
        Assert.AreEqual(Modulation.DISABLED, info.radios[0].modulation);
        Assert.AreEqual(Modulation.FM, info.radios[1].modulation);
        foreach (var radio in info.radios) Assert.IsNotNull(radio);

        Assert.IsNull(info.CanHearTransmission(Cb19, Modulation.AM, 0, false, null, out _, out _));
    }

    [TestMethod]
    public void UdpIsOnlyAcceptedFromTheAuthenticatedAddress()
    {
        var client = new ClientInfo
        {
            ClientGuid = "client_______________1",
            SessionAddress = IPAddress.Parse("192.168.1.20")
        };

        // same IP, any port (the UDP port differs from the TCP one)
        Assert.IsTrue(VoiceRouting.IsFromClientAddress(client, new IPEndPoint(IPAddress.Parse("192.168.1.20"), 50123)));
        Assert.IsTrue(VoiceRouting.IsFromClientAddress(client,
            new IPEndPoint(IPAddress.Parse("192.168.1.20").MapToIPv6(), 50123)));

        // somebody else who knows the client id
        Assert.IsFalse(VoiceRouting.IsFromClientAddress(client, new IPEndPoint(IPAddress.Parse("192.168.1.21"), 50123)));
        Assert.IsFalse(VoiceRouting.IsFromClientAddress(client, null));

        // not authenticated on TCP
        Assert.IsFalse(VoiceRouting.IsFromClientAddress(new ClientInfo { ClientGuid = client.ClientGuid },
            new IPEndPoint(IPAddress.Parse("192.168.1.20"), 50123)));
        Assert.IsFalse(VoiceRouting.IsFromClientAddress(null, new IPEndPoint(IPAddress.Loopback, 1)));
    }
}
