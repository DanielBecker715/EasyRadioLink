using EasyRadioLink.Common.Network.Server.TransmissionLogging;
using NLog;
using NLog.Config;
using NLog.Targets;
using NLog.Targets.Wrappers;

namespace EasyRadioLink.Common.Helpers;

public static class LoggingHelper
{
    public const string TransmissionTargetName = "asyncTransmissionFileTarget";

    // Must match the logger created by LogManager.GetCurrentClassLogger() inside TransmissionLoggingQueue.
    private static readonly string TransmissionLoggerName = typeof(TransmissionLoggingQueue).FullName;

    public static LoggingConfiguration GenerateTransmissionLoggingConfig(LoggingConfiguration config, int archiveFiles)
    {
        config ??= new LoggingConfiguration();

        // Only ever add the transmission target/rule once (the GUI adds it at start-up, the queue on demand).
        if (config.FindTargetByName(TransmissionTargetName) != null) return config;

        var transmissionFileTarget = new FileTarget
        {
            FileName = @"${date:format=yyyy-MM-dd}-transmissionlog.csv",
            ArchiveOldFileOnStartup = true,
            ArchiveFileName = @"${basedir}/TransmissionLogArchive/transmissionlog.old.csv",
            MaxArchiveFiles = archiveFiles,
            ArchiveEvery = FileArchivePeriod.Day,
            Layout = @"${longdate}, ${message}"
        };


        var transmissionWrapper =
            new AsyncTargetWrapper(transmissionFileTarget, 5000, AsyncTargetWrapperOverflowAction.Discard);

        config.AddTarget(TransmissionTargetName, transmissionWrapper);


        var transmissionRule = new LoggingRule(
            TransmissionLoggerName,
            LogLevel.Info,
            transmissionWrapper
        );
        transmissionRule.Final = true;

        config.LoggingRules.Add(transmissionRule);

        return config;
    }
}
