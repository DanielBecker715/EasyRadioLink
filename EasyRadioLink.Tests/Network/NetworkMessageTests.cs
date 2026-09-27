using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Models;
using EasyRadioLink.Common.Models.Player;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EasyRadioLink.Common.Tests.Network;

[TestClass]
public class NetworkMessageTests
{
    private const string Guid = "ufYS_WlLVkmFPjqCgxz6GA";

    [TestMethod]
    public void MessageTypesHaveStableWireNumbers()
    {
        var expected = new Dictionary<string, int>
        {
            { "UPDATE", 0 },
            { "PING", 1 },
            { "SYNC", 2 },
            { "RADIO_UPDATE", 3 },
            { "SERVER_SETTINGS", 4 },
            { "CLIENT_DISCONNECT", 5 },
            { "VERSION_MISMATCH", 6 },
            { "AUTH_FAILED", 7 },
            { "VOICE_KEY", 8 }
        };

        var actual = new Dictionary<string, int>();
        foreach (var value in Enum.GetValues<NetworkMessage.MessageType>()) actual[value.ToString()] = (int)value;

        CollectionAssert.AreEquivalent(expected, actual);
    }

    [TestMethod]
    public void ModulationsKeepTheirWireNumbers()
    {
        Assert.AreEqual(0, (int)Enum.Parse<Modulation>("AM"));
        Assert.AreEqual(1, (int)Enum.Parse<Modulation>("FM"));
        Assert.AreEqual(3, (int)Enum.Parse<Modulation>("DISABLED"));
    }

    [TestMethod]
    public void EncodeWritesMessageTypeAsNumberAndProductVersion()
    {
        var json = new NetworkMessage { MsgType = NetworkMessage.MessageType.AUTH_FAILED }.Encode();

        Assert.IsTrue(json.EndsWith("\n"), "a message is exactly one line terminated by \\n");
        Assert.AreEqual(1, json.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.AreEqual(7, root.GetProperty("MsgType").GetInt32());
        Assert.AreEqual(AppVersion.Product, root.GetProperty("Product").GetString());
        Assert.AreEqual(AppVersion.ProtocolVersion, root.GetProperty("Version").GetString());
        Assert.IsFalse(root.TryGetProperty("Password", out _), "null password must not be written");
        Assert.IsFalse(root.TryGetProperty("Client", out _));
    }

    [TestMethod]
    public void SyncHelloRoundTripsWithPasswordAndClient()
    {
        var info = new PlayerRadioInfoBase();
        info.radios[1] = new RadioBase
            { freq = 27185000, modulation = Modulation.AM, secFreq = 0, enc = true, encKey = 42, Model = "C.B" };
        info.ambient = new Ambient { abType = "jet", vol = 0.5f };

        var message = new NetworkMessage
        {
            MsgType = NetworkMessage.MessageType.SYNC,
            Password = "sëcret pass",
            Client = new ClientInfo
            {
                ClientGuid = Guid,
                Name = "Daniel",
                AllowRecord = true,
                RadioInfo = info,
                // local state that must never go on the wire
                Muted = true,
                VoipPort = new IPEndPoint(IPAddress.Loopback, 1234),
                TransmittingFrequency = "27.185 AM"
            }
        };

        var json = message.Encode();
        var decoded = NetworkMessage.Decode(json);

        Assert.AreEqual(NetworkMessage.MessageType.SYNC, decoded.MsgType);
        Assert.AreEqual("sëcret pass", decoded.Password);
        Assert.AreEqual(AppVersion.Product, decoded.Product);
        Assert.AreEqual(AppVersion.ProtocolVersion, decoded.Version);

        Assert.AreEqual(Guid, decoded.Client.ClientGuid);
        Assert.AreEqual("Daniel", decoded.Client.Name);
        Assert.IsTrue(decoded.Client.AllowRecord);
        Assert.IsFalse(decoded.Client.Muted);
        Assert.IsNull(decoded.Client.VoipPort);
        Assert.IsNull(decoded.Client.TransmittingFrequency);

        Assert.HasCount(Constants.MAX_RADIOS, decoded.Client.RadioInfo.radios);
        Assert.AreEqual(info, decoded.Client.RadioInfo);
        var radio = decoded.Client.RadioInfo.radios[1];
        Assert.AreEqual(27185000d, radio.freq);
        Assert.AreEqual(Modulation.AM, radio.modulation);
        Assert.IsTrue(radio.enc);
        Assert.AreEqual((byte)42, radio.encKey);
        Assert.AreEqual("cb", radio.Model);
        Assert.AreEqual("jet", decoded.Client.RadioInfo.ambient.abType);
    }

    [TestMethod]
    public void VoiceBoostTravelsWithTheBackgroundSound()
    {
        var info = new PlayerRadioInfoBase { ambient = new Ambient { abType = "helicopter", vol = 0.5f, voiceBoost = 0.4f } };
        var json = new NetworkMessage
        {
            MsgType = NetworkMessage.MessageType.RADIO_UPDATE,
            Client = new ClientInfo { ClientGuid = Guid, Name = "A", RadioInfo = info }
        }.Encode();

        var decoded = NetworkMessage.Decode(json).Client.RadioInfo.ambient;
        Assert.AreEqual(0.4f, decoded.voiceBoost);
        Assert.AreEqual(info.ambient, decoded);

        // it is part of the state: a change is sent to the others
        Assert.AreNotEqual(info.ambient, new Ambient { abType = "helicopter", vol = 0.5f });
        Assert.AreEqual(0.4f, info.ambient.Copy().voiceBoost);
    }

    [TestMethod]
    public void RadioInfoWithoutVoiceBoostMeansNoBoost()
    {
        // a 1.1 / 1.2 client: no voiceBoost in its ambient
        var json = "{\"MsgType\":3,\"Client\":{\"ClientGuid\":\"" + Guid + "\",\"Name\":\"A\",\"RadioInfo\":" +
                   "{\"ambient\":{\"abType\":\"jet\",\"vol\":0.3},\"radios\":[]}},\"Version\":\"1.1.0\"}";

        var ambient = NetworkMessage.Decode(json).Client.RadioInfo.ambient;
        Assert.AreEqual("jet", ambient.abType);
        Assert.AreEqual(0f, ambient.voiceBoost);
    }

    [TestMethod]
    public void ClientWireFormatHasNoGameFields()
    {
        var json = new NetworkMessage
        {
            MsgType = NetworkMessage.MessageType.RADIO_UPDATE,
            Client = new ClientInfo { ClientGuid = Guid, Name = "A", RadioInfo = new PlayerRadioInfoBase() }
        }.Encode();

        using var document = JsonDocument.Parse(json);
        var client = document.RootElement.GetProperty("Client");

        var names = new List<string>();
        foreach (var property in client.EnumerateObject()) names.Add(property.Name);
        CollectionAssert.AreEquivalent(new[] { "ClientGuid", "Name", "AllowRecord", "RadioInfo" }, names);

        var radioInfo = client.GetProperty("RadioInfo");
        names.Clear();
        foreach (var property in radioInfo.EnumerateObject()) names.Add(property.Name);
        CollectionAssert.AreEquivalent(new[] { "ambient", "radios" }, names);

        names.Clear();
        foreach (var property in radioInfo.GetProperty("radios")[1].EnumerateObject()) names.Add(property.Name);
        CollectionAssert.AreEquivalent(new[] { "enc", "encKey", "freq", "modulation", "secFreq", "Model" }, names);
        Assert.AreEqual(3, radioInfo.GetProperty("radios")[1].GetProperty("modulation").GetInt32());
    }

    [TestMethod]
    public void ServerReplyRoundTripsClientsAndSettings()
    {
        var message = new NetworkMessage
        {
            MsgType = NetworkMessage.MessageType.SYNC,
            Clients = new List<ClientInfo>
            {
                new() { ClientGuid = Guid, Name = "A", RadioInfo = new PlayerRadioInfoBase() },
                new() { ClientGuid = "abcdefghijklmnopqrstuv", Name = "B" }
            },
            ServerSettings = new Dictionary<string, string> { { "TEST_FREQUENCIES", "27.405,446.19375" } }
        };

        var decoded = NetworkMessage.Decode(message.Encode());

        Assert.HasCount(2, decoded.Clients);
        Assert.AreEqual("B", decoded.Clients[1].Name);
        Assert.IsNull(decoded.Clients[1].RadioInfo);
        Assert.AreEqual("27.405,446.19375", decoded.ServerSettings["TEST_FREQUENCIES"]);
        Assert.IsNull(decoded.Password);
    }

    [TestMethod]
    public void SyncReplyCarriesTheUdpKeyOnlyWhenSet()
    {
        var key = Convert.ToBase64String(new byte[32]);
        var reply = new NetworkMessage
        {
            MsgType = NetworkMessage.MessageType.SYNC,
            Clients = new List<ClientInfo>(),
            UdpKey = key,
            UdpKeyId = 0xCAFE0001
        };

        var decoded = NetworkMessage.Decode(reply.Encode());
        Assert.AreEqual(key, decoded.UdpKey);
        Assert.AreEqual(0xCAFE0001u, decoded.UdpKeyId);

        // every other message leaves the fields out entirely
        using var document = JsonDocument.Parse(new NetworkMessage
            { MsgType = NetworkMessage.MessageType.RADIO_UPDATE }.Encode());
        Assert.IsFalse(document.RootElement.TryGetProperty("UdpKey", out _));
        Assert.IsFalse(document.RootElement.TryGetProperty("UdpKeyId", out _));
    }

    [TestMethod]
    public void ServerSideUdpKeyOfAClientIsNeverSerialised()
    {
        using var transport = new Common.Network.Crypto.UdpTransportSession(Guid,
            Common.Network.Crypto.UdpTransportSession.GenerateKey(), 1, true);
        var client = new ClientInfo { ClientGuid = Guid, Name = "A", UdpTransport = transport };

        // the client list of every SYNC reply / RADIO_UPDATE and the client export serialise ClientInfo
        var json = new NetworkMessage { MsgType = NetworkMessage.MessageType.SYNC, Clients = [client] }.Encode();

        StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex("Udp|Transport|Key",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }

    [TestMethod]
    public void ClientInfoCarriesTheEndToEndPublicKey()
    {
        using var keys = Common.Network.Crypto.E2EKeyPair.Create();
        var client = new ClientInfo { ClientGuid = Guid, Name = "A", E2EPublicKey = keys.PublicKey };

        var json = new NetworkMessage { MsgType = NetworkMessage.MessageType.RADIO_UPDATE, Client = client }.Encode();

        using var document = JsonDocument.Parse(json);
        Assert.AreEqual(keys.PublicKey, document.RootElement.GetProperty("Client").GetProperty("E2EPublicKey").GetString());
        Assert.AreEqual(keys.PublicKey, NetworkMessage.Decode(json).Client.E2EPublicKey);
        Assert.AreEqual(keys.PublicKey, client.DeepClone().E2EPublicKey);
    }

    [TestMethod]
    public void VoiceKeyMessageWireFormat()
    {
        var wrapped = Convert.ToBase64String(new byte[48]);
        var message = new NetworkMessage
        {
            MsgType = NetworkMessage.MessageType.VOICE_KEY,
            VoiceKey = new VoiceKeyMessage
            {
                SenderGuid = Guid,
                TxId = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }),
                Frequency = 27185000,
                Modulation = Modulation.AM,
                Keys = new Dictionary<string, string> { ["abcdefghijklmnopqrstuv"] = wrapped }
            }
        };

        var json = message.Encode();

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.AreEqual(8, root.GetProperty("MsgType").GetInt32());
        var voiceKey = root.GetProperty("VoiceKey");
        var names = new List<string>();
        foreach (var property in voiceKey.EnumerateObject()) names.Add(property.Name);
        CollectionAssert.AreEquivalent(new[] { "SenderGuid", "TxId", "Frequency", "Modulation", "Keys" }, names);
        Assert.AreEqual(0, voiceKey.GetProperty("Modulation").GetInt32());
        Assert.AreEqual("AQIDBAUGBwg=", voiceKey.GetProperty("TxId").GetString());
        Assert.AreEqual(wrapped, voiceKey.GetProperty("Keys").GetProperty("abcdefghijklmnopqrstuv").GetString());
        Assert.IsFalse(root.TryGetProperty("Client", out _));

        var decoded = NetworkMessage.Decode(json);
        Assert.AreEqual(NetworkMessage.MessageType.VOICE_KEY, decoded.MsgType);
        Assert.IsTrue(decoded.VoiceKey.IsValid(out var txId, out _));
        Assert.AreEqual(0x0102030405060708UL, txId);
        Assert.AreEqual(wrapped, decoded.VoiceKey.Keys["abcdefghijklmnopqrstuv"]);

        // other messages have no VoiceKey member
        using var other = JsonDocument.Parse(new NetworkMessage { MsgType = NetworkMessage.MessageType.PING }.Encode());
        Assert.IsFalse(other.RootElement.TryGetProperty("VoiceKey", out _));
    }

    [TestMethod]
    public void UnknownJsonMembersAreIgnored()
    {
        // e.g. a hello from the upstream application this product was forked from: unknown members, no Product
        var decoded = NetworkMessage.Decode(
            "{\"Client\":{\"ClientGuid\":\"" + Guid +
            "\",\"Name\":\"X\",\"Side\":2,\"Seat\":0,\"Position\":{\"lat\":1}},\"MsgType\":2,\"Version\":\"2.4.1.0\",\"LegacyPassword\":\"x\"}");

        Assert.AreEqual(NetworkMessage.MessageType.SYNC, decoded.MsgType);
        Assert.AreEqual("X", decoded.Client.Name);
        Assert.IsNull(decoded.Product);
        Assert.IsFalse(AppVersion.IsSupportedProduct(decoded.Product));
    }

    [TestMethod]
    public void VersionChecks()
    {
        Assert.IsTrue(AppVersion.IsSupportedProduct("EasyRadioLink"));
        Assert.IsFalse(AppVersion.IsSupportedProduct("easyradiolink"));
        Assert.IsFalse(AppVersion.IsSupportedProduct(null));

        Assert.IsTrue(AppVersion.IsSupportedProtocolVersion(AppVersion.ProtocolVersion));
        Assert.IsTrue(AppVersion.IsSupportedProtocolVersion("1.1.0.0"));
        Assert.IsTrue(AppVersion.IsSupportedProtocolVersion("2.0"));
        // 1.0 peers speak plain TCP/UDP - they can't talk to 1.1 (TLS + encrypted UDP)
        Assert.IsFalse(AppVersion.IsSupportedProtocolVersion("1.0.0"));
        Assert.IsFalse(AppVersion.IsSupportedProtocolVersion("1.0.9"));
        Assert.IsFalse(AppVersion.IsSupportedProtocolVersion("0.9.9"));
        Assert.IsFalse(AppVersion.IsSupportedProtocolVersion(null));
        Assert.IsFalse(AppVersion.IsSupportedProtocolVersion("not a version"));

        Assert.IsTrue(System.Version.Parse(AppVersion.MinimumProtocolVersion) <=
                      System.Version.Parse(AppVersion.ProtocolVersion));
        Assert.IsFalse(string.IsNullOrWhiteSpace(AppVersion.Version));
    }
}
