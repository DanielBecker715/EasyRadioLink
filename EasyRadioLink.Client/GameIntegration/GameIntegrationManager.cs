using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using EasyRadioLink.Client.GameIntegration.Msfs;
using EasyRadioLink.Client.Properties;
using EasyRadioLink.Common.Helpers;
using NLog;

namespace EasyRadioLink.Client.GameIntegration;

/// <summary>
///     Detects supported games: every <see cref="PollInterval" /> it looks for the processes of each
///     <see cref="Integrations" /> entry, starts the integration when its game is running (and it is switched on in the
///     settings) and stops it when the game exits or it is switched off. <see cref="Status" /> tells the settings tab
///     what is going on. Started by the main window, stopped when EasyRadioLink closes.
/// </summary>
public sealed class GameIntegrationManager : PropertyChangedBaseClass
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private static readonly Lazy<GameIntegrationManager> LazyInstance = new(() => new GameIntegrationManager());

    // Poll runs on the timer thread, Stop on the UI thread
    private readonly object _sync = new();
    private readonly HashSet<IGameIntegration> _running = new();

    private string _status = "";
    private Timer _timer;

    private GameIntegrationManager()
    {
        Integrations = new IGameIntegration[] { new MsfsIntegration() };
        UpdateStatus();
    }

    public static GameIntegrationManager Instance => LazyInstance.Value;

    public IReadOnlyList<IGameIntegration> Integrations { get; }

    /// <summary>One line for the settings tab: the running integrations' status, or what is waited for.</summary>
    public string Status
    {
        get => _status;
        private set
        {
            if (string.Equals(value, _status, StringComparison.Ordinal)) return;

            _status = value;
            NotifyPropertyChanged();
        }
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_timer != null) return;

            _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, PollInterval);
        }

        Logger.Info("Game detection started");
    }

    /// <summary>Checks the games now (e.g. after an integration was switched on or off in the settings).</summary>
    public void Refresh()
    {
        lock (_sync)
        {
            _timer?.Change(TimeSpan.Zero, PollInterval);
        }
    }

    /// <summary>Stops the detection and every running integration.</summary>
    public void Stop()
    {
        lock (_sync)
        {
            _timer?.Dispose();
            _timer = null;

            foreach (var integration in _running) StopIntegration(integration);
            _running.Clear();

            UpdateStatus();
        }
    }

    private void Poll()
    {
        lock (_sync)
        {
            if (_timer == null) return;

            foreach (var integration in Integrations)
            {
                var run = integration.IsEnabled && IsRunning(integration.ProcessNames);

                if (run && _running.Add(integration))
                {
                    Logger.Info($"{integration.GameName} detected - starting the integration");
                    try
                    {
                        integration.Start();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, $"Unable to start the {integration.GameName} integration");
                    }
                }
                else if (!run && _running.Remove(integration))
                {
                    Logger.Info($"{integration.GameName} exited or switched off - stopping the integration");
                    StopIntegration(integration);
                }
            }

            UpdateStatus();
        }
    }

    private static void StopIntegration(IGameIntegration integration)
    {
        try
        {
            integration.Stop();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Unable to stop the {integration.GameName} integration");
        }
    }

    private void UpdateStatus()
    {
        if (_running.Count > 0)
        {
            Status = string.Join(Environment.NewLine, _running.Select(integration => integration.Status));
            return;
        }

        var enabled = Integrations.Where(integration => integration.IsEnabled).Select(i => i.GameName).ToList();

        Status = enabled.Count == 0
            ? Resources.GameIntegrationStatusOff
            : string.Format(Resources.GameIntegrationStatusWaiting, string.Join(", ", enabled));
    }

    /// <summary>True if a process with one of <paramref name="processNames" /> runs.</summary>
    private static bool IsRunning(IEnumerable<string> processNames)
    {
        foreach (var name in processNames)
        {
            var processes = Process.GetProcessesByName(name);
            var found = processes.Length > 0;
            foreach (var process in processes) process.Dispose();

            if (found) return true;
        }

        return false;
    }
}
