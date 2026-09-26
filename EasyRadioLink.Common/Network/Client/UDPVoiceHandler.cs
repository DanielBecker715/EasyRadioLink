using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EasyRadioLink.Common.Models;
using EasyRadioLink.Common.Models.EventMessages;
using EasyRadioLink.Common.Network.Singletons;
using NLog;

namespace EasyRadioLink.Common.Network.Client;

public class UDPVoiceHandler
{
    private static readonly TimeSpan UDP_VOIP_TIMEOUT = TimeSpan.FromSeconds(42); // seconds for timeout before redoing VoIP

    // pause after a socket error (e.g. network unreachable while the Wi-Fi reconnects) before trying again
    private static readonly TimeSpan ErrorBackoff = TimeSpan.FromSeconds(1);

    // WSAIoctl SIO_UDP_CONNRESET (Windows only)
    private const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    // FIFO so voice packets leave in the order they were recorded
    private readonly ConcurrentQueue<byte[]> _outgoing = new ConcurrentQueue<byte[]>();
    private readonly byte[] _guidAsciiBytes;
    private CancellationTokenSource _stopRequest;
    private readonly IPEndPoint _serverEndpoint;
    private bool _started;
    private SemaphoreSlim _outgoingSemaphore = new SemaphoreSlim(0);

    public UDPVoiceHandler(string guid, IPEndPoint endPoint)
    {
        _guidAsciiBytes = Encoding.ASCII.GetBytes(guid);

        _serverEndpoint = endPoint;
    }

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

    private async void StartUDP()
    {
        using (_stopRequest = new CancellationTokenSource())
        {
            var token = _stopRequest.Token;
            var listener = SetupListener();

            // Send a first ping to check connectivity.
            Logger.Info($"Pinging Server - Starting");
            var pingInterval = TimeSpan.FromSeconds(15);

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
                        await listener.SendAsync(_guidAsciiBytes, token);
                        pingTask = Task.Delay(pingInterval, token);
                    }

                    if (receiveTask.IsCompleted)
                    {
                        if (receiveTask.IsCompletedSuccessfully)
                        {
                            var bytes = receiveTask.Result.Buffer;
                            if (bytes?.Length == 22)
                            {
                                if (!Ready)
                                {
                                    Logger.Info($"Received initial Ping Back from Server");
                                }
                                Ready = true;
                                
                            }
                            else if (Ready && bytes?.Length > 22)
                            {
                                EncodedAudio.Add(bytes);
                            }

                            // Consider this a valid heartbeat. Reset the clock!
                            timeoutTask = Task.Delay(UDP_VOIP_TIMEOUT, token);
                        }

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
                            await listener.SendAsync(outgoing, token);
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

            Interlocked.Exchange(ref _started, false);

            Logger.Info("UDP Voice Handler Thread Stop");
        }
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
                
                _outgoing.Enqueue(udpVoicePacket.EncodePacket());
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