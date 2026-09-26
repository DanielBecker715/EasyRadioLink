using System;
using System.Reflection;

namespace EasyRadioLink.Common.Helpers;

/// <summary>
///     Product and protocol version information shared by the client, the server GUI and the command line server.
///     The product version comes from the assembly metadata (Directory.Build.props), the protocol versions are
///     constants that only change when the TCP/UDP wire format changes.
/// </summary>
public static class AppVersion
{
    /// <summary>Product name. Also sent as <c>NetworkMessage.Product</c> so foreign servers/clients are rejected.</summary>
    public const string Product = "EasyRadioLink";

    /// <summary>
    ///     Wire protocol version sent in every <c>NetworkMessage.Version</c>. 1.1.0: TLS for TCP (pinned server
    ///     identity), AES-256-GCM for every UDP datagram with a per-client key from the SYNC reply.
    /// </summary>
    public const string ProtocolVersion = "1.1.0";

    /// <summary>
    ///     Oldest peer protocol version that is still accepted. Must be &lt;= <see cref="ProtocolVersion" />. 1.0 peers
    ///     can't talk to 1.1 peers at all (no TLS/UDP encryption).
    /// </summary>
    public const string MinimumProtocolVersion = "1.1.0";

    /// <summary>Product version, e.g. "1.0.0" (from the assembly informational version).</summary>
    public static string Version { get; } = ReadProductVersion();

    /// <summary>"EasyRadioLink 1.0.0" - for window titles, logs and recording metadata.</summary>
    public static string ProductAndVersion => $"{Product} {Version}";

    /// <summary>
    ///     True if <paramref name="peerVersion" /> is a parseable version that is not older than
    ///     <see cref="MinimumProtocolVersion" />.
    /// </summary>
    public static bool IsSupportedProtocolVersion(string peerVersion)
    {
        if (string.IsNullOrWhiteSpace(peerVersion)) return false;

        if (!System.Version.TryParse(peerVersion.Trim(), out var parsed)) return false;

        return parsed >= System.Version.Parse(MinimumProtocolVersion);
    }

    /// <summary>True if <paramref name="product" /> identifies an EasyRadioLink peer.</summary>
    public static bool IsSupportedProduct(string product)
    {
        return string.Equals(product, Product, StringComparison.Ordinal);
    }

    private static string ReadProductVersion()
    {
        try
        {
            var assembly = typeof(AppVersion).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
            {
                // strip any "+commit" suffix added by source link
                var plus = informational.IndexOf('+');
                return plus > 0 ? informational.Substring(0, plus) : informational;
            }

            var version = assembly.GetName().Version;
            if (version != null) return $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
        }
        catch (Exception)
        {
            // fall through to the default below
        }

        return "1.0.0";
    }
}
