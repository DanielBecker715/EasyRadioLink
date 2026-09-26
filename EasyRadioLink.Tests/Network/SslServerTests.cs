using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EasyRadioLink.Common.NetCoreServer;
using EasyRadioLink.Common.Network.Crypto;
using EasyRadioLink.Common.Network.Server;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EasyRadioLink.Common.Tests.Network;

/// <summary>The TLS layer of the vendored NetCoreServer (SslServer/SslSession) on loopback.</summary>
[TestClass]
public class SslServerTests
{
    private static X509Certificate2 _identity;

    [ClassInitialize]
    public static void CreateIdentity(TestContext _)
    {
        _identity = X509CertificateLoader.LoadPkcs12(ServerIdentity.CreatePkcs12(), null);
    }

    [ClassCleanup]
    public static void DisposeIdentity()
    {
        _identity?.Dispose();
    }

    private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    private static async Task<SslStream> ConnectTlsAsync(TestServer server, SslProtocols protocols = SslServer.Protocols)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Endpoint.Port);

        string presented = null;
        var ssl = new SslStream(client.GetStream(), false);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            EnabledSslProtocols = protocols,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            {
                presented = ServerIdentity.GetFingerprint(certificate);
                return true;
            }
        }, Timeout());

        // exactly the identity the server was started with
        Assert.AreEqual(ServerIdentity.GetFingerprint(_identity), presented);
        return ssl;
    }

    private static async Task<string> ReadLineAsync(Stream stream)
    {
        var line = new StringBuilder();
        var buffer = new byte[1];
        while (await stream.ReadAsync(buffer, Timeout()) == 1)
        {
            if (buffer[0] == '\n') return line.ToString();
            line.Append((char)buffer[0]);
        }

        return line.Length == 0 ? null : line.ToString();
    }

    private static async Task<int> ReadToEndAsync(Stream stream)
    {
        var buffer = new byte[256];
        var total = 0;
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, Timeout())) > 0) total += read;
        }
        catch (IOException)
        {
            // reset instead of a clean close - also "closed"
        }

        return total;
    }

    [TestMethod]
    public async Task TlsSessionEchoesAndClosesAfterTheLastMessage()
    {
        using var server = new TestServer();
        server.Start();

        using var ssl = await ConnectTlsAsync(server);
        Assert.IsTrue(ssl.SslProtocol is SslProtocols.Tls12 or SslProtocols.Tls13);

        // queued before the handshake completed, delivered first
        Assert.AreEqual("early", await ReadLineAsync(ssl));
        Assert.AreEqual("hello", await ReadLineAsync(ssl));

        await ssl.WriteAsync("ping\n"u8.ToArray(), Timeout());
        Assert.AreEqual("ping", await ReadLineAsync(ssl));

        await ssl.WriteAsync("bye\n"u8.ToArray(), Timeout());
        Assert.AreEqual("goodbye", await ReadLineAsync(ssl));
        Assert.AreEqual(0, await ReadToEndAsync(ssl), "closed right after the last message");
    }

    [TestMethod]
    public async Task Tls12ClientsAreAccepted()
    {
        using var server = new TestServer();
        server.Start();

        using var ssl = await ConnectTlsAsync(server, SslProtocols.Tls12);
        Assert.AreEqual(SslProtocols.Tls12, ssl.SslProtocol);
        Assert.AreEqual("early", await ReadLineAsync(ssl));
    }

    [TestMethod]
    public async Task PlainJsonClientGetsAPlainAnswerAndIsClosed()
    {
        using var server = new TestServer();
        server.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Endpoint.Port);
        var stream = client.GetStream();

        // what an EasyRadioLink 1.0 client sends first
        await stream.WriteAsync("{\"MsgType\":2,\"Version\":\"1.0.0\"}\n"u8.ToArray(), Timeout());

        Assert.AreEqual("plain", await ReadLineAsync(stream));
        Assert.AreEqual(0, await ReadToEndAsync(stream));
        Assert.AreEqual(1, server.PlainTextClients);
    }

    [TestMethod]
    public async Task OtherNonTlsDataIsClosedWithoutAnswer()
    {
        using var server = new TestServer();
        server.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Endpoint.Port);
        var stream = client.GetStream();

        await stream.WriteAsync("GET / HTTP/1.1\r\n\r\n"u8.ToArray(), Timeout());
        Assert.AreEqual(0, await ReadToEndAsync(stream));
    }

    [TestMethod]
    public async Task SessionsAreRegisteredAndUnregistered()
    {
        using var server = new TestServer();
        server.Start();

        using (var ssl = await ConnectTlsAsync(server))
        {
            Assert.AreEqual("early", await ReadLineAsync(ssl));
            Assert.AreEqual(1, server.ConnectedSessions);
        }

        for (var i = 0; i < 100 && server.ConnectedSessions > 0; i++) await Task.Delay(20);
        Assert.AreEqual(0, server.ConnectedSessions);

        server.Stop();
        Assert.IsFalse(server.IsStarted);
    }

    [TestMethod]
    public void ConnectionRateIsLimitedPerAddress()
    {
        var limiter = new ConnectionRateLimiter();
        var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var attacker = IPAddress.Parse("203.0.113.7");

        for (var i = 0; i < ConnectionRateLimiter.Burst; i++)
        {
            Assert.IsTrue(limiter.TryAcquire(attacker, start, out var log), $"connection {i + 1}");
            Assert.IsFalse(log);
        }

        Assert.IsFalse(limiter.TryAcquire(attacker, start, out var firstRefusal));
        Assert.IsTrue(firstRefusal, "the first refusal is logged");
        Assert.IsFalse(limiter.TryAcquire(attacker.MapToIPv6(), start, out var secondRefusal));
        Assert.IsFalse(secondRefusal, "further refusals are not logged (no log flood)");

        // other addresses are not affected
        Assert.IsTrue(limiter.TryAcquire(IPAddress.Parse("203.0.113.8"), start, out _));

        // one new connection per refill interval afterwards
        var later = start + ConnectionRateLimiter.RefillInterval;
        Assert.IsTrue(limiter.TryAcquire(attacker, later, out _));
        Assert.IsFalse(limiter.TryAcquire(attacker, later, out _));

        // the burst comes back after a quiet period
        var muchLater = start + ConnectionRateLimiter.RefillInterval * (ConnectionRateLimiter.Burst + 5);
        for (var i = 0; i < ConnectionRateLimiter.Burst; i++) Assert.IsTrue(limiter.TryAcquire(attacker, muchLater, out _));
        Assert.IsFalse(limiter.TryAcquire(attacker, muchLater, out _));

        Assert.IsFalse(limiter.TryAcquire(null, start, out _));
    }

    [TestMethod]
    public void ServerClientIdsAreShortGuids()
    {
        Assert.IsTrue(ServerSync.IsValidClientGuid("ufYS_WlLVkmFPjqCgxz6GA"));
        Assert.IsTrue(ServerSync.IsValidClientGuid("abcdefghij-_0123456789"));
        Assert.IsFalse(ServerSync.IsValidClientGuid("ufYS_WlLVkmFPjqCgxz6G"));
        Assert.IsFalse(ServerSync.IsValidClientGuid("ufYS_WlLVkmFPjqCgxz6G?"));
        Assert.IsFalse(ServerSync.IsValidClientGuid("ufYS_WlLVkmFPjqCgxz6Gä"));
        Assert.IsFalse(ServerSync.IsValidClientGuid(null));
    }

    private sealed class TestServer : SslServer
    {
        private int _plainTextClients;

        public TestServer() : base(new IPEndPoint(IPAddress.Loopback, 0), _identity)
        {
        }

        public int PlainTextClients => Volatile.Read(ref _plainTextClients);

        protected override SslSession CreateSession()
        {
            return new TestSession(this);
        }

        internal void CountPlainTextClient()
        {
            Interlocked.Increment(ref _plainTextClients);
        }
    }

    private sealed class TestSession : SslSession
    {
        public TestSession(TestServer server) : base(server)
        {
        }

        protected override void OnConnected()
        {
            // before the handshake: must be queued and sent first once TLS is up
            SendAsync("early\n"u8.ToArray());
        }

        protected override void OnHandshaked()
        {
            SendAsync("hello\n"u8.ToArray());
        }

        protected override void OnReceived(byte[] buffer, long offset, long size)
        {
            var text = Encoding.ASCII.GetString(buffer, (int)offset, (int)size);
            if (text.StartsWith("bye", StringComparison.Ordinal))
                DisconnectAfterSend("goodbye\n"u8.ToArray());
            else
                SendAsync(buffer, offset, size);
        }

        protected override Task OnNonTlsDataAsync(byte firstByte, CancellationToken token)
        {
            if (firstByte != (byte)'{') return Task.CompletedTask;

            ((TestServer)Server).CountPlainTextClient();
            return ReplyInPlainTextAsync("plain\n"u8.ToArray(), token);
        }
    }
}
