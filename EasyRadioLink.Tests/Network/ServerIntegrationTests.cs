using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Caliburn.Micro;
using EasyRadioLink.Common.Models;
using EasyRadioLink.Common.Models.EventMessages;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.NetCoreServer;
using EasyRadioLink.Common.Network.Client;
using EasyRadioLink.Common.Network.Crypto;
using EasyRadioLink.Common.Network.Server;
using EasyRadioLink.Common.Network.Singletons;
using EasyRadioLink.Common.Settings;
using EasyRadioLink.Common.Settings.Setting;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EasyRadioLink.Common.Tests.Network;

/// <summary>
///     The real server (ServerState: TLS sync server + UDP voice router) in-process on a free loopback port, with
///     protocol-level test clients built from the Common crypto classes (no audio devices): TLS with a pinned
///     identity, SYNC with the UDP key, encrypted UDP, VOICE_KEY and end-to-end encrypted voice.
///     Alice and Bob are on 27.185 MHz AM, Eve on 446.00625 MHz FM. The busy channel lockout tests use frequencies of
///     their own (27.215 / 27.225 MHz), so the hang time of one test never reaches into another.
/// </summary>
[TestClass]
public class ServerIntegrationTests
{
    private const string Password = "integration pässword";
    private const double Cb19 = 27185000;
    private const double RadioCheck = 27405000;
    private const double Pmr = 446006250;
    private const double Cb21 = 27215000;
    private const double Cb22 = 27225000;

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Silence = TimeSpan.FromSeconds(1);

    private static string _directory;
    private static EventAggregator _events;
    private static ServerState _server;
    private static int _port;
    private static KnownServersStore _pins;

    [ClassInitialize]
    public static async Task StartServer(TestContext _)
    {
        _directory = Path.Combine(Path.GetTempPath(), "erl-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        ServerSettingsStore.ResetInstanceForTests(Path.Combine(_directory, "server.cfg"));
        var settings = ServerSettingsStore.Instance;
        _port = FindFreePort();
        settings.SetServerSetting(ServerSettingsKeys.SERVER_PORT, _port.ToString());
        settings.SetServerSetting(ServerSettingsKeys.SERVER_IP, "127.0.0.1");
        settings.SetServerSetting(ServerSettingsKeys.UPNP_ENABLED, false);
        settings.SetServerSetting(ServerSettingsKeys.HTTP_SERVER_ENABLED, false);
        settings.SetServerPassword(Password);

        _pins = new KnownServersStore(Path.Combine(_directory, "client", KnownServersStore.FileName));

        _events = new EventAggregator();
        _server = new ServerState(_events);

        // both sockets bound (checked without connecting: every connection counts against the rate limit)
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!IsListening() && DateTime.UtcNow < deadline) await Task.Delay(20);

        Assert.IsNull(_server.LastStartError, _server.LastStartError);
        Assert.IsTrue(IsListening(), "the server did not start listening");
        Assert.IsTrue(File.Exists(Path.Combine(_directory, ServerIdentity.FileName)), "identity created next to server.cfg");
    }

    [ClassCleanup]
    public static void StopServer()
    {
        _server?.StopServer();
        ServerSettingsStore.ResetInstanceForTests(null);

        try
        {
            Directory.Delete(_directory, true);
        }
        catch (Exception)
        {
            // best effort (temp folder)
        }
    }

    private static int FindFreePort()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var port = ((IPEndPoint)udp.Client.LocalEndPoint).Port;
            try
            {
                var tcp = new TcpListener(IPAddress.Loopback, port);
                tcp.Start();
                tcp.Stop();
                return port;
            }
            catch (SocketException)
            {
                // TCP port taken - try another one
            }
        }

        throw new InvalidOperationException("no free port");
    }

    private static bool IsListening()
    {
        var properties = IPGlobalProperties.GetIPGlobalProperties();
        return properties.GetActiveTcpListeners().Any(e => e.Port == _port) &&
               properties.GetActiveUdpListeners().Any(e => e.Port == _port);
    }

    private static async Task WaitUntil(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? Wait);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("timed out waiting: " + what);
            await Task.Delay(10);
        }
    }

    private static PlayerRadioInfoBase Tuned(double frequency, Modulation modulation)
    {
        var info = new PlayerRadioInfoBase();
        info.radios[1] = new RadioBase { freq = frequency, modulation = modulation };
        return info;
    }

    private static byte[] Opus(int seed)
    {
        var opus = new byte[160];
        new Random(seed).NextBytes(opus);
        return opus;
    }

    // --- protocol-level test client ---------------------------------------------------------------------------------

    private sealed class TestClient : IDisposable
    {
        private readonly TaskCompletionSource<NetworkMessage> _syncReply =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly bool _ownsKeys;
        private Task _readLoop;

        /// <param name="keys">The key pair of this "app run" (shared by a reconnect); null = a new one.</param>
        public TestClient(string name, double frequency, Modulation modulation, E2EKeyPair keys = null)
        {
            Name = name;
            Guid = name.PadRight(22, '_');
            Frequency = frequency;
            Modulation = modulation;
            _ownsKeys = keys == null;
            Keys = keys ?? E2EKeyPair.Create();
        }

        public string Name { get; }
        public string Guid { get; }
        public double Frequency { get; }
        public Modulation Modulation { get; }
        public E2EKeyPair Keys { get; }
        public ConcurrentDictionary<string, ClientInfo> Clients { get; } = new();
        public E2EVoiceSession Voice { get; private set; }
        public SslStream Ssl { get; private set; }
        public UdpClient Udp { get; private set; }
        public UdpTransportSession Transport { get; private set; }
        public UdpTransportKey UdpKey { get; private set; }
        public string Fingerprint { get; private set; }
        public ServerPinStatus PinStatus { get; private set; }

        /// <summary>Every received line after the SYNC reply, with its decoded message.</summary>
        public ConcurrentQueue<(string Line, NetworkMessage Message)> Received { get; } = new();

        /// <summary>VOICE_KEY messages this client sent.</summary>
        public ConcurrentQueue<VoiceKeyMessage> SentVoiceKeys { get; } = new();

        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IEnumerable<(string Line, VoiceKeyMessage VoiceKey)> ReceivedVoiceKeys =>
            Received.Where(r => r.Message.MsgType == NetworkMessage.MessageType.VOICE_KEY)
                .Select(r => (r.Line, r.Message.VoiceKey));

        public void Dispose()
        {
            Voice?.Dispose();
            Ssl?.Dispose();
            Udp?.Dispose();
            Transport?.Dispose();
            if (_ownsKeys) Keys.Dispose();
        }

        public static async Task<SslStream> ConnectTlsAsync(KnownServersStore pins, Action<string, ServerPinStatus> seen)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, _port);

            var serverKey = KnownServersStore.ServerKey("127.0.0.1", _port);
            string presented = null;
            var status = ServerPinStatus.Mismatch;

            var ssl = new SslStream(tcp.GetStream(), false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "127.0.0.1",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    presented = ServerIdentity.GetFingerprint(certificate);
                    status = pins.Check(serverKey, presented, out _);
                    return status != ServerPinStatus.Mismatch;
                }
            }, new CancellationTokenSource(Wait).Token);

            if (status == ServerPinStatus.FirstUse) pins.Trust(serverKey, presented);
            seen?.Invoke(presented, status);
            return ssl;
        }

        public async Task JoinAsync(KnownServersStore pins, string password = Password)
        {
            Ssl = await ConnectTlsAsync(pins, (fingerprint, status) =>
            {
                Fingerprint = fingerprint;
                PinStatus = status;
            });

            _readLoop = Task.Run(ReadLoopAsync);

            await SendAsync(new NetworkMessage
            {
                MsgType = NetworkMessage.MessageType.SYNC,
                Password = password,
                Client = new ClientInfo
                {
                    ClientGuid = Guid, Name = Name, RadioInfo = Tuned(Frequency, Modulation),
                    E2EPublicKey = Keys.PublicKey
                }
            });

            var reply = await _syncReply.Task.WaitAsync(Wait);
            Assert.AreEqual(NetworkMessage.MessageType.SYNC, reply.MsgType, $"{Name}: SYNC reply");
            Assert.IsTrue(UdpTransportKey.TryParse(reply.UdpKey, reply.UdpKeyId, out var udpKey),
                $"{Name}: the SYNC reply carries a 32 byte UDP key");
            UdpKey = udpKey;

            Transport = new UdpTransportSession(Guid, udpKey.Key, udpKey.KeyId, false);
            Udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            Udp.Connect(IPAddress.Loopback, _port);

            Voice = new E2EVoiceSession(Guid, Keys, Clients, SendAsync);

            // the server learns the voice endpoint from an authenticated ping and answers with an encrypted pong
            await Udp.SendAsync(Transport.Seal(UdpDatagram.PingBody));
            var pong = await ReceiveDatagramAsync(Wait);
            Assert.IsNotNull(pong, $"{Name}: pong");
            Assert.AreEqual(UdpOpenResult.Ok, Transport.Open(pong, out var body));
            Assert.IsTrue(UdpDatagram.IsPing(body));
        }

        public async Task SendAsync(NetworkMessage message)
        {
            if (message.MsgType == NetworkMessage.MessageType.VOICE_KEY) SentVoiceKeys.Enqueue(message.VoiceKey);
            await SendLineAsync(message.Encode());
        }

        public async Task SendLineAsync(string line)
        {
            var bytes = Encoding.UTF8.GetBytes(line);
            await _writeLock.WaitAsync();
            try
            {
                await Ssl.WriteAsync(bytes);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task ReadLoopAsync()
        {
            try
            {
                using var reader = new StreamReader(Ssl, Encoding.UTF8, false, 4096, true);
                string line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    var message = NetworkMessage.Decode(line);
                    switch (message.MsgType)
                    {
                        case NetworkMessage.MessageType.SYNC:
                            foreach (var client in message.Clients ?? []) Clients[client.ClientGuid] = client;
                            _syncReply.TrySetResult(message);
                            continue;
                        case NetworkMessage.MessageType.RADIO_UPDATE:
                        case NetworkMessage.MessageType.UPDATE:
                            if (Clients.TryGetValue(message.Client.ClientGuid, out var known))
                            {
                                if (message.Client.RadioInfo != null) known.RadioInfo = message.Client.RadioInfo;
                                if (message.Client.E2EPublicKey != null) known.E2EPublicKey = message.Client.E2EPublicKey;
                            }
                            else
                            {
                                Clients[message.Client.ClientGuid] = message.Client;
                            }

                            break;
                        case NetworkMessage.MessageType.CLIENT_DISCONNECT:
                            Clients.TryRemove(message.Client.ClientGuid, out _);
                            Voice?.ForgetClient(message.Client.ClientGuid);
                            break;
                        case NetworkMessage.MessageType.VOICE_KEY:
                            Voice?.HandleVoiceKey(message.VoiceKey);
                            break;
                        case NetworkMessage.MessageType.VERSION_MISMATCH:
                        case NetworkMessage.MessageType.AUTH_FAILED:
                            _syncReply.TrySetResult(message);
                            break;
                    }

                    Received.Enqueue((line, message));
                }
            }
            catch (Exception)
            {
                // closed
            }
            finally
            {
                Closed.TrySetResult();
                _syncReply.TrySetCanceled();
            }
        }

        public async Task<byte[]> ReceiveDatagramAsync(TimeSpan timeout)
        {
            try
            {
                using var cts = new CancellationTokenSource(timeout);
                return (await Udp.ReceiveAsync(cts.Token)).Buffer;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        /// <summary>One voice frame: E2E packet -> hop encrypted datagram -> sent.</summary>
        public async Task<(UDPVoicePacket Packet, byte[] Datagram)> TransmitAsync(byte[] opus, ulong packetNumber,
            double? frequency = null)
        {
            var packet = Voice.CreateVoicePacket(opus, packetNumber, frequency ?? Frequency, Modulation);
            Assert.IsNotNull(packet);
            var datagram = Transport.Seal(packet.EncodePacket());
            await Udp.SendAsync(datagram);
            return (packet, datagram);
        }

        /// <summary>Receives, authenticates (hop) and decrypts (E2E) the next voice frame; null if none arrives.</summary>
        public async Task<ReceivedVoiceFrame?> ReceiveVoiceAsync(TimeSpan timeout)
        {
            var datagram = await ReceiveDatagramAsync(timeout);
            if (datagram == null) return null;

            Assert.AreEqual(UdpOpenResult.Ok, Transport.Open(datagram, out var body), $"{Name}: hop layer");
            Assert.IsTrue(UDPVoicePacket.TryDecode(body, true, out var packet), $"{Name}: voice packet");

            var frames = new List<ReceivedVoiceFrame>();
            Voice.Receiver.Receive(packet, DateTime.UtcNow, frames);
            var deadline = DateTime.UtcNow + E2EVoiceReceiver.HoldTime + TimeSpan.FromMilliseconds(100);
            while (frames.Count == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
                Voice.Receiver.Poll(DateTime.UtcNow, frames);
            }

            return frames.Count == 0 ? null : frames[0];
        }

        public async Task SendRadioUpdateAsync(double frequency, Modulation modulation)
        {
            await SendAsync(new NetworkMessage
            {
                MsgType = NetworkMessage.MessageType.RADIO_UPDATE,
                Client = new ClientInfo { ClientGuid = Guid, Name = Name, RadioInfo = Tuned(frequency, modulation) }
            });
        }
    }

    private static bool Knows(TestClient observer, TestClient other, double frequency, Modulation modulation)
    {
        return observer.Clients.TryGetValue(other.Guid, out var info)
               && info.E2EPublicKey == other.Keys.PublicKey
               && VoiceRouting.CanReceive(info.RadioInfo, frequency, modulation);
    }

    // --- server-side inspection -------------------------------------------------------------------------------------

    /// <summary>Walks every field of the server's client objects and sessions: none may hold <paramref name="key" />.</summary>
    private static void AssertServerNeverHeld(byte[] key)
    {
        var clients = (ConcurrentDictionary<string, ClientInfo>)typeof(ServerState)
            .GetField("_connectedClients", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_server);
        var sync = typeof(ServerState).GetField("_serverSync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(_server);
        var sessions = (IDictionary)typeof(SslServer).GetField("Sessions", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(sync);

        Assert.IsNotEmpty(clients);
        Assert.IsGreaterThan(0, sessions.Count);

        var base64 = Convert.ToBase64String(key);
        var hex = Convert.ToHexString(key);
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);

        void Check(string text, string where)
        {
            if (text == null) return;
            Assert.IsFalse(text.Contains(base64, StringComparison.Ordinal) ||
                           text.Contains(hex, StringComparison.OrdinalIgnoreCase), $"transmission key found in {where}");
        }

        void Scan(object value, string where, int depth)
        {
            switch (value)
            {
                case null:
                    return;
                case string text:
                    Check(text, where);
                    return;
                case byte[] bytes:
                    Assert.IsLessThan(0, bytes.AsSpan().IndexOf(key.AsSpan(0, 16)), $"transmission key found in {where}");
                    return;
                case char[] chars:
                    Check(new string(chars), where);
                    return;
                case StringBuilder builder:
                    Check(builder.ToString(), where);
                    return;
            }

            var type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || depth > 4 || !visited.Add(value)) return;
            if (type.Namespace == null || !type.Namespace.StartsWith("EasyRadioLink", StringComparison.Ordinal)) return;

            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
                foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                                                  BindingFlags.DeclaredOnly))
                    Scan(field.GetValue(value), $"{where}.{field.Name}", depth + 1);
        }

        foreach (var client in clients.Values) Scan(client, $"ClientInfo {client.ClientGuid}", 0);
        foreach (var session in sessions.Values) Scan(session, "RadioClientSession", 0);
    }

    // --- tests ------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task VoiceIsEndToEndEncryptedBetweenListenersOnTheFrequency()
    {
        using var alice = new TestClient("Alice", Cb19, Modulation.AM);
        using var bob = new TestClient("Bob", Cb19, Modulation.AM);
        using var eve = new TestClient("Eve", Pmr, Modulation.FM);

        // TLS + pinned identity + SYNC with a UDP key of its own for everybody
        await alice.JoinAsync(_pins);
        await bob.JoinAsync(_pins);
        await eve.JoinAsync(_pins);

        Assert.IsTrue(ServerIdentity.FingerprintsEqual(_server.IdentityFingerprint, alice.Fingerprint));
        Assert.AreEqual(ServerPinStatus.Match, bob.PinStatus, "pinned on first use, matched afterwards");
        Assert.AreEqual(ServerPinStatus.Match, eve.PinStatus);
        Assert.AreEqual(3, new[] { alice.UdpKey.KeyId, bob.UdpKey.KeyId, eve.UdpKey.KeyId }.Distinct().Count());
        Assert.IsFalse(alice.UdpKey.Key.AsSpan().SequenceEqual(bob.UdpKey.Key));

        // everybody knows everybody's end-to-end public key from the client list
        await WaitUntil(() => Knows(alice, bob, Cb19, Modulation.AM) && Knows(alice, eve, Pmr, Modulation.FM) &&
                              Knows(bob, alice, Cb19, Modulation.AM) && Knows(eve, alice, Cb19, Modulation.AM),
            "client lists with public keys");

        // --- Alice talks: the key goes to Bob only, the voice reaches Bob only ---
        var opus1 = Opus(1);
        var (packet1, datagram1) = await alice.TransmitAsync(opus1, 1);
        var transmission = alice.Voice.CurrentTransmission;
        var key = transmission.CopyKeyForTests();

        await WaitUntil(() => alice.SentVoiceKeys.Count == 1, "Alice's VOICE_KEY");
        alice.SentVoiceKeys.TryPeek(out var sent);
        CollectionAssert.AreEqual(new[] { bob.Guid }, sent.Keys.Keys.ToArray(), "Alice wraps the key for Bob only");

        await WaitUntil(() => bob.ReceivedVoiceKeys.Any(), "VOICE_KEY at Bob");
        var (line, forwarded) = bob.ReceivedVoiceKeys.Single();
        Assert.AreEqual(alice.Guid, forwarded.SenderGuid);
        Assert.AreEqual(transmission.TxIdBase64, forwarded.TxId);
        CollectionAssert.AreEqual(new[] { bob.Guid }, forwarded.Keys.Keys.ToArray(), "only Bob's own entry");
        var wrapped = Convert.FromBase64String(forwarded.Keys[bob.Guid]);
        Assert.AreEqual(E2EVoiceCrypto.WrappedKeyLength, wrapped.Length);
        Assert.IsFalse(wrapped.AsSpan().SequenceEqual(key));
        StringAssert.DoesNotMatch(line, new System.Text.RegularExpressions.Regex(
            System.Text.RegularExpressions.Regex.Escape(Convert.ToBase64String(key)) + "|" + Convert.ToHexString(key),
            System.Text.RegularExpressions.RegexOptions.IgnoreCase), "the forwarded VOICE_KEY holds only a wrapped blob");
        await WaitUntil(() => bob.Voice.Receiver.Keys.Contains(alice.Guid, transmission.TxId, DateTime.UtcNow),
            "Bob unwrapped the key");

        var atBob = await bob.ReceiveVoiceAsync(Wait);
        Assert.IsNotNull(atBob, "voice at Bob");
        Assert.IsFalse(atBob.Value.Scrambled);
        CollectionAssert.AreEqual(opus1, atBob.Value.Opus, "Bob decrypts Alice's voice byte for byte");
        Assert.AreEqual(Cb19, atBob.Value.Packet.Frequencies[0]);

        // what the server routes (the hop-decrypted body) does not contain the audio
        Assert.IsLessThan(0, packet1.EncodePacket().AsSpan().IndexOf(opus1.AsSpan(0, 16)));

        Assert.IsNull(await eve.ReceiveDatagramAsync(Silence), "Eve (other frequency) gets no packets");
        Assert.IsFalse(eve.ReceivedVoiceKeys.Any(), "Eve gets no key");

        // --- replayed and tampered datagrams are dropped ---
        await alice.Udp.SendAsync(datagram1);
        Assert.IsNull(await bob.ReceiveDatagramAsync(Silence), "replayed datagram dropped");

        var (_, datagram2) = (await BuildAsync(alice, Opus(2), 2));
        datagram2[UdpDatagram.HeaderLength + 10] ^= 0x20;
        await alice.Udp.SendAsync(datagram2);
        Assert.IsNull(await bob.ReceiveDatagramAsync(Silence), "tampered datagram dropped");

        var (_, datagram3) = await alice.TransmitAsync(Opus(3), 3);
        var third = await bob.ReceiveVoiceAsync(Wait);
        CollectionAssert.AreEqual(Opus(3), third?.Opus, "Alice still gets through");

        // --- the server never holds the transmission key ---
        AssertServerNeverHeld(key);

        // --- a key listed for Eve (who can't hear 27.185 AM) is not forwarded to her ---
        var sneaky = transmission.WrapFor(alice.Keys,
        [
            new VoiceKeyRecipient(bob.Guid, bob.Keys.PublicKey),
            new VoiceKeyRecipient(eve.Guid, eve.Keys.PublicKey)
        ]).Single();
        var bobKeysBefore = bob.ReceivedVoiceKeys.Count();
        await alice.SendAsync(new NetworkMessage { MsgType = NetworkMessage.MessageType.VOICE_KEY, VoiceKey = sneaky });
        await WaitUntil(() => bob.ReceivedVoiceKeys.Count() == bobKeysBefore + 1, "second VOICE_KEY at Bob");
        Assert.AreEqual(1, bob.ReceivedVoiceKeys.Last().VoiceKey.Keys.Count);
        await Task.Delay(Silence);
        Assert.IsFalse(eve.ReceivedVoiceKeys.Any(), "the server forwards keys only to clients that can hear the frequency");

        // --- Eve tunes in mid-transmission: she gets the key and hears the rest ---
        await eve.SendRadioUpdateAsync(Cb19, Modulation.AM);
        await WaitUntil(() => Knows(alice, eve, Cb19, Modulation.AM), "Alice sees Eve on 27.185");

        var aliceKeysBefore = alice.SentVoiceKeys.Count;
        var number = 4UL;
        ReceivedVoiceFrame? atEve = null;
        var deadline = DateTime.UtcNow + Wait;
        while (atEve is not { Scrambled: false } && DateTime.UtcNow < deadline)
        {
            await Task.Delay(40); // one frame every 40 ms, like a real radio
            await alice.TransmitAsync(Opus((int)number), number);
            await bob.ReceiveVoiceAsync(Wait);
            atEve = await eve.ReceiveVoiceAsync(Wait);
            number++;
        }

        Assert.IsNotNull(atEve, "Eve receives packets on 27.185");
        Assert.IsFalse(atEve.Value.Scrambled, "Eve got the key during the transmission");
        CollectionAssert.AreEqual(Opus((int)(number - 1)), atEve.Value.Opus);
        Assert.AreEqual(aliceKeysBefore + 1, alice.SentVoiceKeys.Count, "one more VOICE_KEY, for the newcomer");
        Assert.IsTrue(alice.SentVoiceKeys.Last().Keys.Keys.SequenceEqual([eve.Guid]));
        var eveKey = eve.ReceivedVoiceKeys.Single().VoiceKey;
        Assert.AreEqual(transmission.TxIdBase64, eveKey.TxId, "the same transmission");

        // --- radio check: Alice hears her own echo on 27.405 (a new transmission she knows the key of) ---
        var echoOpus = Opus(99);
        await alice.TransmitAsync(echoOpus, number++, RadioCheck);
        Assert.AreNotEqual(transmission.TxId, alice.Voice.CurrentTransmission.TxId);
        var echo = await alice.ReceiveVoiceAsync(Wait);
        Assert.IsNotNull(echo, "echo");
        CollectionAssert.AreEqual(echoOpus, echo.Value.Opus);
        Assert.AreEqual(RadioCheck, echo.Value.Packet.Frequencies[0]);

        // --- VOICE_KEY flood: a burst of 20, then 10 per second are forwarded, the connection stays ---
        const int floodSize = 60;
        var floodTxIds = new HashSet<string>();
        var floodStart = DateTime.UtcNow;
        for (var i = 0; i < floodSize; i++)
        {
            using var flood = new VoiceTransmission(alice.Guid, Cb19, Modulation.AM);
            var message = flood.WrapFor(alice.Keys, [new VoiceKeyRecipient(bob.Guid, bob.Keys.PublicKey)]).Single();
            floodTxIds.Add(message.TxId);
            await alice.SendAsync(new NetworkMessage { MsgType = NetworkMessage.MessageType.VOICE_KEY, VoiceKey = message });
        }

        var floodSeconds = (DateTime.UtcNow - floodStart).TotalSeconds;
        await Task.Delay(Silence);
        var floodForwarded = bob.ReceivedVoiceKeys.Count(k => floodTxIds.Contains(k.VoiceKey.TxId));
        Assert.IsGreaterThanOrEqualTo(RadioClientSession.MaxVoiceKeysPerSecond, floodForwarded,
            "a burst (e.g. after a TCP stall) gets through");
        Assert.IsLessThanOrEqualTo(
            RadioClientSession.VoiceKeyBurst + (int)Math.Ceiling(floodSeconds * RadioClientSession.MaxVoiceKeysPerSecond) + 1,
            floodForwarded, $"{floodForwarded} of {floodSize} forwarded in {floodSeconds:0.00} s");
        Assert.IsLessThan(floodSize, floodForwarded);
        Assert.IsFalse(alice.Closed.Task.IsCompleted, "dropping is enough - the connection stays");

        // --- a VOICE_KEY in somebody else's name closes the connection ---
        using var forged = new VoiceTransmission(bob.Guid, Cb19, Modulation.AM);
        var forgedMessage = forged.WrapFor(alice.Keys, [new VoiceKeyRecipient(eve.Guid, eve.Keys.PublicKey)]).Single();
        await alice.SendAsync(new NetworkMessage { MsgType = NetworkMessage.MessageType.VOICE_KEY, VoiceKey = forgedMessage });

        await alice.Closed.Task.WaitAsync(Wait);
        await WaitUntil(() => !bob.Clients.ContainsKey(alice.Guid), "Bob sees Alice leave");
        Assert.IsFalse(eve.ReceivedVoiceKeys.Any(k => k.VoiceKey.SenderGuid == bob.Guid), "the forged key was not forwarded");
    }

    private static Task<(UDPVoicePacket, byte[])> BuildAsync(TestClient client, byte[] opus, ulong number)
    {
        var packet = client.Voice.CreateVoicePacket(opus, number, client.Frequency, client.Modulation);
        return Task.FromResult((packet, client.Transport.Seal(packet.EncodePacket())));
    }

    // --- UDP: forged datagrams in a client's name ---------------------------------------------------------------------

    [TestMethod]
    public async Task ForgedFloodFromAnotherPortDoesNotMuteTheClient()
    {
        using var victim = new TestClient("Victim", Cb19, Modulation.AM);
        await victim.JoinAsync(_pins);

        // another host behind the victim's NAT (same IP address - it passes the address check) floods datagrams with
        // the victim's id: far more failed authentications per second than the budget of its endpoint
        using var forger = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        forger.Connect(IPAddress.Loopback, _port);
        using var stop = new CancellationTokenSource();
        long forged = 0;
        var flood = Task.Run(async () =>
        {
            var junk = new byte[UdpDatagram.MinLength + 40];
            var guid = Encoding.ASCII.GetBytes(victim.Guid);
            while (!stop.IsCancellationRequested)
            {
                for (var i = 0; i < 20; i++)
                {
                    RandomNumberGenerator.Fill(junk);
                    guid.CopyTo(junk, 0);
                    junk[UdpDatagram.GuidLength] = 0; // a counter the replay window accepts: it costs a decryption
                    await forger.SendAsync(junk);
                    Interlocked.Increment(ref forged);
                }

                await Task.Delay(5);
            }
        });

        int sent = 0, answered = 0;
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < end)
        {
            await victim.Udp.SendAsync(victim.Transport.Seal(UdpDatagram.PingBody));
            sent++;
            var pong = await victim.ReceiveDatagramAsync(TimeSpan.FromMilliseconds(500));
            if (pong != null && victim.Transport.Open(pong, out var body) == UdpOpenResult.Ok && UdpDatagram.IsPing(body))
                answered++;
            await Task.Delay(20);
        }

        await stop.CancelAsync();
        await flood;

        Assert.IsGreaterThan(4L * UdpAuthFailureBudget.MaxFailuresPerEndpoint, Interlocked.Read(ref forged),
            "the flood exceeded the forger's budget");
        Assert.IsGreaterThanOrEqualTo(sent - 1, answered, $"{answered} of {sent} pings answered during the flood");
    }

    // --- voice keys for listeners who come back ------------------------------------------------------------------------

    /// <summary>Alice transmits (a new frame every 40 ms) until <paramref name="listener" /> decrypts one.</summary>
    private static async Task<ReceivedVoiceFrame?> TransmitUntilHeardAsync(TestClient alice, TestClient listener,
        ulong firstNumber)
    {
        var number = firstNumber;
        var deadline = DateTime.UtcNow + Wait;
        ReceivedVoiceFrame? heard = null;
        while (heard is not { Scrambled: false } && DateTime.UtcNow < deadline)
        {
            await Task.Delay(40);
            await alice.TransmitAsync(Opus((int)number), number);
            heard = await listener.ReceiveVoiceAsync(Wait);
            if (heard is { Scrambled: false }) CollectionAssert.AreEqual(Opus((int)number), heard.Value.Opus);
            number++;
        }

        return heard;
    }

    [TestMethod]
    public async Task ListenerReconnectingDuringATransmissionGetsTheKeyAgain()
    {
        using var alice = new TestClient("AliceRe", Cb19, Modulation.AM);
        using var bobKeys = E2EKeyPair.Create(); // one app run of Bob: the same key pair after the reconnect
        var bob = new TestClient("BobRe", Cb19, Modulation.AM, bobKeys);
        await alice.JoinAsync(_pins);
        await bob.JoinAsync(_pins);
        await WaitUntil(() => Knows(alice, bob, Cb19, Modulation.AM), "Alice knows Bob");

        var first = await TransmitUntilHeardAsync(alice, bob, 1);
        Assert.IsFalse(first?.Scrambled ?? true, "Bob hears Alice");
        var transmission = alice.Voice.CurrentTransmission.TxId;

        // Bob's connection drops, he reconnects while Alice keeps talking
        bob.Dispose();
        await WaitUntil(() => !alice.Clients.ContainsKey(bob.Guid), "Alice sees Bob leave");
        using var bobAgain = new TestClient("BobRe", Cb19, Modulation.AM, bobKeys);
        await bobAgain.JoinAsync(_pins);
        await WaitUntil(() => Knows(alice, bobAgain, Cb19, Modulation.AM), "Alice sees Bob again");

        var heard = await TransmitUntilHeardAsync(alice, bobAgain, 100);
        Assert.IsFalse(heard?.Scrambled ?? true, "Bob gets the key of the running transmission again");
        Assert.AreEqual(transmission, alice.Voice.CurrentTransmission.TxId, "still the same transmission");
    }

    [TestMethod]
    public async Task TakenOverIdIsAnnouncedAsLeftAndGetsTheKeyAgain()
    {
        using var alice = new TestClient("AliceTo", Cb19, Modulation.AM);
        using var bob = new TestClient("BobTo", Cb19, Modulation.AM);
        await alice.JoinAsync(_pins);
        await bob.JoinAsync(_pins);
        await WaitUntil(() => Knows(alice, bob, Cb19, Modulation.AM), "Alice knows Bob");
        Assert.IsFalse((await TransmitUntilHeardAsync(alice, bob, 1))?.Scrambled ?? true);

        // Bob's app restarted (new key pair) and connects again while the server still has his old connection
        using var bobNew = new TestClient("BobTo", Cb19, Modulation.AM);
        await bobNew.JoinAsync(_pins);
        await bob.Closed.Task.WaitAsync(Wait);

        // everybody else sees the old connection leave, then the new one arrive - in this order
        await WaitUntil(() => Knows(alice, bobNew, Cb19, Modulation.AM), "Alice knows Bob's new connection");
        var received = alice.Received.ToList();
        var left = received.FindIndex(r => r.Message.MsgType == NetworkMessage.MessageType.CLIENT_DISCONNECT &&
                                           r.Message.Client?.ClientGuid == bob.Guid);
        var arrived = received.FindLastIndex(r => r.Message.MsgType == NetworkMessage.MessageType.RADIO_UPDATE &&
                                                  r.Message.Client?.E2EPublicKey == bobNew.Keys.PublicKey);
        Assert.IsGreaterThanOrEqualTo(0, left, "CLIENT_DISCONNECT for the taken over connection");
        Assert.IsGreaterThan(left, arrived, "announced after it left");
        Assert.IsFalse(bobNew.Received.Any(r => r.Message.MsgType == NetworkMessage.MessageType.CLIENT_DISCONNECT &&
                                                r.Message.Client?.ClientGuid == bobNew.Guid), "not sent to the new connection");

        var heard = await TransmitUntilHeardAsync(alice, bobNew, 100);
        Assert.IsFalse(heard?.Scrambled ?? true, "the new connection gets the key of the running transmission");
    }

    [TestMethod]
    public async Task ListenerSweepingPastTheFrequencyGetsTheKeyWhenHeSettlesOnIt()
    {
        using var alice = new TestClient("AliceSw", Cb19, Modulation.AM);
        using var bob = new TestClient("BobSw", 27200000, Modulation.AM);
        await alice.JoinAsync(_pins);
        await bob.JoinAsync(_pins);
        await WaitUntil(() => alice.Clients.ContainsKey(bob.Guid), "Alice knows Bob");
        await alice.TransmitAsync(Opus(1), 1); // a transmission is running, Bob does not listen

        // Bob's knob passes 27.185: Alice's view still shows him there while the server has him on 27.190 already
        await bob.SendRadioUpdateAsync(27190000, Modulation.AM);
        await WaitUntil(() => alice.Clients[bob.Guid].RadioInfo.radios[1].freq == 27190000, "Alice sees 27.190");
        var stale = Tuned(Cb19, Modulation.AM);
        alice.Clients[bob.Guid].RadioInfo = stale;
        await Task.Delay(E2EVoiceSession.MinKeyMessageInterval + TimeSpan.FromMilliseconds(25));
        await alice.TransmitAsync(Opus(2), 2); // K is wrapped for Bob - and dropped by the server (not on 27.185)
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.IsFalse(bob.ReceivedVoiceKeys.Any(), "the server forwards keys only to listeners on the frequency");

        // Bob settles on 27.185: Alice gets his new radio state (the same frequency as her stale view) and wraps K again
        await bob.SendRadioUpdateAsync(Cb19, Modulation.AM);
        await WaitUntil(() => !ReferenceEquals(alice.Clients[bob.Guid].RadioInfo, stale), "Alice gets Bob's new state");

        var heard = await TransmitUntilHeardAsync(alice, bob, 3);
        Assert.IsFalse(heard?.Scrambled ?? true, "Bob hears the rest of the transmission");
    }

    // --- busy channel lockout: one speaker per frequency ---------------------------------------------------------------

    /// <summary>
    ///     The senders of the voice datagrams <paramref name="listener" /> receives until nothing arrives for
    ///     <paramref name="quiet" /> (pongs are skipped).
    /// </summary>
    private static async Task<List<string>> ReceiveSendersAsync(TestClient listener, TimeSpan quiet)
    {
        var senders = new List<string>();
        while (true)
        {
            var datagram = await listener.ReceiveDatagramAsync(quiet);
            if (datagram == null) return senders;

            Assert.AreEqual(UdpOpenResult.Ok, listener.Transport.Open(datagram, out var body), $"{listener.Name}: hop layer");
            if (UdpDatagram.IsPing(body)) continue;

            Assert.IsTrue(UDPVoicePacket.TryDecode(body, false, out var packet), $"{listener.Name}: voice packet");
            senders.Add(packet.Guid);
        }
    }

    /// <summary>The sender of the next voice datagram <paramref name="listener" /> receives (pongs are skipped).</summary>
    private static async Task<string> ReceiveSenderAsync(TestClient listener)
    {
        while (true)
        {
            var datagram = await listener.ReceiveDatagramAsync(Wait);
            Assert.IsNotNull(datagram, $"{listener.Name}: a voice datagram");

            Assert.AreEqual(UdpOpenResult.Ok, listener.Transport.Open(datagram, out var body), $"{listener.Name}: hop layer");
            if (UdpDatagram.IsPing(body)) continue;

            Assert.IsTrue(UDPVoicePacket.TryDecode(body, false, out var packet), $"{listener.Name}: voice packet");
            return packet.Guid;
        }
    }

    /// <summary>Alice, Bob and Carol on <paramref name="frequency" /> AM, each knowing the others' public keys.</summary>
    private static async Task<(TestClient Alice, TestClient Bob, TestClient Carol)> JoinBusyChannelTrioAsync(
        string suffix, double frequency)
    {
        var alice = new TestClient("Alice" + suffix, frequency, Modulation.AM);
        var bob = new TestClient("Bob" + suffix, frequency, Modulation.AM);
        var carol = new TestClient("Carol" + suffix, frequency, Modulation.AM);
        await alice.JoinAsync(_pins);
        await bob.JoinAsync(_pins);
        await carol.JoinAsync(_pins);

        // the transmission keys reach the listeners on the frequency
        await WaitUntil(() => Knows(alice, bob, frequency, Modulation.AM) && Knows(alice, carol, frequency, Modulation.AM) &&
                              Knows(bob, alice, frequency, Modulation.AM) && Knows(bob, carol, frequency, Modulation.AM),
            "client lists with public keys");

        return (alice, bob, carol);
    }

    [TestMethod]
    public async Task BusyChannelLockoutLetsOnlyTheFirstSpeakerThroughUntilTheHangTimeIsOver()
    {
        var (alice, bob, carol) = await JoinBusyChannelTrioAsync("Busy", Cb21);
        using var aliceScope = alice;
        using var bobScope = bob;
        using var carolScope = carol;

        // Alice presses a moment earlier: once her first frame is routed she holds 27.215 AM. The next frames follow
        // right away (well within the hang time): two datagrams sent back to back on a FREE channel may be routed in
        // either order (thread pool), so the test must not wait for silence here.
        await alice.TransmitAsync(Opus(1), 1);
        Assert.AreEqual(alice.Guid, await ReceiveSenderAsync(carol), "Carol hears Alice");
        Assert.AreEqual(alice.Guid, await ReceiveSenderAsync(bob), "Bob hears Alice");

        // both talk at the same time (a frame every 40 ms each): only Alice gets through
        for (var number = 2UL; number < 14; number++)
        {
            await alice.TransmitAsync(Opus((int)number), number);
            await bob.TransmitAsync(Opus(100 + (int)number), number);
            await Task.Delay(40);
        }

        // Alice releases; right away (within the hang time) the channel is still hers
        await bob.TransmitAsync(Opus(200), 200);

        var atCarol = await ReceiveSendersAsync(carol, Silence);
        Assert.HasCount(12, atCarol, "every frame of Alice");
        Assert.IsTrue(atCarol.All(sender => sender == alice.Guid),
            "none of Bob's frames reach the listener - neither during Alice's transmission nor within the hang time");
        Assert.IsEmpty(await ReceiveSendersAsync(alice, TimeSpan.FromMilliseconds(200)), "Alice doesn't hear Bob either");

        // what tells Bob's client that it lost the race: it receives Alice on its own frequency while transmitting
        var atBob = await ReceiveSendersAsync(bob, TimeSpan.FromMilliseconds(200));
        Assert.HasCount(12, atBob);
        Assert.IsTrue(atBob.All(sender => sender == alice.Guid));

        // after the hang time Bob is heard - and decrypted
        await Task.Delay(BusyChannelArbiter.HangTimeMilliseconds + 100);
        var heard = await TransmitUntilHeardAsync(bob, carol, 300);
        Assert.IsNotNull(heard, "Carol hears Bob after Alice stopped");
        Assert.IsFalse(heard.Value.Scrambled);
        Assert.AreEqual(bob.Guid, heard.Value.Packet.Guid);

        // now Bob holds the channel and Alice is locked out
        await ReceiveSendersAsync(carol, TimeSpan.FromMilliseconds(100));
        await bob.TransmitAsync(Opus(400), 400);
        await alice.TransmitAsync(Opus(401), 401);
        CollectionAssert.AreEqual(new[] { bob.Guid }, (await ReceiveSendersAsync(carol, Silence)).ToArray());
    }

    [TestMethod]
    public async Task WithoutBusyChannelLockoutBothSpeakersAreHeard()
    {
        var (alice, bob, carol) = await JoinBusyChannelTrioAsync("Free", Cb22);
        using var aliceScope = alice;
        using var bobScope = bob;
        using var carolScope = carol;

        var settings = ServerSettingsStore.Instance;
        try
        {
            // switched off in the server window: the voice router and the clients pick it up at once
            settings.SetGeneralSetting(ServerSettingsKeys.BUSY_CHANNEL_LOCKOUT, false);
            await _events.PublishOnBackgroundThreadAsync(new ServerSettingsChangedMessage());
            await WaitUntil(() => carol.Received.Any(r =>
                    r.Message.MsgType == NetworkMessage.MessageType.SERVER_SETTINGS &&
                    r.Message.ServerSettings != null &&
                    r.Message.ServerSettings.TryGetValue(nameof(ServerSettingsKeys.BUSY_CHANNEL_LOCKOUT), out var value) &&
                    value == "False"),
                "the new setting at the clients");

            for (var number = 1UL; number < 13; number++)
            {
                await alice.TransmitAsync(Opus((int)number), number);
                await bob.TransmitAsync(Opus(100 + (int)number), number);
                await Task.Delay(40);
            }

            var atCarol = await ReceiveSendersAsync(carol, Silence);
            Assert.AreEqual(12, atCarol.Count(sender => sender == alice.Guid), "Alice");
            Assert.AreEqual(12, atCarol.Count(sender => sender == bob.Guid), "and Bob at the same time, as before 1.3");
            Assert.HasCount(12, await ReceiveSendersAsync(alice, Silence), "Alice hears Bob");
        }
        finally
        {
            settings.SetGeneralSetting(ServerSettingsKeys.BUSY_CHANNEL_LOCKOUT, true);
            await _events.PublishOnBackgroundThreadAsync(new ServerSettingsChangedMessage());
        }
    }

    // --- the real client's identity check (TCPClientHandler) ----------------------------------------------------------

    private sealed class StatusCollector : IHandle<TCPClientStatusMessage>
    {
        public TaskCompletionSource<TCPClientStatusMessage> First { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task HandleAsync(TCPClientStatusMessage message, CancellationToken cancellationToken)
        {
            First.TrySetResult(message);
            return Task.CompletedTask;
        }
    }

    /// <summary>Connects the real client connection with <paramref name="pins" />; returns its first status message.</summary>
    private static async Task<TCPClientStatusMessage> ConnectRealClientAsync(KnownServersStore pins)
    {
        var collector = new StatusCollector();
        EventBus.Instance.SubscribeOnPublishedThread(collector);
        using var keys = E2EKeyPair.Create();
        var client = new TCPClientHandler(ShortGuid.NewGuid().ToString(), new ClientInfo { Name = "real client" },
            keys, Password, "127.0.0.1", pins);
        try
        {
            client.TryConnect(new IPEndPoint(IPAddress.Loopback, _port));
            return await collector.First.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            await client.RequestDisconnectAsync(true);
            EventBus.Instance.Unsubscribe(collector);
        }
    }

    [TestMethod]
    public async Task RealClientRefusesToConnectWhenItsKnownServersFileIsDamaged()
    {
        var path = Path.Combine(_directory, "damaged", KnownServersStore.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string damaged = "{ \"127.0.0.1:5010\": \"AB:CD\"";
        File.WriteAllText(path, damaged);

        var status = await ConnectRealClientAsync(new KnownServersStore(path));

        Assert.IsFalse(status.Connected);
        Assert.AreEqual(TCPClientStatusMessage.ErrorCode.IDENTITY_CHECK_FAILED, status.Error,
            "not 'identity changed', and never a silent first use");
        Assert.IsNull(status.IdentityMismatch, "nothing to trust");
        StringAssert.Contains(status.ErrorDetail, "damaged");
        StringAssert.Contains(status.ErrorDetail, path);
        Assert.AreEqual(damaged, File.ReadAllText(path), "the damaged file is not overwritten");
    }

    [TestMethod]
    public async Task RealClientRefusesToConnectWhenItsKnownServersFileIsLocked()
    {
        var path = Path.Combine(_directory, "locked", KnownServersStore.FileName);
        var pins = new KnownServersStore(path);
        pins.Trust(KnownServersStore.ServerKey("127.0.0.1", _port), _server.IdentityFingerprint);

        TCPClientStatusMessage status;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            status = await ConnectRealClientAsync(pins);
        }

        Assert.AreEqual(TCPClientStatusMessage.ErrorCode.IDENTITY_CHECK_FAILED, status.Error);
        Assert.IsNull(status.IdentityMismatch);
        StringAssert.Contains(status.ErrorDetail, "can't be read");
    }

    [TestMethod]
    public async Task RealClientAsksWhenThePinOfTheServerIsUnreadable()
    {
        var path = Path.Combine(_directory, "invalid-entry", KnownServersStore.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var key = KnownServersStore.ServerKey("127.0.0.1", _port);
        File.WriteAllText(path, $"{{ \"{key}\": \"not a fingerprint\" }}");

        var status = await ConnectRealClientAsync(new KnownServersStore(path));

        Assert.AreEqual(TCPClientStatusMessage.ErrorCode.SERVER_IDENTITY_UNKNOWN, status.Error);
        Assert.IsNotNull(status.IdentityMismatch);
        Assert.IsNull(status.IdentityMismatch.PinnedFingerprint, "there is no known fingerprint to show");
        Assert.IsTrue(ServerIdentity.FingerprintsEqual(_server.IdentityFingerprint,
            status.IdentityMismatch.PresentedFingerprint));
        Assert.AreEqual(key, status.IdentityMismatch.ServerKey);
        StringAssert.Contains(File.ReadAllText(path), "not a fingerprint", "not pinned silently");
    }

    [TestMethod]
    public async Task RealClientReportsAChangedIdentityWithBothFingerprints()
    {
        var path = Path.Combine(_directory, "changed", KnownServersStore.FileName);
        var pins = new KnownServersStore(path);
        var old = ServerIdentity.FormatFingerprint(new byte[32]);
        pins.Trust(KnownServersStore.ServerKey("127.0.0.1", _port), old);

        var status = await ConnectRealClientAsync(pins);

        Assert.AreEqual(TCPClientStatusMessage.ErrorCode.SERVER_IDENTITY_CHANGED, status.Error);
        Assert.AreEqual(old, status.IdentityMismatch.PinnedFingerprint);
        Assert.IsTrue(ServerIdentity.FingerprintsEqual(_server.IdentityFingerprint,
            status.IdentityMismatch.PresentedFingerprint));
    }

    [TestMethod]
    public async Task PlainJson10ClientGetsVersionMismatch()
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, _port);
        var stream = tcp.GetStream();

        var hello = "{\"Client\":{\"ClientGuid\":\"OldClient_____________\",\"Name\":\"old\"},\"MsgType\":2," +
                    "\"Version\":\"1.0.0\",\"Product\":\"EasyRadioLink\"}\n";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(hello));

        var line = await new StreamReader(stream).ReadLineAsync().WaitAsync(Wait);
        var reply = NetworkMessage.Decode(line);

        Assert.AreEqual(NetworkMessage.MessageType.VERSION_MISMATCH, reply.MsgType);
        Assert.AreEqual("1.1.0", reply.Version);
        Assert.IsNull(reply.UdpKey);
    }

    [TestMethod]
    public async Task ClientWithoutEndToEndKeyIsRefused()
    {
        using var ssl = await TestClient.ConnectTlsAsync(_pins, null);
        var hello = new NetworkMessage
        {
            MsgType = NetworkMessage.MessageType.SYNC,
            Password = Password,
            Client = new ClientInfo { ClientGuid = "NoE2EKey".PadRight(22, '_'), Name = "old", RadioInfo = Tuned(Cb19, Modulation.AM) }
        };
        await ssl.WriteAsync(Encoding.UTF8.GetBytes(hello.Encode()));

        using var reader = new StreamReader(ssl);
        var reply = NetworkMessage.Decode(await reader.ReadLineAsync().WaitAsync(Wait));

        Assert.AreEqual(NetworkMessage.MessageType.VERSION_MISMATCH, reply.MsgType);
        Assert.IsNull(reply.UdpKey, "no UDP key for a refused client");

        string after;
        try
        {
            after = await reader.ReadLineAsync().WaitAsync(Wait);
        }
        catch (IOException)
        {
            after = null;
        }

        Assert.IsNull(after, "closed after VERSION_MISMATCH");
    }

    [TestMethod]
    public async Task ChangedServerIdentityIsRefusedBeforeAnythingIsSent()
    {
        var wrongPins = new KnownServersStore(Path.Combine(_directory, "wrong", KnownServersStore.FileName));
        wrongPins.Trust(KnownServersStore.ServerKey("127.0.0.1", _port), ServerIdentity.FormatFingerprint(new byte[32]));

        await Assert.ThrowsExactlyAsync<AuthenticationException>(() => TestClient.ConnectTlsAsync(wrongPins, null));
    }
}
