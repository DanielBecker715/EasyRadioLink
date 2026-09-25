using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Threading;
using EasyRadioLink.Common.Network.Singletons;
using MahApps.Metro.Controls;
using NLog;

namespace EasyRadioLink.Client.UI.ClientWindow.ClientList;

/// <summary>
///     "Connected Users": everybody on the server, sorted by name, with recording permission and a local mute.
///     Refreshed every 3 seconds.
/// </summary>
public partial class ClientListWindow : MetroWindow
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly ObservableCollection<ClientListItem> _clientList = new();
    private readonly DispatcherTimer _updateTimer;

    public ClientListWindow()
    {
        InitializeComponent();
        ClientList.ItemsSource = _clientList;
        UpdateList();

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _updateTimer.Tick += UpdateTimer_Tick;
        _updateTimer.Start();
    }

    private void UpdateList()
    {
        var clients = ConnectedClientsSingleton.Instance.Values
            .Where(client => client != null)
            .Select(client => new ClientListItem(client))
            .OrderBy(client => client.Name ?? "", StringComparer.OrdinalIgnoreCase)
            .ToList();

        _clientList.Clear();
        foreach (var client in clients) _clientList.Add(client);

        ClientCountText.Text = string.Format(Properties.Resources.ClientListCount, clients.Count);
    }

    private void UpdateTimer_Tick(object sender, EventArgs e)
    {
        try
        {
            UpdateList();
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Unable to refresh the user list");
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        _updateTimer?.Stop();
    }
}
