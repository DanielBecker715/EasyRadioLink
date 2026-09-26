// Based on NetCoreServer (https://github.com/chronoxor/NetCoreServer)
// Copyright (c) 2019-2020 Ivan Shynkarenka - MIT License (see THIRD-PARTY-NOTICES.txt).
// TLS session on SslStream (replaces the plain TcpSession) - EasyRadioLink contributors.

using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EasyRadioLink.Common.NetCoreServer;

/// <summary>
///     One TLS connection of an <see cref="SslServer" />.
///     <para>
///         Life cycle: <see cref="OnConnected" /> (TCP accepted - may refuse with <see cref="Disconnect" />) -> the first
///         byte is peeked: a TLS handshake record starts the TLS handshake, anything else goes to
///         <see cref="OnNonTlsDataAsync" /> (e.g. to answer an old plain text client) -> <see cref="OnHandshaked" /> ->
///         <see cref="OnReceived" /> for every decrypted chunk (one reader, called sequentially) -> <see cref="OnDisconnected" />.
///     </para>
///     <para>
///         Sending is queued (<see cref="SendAsync(byte[])" />): one send loop writes the queued bytes in order, data
///         queued before the handshake is written after it. <see cref="DisconnectAfterSend" /> sends a last message and
///         then closes the connection.
///     </para>
/// </summary>
/// <remarks>Thread-safe</remarks>
public class SslSession : IDisposable
{
    // first byte of a TLS record carrying a handshake message (ClientHello)
    private const byte TlsHandshakeRecordType = 0x16;

    // a plain text client gets its answer within this time, then the connection is closed anyway
    private static readonly TimeSpan PlainTextReplyTimeout = TimeSpan.FromSeconds(5);
    private const int MaxPlainTextBytes = 64 * 1024;

    // DisconnectAfterSend: close even if the peer does not read its last message
    private static readonly TimeSpan DisconnectAfterSendTimeout = TimeSpan.FromSeconds(5);

    private readonly object _sendLock = new();
    private int _connected;
    private CancellationTokenSource _cts;
    private bool _disconnectWhenSent;
    private bool _handshaked;
    private Buffer _sendBufferFlush = new();
    private Buffer _sendBufferMain = new();
    private bool _sending;
    private SslStream _sslStream;

    /// <summary>Initialize the session with a given server</summary>
    public SslSession(SslServer server)
    {
        Id = Guid.NewGuid();
        Server = server;
        OptionReceiveBufferSize = server.OptionReceiveBufferSize;
    }

    /// <summary>Session Id</summary>
    public Guid Id { get; }

    /// <summary>Server</summary>
    public SslServer Server { get; }

    /// <summary>Socket</summary>
    public Socket Socket { get; private set; }

    /// <summary>Is the session connected?</summary>
    public bool IsConnected => Volatile.Read(ref _connected) == 1;

    /// <summary>Did the TLS handshake complete?</summary>
    public bool IsHandshaked
    {
        get
        {
            lock (_sendLock)
            {
                return _handshaked;
            }
        }
    }

    /// <summary>Negotiated TLS version (None before the handshake).</summary>
    public SslProtocols SslProtocol { get; private set; }

    /// <summary>Number of bytes queued and not yet handed to the send loop</summary>
    public long BytesPending { get; private set; }

    /// <summary>Number of bytes being sent</summary>
    public long BytesSending { get; private set; }

    public long BytesSent { get; private set; }
    public long BytesReceived { get; private set; }

    /// <summary>Option: receive buffer size</summary>
    public int OptionReceiveBufferSize { get; set; }

    /// <summary>
    ///     Option: close the session when more than this many bytes wait to be sent (a peer that does not read).
    ///     0 = no limit.
    /// </summary>
    public long OptionSendBufferLimit { get; set; }

    #region Connect/Disconnect session

    /// <summary>Connect the session (called by the server's accept loop)</summary>
    internal void Connect(Socket socket)
    {
        Socket = socket;
        _cts = new CancellationTokenSource();

        if (Server.OptionKeepAlive) socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        if (Server.OptionNoDelay) socket.NoDelay = true;

        BytesPending = 0;
        BytesSending = 0;
        BytesSent = 0;
        BytesReceived = 0;

        Volatile.Write(ref _connected, 1);

        // may refuse the connection (ban list, connection limits) by calling Disconnect()
        OnConnected();
        if (!IsConnected) return;

        Server.OnConnectedInternal(this);
        if (!IsConnected) return;

        _ = RunAsync(_cts.Token);
    }

    /// <summary>Disconnect the session (idempotent, any thread)</summary>
    /// <returns>'true' if the session was disconnected by this call, 'false' if it was already disconnected</returns>
    public virtual bool Disconnect()
    {
        if (Interlocked.Exchange(ref _connected, 0) == 0) return false;

        try
        {
            _cts?.Cancel();
        }
        catch (Exception)
        {
            // ignored
        }

        try
        {
            Socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception)
        {
            // ignored
        }

        try
        {
            Socket.Close();
        }
        catch (Exception)
        {
            // ignored
        }

        DisposeStream();

        lock (_sendLock)
        {
            _sendBufferMain.Clear();
            _sendBufferFlush.Clear();
            BytesPending = 0;
            BytesSending = 0;
        }

        OnDisconnected();

        Server.OnDisconnectedInternal(this);
        Server.UnregisterSession(Id);

        return true;
    }

    private void DisposeStream()
    {
        try
        {
            Interlocked.Exchange(ref _sslStream, null)?.Dispose();
        }
        catch (Exception)
        {
            // ignored
        }
    }

    #endregion

    #region Receive loop

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            // peek, so the TLS stack still sees the whole ClientHello
            var first = new byte[1];
            var peeked = await Socket.ReceiveAsync(first, SocketFlags.Peek, token).ConfigureAwait(false);
            if (peeked <= 0) return;

            if (first[0] != TlsHandshakeRecordType)
            {
                await OnNonTlsDataAsync(first[0], token).ConfigureAwait(false);
                return;
            }

            var sslStream = new SslStream(new NetworkStream(Socket, false), false);
            Interlocked.Exchange(ref _sslStream, sslStream);
            if (!IsConnected)
            {
                // Disconnect() ran before the stream existed
                DisposeStream();
                return;
            }

            using (var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                handshakeTimeout.CancelAfter(Server.OptionHandshakeTimeout);
                await sslStream.AuthenticateAsServerAsync(Server.CreateAuthenticationOptions(), handshakeTimeout.Token)
                    .ConfigureAwait(false);
            }

            SslProtocol = sslStream.SslProtocol;

            bool startSending;
            lock (_sendLock)
            {
                _handshaked = true;
                startSending = TryClaimSendLoop();
            }

            OnHandshaked();

            if (startSending) _ = SendLoopAsync();

            var buffer = new byte[Math.Max(OptionReceiveBufferSize, 1024)];
            while (IsConnected)
            {
                var read = await sslStream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read <= 0) break;

                BytesReceived += read;
                Interlocked.Add(ref Server._bytesReceived, read);

                OnReceived(buffer, 0, read);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException
                                       or ObjectDisposedException)
        {
            // closed by the peer, timed out or closed by Disconnect()
        }
        catch (AuthenticationException ex)
        {
            OnHandshakeFailed(ex);
        }
        catch (Exception ex)
        {
            OnSessionException(ex);
        }
        finally
        {
            Disconnect();
            DisposeStream();
        }
    }

    /// <summary>
    ///     For a client that does not speak TLS: reads (and discards) its first line, answers <paramref name="reply" />
    ///     in plain text and waits briefly until the client closed the connection (bounded in time and size). Reading
    ///     the request first matters: closing a socket with unread data resets the connection, and the client would lose
    ///     the reply.
    /// </summary>
    protected async Task ReplyInPlainTextAsync(byte[] reply, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(PlainTextReplyTimeout);

        var buffer = new byte[4096];
        var total = 0;

        while (total < MaxPlainTextBytes)
        {
            var read = await Socket.ReceiveAsync(buffer, SocketFlags.None, timeout.Token).ConfigureAwait(false);
            if (read <= 0) return;

            total += read;
            if (Array.IndexOf(buffer, (byte)'\n', 0, read) >= 0) break;
        }

        var sent = 0;
        while (sent < reply.Length)
            sent += await Socket.SendAsync(reply.AsMemory(sent), SocketFlags.None, timeout.Token).ConfigureAwait(false);

        Socket.Shutdown(SocketShutdown.Send);

        total = 0;
        while (total < MaxPlainTextBytes)
        {
            var read = await Socket.ReceiveAsync(buffer, SocketFlags.None, timeout.Token).ConfigureAwait(false);
            if (read <= 0) break;

            total += read;
        }
    }

    #endregion

    #region Send data

    /// <summary>Queue data to send (asynchronous, in order)</summary>
    /// <returns>'true' if the data was queued, 'false' if the session is not connected or closing</returns>
    public virtual bool SendAsync(byte[] buffer)
    {
        return SendAsync(buffer, 0, buffer.Length);
    }

    /// <summary>Queue text (UTF-8) to send</summary>
    public virtual bool SendAsync(string text)
    {
        return SendAsync(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Queue a buffer fragment to send (asynchronous, in order)</summary>
    public virtual bool SendAsync(byte[] buffer, long offset, long size)
    {
        if (!IsConnected) return false;
        if (size == 0) return true;

        bool overflow;
        bool start;
        long queued;
        lock (_sendLock)
        {
            // closing: nothing may follow the last message
            if (_disconnectWhenSent) return false;

            _sendBufferMain.Append(buffer, offset, size);
            BytesPending = _sendBufferMain.Size;
            queued = BytesPending + BytesSending;

            overflow = OptionSendBufferLimit > 0 && queued > OptionSendBufferLimit;
            start = !overflow && TryClaimSendLoop();
        }

        if (overflow)
        {
            OnSendBufferOverflow(queued);
            Disconnect();
            return false;
        }

        if (start) _ = SendLoopAsync();

        return true;
    }

    /// <summary>
    ///     Sends <paramref name="lastMessage" /> (may be null) after everything queued before and then closes the
    ///     connection - at the latest after a few seconds, even if the peer does not read. Before the TLS handshake
    ///     nothing can be sent: the connection is closed at once.
    /// </summary>
    public void DisconnectAfterSend(byte[] lastMessage)
    {
        if (!IsConnected) return;

        var start = false;
        var disconnectNow = false;
        lock (_sendLock)
        {
            if (_disconnectWhenSent) return;

            _disconnectWhenSent = true;

            if (!_handshaked)
            {
                disconnectNow = true;
            }
            else
            {
                if (lastMessage is { Length: > 0 })
                {
                    _sendBufferMain.Append(lastMessage, 0, lastMessage.Length);
                    BytesPending = _sendBufferMain.Size;
                }

                if (!_sending)
                {
                    if (_sendBufferMain.IsEmpty) disconnectNow = true;
                    else start = _sending = true;
                }
            }
        }

        if (disconnectNow)
        {
            Disconnect();
            return;
        }

        if (start) _ = SendLoopAsync();

        _ = Task.Delay(DisconnectAfterSendTimeout).ContinueWith(_ => Disconnect(), TaskScheduler.Default);
    }

    // under _sendLock: true if the caller must start the send loop
    private bool TryClaimSendLoop()
    {
        if (_sending || !_handshaked || _sendBufferMain.IsEmpty) return false;

        _sending = true;
        return true;
    }

    private async Task SendLoopAsync()
    {
        var disconnect = false;
        try
        {
            while (true)
            {
                Buffer flush;
                lock (_sendLock)
                {
                    if (!IsConnected)
                    {
                        _sending = false;
                        return;
                    }

                    if (_sendBufferMain.IsEmpty)
                    {
                        _sending = false;
                        disconnect = _disconnectWhenSent;
                        break;
                    }

                    (_sendBufferMain, _sendBufferFlush) = (_sendBufferFlush, _sendBufferMain);
                    flush = _sendBufferFlush;
                    BytesSending = flush.Size;
                    BytesPending = 0;
                }

                var sslStream = Volatile.Read(ref _sslStream) ?? throw new ObjectDisposedException(nameof(SslStream));
                await sslStream.WriteAsync(flush.Data.AsMemory(0, (int)flush.Size), _cts.Token).ConfigureAwait(false);

                long sent;
                long pending;
                lock (_sendLock)
                {
                    sent = flush.Size;
                    flush.Clear();
                    BytesSending = 0;
                    BytesSent += sent;
                    pending = BytesPending;
                }

                Interlocked.Add(ref Server._bytesSent, sent);
                OnSent(sent, pending);
            }
        }
        catch (Exception)
        {
            // a failed write leaves the TLS stream unusable
            lock (_sendLock)
            {
                _sending = false;
            }

            disconnect = true;
        }

        if (disconnect) Disconnect();
    }

    #endregion

    #region Session handlers

    /// <summary>TCP connection accepted (before TLS). Call <see cref="Disconnect" /> to refuse it.</summary>
    protected virtual void OnConnected()
    {
    }

    /// <summary>The TLS handshake completed.</summary>
    protected virtual void OnHandshaked()
    {
    }

    /// <summary>The TLS handshake failed (not a TLS client, no common protocol/cipher, ...).</summary>
    protected virtual void OnHandshakeFailed(AuthenticationException exception)
    {
    }

    /// <summary>
    ///     The client's first byte does not start a TLS handshake. The connection is closed when the returned task
    ///     completes; <see cref="ReplyInPlainTextAsync" /> can answer first.
    /// </summary>
    protected virtual Task OnNonTlsDataAsync(byte firstByte, CancellationToken token)
    {
        return Task.CompletedTask;
    }

    /// <summary>Client disconnected (called once)</summary>
    protected virtual void OnDisconnected()
    {
    }

    /// <summary>Decrypted data received (sequential calls from the session's receive loop)</summary>
    protected virtual void OnReceived(byte[] buffer, long offset, long size)
    {
    }

    /// <summary>A chunk was written (<paramref name="pending" /> bytes wait)</summary>
    protected virtual void OnSent(long sent, long pending)
    {
    }

    /// <summary>More than <see cref="OptionSendBufferLimit" /> bytes were queued - the session is closed.</summary>
    protected virtual void OnSendBufferOverflow(long queued)
    {
    }

    /// <summary>Unexpected exception in the receive loop (the session is closed)</summary>
    protected virtual void OnSessionException(Exception exception)
    {
    }

    #endregion

    #region IDisposable implementation

    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposingManagedResources)
    {
        if (IsDisposed) return;

        if (disposingManagedResources) Disconnect();

        IsDisposed = true;
    }

    #endregion
}
