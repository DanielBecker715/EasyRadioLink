using System;
using System.IO;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace EasyRadioLink.Installer
{
    /// <summary>
    ///     File logging of the setup. An install logs next to the setup executable (installer-log.txt in the extracted
    ///     package); an uninstall logs to %TEMP% because the install folder is deleted.
    /// </summary>
    internal static class SetupLog
    {
        private const long MaxLogFileSize = 10 * 1024 * 1024;

        public static string InstallLogPath => Path.Combine(AppContext.BaseDirectory, SetupEngine.InstallerLogFileName);

        public static string UninstallLogPath => Path.Combine(Path.GetTempPath(), SetupEngine.UninstallLogFileName);

        /// <summary>The log file in use (shown in error messages).</summary>
        public static string LogFilePath { get; private set; } = InstallLogPath;

        public static void Initialize(string logFilePath)
        {
            LogFilePath = logFilePath;

            // Keep one old copy if the log grows too large.
            try
            {
                var logFileInfo = new FileInfo(logFilePath);
                if (logFileInfo.Exists && logFileInfo.Length >= MaxLogFileSize)
                {
                    var oldLogFilePath = Path.ChangeExtension(logFilePath, ".old.txt");
                    if (File.Exists(oldLogFilePath))
                    {
                        File.Delete(oldLogFilePath);
                    }

                    File.Move(logFilePath, oldLogFilePath);
                }
            }
            catch (Exception)
            {
                // logging must never stop the setup
            }

            var config = new LoggingConfiguration();

            // Synchronous on purpose: the setup exits right after it finishes and must not lose the last entries.
            var fileTarget = new FileTarget("file")
            {
                FileName = logFilePath,
                Layout = @"${longdate} | ${level:uppercase=true} | ${logger} | ${message} ${exception:format=toString,Data:maxInnerExceptionLevel=2}"
            };
            config.AddTarget(fileTarget);

#if DEBUG
            config.LoggingRules.Add(new LoggingRule("*", LogLevel.Debug, fileTarget));
#else
            config.LoggingRules.Add(new LoggingRule("*", LogLevel.Info, fileTarget));
#endif

            LogManager.Configuration = config;
        }
    }
}
