// Based on NetCoreServer (https://github.com/chronoxor/NetCoreServer)
// Copyright (c) 2019-2020 Ivan Shynkarenka - MIT License (see THIRD-PARTY-NOTICES.txt).
// TLS server on SslStream (replaces the plain TcpServer) - EasyRadioLink contributors.

using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace EasyRadioLink.Common.NetCoreServer;

/// <summary>
///     TLS server: accepts TCP connections and runs each one as an <see cref="SslSession" /> (TLS 1.2 or 1.3, server
///     certificate only, no client certificates, no renegotiation).
/// </summary>
/// <remarks>Thread-safe</remarks>
public class SslServer : IDisposable
{
    /// <summary>TLS versions offered to clients.</summary>
    public const SslProtocols Protocols = SslProtocols.Tls12 | SslProtocols.Tls13;

    // pause after an accept error (e.g. out of file descriptors) so the accept loop does not spin
    private static readonly TimeSpan AcceptErrorBackoff = TimeSpan.FromMilliseconds(100);

    // Server acceptor
    private Socket _acceptorSocket;

    // Server statistic
    internal long _bytesReceived;
    internal long _bytesSent;

    /// <summary>
    ///     Initialize the TLS server with a given IP endpoint and server certificate (with private key). The certificate
    ///     is not disposed by the server.
    /// </summary>
    public SslServer(IPEndPoint endpoint, X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(certificate);
        if (!certificate.HasPrivateKey)
            throw new ArgumentException("The server certificate needs its private key", nameof(certificate));

        Id = Guid.NewGuid();
        Endpoint = endpoint;
        Certificate = certificate;
        // built once and shared by every handshake; "offline": never fetch anything for the (self-signed) chain
        CertificateContext = SslStreamCertificateContext.Create(certificate, null, true);
    }

    /// <summary>Server Id</summary>
    public Guid Id { get; }

    /// <summary>IP endpoint (the actual one after <see cref="Start" />)</summary>
    public IPEndPoint Endpoint { get; private set; }

    /// <summary>The server certificate.</summary>
    public X509Certificate2 Certificate { get; }

    public SslStreamCertificateContext CertificateContext { get; }

    /// <summary>Number of sessions connected to the server</summary>
    public long ConnectedSessions => Sessions.Count;

    public long BytesSent => _bytesSent;
    public long BytesReceived => _bytesReceived;

    /// <summary>Option: acceptor backlog size</summary>
    public int OptionAcceptorBacklog { get; set; } = 1024;

    /// <summary>Option: dual mode socket (IPv4 and IPv6 when bound to an IPv6 address)</summary>
    public bool OptionDualMode { get; set; }

    /// <summary>Option: SO_KEEPALIVE</summary>
    public bool OptionKeepAlive { get; set; }

    /// <summary>Option: disable Nagle's algorithm</summary>
    public bool OptionNoDelay { get; set; }

    /// <summary>Option: SO_REUSEADDR</summary>
    public bool OptionReuseAddress { get; set; }

    /// <summary>Option: SO_EXCLUSIVEADDRUSE</summary>
    public bool OptionExclusiveAddressUse { get; set; }

    /// <summary>Option: receive buffer size of each session (one TLS record is at most 16 KB)</summary>
    public int OptionReceiveBufferSize { get; set; } = 16 * 1024;

    /// <summary>Option: the TLS handshake of a session must finish within this time</summary>
    public TimeSpan OptionHandshakeTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Is the server started?</summary>
    public bool IsStarted { get; private set; }

    /// <summary>Is the server accepting new clients?</summary>
    public bool IsAccepting { get; private set; }

    #region Session factory

    /// <summary>Create session factory method</summary>
    protected virtual SslSession CreateSession()
    {
        return new SslSession(this);
    }

    #endregion

    /// <summary>TLS options of one handshake (a new object per session, sharing <see cref="CertificateContext" />).</summary>
    internal SslServerAuthenticationOptions CreateAuthenticationOptions()
    {
        return new SslServerAuthenticationOptions
        {
            ServerCertificateContext = CertificateContext,
            EnabledSslProtocols = Protocols,
            ClientCertificateRequired = false,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            AllowRenegotiation = false,
            EncryptionPolicy = EncryptionPolicy.RequireEncryption
        };
    }

    #region Error handling

    private void SendError(SocketError error)
    {
        // Skip disconnect errors
        if (error == SocketError.ConnectionAborted ||
            error == SocketError.ConnectionRefused ||
            error == SocketError.ConnectionReset ||
            error == SocketError.OperationAborted ||
            error == SocketError.Shutdown)
            return;

        OnError(error);
    }

    #endregion

    #region Start/Stop server

    /// <summary>Start the server</summary>
    /// <returns>'true' if the server was successfully started, 'false' if it was already started</returns>
    public virtual bool Start()
    {
        if (IsStarted) return false;

        var acceptor = new Socket(Endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            acceptor.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, OptionReuseAddress);
            acceptor.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse,
                OptionExclusiveAddressUse);
            // must be applied before listening
            if (acceptor.AddressFamily == AddressFamily.InterNetworkV6) acceptor.DualMode = OptionDualMode;

            acceptor.Bind(Endpoint);
            Endpoint = (IPEndPoint)acceptor.LocalEndPoint;
            acceptor.Listen(OptionAcceptorBacklog);
        }
        catch
        {
            acceptor.Dispose();
            throw;
        }

        _acceptorSocket = acceptor;
        _bytesSent = 0;
        _bytesReceived = 0;

        IsStarted = true;
        OnStarted();

        IsAccepting = true;
        _ = AcceptLoopAsync(acceptor);

        return true;
    }

    /// <summary>Stop the server (closes the acceptor and every session)</summary>
    /// <returns>'true' if the server was successfully stopped, 'false' if it was not started</returns>
    public virtual bool Stop()
    {
        if (!IsStarted) return false;

        IsAccepting = false;

        try
        {
            _acceptorSocket?.Close();
        }
        catch (Exception)
        {
            // ignored
        }

        _acceptorSocket = null;

        DisconnectAll();

        IsStarted = false;
        OnStopped();

        return true;
    }

    #endregion

    #region Accepting clients

    private async Task AcceptLoopAsync(Socket acceptor)
    {
        while (IsAccepting)
        {
            Socket socket;
            try
            {
                socket = await acceptor.AcceptAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                if (!IsAccepting) break;

                SendError(ex.SocketErrorCode);
                await Task.Delay(AcceptErrorBackoff).ConfigureAwait(false);
                continue;
            }

            if (!IsAccepting)
            {
                socket.Dispose();
                break;
            }

            SslSession session = null;
            try
            {
                session = CreateSession();
                RegisterSession(session);
                session.Connect(socket);
            }
            catch (Exception)
            {
                // a broken connection (e.g. reset right away) must not stop the accept loop
                if (session != null)
                {
                    session.Disconnect();
                    UnregisterSession(session.Id);
                }

                socket.Dispose();
            }
        }
    }

    #endregion

    #region Session management

    // Server sessions
    protected readonly ConcurrentDictionary<Guid, SslSession> Sessions = new();

    /// <summary>Disconnect all connected sessions</summary>
    public virtual bool DisconnectAll()
    {
        if (!IsStarted) return false;

        foreach (var session in Sessions.Values) session.Disconnect();

        return true;
    }

    /// <summary>Find a session with a given Id (null if it is not connected)</summary>
    public SslSession FindSession(Guid id)
    {
        return Sessions.TryGetValue(id, out var result) ? result : null;
    }

    internal void RegisterSession(SslSession session)
    {
        Sessions.TryAdd(session.Id, session);
    }

    internal void UnregisterSession(Guid id)
    {
        Sessions.TryRemove(id, out _);
    }

    #endregion

    #region Multicasting

    /// <summary>Multicast data to all connected sessions (queued per session, TLS encrypted)</summary>
    public virtual bool Multicast(byte[] buffer)
    {
        if (!IsStarted) return false;
        if (buffer.Length == 0) return true;

        foreach (var session in Sessions.Values) session.SendAsync(buffer);

        return true;
    }

    /// <summary>Multicast text (UTF-8) to all connected sessions</summary>
    public virtual bool Multicast(string text)
    {
        return Multicast(Encoding.UTF8.GetBytes(text));
    }

    #endregion

    #region Server handlers

    /// <summary>Handle server started notification</summary>
    protected virtual void OnStarted()
    {
    }

    /// <summary>Handle server stopped notification</summary>
    protected virtual void OnStopped()
    {
    }

    /// <summary>Handle session connected notification (before the TLS handshake)</summary>
    protected virtual void OnConnected(SslSession session)
    {
    }

    /// <summary>Handle session disconnected notification</summary>
    protected virtual void OnDisconnected(SslSession session)
    {
    }

    /// <summary>Handle error notification</summary>
    protected virtual void OnError(SocketError error)
    {
    }

    internal void OnConnectedInternal(SslSession session)
    {
        OnConnected(session);
    }

    internal void OnDisconnectedInternal(SslSession session)
    {
        OnDisconnected(session);
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

        if (disposingManagedResources) Stop();

        IsDisposed = true;
    }

    #endregion
}
