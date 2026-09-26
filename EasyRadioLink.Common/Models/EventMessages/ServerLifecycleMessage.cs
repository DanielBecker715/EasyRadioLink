using System.Collections.Generic;
using System.Collections.ObjectModel;
using EasyRadioLink.Common.Models.Player;

namespace EasyRadioLink.Common.Models.EventMessages;

public class StartServerMessage
{
}

public class StopServerMessage
{
}

public class ServerStateMessage
{
    private readonly List<ClientInfo> _srClients;

    public ServerStateMessage(bool isRunning, List<ClientInfo> srClients, string guid = null)
    {
        _srClients = srClients;
        IsRunning = isRunning;
        DisconnectingClientGuid = guid;
    }

    public string DisconnectingClientGuid { get; private set; }

    //SUPER SAFE
    public ReadOnlyCollection<ClientInfo> Clients => new(_srClients);

    public bool IsRunning { get; }
    public int Count => _srClients.Count;
}

/// <summary>
///     Published when the server could not be started, e.g. because its port is already in use. The server is
///     stopped again; <see cref="Error" /> is a message for the admin.
/// </summary>
public class ServerStartFailedMessage
{
    public ServerStartFailedMessage(string error)
    {
        Error = error;
    }

    public string Error { get; }
}

/// <summary>
///     Published when the server loaded (or created) its TLS identity - hosts show <see cref="Fingerprint" /> so the
///     admin can share it with the users.
/// </summary>
public class ServerIdentityMessage
{
    public ServerIdentityMessage(string fingerprint, string filePath, bool created, string permissionWarning = null)
    {
        Fingerprint = fingerprint;
        FilePath = filePath;
        Created = created;
        PermissionWarning = permissionWarning;
    }

    /// <summary>SHA-256 of the server's public key, "AB:CD:...".</summary>
    public string Fingerprint { get; }

    /// <summary>Full path of server-identity.pfx.</summary>
    public string FilePath { get; }

    /// <summary>True if the identity was created by this start.</summary>
    public bool Created { get; }

    /// <summary>
    ///     Set if accounts other than the server's own (and SYSTEM/Administrators) can read the identity file
    ///     (<c>ServerIdentity.CheckPermissions</c>) - hosts show it as a warning.
    /// </summary>
    public string PermissionWarning { get; }
}

public class KickClientMessage
{
    public KickClientMessage(ClientInfo client)
    {
        Client = client;
    }

    public ClientInfo Client { get; }
}

public class BanClientMessage
{
    public BanClientMessage(ClientInfo client)
    {
        Client = client;
    }

    public ClientInfo Client { get; }
}

public class ServerSettingsChangedMessage
{
}

// Published by the server UI when the radio check (echo) frequencies change, handled by the UDP voice router.
public class ServerFrequenciesChanged
{
    // MHz list, e.g. "27.405,446.19375"
    public string TestFrequencies { get; set; }
}