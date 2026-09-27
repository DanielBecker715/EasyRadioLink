using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using EasyRadioLink.Client.Properties;
using EasyRadioLink.Client.Singletons;
using EasyRadioLink.Client.Utils;
using EasyRadioLink.Common.Helpers;
using EasyRadioLink.Common.Settings;
using NLog;

namespace EasyRadioLink.Client.GameIntegration.Msfs;

/// <summary>
///     Microsoft Flight Simulator 2024 over SimConnect (<see cref="SimConnectLibrary" />). While the simulator runs a
///     background thread connects to it (retrying until the simulator accepts connections) and
///     <list type="bullet">
///         <item>
///             keeps the radio and COM1 of the user's aircraft on the same frequency (<see cref="ComFrequencySync" />,
///             setting <see cref="GlobalSettingsKeys.MsfsRadioSync" />) - only while EasyRadioLink is connected to a
///             server, since the radio is off otherwise;
///         </item>
///         <item>
///             sets the background sound by the aircraft: prop, jet or helicopter (<see cref="AircraftBackgroundSound" />,
///             <see cref="ClientStateSingleton.BackgroundSoundOverride" />, setting
///             <see cref="GlobalSettingsKeys.MsfsAircraftBackgroundSound" />).
///         </item>
///     </list>
///     Both settings are read continuously, so switching them takes effect at once.
/// </summary>
public sealed class MsfsIntegration : IGameIntegration
{
    private const string ClientName = "EasyRadioLink";

    // how long to wait before trying again when SimConnect.dll is missing / the simulator refuses the connection
    private static readonly TimeSpan LibraryRetryInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ConnectRetryInterval = TimeSpan.FromSeconds(5);

    // longest wait for a message before the radios are compared again
    private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(100);

    // "COM_RADIO_SET_HZ": COM1 active frequency in Hz
    private const string ComSetEventName = "COM_RADIO_SET_HZ";

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly GlobalSettingsStore _settings = GlobalSettingsStore.Instance;
    private readonly ComFrequencySync _comSync = new();

    private CancellationTokenSource _cts;
    private Thread _thread;

    private volatile string _status = "";

    // received from the simulator (integration thread only)
    private double _simComFrequency = double.NaN;
    private string _aircraftTitle;
    private string _aircraftSound;
    private bool _aircraftKnown;

    public string GameName => "Microsoft Flight Simulator 2024";

    public IReadOnlyList<string> ProcessNames { get; } = new[] { "FlightSimulator2024" };

    public bool IsEnabled => RadioSyncEnabled || BackgroundSoundEnabled;

    public string Status => _status;

    private bool RadioSyncEnabled => _settings.GetClientSettingBool(GlobalSettingsKeys.MsfsRadioSync);

    private bool BackgroundSoundEnabled =>
        _settings.GetClientSettingBool(GlobalSettingsKeys.MsfsAircraftBackgroundSound);

    public void Start()
    {
        if (_thread != null) return;

        _status = Resources.MsfsStatusConnecting;
        _cts = new CancellationTokenSource();

        var token = _cts.Token;
        _thread = new Thread(() => Run(token))
        {
            IsBackground = true,
            Name = "MSFS SimConnect"
        };
        _thread.Start();
    }

    public void Stop()
    {
        if (_thread == null) return;

        _cts.Cancel();
        if (!_thread.Join(TimeSpan.FromSeconds(2))) Logger.Warn("The SimConnect thread did not stop in time");

        _cts.Dispose();
        _cts = null;
        _thread = null;

        ClientStateSingleton.Instance.BackgroundSoundOverride = null;
        _status = "";
    }

    private void Run(CancellationToken token)
    {
        using var messageEvent = new AutoResetEvent(false);

        while (!token.IsCancellationRequested)
        {
            var library = SimConnectLibrary.TryLoad();
            if (library == null)
            {
                _status = Resources.MsfsStatusNoSimConnect;
                token.WaitHandle.WaitOne(LibraryRetryInterval);
                continue;
            }

            try
            {
                var handle = Connect(library, messageEvent);
                if (handle == IntPtr.Zero)
                {
                    // the simulator is still starting up
                    _status = Resources.MsfsStatusConnecting;
                    token.WaitHandle.WaitOne(ConnectRetryInterval);
                    continue;
                }

                try
                {
                    Logger.Info("Connected to Microsoft Flight Simulator");
                    _status = Resources.MsfsStatusConnected;

                    Pump(library, handle, messageEvent, token);
                }
                finally
                {
                    library.Close(handle);
                    OnDisconnected();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "SimConnect failed");
                _status = Resources.MsfsStatusConnecting;
                token.WaitHandle.WaitOne(ConnectRetryInterval);
            }
        }
    }

    /// <summary>Opens the connection and requests the data. Returns 0 if the simulator does not accept it (yet).</summary>
    private static IntPtr Connect(SimConnectLibrary library, AutoResetEvent messageEvent)
    {
        if (library.Open(out var handle, ClientName, IntPtr.Zero, 0,
                messageEvent.SafeWaitHandle.DangerousGetHandle(), 0) != SimConnectLibrary.S_OK
            || handle == IntPtr.Zero)
            return IntPtr.Zero;

        try
        {
            Check(library.AddToDataDefinition(handle, (uint)Definition.Com, "COM ACTIVE FREQUENCY:1", "MHz",
                SimConnectDataType.Float64, 0, SimConnectConstants.Unused), "COM ACTIVE FREQUENCY");

            // keep the order in sync with ReadAircraft
            Check(library.AddToDataDefinition(handle, (uint)Definition.Aircraft, "ENGINE TYPE", "Enum",
                SimConnectDataType.Int32, 0, SimConnectConstants.Unused), "ENGINE TYPE");
            Check(library.AddToDataDefinition(handle, (uint)Definition.Aircraft, "CATEGORY", null,
                SimConnectDataType.String256, 0, SimConnectConstants.Unused), "CATEGORY");
            Check(library.AddToDataDefinition(handle, (uint)Definition.Aircraft, "TITLE", null,
                SimConnectDataType.String256, 0, SimConnectConstants.Unused), "TITLE");

            Check(library.MapClientEventToSimEvent(handle, (uint)ClientEvent.ComSet, ComSetEventName),
                ComSetEventName);

            // COM1 every simulation frame, the aircraft every second - both only when something changed
            Check(library.RequestDataOnSimObject(handle, (uint)Request.Com, (uint)Definition.Com,
                SimConnectConstants.ObjectIdUser, SimConnectPeriod.SimFrame,
                SimConnectConstants.DataRequestFlagChanged, 0, 0, 0), "COM request");
            Check(library.RequestDataOnSimObject(handle, (uint)Request.Aircraft, (uint)Definition.Aircraft,
                SimConnectConstants.ObjectIdUser, SimConnectPeriod.Second,
                SimConnectConstants.DataRequestFlagChanged, 0, 0, 0), "aircraft request");

            return handle;
        }
        catch
        {
            library.Close(handle);
            throw;
        }
    }

    private static void Check(int hresult, string what)
    {
        if (hresult != SimConnectLibrary.S_OK)
            throw new InvalidOperationException($"SimConnect: {what} failed (0x{hresult:X8})");
    }

    /// <summary>Handles messages and syncs the radio until the simulator quits or the integration is stopped.</summary>
    private void Pump(SimConnectLibrary library, IntPtr handle, AutoResetEvent messageEvent, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            WaitHandle.WaitAny(new[] { messageEvent, token.WaitHandle }, PumpInterval);

            // drain: GetNextDispatch fails once no message is waiting
            while (library.GetNextDispatch(handle, out var data, out _) == SimConnectLibrary.S_OK && data != IntPtr.Zero)
                if (!Handle(data))
                    return;

            if (token.IsCancellationRequested) return;

            UpdateBackgroundSound();
            SyncRadio(library, handle);
        }
    }

    /// <returns>false if the simulator quit</returns>
    private bool Handle(IntPtr data)
    {
        var id = (SimConnectRecvId)Marshal.ReadInt32(data, 8);

        switch (id)
        {
            case SimConnectRecvId.Quit:
                Logger.Info("Microsoft Flight Simulator closed the SimConnect connection");
                return false;

            case SimConnectRecvId.Exception:
                Logger.Warn($"SimConnect exception {Marshal.ReadInt32(data, SimConnectConstants.ExceptionCodeOffset)}");
                break;

            case SimConnectRecvId.SimObjectData:
                var request = (Request)Marshal.ReadInt32(data, SimConnectConstants.SimObjectDataRequestIdOffset);
                var values = data + SimConnectConstants.SimObjectDataOffset;

                if (request == Request.Com) ReadCom(values);
                else if (request == Request.Aircraft) ReadAircraft(values);
                break;
        }

        return true;
    }

    private void ReadCom(IntPtr values)
    {
        var mhz = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(values));
        if (double.IsFinite(mhz) && mhz > 0) _simComFrequency = Math.Round(mhz * RadioCalculator.MHz);
    }

    private void ReadAircraft(IntPtr values)
    {
        var engineType = Marshal.ReadInt32(values);
        var category = ReadString256(values + 4);
        var title = ReadString256(values + 4 + 256);

        var sound = AircraftBackgroundSound.ForAircraft(category, engineType);

        if (!_aircraftKnown || title != _aircraftTitle || sound != _aircraftSound)
            Logger.Info($"Aircraft: {title} (category {category}, engine type {engineType}) - background sound {sound ?? "from the profile"}");

        _aircraftKnown = true;
        _aircraftTitle = title;
        _aircraftSound = sound;

        _status = string.Format(Resources.MsfsStatusAircraft, title.Length > 0 ? title : category,
            sound ?? Resources.MsfsStatusProfileSound);
    }

    private static string ReadString256(IntPtr value)
    {
        var bytes = new byte[256];
        Marshal.Copy(value, bytes, 0, bytes.Length);

        var length = Array.IndexOf(bytes, (byte)0);
        if (length < 0) length = bytes.Length;

        return Encoding.UTF8.GetString(bytes, 0, length).Trim();
    }

    private void UpdateBackgroundSound()
    {
        ClientStateSingleton.Instance.BackgroundSoundOverride =
            BackgroundSoundEnabled && _aircraftKnown ? _aircraftSound : null;
    }

    private void SyncRadio(SimConnectLibrary library, IntPtr handle)
    {
        // the radio is only on while connected to a server
        var radio = RadioSyncEnabled && !double.IsNaN(_simComFrequency) ? RadioHelper.GetRadio() : null;
        if (radio == null)
        {
            // the next time both radios are on it is a first contact again
            _comSync.Reset();
            return;
        }

        var action = _comSync.Update(_simComFrequency, radio.freq, DateTime.UtcNow);

        switch (action.Direction)
        {
            case ComSyncDirection.SimToApp:
                Logger.Info($"COM1 tuned to {RadioCalculator.FormatMHz(action.FrequencyHz)} MHz - tuning the radio");
                if (!RadioHelper.SetFrequency(action.FrequencyHz)) _comSync.Reset();
                break;

            case ComSyncDirection.AppToSim:
                Logger.Info($"Radio tuned to {RadioCalculator.FormatMHz(action.FrequencyHz)} MHz - tuning COM1");
                var result = library.TransmitClientEvent(handle, SimConnectConstants.ObjectIdUser,
                    (uint)ClientEvent.ComSet, (uint)Math.Round(action.FrequencyHz),
                    SimConnectConstants.GroupPriorityHighest, SimConnectConstants.EventFlagGroupIdIsPriority);
                if (result != SimConnectLibrary.S_OK)
                {
                    Logger.Warn($"Unable to tune COM1 (0x{result:X8})");
                    _comSync.Reset();
                }

                break;
        }
    }

    private void OnDisconnected()
    {
        _simComFrequency = double.NaN;
        _aircraftKnown = false;
        _aircraftTitle = null;
        _aircraftSound = null;
        _comSync.Reset();

        ClientStateSingleton.Instance.BackgroundSoundOverride = null;
    }

    private enum Definition : uint
    {
        Com = 1,
        Aircraft = 2
    }

    private enum Request : uint
    {
        Com = 1,
        Aircraft = 2
    }

    private enum ClientEvent : uint
    {
        ComSet = 1
    }
}
