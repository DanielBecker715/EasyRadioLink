using System;

namespace EasyRadioLink.Common.Network.Server;

/// <summary>
///     The server could not be started (e.g. the port is already in use). The message is meant for the admin; the
///     host (server window / command line) decides what to do with it.
/// </summary>
public class ServerStartException : Exception
{
    public ServerStartException(string message, Exception innerException = null) : base(message, innerException)
    {
    }
}
