using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using EasyRadioLink.Common.Helpers;
using NLog;

namespace EasyRadioLink.Client.GameIntegration.Msfs;

/// <summary>
///     The native SimConnect client library (<c>SimConnect.dll</c> of the Microsoft Flight Simulator 2024 SDK). The
///     simulator itself only contains the SimConnect server, so the client ships the library next to EasyRadioLink.exe
///     (proprietary Microsoft code, see THIRD-PARTY-NOTICES.txt). It is loaded at run time: EasyRadioLink runs
///     without it, only the simulator integration needs it. Searched in <see cref="CandidatePaths" /> order - the
///     program folder first, then the SDK folders (development builds). Only the few functions the integration uses are
///     bound.
/// </summary>
internal sealed class SimConnectLibrary
{
    public const string FileName = "SimConnect.dll";

    public const int S_OK = 0;

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private static readonly object LoadLock = new();
    private static SimConnectLibrary _loaded;

    private SimConnectLibrary(IntPtr library, string path)
    {
        Path = path;
        Open = Bind<OpenFn>(library, "SimConnect_Open");
        Close = Bind<CloseFn>(library, "SimConnect_Close");
        AddToDataDefinition = Bind<AddToDataDefinitionFn>(library, "SimConnect_AddToDataDefinition");
        RequestDataOnSimObject = Bind<RequestDataOnSimObjectFn>(library, "SimConnect_RequestDataOnSimObject");
        GetNextDispatch = Bind<GetNextDispatchFn>(library, "SimConnect_GetNextDispatch");
        MapClientEventToSimEvent = Bind<MapClientEventToSimEventFn>(library, "SimConnect_MapClientEventToSimEvent");
        TransmitClientEvent = Bind<TransmitClientEventFn>(library, "SimConnect_TransmitClientEvent");
    }

    public string Path { get; }

    public OpenFn Open { get; }
    public CloseFn Close { get; }
    public AddToDataDefinitionFn AddToDataDefinition { get; }
    public RequestDataOnSimObjectFn RequestDataOnSimObject { get; }
    public GetNextDispatchFn GetNextDispatch { get; }
    public MapClientEventToSimEventFn MapClientEventToSimEvent { get; }
    public TransmitClientEventFn TransmitClientEvent { get; }

    /// <summary>
    ///     Where SimConnect.dll is looked for: the program folder, the MSFS 2024 / 2020 SDK (environment variables
    ///     <c>MSFS2024_SDK</c> / <c>MSFS_SDK</c> set by the SDK installer, then the default install folders).
    /// </summary>
    public static IEnumerable<string> CandidatePaths()
    {
        yield return System.IO.Path.Combine(AppPaths.ProgramDirectory, FileName);

        foreach (var variable in new[] { "MSFS2024_SDK", "MSFS_SDK" })
        {
            var sdk = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(sdk))
                yield return System.IO.Path.Combine(sdk.Trim().Trim('"'), "SimConnect SDK", "lib", FileName);
        }

        yield return System.IO.Path.Combine(@"C:\MSFS 2024 SDK", "SimConnect SDK", "lib", FileName);
        yield return System.IO.Path.Combine(@"C:\MSFS SDK", "SimConnect SDK", "lib", FileName);
    }

    /// <summary>The loaded library, or null if SimConnect.dll was not found or is not usable (retried on every call).</summary>
    public static SimConnectLibrary TryLoad()
    {
        lock (LoadLock)
        {
            if (_loaded != null) return _loaded;

            foreach (var path in CandidatePaths())
            {
                if (!File.Exists(path)) continue;

                if (!NativeLibrary.TryLoad(path, out var handle))
                {
                    Logger.Warn($"Unable to load {path}");
                    continue;
                }

                try
                {
                    _loaded = new SimConnectLibrary(handle, path);
                    Logger.Info($"Loaded {path}");
                    return _loaded;
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, $"{path} is not a usable SimConnect library");
                    NativeLibrary.Free(handle);
                }
            }

            return null;
        }
    }

    private static T Bind<T>(IntPtr library, string name) where T : Delegate
    {
        return Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
    }

    // SimConnect.h - x64 has one calling convention; strings are ANSI

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public delegate int OpenFn(out IntPtr handle, string name, IntPtr hWnd, uint userEventWin32, IntPtr eventHandle,
        uint configIndex);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int CloseFn(IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public delegate int AddToDataDefinitionFn(IntPtr handle, uint defineId, string datumName, string unitsName,
        SimConnectDataType datumType, float epsilon, uint datumId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int RequestDataOnSimObjectFn(IntPtr handle, uint requestId, uint defineId, uint objectId,
        SimConnectPeriod period, uint flags, uint origin, uint interval, uint limit);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int GetNextDispatchFn(IntPtr handle, out IntPtr data, out uint size);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public delegate int MapClientEventToSimEventFn(IntPtr handle, uint eventId, string eventName);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int TransmitClientEventFn(IntPtr handle, uint objectId, uint eventId, uint data, uint groupId,
        uint flags);
}

internal enum SimConnectDataType : uint
{
    Int32 = 1,
    Float64 = 4,
    String256 = 9
}

internal enum SimConnectPeriod : uint
{
    Never = 0,
    Once = 1,
    VisualFrame = 2,
    SimFrame = 3,
    Second = 4
}

/// <summary>SIMCONNECT_RECV_ID - the kind of a received message.</summary>
internal enum SimConnectRecvId : uint
{
    Null = 0,
    Exception = 1,
    Open = 2,
    Quit = 3,
    SimObjectData = 8
}

internal static class SimConnectConstants
{
    public const uint ObjectIdUser = 0;
    public const uint Unused = 0xFFFFFFFF;

    // SIMCONNECT_DATA_REQUEST_FLAG_CHANGED: only send when a value changed
    public const uint DataRequestFlagChanged = 0x1;

    // TransmitClientEvent: the group id is a priority (SIMCONNECT_GROUP_PRIORITY_HIGHEST)
    public const uint GroupPriorityHighest = 1;
    public const uint EventFlagGroupIdIsPriority = 0x10;

    // SIMCONNECT_RECV: dwSize, dwVersion, dwID
    public const int RecvHeaderSize = 12;

    // SIMCONNECT_RECV_SIMOBJECT_DATA: header, dwRequestID, dwObjectID, dwDefineID, dwFlags, dwentrynumber, dwoutof,
    // dwDefineCount, then the data
    public const int SimObjectDataRequestIdOffset = RecvHeaderSize;
    public const int SimObjectDataOffset = RecvHeaderSize + 7 * 4;

    // SIMCONNECT_RECV_EXCEPTION: header, dwException, dwSendID, dwIndex
    public const int ExceptionCodeOffset = RecvHeaderSize;
}
