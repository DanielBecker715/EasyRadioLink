using System;
using System.Threading;
using System.Threading.Tasks;
using Caliburn.Micro;
using EasyRadioLink.Client.Network.Models;
using EasyRadioLink.Client.Radios;
using EasyRadioLink.Common;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Models;
using EasyRadioLink.Common.Models.EventMessages;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Network.Singletons;
using EasyRadioLink.Common.Settings;
using EasyRadioLink.Common.Settings.Setting;
using RadioReceivingState = EasyRadioLink.Common.Models.RadioReceivingState;

namespace EasyRadioLink.Client.Singletons;

/// <summary>
///     Application wide client state: the local radio, TX/RX indicators, identity and connection status.
/// </summary>
public sealed class ClientStateSingleton : PropertyChangedBaseClass, IHandle<TCPClientStatusMessage>
{
    // used when the user never entered a name
    private const string FallbackName = "Operator";

    // longest name the server accepts
    private const int MaxNameLength = 64;

    private static volatile ClientStateSingleton _instance;
    private static readonly object _lock = new();

    private bool isConnected;

    private bool isConnectionErrored;

    private string _lastSeenName;

    private ClientStateSingleton()
    {
        RadioSendingState = new RadioSendingState();
        RadioReceivingState = new RadioReceivingState[Constants.MAX_RADIOS];

        ShortGUID = ShortGuid.NewGuid();
        PlayerRadioInfo = new PlayerRadioInfo();

        LastSent = 0;

        IsConnected = false;

        _lastSeenName = GlobalSettingsStore.Instance.GetClientSetting(GlobalSettingsKeys.LastSeenName).RawValue;
        if (string.IsNullOrWhiteSpace(_lastSeenName)) _lastSeenName = DefaultName;

        EventBus.Instance.SubscribeOnUIThread(this);
    }

    /// <summary>The local radio (one instance for the whole application).</summary>
    public PlayerRadioInfo PlayerRadioInfo { get; }

    /// <summary>
    ///     Ticks of the last RADIO_UPDATE sent by <see cref="RadioStateSyncService" />. Setting it to 0 marks the
    ///     radio state as dirty and forces an update within 200 ms.
    /// </summary>
    public long LastSent { get; set; }

    public RadioSendingState RadioSendingState { get; set; }

    // indexed by radio slot (0..10) - only PlayerRadioInfo.RadioId is used
    public RadioReceivingState[] RadioReceivingState { get; }

    public bool IsConnected
    {
        get => isConnected;
        set
        {
            isConnected = value;
            NotifyPropertyChanged();
        }
    }

    public string ShortGUID { get; }

    public bool IsConnectionErrored
    {
        get => isConnectionErrored;
        set
        {
            isConnectionErrored = value;
            NotifyPropertyChanged();
        }
    }

    /// <summary>Name suggested for new users: the Windows user name.</summary>
    public static string DefaultName
    {
        get
        {
            try
            {
                var name = Environment.UserName?.Trim();
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch (Exception)
            {
                // not available
            }

            return FallbackName;
        }
    }

    /// <summary>
    ///     The display name as entered by the user (persisted in global.cfg <c>LastSeenName</c>). Not trimmed so it can be
    ///     bound to a text box - use <see cref="EffectiveName" /> for the name that is actually sent.
    /// </summary>
    public string LastSeenName
    {
        get => _lastSeenName ?? "";
        set
        {
            value ??= "";
            if (string.Equals(value, _lastSeenName, StringComparison.Ordinal)) return;

            _lastSeenName = value;
            GlobalSettingsStore.Instance.SetClientSetting(GlobalSettingsKeys.LastSeenName, value, true);
            NotifyPropertyChanged();
            NotifyPropertyChanged(nameof(EffectiveName));
        }
    }

    /// <summary>The name shown to other users: trimmed, at most 64 characters, never empty.</summary>
    public string EffectiveName
    {
        get
        {
            var name = _lastSeenName?.Trim();
            if (string.IsNullOrEmpty(name)) name = DefaultName;
            if (name.Length > MaxNameLength) name = name.Substring(0, MaxNameLength).Trim();

            return name;
        }
    }

    public static ClientStateSingleton Instance
    {
        get
        {
            if (_instance == null)
                lock (_lock)
                {
                    if (_instance == null)
                        _instance = new ClientStateSingleton();
                }

            return _instance;
        }
    }

    /// <summary>The server allows showing the number of users on a frequency (SHOW_TUNED_COUNT).</summary>
    public static bool ShowTunedCount => SyncedServerSettings.Instance.GetSettingAsBool(ServerSettingsKeys.SHOW_TUNED_COUNT);

    public Task HandleAsync(TCPClientStatusMessage message, CancellationToken cancellationToken)
    {
        IsConnected = message.Connected;

        if (!message.Connected && message.Error != TCPClientStatusMessage.ErrorCode.USER_DISCONNECTED)
            IsConnectionErrored = true;
        else
            IsConnectionErrored = false;

        return Task.CompletedTask;
    }

    /// <summary>
    ///     Number of other users with a radio tuned to <paramref name="freq" /> / <paramref name="modulation" />
    ///     (0 unless the server enables SHOW_TUNED_COUNT - see <see cref="ShowTunedCount" />).
    /// </summary>
    public int ClientsOnFreq(double freq, Modulation modulation)
    {
        if (!ShowTunedCount) return 0;

        var count = 0;

        foreach (var client in ConnectedClientsSingleton.Instance.Clients)
        {
            if (client.Key.Equals(ShortGUID)) continue;

            var radioInfo = client.Value?.RadioInfo;
            if (radioInfo == null) continue;

            var receivingRadio = radioInfo.CanHearTransmission(freq, modulation, 0, null, out _, out _);

            if (receivingRadio != null) count++;
        }

        return count;
    }
}
