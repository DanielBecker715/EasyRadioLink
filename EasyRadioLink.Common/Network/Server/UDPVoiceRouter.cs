using Caliburn.Micro;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Models;
using EasyRadioLink.Common.Models.EventMessages;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Network.Crypto;
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

/// <summary>
///     UDP side of the server. Every datagram is encrypted with the key of the client it belongs to
///     (<see cref="UdpDatagram" />). Per datagram, cheapest check first: length, known client id, source address
///     (the IP of the client's TCP connection), failed-authentication budget of the source endpoint (not for the
///     client's last authenticated endpoint, <see cref="UdpAuthFailureBudget" />), then decryption + replay window
///     (<see cref="UdpTransportSession.Open" />), then the per-sender rate limit. A ping is answered with an encrypted
///     pong; a voice packet must name its authenticated sender, passes the busy channel lockout (server setting
///     BUSY_CHANNEL_LOCKOUT, <see cref="BusyChannelArbiter" />: one speaker per frequency), is routed by
///     frequency/modulation as before and encrypted again for every recipient with the recipient's key and the server's
///     counter.
/// </summary>
internal class UDPVoiceRouter : IHandle<ServerFrequenciesChanged>, IHandle<ServerSettingsChangedMessage>
{
    // datagrams that failed authentication are counted and summarised in the log at most this often
    private static readonly TimeSpan DropLogInterval = TimeSpan.FromMinutes(1);

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

    // one speaker per frequency (BUSY_CHANNEL_LOCKOUT, read again when the server settings change)
    private readonly BusyChannelArbiter _busyChannels;
    private volatile bool _busyChannelLockout;
    private long _busyDropsLogged;
    private long _lastBusyDropLogMilliseconds = Environment.TickCount64;

    private TransmissionLoggingQueue _transmissionLoggingQueue;

    // receive loop only
    private readonly UdpAuthFailureBudget _authFailureBudget = new();
    private long _authenticationFailures;
    private long _budgetDrops;
    private DateTime _lastDropLogUtc = DateTime.UtcNow;

    public UDPVoiceRouter(ConcurrentDictionary<string, ClientInfo> clientsList, IEventAggregator eventAggregator)
    {
        _clientsList = clientsList;
        _eventAggregator = eventAggregator;

        // a holder that has left frees its channel at once (only asked when a packet collides with its channel)
        _busyChannels = new BusyChannelArbiter(guid => _clientsList.ContainsKey(guid));

        var freqString = _serverSettings.GetGeneralSetting(ServerSettingsKeys.TEST_FREQUENCIES).StringValue;
        UpdateTestFrequencies(freqString);
        UpdateBusyChannelLockout(true);

        _eventAggregator.SubscribeOnBackgroundThread(this);
    }

    public Task HandleAsync(ServerFrequenciesChanged message, CancellationToken cancellationToken)
    {
        if (message.TestFrequencies != null)
            UpdateTestFrequencies(message.TestFrequencies);

        return Task.CompletedTask;
    }

    public Task HandleAsync(ServerSettingsChangedMessage message, CancellationToken cancellationToken)
    {
        UpdateBusyChannelLockout(false);

        return Task.CompletedTask;
    }

    private void UpdateBusyChannelLockout(bool starting)
    {
        bool enabled;
        try
        {
            enabled = _serverSettings.GetGeneralSetting(ServerSettingsKeys.BUSY_CHANNEL_LOCKOUT).BoolValue;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Unable to read BUSY_CHANNEL_LOCKOUT - using the default (on)");
            enabled = true;
        }

        if (!starting && enabled == _busyChannelLockout) return;

        // switched on again later: nobody holds a channel from before
        if (!enabled) _busyChannels.Clear();

        _busyChannelLockout = enabled;
        Logger.Info(enabled
            ? "Busy channel lockout on: one speaker per frequency"
            : "Busy channel lockout off: several stations may transmit on one frequency at the same time");
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

    private async Task ProcessIncomingPacketsAsync(UdpClient listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var inbound = await listener.ReceiveAsync(token);
                HandleDatagram(listener, inbound.Buffer, inbound.RemoteEndPoint, token);
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

    /// <summary>
    ///     Runs on the receive loop: junk, unknown clients, spoofed senders, forged, replayed and flooding datagrams are
    ///     dropped here without creating any other work (and without logging each of them).
    /// </summary>
    private void HandleDatagram(UdpClient listener, byte[] datagram, IPEndPoint receivedFrom, CancellationToken token)
    {
        if (datagram == null || !UdpDatagram.HasValidLength(datagram.Length)) return;

        // unknown client id: dropped before any crypto
        var guid = UdpDatagram.ReadClientGuid(datagram);
        if (!_clientsList.TryGetValue(guid, out var sender)) return;

        var transport = sender.UdpTransport;
        if (transport == null || !VoiceRouting.IsFromClientAddress(sender, receivedFrom)) return;

        var nowTicks = DateTime.UtcNow.Ticks;

        // The client's last authenticated endpoint is always heard. Every other endpoint (a first datagram, a NAT
        // rebinding - or forged datagrams in the client's name, e.g. from another host behind the same NAT) has a
        // budget of failed authentications of its own, so a forger can use up only its own budget, never the client's.
        var fromAuthenticatedEndpoint = VoiceRouting.IsFromAuthenticatedEndpoint(sender, receivedFrom);
        if (!fromAuthenticatedEndpoint && !_authFailureBudget.AllowAttempt(receivedFrom, nowTicks))
        {
            _budgetDrops++;
            LogDroppedDatagrams();
            return;
        }

        var result = transport.Open(datagram, out var body);
        if (result != UdpOpenResult.Ok)
        {
            if (result == UdpOpenResult.AuthenticationFailed)
            {
                if (!fromAuthenticatedEndpoint) _authFailureBudget.RecordFailure(receivedFrom, nowTicks);
                _authenticationFailures++;
                LogDroppedDatagrams();
            }

            return;
        }

        // voice and pings count against the sender's rate limit (a flood costs the flooder, not every listener)
        if (!VoiceRouting.AllowVoicePacket(sender, nowTicks)) return;

        // only an authenticated datagram may move the client's voice endpoint (e.g. after a NAT rebinding)
        sender.VoipPort = receivedFrom;

        if (UdpDatagram.IsPing(body))
        {
            var pong = transport.Seal(UdpDatagram.PingBody);
            if (pong != null) _ = SendDatagramAsync(listener, pong, receivedFrom);

            return;
        }

        if (sender.Muted) return;

        _ = Task.Run(async Task () => await ProcessVoicePacketAsync(listener, body, sender), token);
    }

    private void LogDroppedDatagrams()
    {
        var now = DateTime.UtcNow;
        if (now - _lastDropLogUtc < DropLogInterval) return;

        Logger.Warn($"Dropped {_authenticationFailures} UDP datagrams that failed authentication (wrong key, " +
                    $"tampered or forged) and {_budgetDrops} more without decrypting them (too many failures from " +
                    $"their source) since {_lastDropLogUtc:u}");
        _authenticationFailures = 0;
        _budgetDrops = 0;
        _lastDropLogUtc = now;
    }

    /// <summary>Debug summary of the packets dropped by the busy channel lockout, at most once per minute.</summary>
    private void LogBusyChannelDrops()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastBusyDropLogMilliseconds);
        if (now - last < (long)DropLogInterval.TotalMilliseconds) return;
        if (Interlocked.CompareExchange(ref _lastBusyDropLogMilliseconds, now, last) != last) return;

        var total = _busyChannels.Drops;
        var dropped = total - Interlocked.Exchange(ref _busyDropsLogged, total);
        if (dropped > 0 && Logger.IsDebugEnabled)
            Logger.Debug($"Busy channel lockout: dropped {dropped} voice packets of stations that transmitted on a " +
                         $"frequency somebody else was using (last {(now - last) / 1000} s)");
    }

    private async Task SendDatagramAsync(UdpClient listener, byte[] datagram, IPEndPoint endPoint)
    {
        try
        {
            await listener.SendAsync(datagram, endPoint, _stopCancellationToken.Token);
        }
        catch (Exception)
        {
            // Deliberately ignored (stopping, client gone) - can be spammy.
        }
    }

    /// <param name="body">The decrypted body: an encoded <see cref="UDPVoicePacket" />.</param>
    /// <param name="sender">The authenticated sender (registered, right address, not muted).</param>
    private async Task ProcessVoicePacketAsync(UdpClient listener, byte[] body, ClientInfo sender)
    {
        try
        {
            if (!UDPVoicePacket.TryDecode(body, false, out var udpVoicePacket)) return;

            // nobody speaks with another user's id: the packet must name the sender whose key authenticated it
            if (udpVoicePacket.Guid != sender.ClientGuid || udpVoicePacket.OriginalClientGuid != sender.ClientGuid)
                return;

            // one speaker per frequency: another station holds the channel - nobody hears this packet
            if (!VoiceRouting.PassesBusyChannelLockout(_busyChannels, _busyChannelLockout, sender, udpVoicePacket,
                    BusyChannelArbiter.NowMilliseconds))
            {
                LogBusyChannelDrops();
                return;
            }

            var recipients =
                VoiceRouting.SelectRecipientClients(_clientsList.Values, sender, udpVoicePacket, _testFrequencies);
            if (recipients.Count == 0) return;

            var sends = new List<Task>(recipients.Count);
            foreach (var recipient in recipients)
            {
                var endPoint = recipient.VoipPort;
                var datagram = recipient.UdpTransport?.Seal(body);
                if (endPoint == null || datagram == null) continue;

                sends.Add(SendDatagramAsync(listener, datagram, endPoint));
            }

            await Task.WhenAll(sends);

            //mark as transmitting for the UI
            var mainFrequency = udpVoicePacket.Frequencies.FirstOrDefault();
            // Only trigger transmitting frequency update for "proper" packets (excluding invalid frequencies)
            if (mainFrequency > 0 && udpVoicePacket.Modulations.Length > 0)
            {
                var mainModulation = (Modulation)udpVoicePacket.Modulations[0];
                sender.TransmittingFrequency = $"{RadioCalculator.FormatMHz(mainFrequency)} {mainModulation}";
                sender.LastTransmissionReceived = DateTime.Now;

                _transmissionLoggingQueue?.LogTransmission(sender);
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
}
