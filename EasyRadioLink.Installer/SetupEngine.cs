using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using NLog;
using EasyRadioLink.Installer.Properties;

namespace EasyRadioLink.Installer
{
    /// <summary>
    ///     Options chosen in the setup window for an install or update.
    /// </summary>
    public class InstallOptions
    {
        /// <summary>Extracted download package (the folder that contains Client, Server, ...).</summary>
        public string PackageDirectory { get; init; }

        /// <summary>Target folder, e.g. C:\Program Files\EasyRadioLink.</summary>
        public string InstallDirectory { get; init; }

        public bool StartMenuShortcuts { get; init; }

        public bool DesktopShortcut { get; init; }
    }

    public class InstallResult
    {
        /// <summary>Exit code of the Visual C++ runtime installer, null if it was not run.</summary>
        public int? VcRedistExitCode { get; set; }

        public bool VcRedistFailed { get; set; }

        public bool RebootRequired { get; set; }

        public bool ShortcutsFailed { get; set; }
    }

    public class UninstallResult
    {
        /// <summary>Folder that still exists after the uninstall (kept user files or locked files), otherwise null.</summary>
        public string RemainingDirectory { get; set; }

        /// <summary>Number of program files that could not be deleted (for example because they were locked).</summary>
        public int FailedFiles { get; set; }
    }

    /// <summary>
    ///     Install / update / uninstall logic of the EasyRadioLink setup. Contains no UI code: the public operations may run on
    ///     a background thread and report progress through a callback.
    /// </summary>
    /// <remarks>
    ///     Every installed file is recorded (relative to the install folder) in <see cref="ManifestFileName" />. Updates and
    ///     the uninstaller delete exactly those files, so data that the programs create next to their executables (for example
    ///     server.cfg, banned.txt, Presets\ and logs in the Server folder) survives an update. User settings of the client live
    ///     in %AppData%\EasyRadioLink and recordings in Documents\EasyRadioLink\Recordings; the setup never touches them.
    /// </remarks>
    public static class SetupEngine
    {
        public const string ProductName = "EasyRadioLink";
        public const string Publisher = "EasyRadioLink";

        public const string SetupExeName = "EasyRadioLink-Setup.exe";
        public const string ManifestFileName = "install-manifest.txt";
        public const string InstallerLogFileName = "installer-log.txt";
        public const string InstallerOldLogFileName = "installer-log.old.txt";
        public const string UninstallLogFileName = "EasyRadioLink-uninstall-log.txt";
        public const string VcRedistFileName = "VC_redist.x64.exe";

        public const string ClientFolder = "Client";
        public const string ServerFolder = "Server";
        public const string CliWindowsFolder = "ServerCommandLine-Windows";
        public const string CliLinuxFolder = "ServerCommandLine-Linux";

        public const string ClientExe = "EasyRadioLink.exe";
        public const string ServerExe = "EasyRadioLink.Server.exe";
        public const string CliWindowsExe = "EasyRadioLink.Server.Cli.exe";
        public const string CliLinuxExe = "EasyRadioLink.Server.Cli";

        /// <summary>HKLM (64-bit view) key with the install location and the chosen options.</summary>
        public const string RegistryKeyPath = @"SOFTWARE\EasyRadioLink";

        /// <summary>HKLM (64-bit view) key of the "Apps &amp; Features" / "Programs and Features" entry.</summary>
        public const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\EasyRadioLink";

        public const string InstallPathValue = "InstallPath";
        public const string VersionValue = "Version";
        public const string StartMenuShortcutsValue = "StartMenuShortcuts";
        public const string DesktopShortcutValue = "DesktopShortcut";

        public const string UninstallArgument = "-uninstall";
        public const string PathArgumentPrefix = "-path=";
        public const string WaitPidArgumentPrefix = "-waitpid=";

        /// <summary>Prefix of the temporary folder the uninstaller copies itself to (so it can delete the install folder).</summary>
        public const string TempUninstallFolderPrefix = "EasyRadioLink-Uninstall-";

        /// <summary>Program sub folders shipped in the package, in install order.</summary>
        public static readonly string[] ProgramFolders = { ClientFolder, ServerFolder, CliWindowsFolder, CliLinuxFolder };

        /// <summary>
        ///     Sub folders whose programs write files next to their executable: the servers (server.cfg, banned.txt, logs,
        ///     client export) and the client (clientlog.txt). The install folder itself, which holds the elevated setup,
        ///     stays write-protected.
        /// </summary>
        private static readonly string[] UserWritableFolders = { ClientFolder, ServerFolder, CliWindowsFolder };

        /// <summary>Text files in the package root that are copied to the install folder (if present).</summary>
        private static readonly string[] RootDocuments = { "README.txt", "LICENSE.txt", "THIRD-PARTY-NOTICES.txt" };

        /// <summary>Files next to the setup executable that belong to the setup (only a non single-file dev build has more than the exe).</summary>
        private static readonly string[] SetupFiles =
        {
            SetupExeName, "EasyRadioLink-Setup.dll", "EasyRadioLink-Setup.runtimeconfig.json",
            "EasyRadioLink-Setup.deps.json", "NLog.dll"
        };

        /// <summary>Package files that must exist, otherwise the download was not extracted (completely).</summary>
        private static readonly string[] RequiredPackageFiles =
        {
            Path.Combine(ClientFolder, ClientExe),
            Path.Combine(ClientFolder, "opus.dll"),
            Path.Combine(ClientFolder, "speexdsp.dll"),
            Path.Combine(ClientFolder, "radios.json"),
            Path.Combine(ServerFolder, ServerExe)
        };

        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>Product version of this setup, e.g. "1.0.0" (from Directory.Build.props).</summary>
        public static string Version { get; } = GetVersion();

        /// <summary>Full path of the running setup executable.</summary>
        public static string SetupProcessPath { get; } = GetSetupProcessPath();

        /// <summary>Folder the setup runs from (the extracted download package for a normal install).</summary>
        public static string PackageDirectory { get; } = NormalizeDirectory(AppContext.BaseDirectory);

        public static string DefaultInstallPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProductName);

        public static string StartMenuFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), ProductName);

        public static string DesktopShortcutPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                ProductName + ".lnk");

        #region Paths and validation

        private static string GetVersion()
        {
            var assembly = typeof(SetupEngine).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                // Strip a "+<commit hash>" suffix in case source revision info is enabled.
                var plus = informational.IndexOf('+');
                return plus > 0 ? informational.Substring(0, plus) : informational;
            }

            var version = assembly.GetName().Version;
            return version != null ? version.ToString(3) : "1.0.0";
        }

        private static string GetSetupProcessPath()
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path))
            {
                path = Process.GetCurrentProcess().MainModule?.FileName;
            }

            return path ?? Path.Combine(AppContext.BaseDirectory, SetupExeName);
        }

        /// <summary>Full path without trailing separator. Throws for invalid paths.</summary>
        public static string NormalizeDirectory(string path)
        {
            var full = Path.GetFullPath(path.Trim().Trim('"'));
            var root = Path.GetPathRoot(full);
            if (full.Length > (root?.Length ?? 0))
            {
                full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            return full;
        }

        /// <summary>Normalized full path, or null if the text is not a valid absolute path.</summary>
        public static string TryNormalizeDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                var trimmed = path.Trim().Trim('"');
                if (!Path.IsPathFullyQualified(trimmed) || trimmed.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                {
                    return null;
                }

                return NormalizeDirectory(trimmed);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"Invalid path: {path}");
                return null;
            }
        }

        public static bool PathsEqual(string a, string b)
        {
            if (a == null || b == null)
            {
                return false;
            }

            return string.Equals(NormalizeDirectory(a), NormalizeDirectory(b), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True if <paramref name="path" /> is <paramref name="directory" /> itself or anything below it.</summary>
        public static bool IsInsideDirectory(string path, string directory)
        {
            if (path == null || directory == null)
            {
                return false;
            }

            var fullPath = NormalizeDirectory(path);
            var fullDirectory = NormalizeDirectory(directory);

            if (string.Equals(fullPath, fullDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var prefix = fullDirectory.EndsWith(Path.DirectorySeparatorChar)
                ? fullDirectory
                : fullDirectory + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        ///     Refuses drive roots and well-known system / profile folders, so neither the install nor the uninstall can ever
        ///     write into or clean up such a folder.
        /// </summary>
        public static bool IsSafeInstallDirectory(string directory)
        {
            if (directory == null || !Path.IsPathFullyQualified(directory))
            {
                return false;
            }

            string full;
            try
            {
                full = NormalizeDirectory(directory);
            }
            catch (Exception)
            {
                return false;
            }

            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root) || string.Equals(NormalizeDirectory(root), full,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var forbidden = new[]
            {
                Environment.SpecialFolder.Windows, Environment.SpecialFolder.System,
                Environment.SpecialFolder.SystemX86, Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonProgramFiles,
                Environment.SpecialFolder.CommonProgramFilesX86, Environment.SpecialFolder.CommonApplicationData,
                Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Desktop,
                Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory,
                Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.CommonDocuments,
                Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms,
                Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu
            };

            foreach (var specialFolder in forbidden)
            {
                var special = Environment.GetFolderPath(specialFolder);
                if (!string.IsNullOrEmpty(special) && PathsEqual(special, full))
                {
                    return false;
                }
            }

            var usersFolder = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            if (!string.IsNullOrEmpty(usersFolder) && PathsEqual(usersFolder, full))
            {
                return false;
            }

            return true;
        }

        /// <summary>True if the folder contains a complete extracted EasyRadioLink package.</summary>
        public static bool IsPackageComplete(string packageDirectory)
        {
            var complete = true;
            foreach (var required in RequiredPackageFiles)
            {
                if (!File.Exists(Path.Combine(packageDirectory, required)))
                {
                    Logger.Warn($"Package file missing: {required}");
                    complete = false;
                }
            }

            return complete;
        }

        /// <summary>
        ///     True if the folder is an EasyRadioLink installation made by this setup (it has an install manifest) or is the
        ///     registered install location.
        /// </summary>
        public static bool LooksLikeInstallation(string directory)
        {
            if (directory == null || !Directory.Exists(directory))
            {
                return false;
            }

            if (File.Exists(Path.Combine(directory, ManifestFileName)))
            {
                return true;
            }

            var registered = ReadInstalledPath();
            return registered != "" && PathsEqual(registered, directory) &&
                   File.Exists(Path.Combine(directory, ClientFolder, ClientExe));
        }

        #endregion

        #region Registry

        private static RegistryKey OpenLocalMachine()
        {
            return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        }

        /// <summary>Install location of the current installation, or "" if EasyRadioLink is not installed.</summary>
        public static string ReadInstalledPath()
        {
            try
            {
                using var hklm = OpenLocalMachine();
                using var key = hklm.OpenSubKey(RegistryKeyPath);
                return key?.GetValue(InstallPathValue) as string ?? "";
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Unable to read the install path from the registry");
                return "";
            }
        }

        /// <summary>Reads a remembered checkbox value of the previous installation.</summary>
        public static bool ReadInstalledOption(string valueName, bool defaultValue)
        {
            try
            {
                using var hklm = OpenLocalMachine();
                using var key = hklm.OpenSubKey(RegistryKeyPath);
                var value = key?.GetValue(valueName);
                return value is int intValue ? intValue != 0 : defaultValue;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"Unable to read {valueName} from the registry");
                return defaultValue;
            }
        }

        private static void WriteRegistry(InstallOptions options, long installedBytes)
        {
            var installDirectory = options.InstallDirectory;
            using var hklm = OpenLocalMachine();

            using (var key = hklm.CreateSubKey(RegistryKeyPath, true))
            {
                key.SetValue(InstallPathValue, installDirectory, RegistryValueKind.String);
                key.SetValue(VersionValue, Version, RegistryValueKind.String);
                key.SetValue(StartMenuShortcutsValue, options.StartMenuShortcuts ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue(DesktopShortcutValue, options.DesktopShortcut ? 1 : 0, RegistryValueKind.DWord);
            }

            var setupPath = Path.Combine(installDirectory, SetupExeName);
            var clientPath = Path.Combine(installDirectory, ClientFolder, ClientExe);

            using (var key = hklm.CreateSubKey(UninstallKeyPath, true))
            {
                key.SetValue("DisplayName", ProductName, RegistryValueKind.String);
                key.SetValue("DisplayVersion", Version, RegistryValueKind.String);
                key.SetValue("Publisher", Publisher, RegistryValueKind.String);
                key.SetValue("InstallLocation", installDirectory, RegistryValueKind.String);
                key.SetValue("DisplayIcon", clientPath + ",0", RegistryValueKind.String);
                key.SetValue("UninstallString", $"\"{setupPath}\" {UninstallArgument}", RegistryValueKind.String);
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"), RegistryValueKind.String);
                key.SetValue("Comments", "Digital radio voice communication", RegistryValueKind.String);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, installedBytes / 1024),
                    RegistryValueKind.DWord);
            }

            Logger.Info($"Registry entries written ({RegistryKeyPath}, {UninstallKeyPath})");
        }

        private static void DeleteRegistry()
        {
            using var hklm = OpenLocalMachine();
            hklm.DeleteSubKeyTree(UninstallKeyPath, false);
            hklm.DeleteSubKeyTree(RegistryKeyPath, false);
            Logger.Info("Registry entries removed");
        }

        #endregion

        #region Running programs

        /// <summary>Running EasyRadioLink programs (client, server, command-line server). Never includes the setup itself.</summary>
        public static List<Process> FindRunningApps()
        {
            var currentId = Environment.ProcessId;
            var result = new List<Process>();

            foreach (var process in Process.GetProcesses())
            {
                var keep = false;
                try
                {
                    var name = process.ProcessName;
                    keep = process.Id != currentId
                           && name.StartsWith(ProductName, StringComparison.OrdinalIgnoreCase)
                           && !name.StartsWith("EasyRadioLink-Setup", StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception)
                {
                    // process exited while enumerating
                }

                if (keep)
                {
                    result.Add(process);
                }
                else
                {
                    process.Dispose();
                }
            }

            return result;
        }

        /// <summary>Closes (and if needed terminates) every running EasyRadioLink program.</summary>
        public static void CloseRunningApps()
        {
            foreach (var process in FindRunningApps())
            {
                try
                {
                    Logger.Info($"Closing {process.ProcessName} (PID {process.Id})");

                    // Ask nicely first so the client can save its radio state, then terminate.
                    if (process.CloseMainWindow())
                    {
                        process.WaitForExit(5000);
                    }

                    if (!process.HasExited)
                    {
                        Logger.Info($"Terminating {process.ProcessName} (PID {process.Id})");
                        process.Kill(true);
                        process.WaitForExit(5000);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, $"Unable to close process {process.Id}");
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        #endregion

        #region Install

        /// <summary>Installs or updates EasyRadioLink. Throws on fatal errors (details are logged by the caller).</summary>
        public static InstallResult Install(InstallOptions options, Action<string> progress)
        {
            var result = new InstallResult();
            var source = options.PackageDirectory;
            var target = options.InstallDirectory;

            Logger.Info($"Installing EasyRadioLink {Version} from {source} to {target} " +
                        $"(Start menu: {options.StartMenuShortcuts}, desktop: {options.DesktopShortcut})");

            progress(Resources.ProgressClosingApps);
            CloseRunningApps();

            var files = CollectPackageFiles(source);
            if (files.Count == 0)
            {
                throw new InvalidOperationException($"No program files found in {source}");
            }

            progress(Resources.ProgressRemovingOld);

            // A previous installation in another folder is removed (program files only), so it does not stay orphaned.
            var previous = ReadInstalledPath();
            if (previous != "" && !PathsEqual(previous, target) && LooksLikeInstallation(previous) &&
                IsSafeInstallDirectory(previous))
            {
                Logger.Info($"Removing the previous installation at {previous}");
                RemoveProgramFiles(previous);
                DeleteFile(Path.Combine(previous, ManifestFileName));
                RemoveEmptyDirectories(previous, true);
            }

            if (Directory.Exists(target))
            {
                RemoveProgramFiles(target);
            }

            Directory.CreateDirectory(target);

            // Written before copying, so an interrupted install can still be cleaned up by the next run or the uninstaller.
            WriteManifest(target, files.Select(f => f.RelativePath));

            progress(Resources.ProgressCopying);
            long installedBytes = 0;
            foreach (var file in files)
            {
                var destination = Path.Combine(target, file.RelativePath);
                var destinationDirectory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }

                if (File.Exists(destination))
                {
                    File.SetAttributes(destination, FileAttributes.Normal);
                }

                File.Copy(file.SourcePath, destination, true);
                installedBytes += new FileInfo(destination).Length;
            }

            Logger.Info($"Copied {files.Count} files ({installedBytes / 1024} KB)");

            foreach (var folder in UserWritableFolders)
            {
                GrantUserModifyAccess(Path.Combine(target, folder));
            }

            progress(Resources.ProgressRegistry);
            WriteRegistry(options, installedBytes);

            progress(Resources.ProgressShortcuts);
            try
            {
                UpdateShortcuts(target, options.StartMenuShortcuts, options.DesktopShortcut);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Unable to create the shortcuts");
                result.ShortcutsFailed = true;
            }

            progress(Resources.ProgressVcRedist);
            InstallVcRedist(source, result);

            progress(Resources.ProgressDone);
            Logger.Info("Installation finished");
            return result;
        }

        private class PackageFile
        {
            public string SourcePath { get; init; }
            public string RelativePath { get; init; }
        }

        private static List<PackageFile> CollectPackageFiles(string source)
        {
            var files = new List<PackageFile>();

            var setupDirectory = Path.GetDirectoryName(SetupProcessPath) ?? source;
            foreach (var setupFile in SetupFiles)
            {
                var path = Path.Combine(setupDirectory, setupFile);
                if (File.Exists(path))
                {
                    files.Add(new PackageFile { SourcePath = path, RelativePath = setupFile });
                }
            }

            // The uninstall entry points to EasyRadioLink-Setup.exe, also if the downloaded setup was renamed.
            if (!files.Any(f => string.Equals(f.RelativePath, SetupExeName, StringComparison.OrdinalIgnoreCase)))
            {
                files.Add(new PackageFile { SourcePath = SetupProcessPath, RelativePath = SetupExeName });
            }

            foreach (var document in RootDocuments)
            {
                var path = Path.Combine(source, document);
                if (File.Exists(path))
                {
                    files.Add(new PackageFile { SourcePath = path, RelativePath = document });
                }
                else
                {
                    Logger.Warn($"Package document missing: {document}");
                }
            }

            foreach (var folder in ProgramFolders)
            {
                var folderPath = Path.Combine(source, folder);
                if (!Directory.Exists(folderPath))
                {
                    Logger.Warn($"Package folder missing, skipped: {folder}");
                    continue;
                }

                foreach (var path in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories))
                {
                    files.Add(new PackageFile
                    {
                        SourcePath = path,
                        RelativePath = Path.GetRelativePath(source, path)
                    });
                }
            }

            return files;
        }

        /// <summary>
        ///     Gives the installing user write access to the folders whose programs store data next to the executable
        ///     (server.cfg, banned.txt, logs), so the server works without running as administrator.
        /// </summary>
        private static void GrantUserModifyAccess(string directory)
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            try
            {
                var user = WindowsIdentity.GetCurrent().User;
                if (user == null)
                {
                    return;
                }

                var directoryInfo = new DirectoryInfo(directory);
                var security = directoryInfo.GetAccessControl();
                security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.Modify,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None,
                    AccessControlType.Allow));
                directoryInfo.SetAccessControl(security);
                Logger.Info($"Granted modify access on {directory} to {user.Value}");
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"Unable to set permissions on {directory}");
            }
        }

        private static void InstallVcRedist(string source, InstallResult result)
        {
            var installer = Path.Combine(source, VcRedistFileName);
            if (!File.Exists(installer))
            {
                Logger.Warn($"{VcRedistFileName} not found in the package - skipping the Visual C++ runtime");
                return;
            }

            try
            {
                var logFile = Path.Combine(Path.GetTempPath(), "EasyRadioLink-vc_redist.log");
                Logger.Info($"Running {installer}");

                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = installer,
                    Arguments = $"/install /quiet /norestart /log \"{logFile}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (process == null)
                {
                    result.VcRedistFailed = true;
                    return;
                }

                if (!process.WaitForExit(TimeSpan.FromMinutes(10)))
                {
                    Logger.Warn("The Visual C++ runtime installer is still running after 10 minutes - not waiting any longer");
                    return;
                }

                var exitCode = process.ExitCode;
                result.VcRedistExitCode = exitCode;
                Logger.Info($"Visual C++ runtime installer exit code: {exitCode}");

                switch (exitCode)
                {
                    case 0: // installed
                    case 1638: // same or newer version already installed
                        break;
                    case 3010: // installed, restart required
                    case 1641: // installed, restart initiated
                        result.RebootRequired = true;
                        break;
                    default:
                        result.VcRedistFailed = true;
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Unable to install the Visual C++ runtime");
                result.VcRedistFailed = true;
            }
        }

        /// <summary>
        ///     Copies the running setup (and, for a non single-file dev build, its companion files) into
        ///     <paramref name="directory" />. Returns the path of the copied setup executable.
        /// </summary>
        public static string CopySetupTo(string directory)
        {
            Directory.CreateDirectory(directory);
            var setupDirectory = Path.GetDirectoryName(SetupProcessPath) ?? AppContext.BaseDirectory;

            foreach (var setupFile in SetupFiles)
            {
                var source = Path.Combine(setupDirectory, setupFile);
                if (File.Exists(source))
                {
                    File.Copy(source, Path.Combine(directory, setupFile), true);
                }
            }

            // The published setup is a single file named EasyRadioLink-Setup.exe; keep working if it was renamed.
            var target = Path.Combine(directory, SetupExeName);
            if (!File.Exists(target))
            {
                File.Copy(SetupProcessPath, target, true);
            }

            return target;
        }

        #endregion

        #region Shortcuts

        /// <summary>Creates / removes the Start menu folder and the desktop shortcut according to the options.</summary>
        private static void UpdateShortcuts(string installDirectory, bool startMenu, bool desktop)
        {
            RemoveShortcuts();

            var clientExe = Path.Combine(installDirectory, ClientFolder, ClientExe);
            var serverExe = Path.Combine(installDirectory, ServerFolder, ServerExe);
            var setupExe = Path.Combine(installDirectory, SetupExeName);

            if (startMenu)
            {
                var folder = StartMenuFolder;
                Directory.CreateDirectory(folder);

                CreateShortcut(Path.Combine(folder, ProductName + ".lnk"), clientExe, "",
                    "EasyRadioLink - digital radio voice communication");
                CreateShortcut(Path.Combine(folder, ProductName + " Server.lnk"), serverExe, "",
                    "EasyRadioLink Server");
                CreateShortcut(Path.Combine(folder, "Uninstall " + ProductName + ".lnk"), setupExe,
                    UninstallArgument, "Uninstall EasyRadioLink");
            }

            if (desktop)
            {
                CreateShortcut(DesktopShortcutPath, clientExe, "",
                    "EasyRadioLink - digital radio voice communication");
            }
        }

        private static void CreateShortcut(string linkPath, string targetPath, string arguments, string description)
        {
            if (!File.Exists(targetPath))
            {
                Logger.Warn($"Shortcut target missing, skipped: {targetPath}");
                return;
            }

            Logger.Info($"Creating shortcut {linkPath} -> {targetPath} {arguments}");
            ShortcutHelper.CreateShortcut(linkPath, targetPath, Path.GetDirectoryName(targetPath), arguments, "",
                ShortcutHelper.ShortcutWindowStyles.WshNormalFocus, description);
        }

        private static void RemoveShortcuts()
        {
            var folder = StartMenuFolder;
            if (Directory.Exists(folder))
            {
                // Only our own .lnk files, then the folder if it is empty.
                foreach (var link in Directory.EnumerateFiles(folder, "*.lnk"))
                {
                    DeleteFile(link);
                }

                DeleteDirectoryIfEmpty(folder);
            }

            DeleteFile(DesktopShortcutPath);
        }

        #endregion

        #region Uninstall

        /// <summary>
        ///     Removes only the shortcuts and the registry entries (used when the registered install folder no longer
        ///     contains EasyRadioLink, so "Apps &amp; Features" does not keep a dead entry).
        /// </summary>
        public static void RemoveRegistration()
        {
            try
            {
                RemoveShortcuts();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Unable to remove the shortcuts");
            }

            DeleteRegistry();
        }

        /// <summary>
        ///     Files in the program folders that were not installed by the setup, e.g. server.cfg, banned.txt, Presets\ and
        ///     logs written by the server. Relative to the install folder.
        /// </summary>
        public static List<string> FindUserFiles(string installDirectory)
        {
            var result = new List<string>();
            var manifest = ReadManifest(installDirectory);

            foreach (var folder in ProgramFolders)
            {
                var folderPath = Path.Combine(installDirectory, folder);
                if (!Directory.Exists(folderPath))
                {
                    continue;
                }

                try
                {
                    foreach (var path in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories))
                    {
                        var relative = Path.GetRelativePath(installDirectory, path);
                        var installedBySetup = manifest != null
                            ? manifest.Contains(relative)
                            : IsKnownProgramFile(relative);
                        if (!installedBySetup)
                        {
                            result.Add(relative);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, $"Unable to list {folderPath}");
                }
            }

            return result;
        }

        /// <summary>Uninstalls EasyRadioLink from the folder. Throws on fatal errors (details are logged by the caller).</summary>
        public static UninstallResult Uninstall(string installDirectory, bool deleteUserFiles, Action<string> progress)
        {
            var result = new UninstallResult();
            Logger.Info($"Uninstalling EasyRadioLink from {installDirectory} (delete user files: {deleteUserFiles})");

            if (!IsSafeInstallDirectory(installDirectory))
            {
                throw new InvalidOperationException($"Refusing to uninstall from unsafe folder {installDirectory}");
            }

            progress(Resources.ProgressClosingApps);
            CloseRunningApps();

            progress(Resources.ProgressRemoving);
            try
            {
                RemoveShortcuts();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Unable to remove the shortcuts");
            }

            if (Directory.Exists(installDirectory))
            {
                result.FailedFiles = RemoveProgramFiles(installDirectory);

                if (deleteUserFiles)
                {
                    foreach (var folder in ProgramFolders)
                    {
                        var folderPath = Path.Combine(installDirectory, folder);
                        try
                        {
                            if (Directory.Exists(folderPath))
                            {
                                Directory.Delete(folderPath, true);
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn(ex, $"Unable to delete {folderPath}");
                        }
                    }
                }

                // Keep the manifest if program files are left (locked), so a second uninstall can still remove them.
                if (result.FailedFiles == 0)
                {
                    DeleteFile(Path.Combine(installDirectory, ManifestFileName));
                }

                DeleteFile(Path.Combine(installDirectory, InstallerLogFileName));
                DeleteFile(Path.Combine(installDirectory, InstallerOldLogFileName));

                RemoveEmptyDirectories(installDirectory, true);

                if (Directory.Exists(installDirectory))
                {
                    result.RemainingDirectory = installDirectory;
                    Logger.Info($"Folder kept (not empty): {installDirectory}");
                }
            }

            progress(Resources.ProgressRegistry);
            DeleteRegistry();

            progress(Resources.ProgressDone);
            Logger.Info($"Uninstall finished ({result.FailedFiles} files could not be deleted)");
            return result;
        }

        #endregion

        #region Manifest and file helpers

        private static void WriteManifest(string installDirectory, IEnumerable<string> relativePaths)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"# EasyRadioLink {Version} - files installed by {SetupExeName}.");
            builder.AppendLine("# Used to update and uninstall EasyRadioLink. Do not edit.");
            foreach (var relative in relativePaths)
            {
                builder.AppendLine(relative);
            }

            builder.AppendLine(ManifestFileName);

            var path = Path.Combine(installDirectory, ManifestFileName);
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }

            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        }

        /// <summary>Relative paths from the install manifest, or null if there is none.</summary>
        private static HashSet<string> ReadManifest(string installDirectory)
        {
            var path = Path.Combine(installDirectory, ManifestFileName);
            if (!File.Exists(path))
            {
                return null;
            }

            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadAllLines(path))
            {
                var relative = line.Trim();
                if (relative.Length == 0 || relative.StartsWith('#') || Path.IsPathRooted(relative))
                {
                    continue;
                }

                // Never follow entries that point outside the install folder.
                var full = Path.GetFullPath(Path.Combine(installDirectory, relative));
                if (IsInsideDirectory(full, installDirectory) && !PathsEqual(full, installDirectory))
                {
                    result.Add(Path.GetRelativePath(installDirectory, full));
                }
            }

            return result;
        }

        /// <summary>
        ///     Deletes the program files of an installation (from its manifest, or by known names for an installation without
        ///     manifest). Files created by the user or the programs stay. Returns the number of files that could not be deleted.
        /// </summary>
        private static int RemoveProgramFiles(string installDirectory)
        {
            var failed = 0;
            var manifest = ReadManifest(installDirectory);

            if (manifest != null)
            {
                Logger.Info($"Removing {manifest.Count} program files listed in {ManifestFileName}");
                foreach (var relative in manifest)
                {
                    if (string.Equals(relative, ManifestFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!DeleteFile(Path.Combine(installDirectory, relative)))
                    {
                        failed++;
                    }
                }
            }
            else
            {
                Logger.Info($"No {ManifestFileName} in {installDirectory} - removing known program files");
                foreach (var setupFile in SetupFiles)
                {
                    DeleteFile(Path.Combine(installDirectory, setupFile));
                }

                foreach (var document in RootDocuments)
                {
                    DeleteFile(Path.Combine(installDirectory, document));
                }

                foreach (var folder in ProgramFolders)
                {
                    var folderPath = Path.Combine(installDirectory, folder);
                    if (!Directory.Exists(folderPath))
                    {
                        continue;
                    }

                    foreach (var path in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories)
                                 .ToList())
                    {
                        if (IsKnownProgramFile(Path.GetRelativePath(installDirectory, path)) && !DeleteFile(path))
                        {
                            failed++;
                        }
                    }
                }
            }

            RemoveEmptyDirectories(installDirectory, false);
            return failed;
        }

        /// <summary>
        ///     Recognises program files inside the program folders by name. Only used for an installation without manifest;
        ///     otherwise the manifest is authoritative.
        /// </summary>
        private static bool IsKnownProgramFile(string relativePath)
        {
            var segments = relativePath.Split(Path.DirectorySeparatorChar);
            if (segments.Length < 2)
            {
                return false;
            }

            var fileName = segments[^1];
            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            var inClient = string.Equals(segments[0], ClientFolder, StringComparison.OrdinalIgnoreCase);

            if (segments.Any(s => string.Equals(s, "runtimes", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (inClient && segments.Length > 2 &&
                (string.Equals(segments[1], "AudioEffects", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(segments[1], "RadioModels", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (inClient && string.Equals(fileName, "radios.json", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(fileName, CliLinuxExe, StringComparison.Ordinal))
            {
                return true;
            }

            return extension is ".exe" or ".dll" or ".so" or ".pdb" ||
                   fileName.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) ||
                   fileName.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Deletes empty folders below the program folders (and the install folder itself if requested).</summary>
        private static void RemoveEmptyDirectories(string installDirectory, bool includeRoot)
        {
            foreach (var folder in ProgramFolders)
            {
                var folderPath = Path.Combine(installDirectory, folder);
                if (Directory.Exists(folderPath))
                {
                    RemoveEmptyDirectoriesRecursive(folderPath);
                }
            }

            if (includeRoot)
            {
                DeleteDirectoryIfEmpty(installDirectory);
            }
        }

        private static void RemoveEmptyDirectoriesRecursive(string directory)
        {
            try
            {
                foreach (var subDirectory in Directory.GetDirectories(directory))
                {
                    RemoveEmptyDirectoriesRecursive(subDirectory);
                }

                DeleteDirectoryIfEmpty(directory);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"Unable to clean up {directory}");
            }
        }

        private static void DeleteDirectoryIfEmpty(string directory)
        {
            try
            {
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"Unable to delete folder {directory}");
            }
        }

        /// <summary>Deletes a file if it exists. Returns false only if the file exists and could not be deleted.</summary>
        private static bool DeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"Unable to delete {path}");
                return false;
            }
        }

        #endregion
    }
}
