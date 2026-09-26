using System.Net;
using EasyRadioLink.Common.Network.Client;
using EasyRadioLink.Common.Network.Crypto;

namespace EasyRadioLink.Common.Models.EventMessages;

public class TCPClientStatusMessage
{
    public enum ErrorCode
    {
        MISMATCHED_SERVER, // not an EasyRadioLink server or unsupported protocol version
        TIMEOUT, // could not connect or the connection was lost
        INVALID_SERVER, // the server accepted the connection but did not complete the handshake (e.g. no TLS: a 1.0 server)
        USER_DISCONNECTED, // disconnect requested by the user - not an error
        AUTH_FAILED, // wrong or missing server password
        SERVER_IDENTITY_CHANGED, // the server presented another identity than the pinned one - see IdentityMismatch

        // the saved identity of this server in known-servers.json is unreadable (not a fingerprint): its identity is
        // unknown - IdentityMismatch has the presented fingerprint (PinnedFingerprint null); ask the user
        SERVER_IDENTITY_UNKNOWN,

        // the identity could not be checked at all (known-servers.json can't be read or is damaged, or the check
        // failed) - ErrorDetail says why; nothing may be trusted from here, the user has to repair the file
        IDENTITY_CHECK_FAILED
    }

    public TCPClientStatusMessage(bool connected)
    {
        Connected = connected;
    }

    public TCPClientStatusMessage(bool connected, IPEndPoint address)
    {
        Connected = connected;
        Address = address;
    }

    /// <summary>
    ///     Connected: the server accepted the handshake and sent this client's UDP key; <paramref name="voiceSession" /> is
    ///     the end-to-end voice encryption of this connection.
    /// </summary>
    public TCPClientStatusMessage(IPEndPoint address, UdpTransportKey udpKey, E2EVoiceSession voiceSession)
    {
        Connected = true;
        Address = address;
        UdpKey = udpKey;
        VoiceSession = voiceSession;
    }

    public TCPClientStatusMessage(bool connected, ErrorCode error, ServerIdentityMismatch identityMismatch = null,
        string errorDetail = null)
    {
        Error = error;
        Connected = connected;
        IdentityMismatch = identityMismatch;
        ErrorDetail = errorDetail;
    }

    public ErrorCode Error { get; }
    public IPEndPoint Address { get; }

    public bool Connected { get; }

    /// <summary>Connected: the key for the UDP voice traffic of this connection (from the SYNC reply).</summary>
    public UdpTransportKey UdpKey { get; }

    /// <summary>
    ///     Connected: end-to-end encryption of the voice of this connection (keys per transmission). Closed by the
    ///     connection when it ends.
    /// </summary>
    public E2EVoiceSession VoiceSession { get; }

    /// <summary>
    ///     <see cref="ErrorCode.SERVER_IDENTITY_CHANGED" />: the pinned and the presented identity;
    ///     <see cref="ErrorCode.SERVER_IDENTITY_UNKNOWN" />: the presented identity (no usable pin).
    /// </summary>
    public ServerIdentityMismatch IdentityMismatch { get; }

    /// <summary><see cref="ErrorCode.IDENTITY_CHECK_FAILED" />: why the identity could not be checked (for the user).</summary>
    public string ErrorDetail { get; }
}
