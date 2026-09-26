using System.Collections.Concurrent;
using System.Globalization;
using System.Threading;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Settings;
using EasyRadioLink.Common.Settings.Setting;
using NLog;
using NLog.Targets;
using NLog.Targets.Wrappers;

namespace EasyRadioLink.Common.Network.Server.TransmissionLogging;

internal class TransmissionLoggingQueue
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly ServerSettingsStore _serverSettings = ServerSettingsStore.Instance;
    private FileTarget _fileTarget;
    private bool _log;
    private bool _stop;

    public TransmissionLoggingQueue()
    {
        _stop = false;
    }

    private ConcurrentDictionary<ClientInfo, TransmissionLog> _currentTransmissionLog { get; } = new();

    public void LogTransmission(ClientInfo client)
    {
        if (!_stop)
            try
            {
                if (_log)
                    _currentTransmissionLog.AddOrUpdate(client,
                        new TransmissionLog(client.LastTransmissionReceived, client.TransmittingFrequency),
                        (k, v) => UpdateTransmission(client, v));
            }
            catch
            {
            }
    }

    private TransmissionLog UpdateTransmission(ClientInfo client, TransmissionLog log)
    {
        log.TransmissionEnd = client.LastTransmissionReceived;
        return log;
    }

    public void Start()
    {
        new Thread(LogCompleteTransmissions).Start();
    }

    public void Stop()
    {
        _stop = true;
    }

    private void LogCompleteTransmissions()
    {
        while (!_stop)
        {
            Thread.Sleep(500);
            if (_log != _serverSettings.GetGeneralSetting(ServerSettingsKeys.TRANSMISSION_LOG_ENABLED).BoolValue)
            {
                _log = _serverSettings.GetGeneralSetting(ServerSettingsKeys.TRANSMISSION_LOG_ENABLED).BoolValue;
                var newSetting = _log ? "TRANSMISSION LOGGING ENABLED" : "TRANSMISSION LOGGING DISABLED";

                if (_serverSettings.GetGeneralSetting(ServerSettingsKeys.TRANSMISSION_LOG_ENABLED).BoolValue
                    && _fileTarget == null) // require initialization of transmission logging filetarget and rule
                {
                    var config = LogManager.Configuration;

                    config = LoggingHelper.GenerateTransmissionLoggingConfig(config,
                        _serverSettings.GetGeneralSetting(ServerSettingsKeys.TRANSMISSION_LOG_RETENTION).IntValue,
                        _serverSettings.ConfigDirectory);

                    LogManager.Configuration = config;

                    var b = (WrapperTargetBase)LogManager.Configuration.FindTargetByName(LoggingHelper.TransmissionTargetName);
                    _fileTarget = (FileTarget)b?.WrappedTarget;
                }

                Logger.Info($"EVENT, {newSetting}");
            }

            if (_serverSettings.GetGeneralSetting(ServerSettingsKeys.TRANSMISSION_LOG_ENABLED).BoolValue &&
                _fileTarget != null &&
                _fileTarget.MaxArchiveFiles != _serverSettings
                    .GetGeneralSetting(ServerSettingsKeys.TRANSMISSION_LOG_RETENTION).IntValue)
            {
                _fileTarget.MaxArchiveFiles = _serverSettings
                    .GetGeneralSetting(ServerSettingsKeys.TRANSMISSION_LOG_RETENTION).IntValue;
                LogManager.ReconfigExistingLoggers();
            }

            if (_log && !_currentTransmissionLog.IsEmpty)
                foreach (var LoggedTransmission in _currentTransmissionLog)
                    if (LoggedTransmission.Value.IsComplete())
                        if (_currentTransmissionLog.TryRemove(LoggedTransmission.Key, out var completedLog))
                            // CSV: TRANSMISSION, client id, name, frequency, start, end, UDP endpoint
                            Logger.Info(
                                $"TRANSMISSION, {LoggedTransmission.Key.ClientGuid}, {CsvSafe(LoggedTransmission.Key.Name)}, " +
                                $"{LoggedTransmission.Value.TransmissionFrequency}, " +
                                $"{FormatTime(completedLog.TransmissionStart)}, {FormatTime(completedLog.TransmissionEnd)}, " +
                                $"{LoggedTransmission.Key.VoipPort}");
        }
    }

    private static string CsvSafe(string value)
    {
        return (value ?? "").Replace(",", " ").Replace("\r", " ").Replace("\n", " ");
    }

    private static string FormatTime(System.DateTime time)
    {
        return time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
    }
}
