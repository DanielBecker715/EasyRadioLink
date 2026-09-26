using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using EasyRadioLink.Common.Models;
using EasyRadioLink.Common.Models.EventMessages;
using EasyRadioLink.Common.Network.Crypto;
using EasyRadioLink.Common.Network.Singletons;
using NLog;

namespace EasyRadioLink.Common.Network.Client;

/// <summary>
///     UDP voice connection of the client. Every datagram is encrypted with this connection's key from the SYNC reply
///     (<see cref="UdpTransportSession" />, <see cref="UdpDatagram" /> layout): voice packets and the ping every 15 s
///     are sealed right before they are sent; received datagrams are only used if they authenticate and pass the replay
///     window - anything else is dropped (and does not count as a sign of life). <see cref="Ready" /> becomes true with
///     the first authenticated pong.
/// </summary>
public class UDPVoiceHandler
{
    private static readonly TimeSpan UDP_VOIP_TIMEOUT = TimeSpan.FromSeconds(42); // seconds for timeout before redoing VoIP

    // pause after a socket error (e.g. network unreachable while the Wi-Fi reconnects) before trying again
    private static readonly TimeSpan ErrorBackoff = TimeSpan.FromSeconds(1);

    // WSAIoctl SIO_UDP_CONNRESET (Windows only)
    private const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    // FIFO so voice packets leave in the order they were recorded (plain bodies - sealed when they are sent)
    private readonly ConcurrentQueue<byte[]> _outgoing = new ConcurrentQueue<byte[]>();
    private readonly byte[] _guidAsciiBytes;
    private CancellationTokenSource _stopRequest;
    private readonly IPEndPoint _serverEndpoint;
    private readonly UdpTransportSession _transport;
    private bool _started;
    private SemaphoreSlim _outgoingSemaphore = new SemaphoreSlim(0);

    /// <param name="guid">The client's 22 character id (owner of the key).</param>
    /// <param name="endPoint">The server's voice endpoint (same as TCP).</param>
    /// <param name="udpKey">This connection's UDP key from the SYNC reply (copied - the caller may clear it).</param>
    public UDPVoiceHandler(string guid, IPEndPoint endPoint, UdpTransportKey udpKey)
    {
        ArgumentNullException.ThrowIfNull(udpKey);

        _guidAsciiBytes = System.Text.Encoding.ASCII.GetBytes(guid);
        _transport = new UdpTransportSession(guid, udpKey.Key, udpKey.KeyId, false);

        _serverEndpoint = endPoint;
    }

    /// <summary>Decrypted voice packets (encoded <see cref="UDPVoicePacket" />s) received from the server.</summary>
    public BlockingCollection<byte[]> EncodedAudio { get; } = new();


    public bool Ready
    {
        get => field;
        private set
        {
            if (Interlocked.CompareExchange(ref field, value, !value) != value)
            {
                EventBus.Instance.PublishOnUIThreadAsync(new VOIPStatusMessage(field));
            }
        }
    }

    public void Connect()
    {
        if (!Interlocked.CompareExchange(ref _started, true, false))
        {
            new Thread(StartUDP).Start();
        }
    }

    private UdpClient SetupListener()
    {
        Ready = false;
        var listener = new UdpClient();

        if (OperatingSystem.IsWindows())
        {
            try
            {
                listener.AllowNatTraversal(true);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"Unable to set NAT traversal for UDP voice socket");
            }

            try
            {
                // don't fail the pending receive with WSAECONNRESET when an ICMP "port unreachable" comes back
                listener.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Unable to disable UDP connection reset errors");
            }
        }

        listener.Connect(_serverEndpoint);

        return listener;
    }

    private void CloseListener(UdpClient listener)
    {
        Ready = false;
        try
        {
            listener.Close();
        }
        catch (Exception e)
        {
            Logger.Warn(e, "Failed to close listener");
        }
    }

    /// <summary>Seals and sends one body; false if nothing may be sent any more (counter used up).</summary>
    private async Task<bool> SendSealedAsync(UdpClient listener, byte[] body, CancellationToken token)
    {
        var datagram = _transport.Seal(body);
        if (datagram == null)
        {
            Logger.Error("UDP counter used up or voice connection closed - not sending");
            return false;
        }

        await listener.SendAsync(datagram, token);
        return true;
    }

    private async void StartUDP()
    {
        using (_stopRequest = new CancellationTokenSource())
        {
            var token = _stopRequest.Token;
            var listener = SetupListener();

            // Send a first ping to check connectivity.
            Logger.Info($"Pinging Server - Starting (UDP key #{_transport.KeyId:x8})");
            var pingInterval = TimeSpan.FromSeconds(15);
            var pingBody = UdpDatagram.PingBody.ToArray();

            // Initial states to avoid null checks and also avoid throwing before we enter the loop.
            var receiveTask = Task.FromException<UdpReceiveResult>(new Exception());
            var pingTask = Task.CompletedTask;
            var timeoutTask = Task.Delay(UDP_VOIP_TIMEOUT, token);
            var outgoingAvailableTask = _outgoingSemaphore.WaitAsync(token);
            while (!token.IsCancellationRequested)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (pingTask.IsCompletedSuccessfully)
                    {
                        // Send ping every 15s.
                        await SendSealedAsync(listener, pingBody, token);
                        pingTask = Task.Delay(pingInterval, token);
                    }

                    if (receiveTask.IsCompleted)
                    {
                        // Only an authenticated datagram is a valid heartbeat. Reset the clock!
                        if (receiveTask.IsCompletedSuccessfully && HandleDatagram(receiveTask.Result.Buffer))
                            timeoutTask = Task.Delay(UDP_VOIP_TIMEOUT, token);

                        receiveTask = listener.ReceiveAsync(token).AsTask();
                    }


                    // Process the send queue. While not ready the packet is dropped - otherwise the completed wait
                    // would make the loop below spin until the server answers.
                    if (outgoingAvailableTask.IsCompletedSuccessfully)
                    {
                        // replace the wait first: if the send throws, the catch below must not start a second one
                        outgoingAvailableTask = _outgoingSemaphore.WaitAsync(token);

                        if (_outgoing.TryDequeue(out var outgoing) && Ready)
                        {
                            await SendSealedAsync(listener, outgoing, token);
                        }
                    }

                    // Reset the socket on a timeout.
                    if (timeoutTask.IsCompletedSuccessfully)
                    {
                        Logger.Error("VoIP Timeout - Recreating VoIP Connection");

                        // reset the clock first: if recreating the socket fails it is retried on the next timeout
                        timeoutTask = Task.Delay(UDP_VOIP_TIMEOUT, token);

                        CloseListener(listener);
                        listener = SetupListener();
                        pingTask = Task.CompletedTask;
                        receiveTask = listener.ReceiveAsync(token).AsTask();
                    }

                    await Task.WhenAny([timeoutTask, pingTask, receiveTask, outgoingAvailableTask]);
                }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested) break;

                    Logger.Warn(ex, "Voice handler exception");
                    // Reset everything but the timeout.
                    receiveTask = Task.FromException<UdpReceiveResult>(new Exception());

                    // Re-ping after a short pause - pinging again at once would spin while the network is down.
                    pingTask = Task.Delay(ErrorBackoff, token);

                    // Keep a pending (or completed, not yet processed) wait: an abandoned waiter would swallow a later
                    // Release() and leave a voice packet stuck in the queue.
                    if (outgoingAvailableTask.IsFaulted || outgoingAvailableTask.IsCanceled)
                        outgoingAvailableTask = _outgoingSemaphore.WaitAsync(token);

                    try
                    {
                        await Task.Delay(ErrorBackoff, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            receiveTask = null;
            outgoingAvailableTask = null;
            pingTask = null;
            timeoutTask = null;

            CloseListener(listener);
            _outgoing.Clear();

            if (_transport.AuthenticationFailures > 0 || _transport.ReplaysRejected > 0)
                Logger.Info($"UDP: dropped {_transport.AuthenticationFailures} datagrams that failed " +
                            $"authentication and {_transport.ReplaysRejected} replayed/old ones");

            _transport.Dispose();

            Interlocked.Exchange(ref _started, false);

            Logger.Info("UDP Voice Handler Thread Stop");
        }
    }

    /// <summary>Authenticates a received datagram; false (dropped) unless it is authentic and new.</summary>
    private bool HandleDatagram(byte[] datagram)
    {
        if (_transport.Open(datagram, out var body) != UdpOpenResult.Ok) return false;

        if (UdpDatagram.IsPing(body))
        {
            if (!Ready) Logger.Info("Received initial Ping Back from Server");

            Ready = true;
        }
        else if (Ready)
        {
            EncodedAudio.Add(body);
        }

        return true;
    }

    public void RequestStop()
    {
        try
        {
            _stopRequest?.Cancel();
        }
        catch (Exception)
        {
        }
    }

    public bool Send(UDPVoicePacket udpVoicePacket)
    {
        if (udpVoicePacket != null)
            try
            {
                udpVoicePacket.GuidBytes ??= _guidAsciiBytes;
                udpVoicePacket.OriginalClientGuidBytes ??= _guidAsciiBytes;

                var body = udpVoicePacket.EncodePacket();
                if (body.Length > UdpDatagram.MaxBodyLength) return false;

                _outgoing.Enqueue(body);
                _outgoingSemaphore.Release(1);

                return true;
            }
            catch (Exception e)
            {
                Logger.Error(e, "Exception Sending Audio Message");
            }


        return false;
    }
}
