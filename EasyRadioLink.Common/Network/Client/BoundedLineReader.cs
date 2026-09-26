using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EasyRadioLink.Common.Network.Client;

/// <summary>A line longer than the reader's limit: the peer is broken or hostile - close the connection.</summary>
public sealed class LineTooLongException : IOException
{
    public LineTooLongException(int maxLineBytes) : base($"Received a line longer than {maxLineBytes / 1024} KB")
    {
    }
}

/// <summary>
///     Reads '\n' terminated UTF-8 lines (a trailing '\r' is removed) from a stream, like
///     <see cref="StreamReader.ReadLineAsync(CancellationToken)" /> - but never buffers more than
///     <see cref="MaxLineBytes" /> for one line: a longer line throws <see cref="LineTooLongException" /> instead of
///     growing without limit. The client reads the server's messages with it (the server has the same 512 KB cap for
///     the lines it receives). Not thread-safe; does not own the stream.
/// </summary>
public sealed class BoundedLineReader
{
    /// <summary>Default limit: 512 KB (a message is a few KB; the SYNC reply with 1000 clients well below this).</summary>
    public const int DefaultMaxLineBytes = 512 * 1024;

    // invalid bytes become U+FFFD, like StreamReader
    private static readonly UTF8Encoding Utf8 = new(false, false);

    private readonly byte[] _buffer;
    private readonly MemoryStream _line = new();
    private readonly Stream _stream;
    private int _end;
    private int _start;

    public BoundedLineReader(Stream stream, int maxLineBytes = DefaultMaxLineBytes, int bufferSize = 16 * 1024)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        if (maxLineBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxLineBytes));
        if (bufferSize < 1) throw new ArgumentOutOfRangeException(nameof(bufferSize));

        MaxLineBytes = maxLineBytes;
        _buffer = new byte[bufferSize];
    }

    public int MaxLineBytes { get; }

    /// <summary>The next line without its terminator; null at the end of the stream.</summary>
    public async ValueTask<string> ReadLineAsync(CancellationToken token = default)
    {
        while (true)
        {
            if (_start < _end)
            {
                var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
                if (newline >= 0)
                {
                    Append(_start, newline - _start);
                    _start = newline + 1;
                    return TakeLine();
                }

                Append(_start, _end - _start);
            }

            _start = 0;
            _end = await _stream.ReadAsync(_buffer.AsMemory(), token).ConfigureAwait(false);
            if (_end == 0) return _line.Length > 0 ? TakeLine() : null; // a last line without '\n', like StreamReader
        }
    }

    private void Append(int offset, int count)
    {
        if (count == 0) return;
        if (_line.Length + count > MaxLineBytes)
        {
            _line.SetLength(0);
            throw new LineTooLongException(MaxLineBytes);
        }

        _line.Write(_buffer, offset, count);
    }

    private string TakeLine()
    {
        var length = (int)_line.Length;
        var bytes = _line.GetBuffer();
        if (length > 0 && bytes[length - 1] == (byte)'\r') length--;

        var line = Utf8.GetString(bytes, 0, length);
        _line.SetLength(0);
        return line;
    }
}
