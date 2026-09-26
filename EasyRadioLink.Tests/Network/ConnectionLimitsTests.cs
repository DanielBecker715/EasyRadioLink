using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EasyRadioLink.Common.Network.Client;
using EasyRadioLink.Common.Network.Server;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EasyRadioLink.Common.Tests.Network;

/// <summary>Rate limits and log summaries of the connections, and the client's line length limit.</summary>
[TestClass]
public class ConnectionLimitsTests
{
    private static readonly DateTime T0 = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void VoiceKeysAllowABurstAfterAStallThenTheirRate()
    {
        var bucket = new TokenBucket(RadioClientSession.VoiceKeyBurst, RadioClientSession.MaxVoiceKeysPerSecond);

        // messages that queued up during a TCP stall arrive at once: a burst of 20 is fine
        for (var i = 0; i < RadioClientSession.VoiceKeyBurst; i++) Assert.IsTrue(bucket.TryTake(T0), $"message {i + 1}");
        Assert.IsFalse(bucket.TryTake(T0), "then the bucket is empty");

        // refilled with 10 per second
        Assert.IsFalse(bucket.TryTake(T0.AddMilliseconds(50)));
        Assert.IsTrue(bucket.TryTake(T0.AddMilliseconds(110)));
        Assert.IsFalse(bucket.TryTake(T0.AddMilliseconds(120)));

        var accepted = 0;
        for (var ms = 200; ms <= 10_000; ms += 10)
            if (bucket.TryTake(T0.AddMilliseconds(ms)))
                accepted++;
        Assert.IsInRange(97, 100, accepted, "10 per second over ~10 s");

        // never more than the burst, however long it was quiet
        var later = T0.AddHours(1);
        accepted = Enumerable.Range(0, 100).Count(_ => bucket.TryTake(later));
        Assert.AreEqual(RadioClientSession.VoiceKeyBurst, accepted);

        // a clock that jumps back refills nothing
        Assert.IsFalse(bucket.TryTake(T0));
    }

    [TestMethod]
    public void RefusedConnectionsAreSummarisedOncePerInterval()
    {
        var summary = new ConnectionLogSummary(T0);

        for (var i = 0; i < 12; i++) summary.Record(RefusedConnectionReason.NotTls);
        summary.Record(RefusedConnectionReason.HandshakeTimeout);
        summary.Record(RefusedConnectionReason.ConnectionLimit);

        Assert.IsNull(summary.TakeSummary(T0.AddSeconds(30), out _), "not due yet");

        var text = summary.TakeSummary(T0 + ConnectionLogSummary.Interval, out var important);
        Assert.IsNotNull(text);
        Assert.IsFalse(important);
        StringAssert.Contains(text, "12 closed - not TLS");
        StringAssert.Contains(text, "1 closed - no TLS handshake and login within 15 s");
        StringAssert.Contains(text, "1 refused - too many connections");

        // counted afresh; nothing counted = nothing logged
        Assert.IsNull(summary.TakeSummary(T0 + 2 * ConnectionLogSummary.Interval, out _));

        // a full server is worth a warning
        summary.Record(RefusedConnectionReason.ServerFull);
        text = summary.TakeSummary(T0.AddMinutes(2.5), out important, true);
        Assert.IsTrue(important);
        StringAssert.Contains(text, "server full");
    }

    // --- client line limit ------------------------------------------------------------------------------------------

    /// <summary>A stream that returns its data in small pieces (like TLS records).</summary>
    private sealed class ChunkedStream : Stream
    {
        private readonly int _chunk;
        private readonly MemoryStream _data;

        public ChunkedStream(byte[] data, int chunk)
        {
            _data = new MemoryStream(data);
            _chunk = chunk;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return _data.Read(buffer, offset, Math.Min(count, _chunk));
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    [DataRow(4096)]
    public async Task LinesAreReadLikeStreamReaderDoes(int chunk)
    {
        var text = "{\"a\":1}\n\n{\"b\":\"äöü – 📻\"}\r\nlast line without newline";
        var reader = new BoundedLineReader(new ChunkedStream(Encoding.UTF8.GetBytes(text), chunk), 64, 5);

        Assert.AreEqual("{\"a\":1}", await reader.ReadLineAsync());
        Assert.AreEqual("", await reader.ReadLineAsync());
        Assert.AreEqual("{\"b\":\"äöü – 📻\"}", await reader.ReadLineAsync(), "UTF-8 split across reads");
        Assert.AreEqual("last line without newline", await reader.ReadLineAsync());
        Assert.IsNull(await reader.ReadLineAsync());
        Assert.IsNull(await reader.ReadLineAsync());
    }

    [TestMethod]
    public async Task ALineLongerThanTheLimitClosesTheConnection()
    {
        var exactly = new string('x', 100);
        var data = Encoding.ASCII.GetBytes(exactly + "\n" + new string('y', 101) + "\nnot reached\n");
        var reader = new BoundedLineReader(new ChunkedStream(data, 7), 100, 16);

        Assert.AreEqual(exactly, await reader.ReadLineAsync(), "exactly the limit is fine");
        await Assert.ThrowsExactlyAsync<LineTooLongException>(async () => await reader.ReadLineAsync());

        // the default is the server's limit: 512 KB
        Assert.AreEqual(512 * 1024, new BoundedLineReader(Stream.Null).MaxLineBytes);
    }

    [TestMethod]
    public async Task AnEndlessLineIsRefusedWithoutBufferingIt()
    {
        // a hostile server sends data without a line break forever
        var endless = new EndlessStream();
        var reader = new BoundedLineReader(endless);

        await Assert.ThrowsExactlyAsync<LineTooLongException>(async () => await reader.ReadLineAsync());
        Assert.IsLessThan((long)BoundedLineReader.DefaultMaxLineBytes + 64 * 1024, endless.BytesRead);
    }

    private sealed class EndlessStream : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            buffer.AsSpan(offset, count).Fill((byte)'{');
            BytesRead += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            return Task.FromResult(Read(buffer, offset, count));
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
