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
            { "AUTH_FAILED", 7 }
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
        Assert.IsTrue(AppVersion.IsSupportedProtocolVersion("1.0.0.0"));
        Assert.IsTrue(AppVersion.IsSupportedProtocolVersion("2.0"));
        Assert.IsFalse(AppVersion.IsSupportedProtocolVersion("0.9.9"));
        Assert.IsFalse(AppVersion.IsSupportedProtocolVersion(null));
        Assert.IsFalse(AppVersion.IsSupportedProtocolVersion("not a version"));

        Assert.IsTrue(System.Version.Parse(AppVersion.MinimumProtocolVersion) <=
                      System.Version.Parse(AppVersion.ProtocolVersion));
        Assert.IsFalse(string.IsNullOrWhiteSpace(AppVersion.Version));
    }
}
