using Caliburn.Micro;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Models;
using EasyRadioLink.Common.Models.EventMessages;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Network.Server.TransmissionLogging;
using EasyRadioLink.Common.Settings;
using EasyRadioLink.Common.Settings.Setting;
using NLog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LogManager = NLog.LogManager;

namespace EasyRadioLink.Common.Network.Server;

internal class UDPVoiceRouter : IHandle<ServerFrequenciesChanged>
{
    // WSAIoctl SIO_UDP_CONNRESET: stop Windows from failing the next receive with WSAECONNRESET (10054) after an
    // ICMP "port unreachable" for a packet sent to a client that has gone away
    private const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly ConcurrentDictionary<string, ClientInfo> _clientsList;
    private readonly IEventAggregator _eventAggregator;

    // one router per server start - cancelled by RequestStop, also while the port is still being bound
    private readonly CancellationTokenSource _stopCancellationToken = new();
    private UdpClient _listener;

    private readonly ServerSettingsStore _serverSettings = ServerSettingsStore.Instance;
    private volatile List<double> _testFrequencies = new();

    private TransmissionLoggingQueue _transmissionLoggingQueue;

    public UDPVoiceRouter(ConcurrentDictionary<string, ClientInfo> clientsList, IEventAggregator eventAggregator)
    {
        _clientsList = clientsList;
        _eventAggregator = eventAggregator;
        _eventAggregator.SubscribeOnBackgroundThread(this);

        var freqString = _serverSettings.GetGeneralSetting(ServerSettingsKeys.TEST_FREQUENCIES).StringValue;
        UpdateTestFrequencies(freqString);
    }

    public Task HandleAsync(ServerFrequenciesChanged message, CancellationToken cancellationToken)
    {
        if (message.TestFrequencies != null)
            UpdateTestFrequencies(message.TestFrequencies);

        return Task.CompletedTask;
    }

    private void UpdateTestFrequencies(string freqString)
    {
        var newList = RadioCalculator.ParseFrequencyListMHz(freqString);

        foreach (var frequency in newList) Logger.Info("Adding Test Frequency: " + frequency);

        _testFrequencies = newList;
    }

    /// <summary>
    ///     Binds the voice port and routes packets until <see cref="RequestStop" />. Throws
    ///     <see cref="ServerStartException" /> if the UDP port can't be bound.
    /// </summary>
    public async Task Listen()
    {
        Logger.Info("UDP Voice Router starting...");
        var token = _stopCancellationToken.Token;

        var listener = new UdpClient();

        if (OperatingSystem.IsWindows())
        {
            try
            {
                listener.AllowNatTraversal(true);
            }
            catch
            {
                // ignored
            }

            try
            {
                listener.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Unable to disable UDP connection reset errors");
            }
        }

        listener.ExclusiveAddressUse = true;
        listener.DontFragment = true;

        var port = _serverSettings.GetServerPort();
        var bindAddress = _serverSettings.GetServerIP();
        if (!await TryBindAsync(listener, new IPEndPoint(bindAddress, port), token))
        {
            listener.Dispose();

            // stopped while binding - not an error
            if (token.IsCancellationRequested) return;

            throw new ServerStartException(
                $"Unable to start the voice server on UDP {bindAddress}:{port}: the port is already in use (is another EasyRadioLink server running?) or the bind IP is wrong.");
        }

        _listener = listener;

        // RequestStop may have run before _listener was set
        if (!token.IsCancellationRequested)
        {
            _transmissionLoggingQueue = new TransmissionLoggingQueue();
            _transmissionLoggingQueue.Start();

            // Incoming queue.
            await ProcessIncomingPacketsAsync(listener, token);
        }

        _transmissionLoggingQueue?.Stop();

        try
        {
            listener.Close();
        }
        catch (Exception e)
        {
            Logger.Warn(e, "Error closing UDP Voice router socket");
        }

        Logger.Info("UDP Voice Router stopped.");
    }

    // A quick Stop/Start can race with the previous socket being released - retry for a moment.
    private static async Task<bool> TryBindAsync(UdpClient listener, IPEndPoint endPoint, CancellationToken token)
    {
        for (var attempt = 1; attempt <= 10 && !token.IsCancellationRequested; attempt++)
            try
            {
                listener.Client.Bind(endPoint);
                return true;
            }
            catch (SocketException ex)
            {
                Logger.Warn(ex, $"UDP bind to {endPoint} failed (attempt {attempt})");
                try
                {
                    await Task.Delay(300, token);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"UDP bind to {endPoint} failed");
                return false;
            }

        return false;
    }

    public void RequestStop()
    {
        try
        {
            _eventAggregator.Unsubscribe(this);
        }
        catch (Exception)
        {
            // ignored
        }

        try
        {
            _stopCancellationToken.Cancel();
        }
        catch (Exception)
        {
            // ignored
        }

        // release the port immediately so the server can be restarted straight away
        try
        {
            _listener?.Close();
        }
        catch (Exception)
        {
            // ignored
        }

        _listener = null;

        _transmissionLoggingQueue?.Stop();
    }

    private async Task DispatchOutgoingPacketsAsync(UdpClient listener, OutgoingUDPPackets outgoingUdpPacket)
    {
        var recipients = new List<Task>(outgoingUdpPacket.OutgoingEndPoints.Count);
        foreach (var outgoingEndPoint in outgoingUdpPacket.OutgoingEndPoints)
        {
            try
            {
                recipients.Add(listener.SendAsync(outgoingUdpPacket.ReceivedPacket, outgoingEndPoint, _stopCancellationToken.Token).AsTask());
            }
            catch (OperationCanceledException)
            {
                // Expected termination.
            }
            catch (Exception)
            {
                // Deliberately ignored, can be spammy.
            }
        }

        await Task.WhenAll(recipients);
    }

    private async Task ProcessIncomingPacketsAsync(UdpClient listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var inbound = await listener.ReceiveAsync(token);
                var rawBytes = inbound.Buffer;
                var receivedFromEP = inbound.RemoteEndPoint;
                if (rawBytes?.Length == UDPVoicePacket.GuidLength)
                {
                    try
                    {
                        //22 bytes are guid!
                        var guid = Encoding.ASCII.GetString(rawBytes, 0, UDPVoicePacket.GuidLength);

                        // Only clients that completed the TCP handshake (and the password check) get a pong, and only
                        // from the IP address they authenticated from - unknown peers never become "ready" and can't
                        // receive voice.
                        if (_clientsList.TryGetValue(guid, out var client) &&
                            VoiceRouting.IsFromClientAddress(client, receivedFromEP))
                        {
                            client.VoipPort = receivedFromEP;

                            //send back ping UDP, don't care much about the result.
                            _ = Task.Run(async Task () => await listener.SendAsync(rawBytes, rawBytes.Length, receivedFromEP), token);
                        }
                    }
                    catch (Exception e)
                    {
                        Logger.Error(e, "Bad send?");
                    }
                }
                else if (rawBytes?.Length > UDPVoicePacket.GuidLength)
                {
                    // Cheap checks right here on the receive loop: junk, unknown or spoofed senders, muted clients and
                    // floods never create any work. The last 22 bytes are the sender's client id.
                    var guid = Encoding.ASCII.GetString(rawBytes, rawBytes.Length - UDPVoicePacket.GuidLength,
                        UDPVoicePacket.GuidLength);
                    if (!_clientsList.TryGetValue(guid, out var sender) ||
                        !VoiceRouting.IsFromClientAddress(sender, receivedFromEP) ||
                        sender.Muted ||
                        !VoiceRouting.AllowVoicePacket(sender, DateTime.UtcNow.Ticks))
                        continue;

                    sender.VoipPort = receivedFromEP;

                    _ = Task.Run(async Task () => await ProcessPendingPacketAsync(listener, new PendingPacket
                    {
                        RawBytes = rawBytes,
                        ReceivedFrom = receivedFromEP
                    }, sender), token);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal termination, let the top while loop catch it.
            }
            catch (Exception e) when (token.IsCancellationRequested)
            {
                // socket closed by RequestStop
                Logger.Debug(e, "UDP Voice Router listener closed");
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset)
            {
                // ICMP "port unreachable" from a client that has gone away (SIO_UDP_CONNRESET could not be disabled)
                Logger.Debug(e, "UDP connection reset by a client");
            }
            catch (Exception e)
            {
                Logger.Error(e, "Error in UDP Voice Router listener");
            }
        }

        Logger.Info("UDP Voice Router Listener stopped.");
    }

    /// <param name="client">The sender - already checked by the receive loop (registered, right address, not muted).</param>
    private async Task ProcessPendingPacketAsync(UdpClient listener, PendingPacket udpPacket, ClientInfo client)
    {
        if (udpPacket == null)
        {
            return;
        }

        try
        {
            var udpVoicePacket = UDPVoicePacket.DecodeVoicePacket(udpPacket.RawBytes);

            if (udpVoicePacket == null) return;

            var outgoingVoice = GenerateOutgoingPacket(udpVoicePacket, udpPacket, client);

            if (outgoingVoice == null) return;

            await DispatchOutgoingPacketsAsync(listener, outgoingVoice);

            //mark as transmitting for the UI
            var mainFrequency = udpVoicePacket.Frequencies.FirstOrDefault();
            // Only trigger transmitting frequency update for "proper" packets (excluding invalid frequencies)
            if (mainFrequency > 0 && udpVoicePacket.Modulations.Length > 0)
            {
                var mainModulation = (Modulation)udpVoicePacket.Modulations[0];
                client.TransmittingFrequency = $"{RadioCalculator.FormatMHz(mainFrequency)} {mainModulation}";
                client.LastTransmissionReceived = DateTime.Now;

                _transmissionLoggingQueue?.LogTransmission(client);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected termination.
        }
        catch (Exception)
        {
            //Hide for now, slows down too much....
        }
    }

    private OutgoingUDPPackets GenerateOutgoingPacket(UDPVoicePacket udpVoice, PendingPacket pendingPacket,
        ClientInfo sender)
    {
        var outgoingList = VoiceRouting.SelectRecipients(_clientsList.Values, sender, udpVoice, _testFrequencies);

        if (outgoingList.Count > 0)
            return new OutgoingUDPPackets
            {
                OutgoingEndPoints = outgoingList,
                ReceivedPacket = pendingPacket.RawBytes
            };

        return null;
    }
}
