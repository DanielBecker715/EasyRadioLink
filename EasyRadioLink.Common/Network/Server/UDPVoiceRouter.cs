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
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly ConcurrentDictionary<string, ClientInfo> _clientsList;
    private readonly IEventAggregator _eventAggregator;

    private CancellationTokenSource _stopCancellationToken;
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

    public async Task Listen()
    {
        Logger.Info("UDP Voice Router starting...");
        _transmissionLoggingQueue = new TransmissionLoggingQueue();
        _transmissionLoggingQueue.Start();

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
        }

        listener.ExclusiveAddressUse = true;
        listener.DontFragment = true;

        var port = _serverSettings.GetServerPort();
        var bindAddress = _serverSettings.GetServerIP();
        if (!await TryBindAsync(listener, new IPEndPoint(bindAddress, port)))
        {
            var error =
                $"Unable to start the EasyRadioLink voice server on UDP {bindAddress}:{port} - is another server already running or the bind IP wrong?";
            Logger.Error(error);
            Console.Error.WriteLine(error);
            LogManager.Flush();

            listener.Dispose();
            Environment.Exit(1);
            return;
        }

        _listener = listener;

        // Incoming queue.
        await ProcessIncomingPacketsAsync(listener);

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
    private static async Task<bool> TryBindAsync(UdpClient listener, IPEndPoint endPoint)
    {
        for (var attempt = 1; attempt <= 10; attempt++)
            try
            {
                listener.Client.Bind(endPoint);
                return true;
            }
            catch (SocketException ex)
            {
                Logger.Warn(ex, $"UDP bind to {endPoint} failed (attempt {attempt})");
                await Task.Delay(300);
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

        _stopCancellationToken?.Cancel();

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
        _transmissionLoggingQueue = null;
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

    private async Task ProcessIncomingPacketsAsync(UdpClient listener)
    {
        using (_stopCancellationToken = new CancellationTokenSource())
        {
            while (!_stopCancellationToken.IsCancellationRequested)
            {
                try
                {
                    var inbound = await listener.ReceiveAsync(_stopCancellationToken.Token);
                    var rawBytes = inbound.Buffer;
                    var receivedFromEP = inbound.RemoteEndPoint;
                    if (rawBytes?.Length == UDPVoicePacket.GuidLength)
                    {
                        try
                        {
                            //22 bytes are guid!
                            var guid = Encoding.ASCII.GetString(rawBytes, 0, UDPVoicePacket.GuidLength);

                            // Only clients that completed the TCP handshake (and the password check) get a pong -
                            // unknown peers never become "ready" and can't receive voice.
                            if (_clientsList.TryGetValue(guid, out var client))
                            {
                                client.VoipPort = receivedFromEP;

                                //send back ping UDP, don't care much about the result.
                                _ = Task.Run(async Task () => await listener.SendAsync(rawBytes, rawBytes.Length, receivedFromEP), _stopCancellationToken.Token);
                            }
                        }
                        catch (Exception e)
                        {
                            Logger.Error(e, "Bad send?");
                        }
                    }
                    else if (rawBytes?.Length > UDPVoicePacket.GuidLength)
                    {
                        _ = Task.Run(async Task () => await ProcessPendingPacketAsync(listener, new PendingPacket
                        {
                            RawBytes = rawBytes,
                            ReceivedFrom = receivedFromEP
                        }), _stopCancellationToken.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Normal termination, let the top while loop catch it.
                }
                catch (Exception e) when (_stopCancellationToken.IsCancellationRequested)
                {
                    // socket closed by RequestStop
                    Logger.Debug(e, "UDP Voice Router listener closed");
                }
                catch (Exception e)
                {
                    Logger.Error(e, "Error in UDP Voice Router listener");
                }
            }

            Logger.Info("UDP Voice Router Listener stopped.");
        }
    }

    private async Task ProcessPendingPacketAsync(UdpClient listener, PendingPacket udpPacket)
    {
        if (udpPacket == null)
        {
            return;
        }

        try
        {
            //last 22 bytes are guid!
            var guid = Encoding.ASCII.GetString(
                udpPacket.RawBytes, udpPacket.RawBytes.Length - UDPVoicePacket.GuidLength, UDPVoicePacket.GuidLength);

            // the sender must be a registered (authenticated) client
            if (!_clientsList.TryGetValue(guid, out var client)) return;

            client.VoipPort = udpPacket.ReceivedFrom;

            // muted by the server admin - drop the audio
            if (client.Muted) return;

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
        var strictEncryption = _serverSettings.GetGeneralSetting(ServerSettingsKeys.STRICT_RADIO_ENCRYPTION).BoolValue;

        var outgoingList = VoiceRouting.SelectRecipients(_clientsList.Values, sender, udpVoice, strictEncryption,
            _testFrequencies);

        if (outgoingList.Count > 0)
            return new OutgoingUDPPackets
            {
                OutgoingEndPoints = outgoingList.ToList(),
                ReceivedPacket = pendingPacket.RawBytes
            };

        return null;
    }
}
