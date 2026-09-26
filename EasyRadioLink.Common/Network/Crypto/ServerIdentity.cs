using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using NLog;

namespace EasyRadioLink.Common.Network.Crypto;

/// <summary>
///     The server's TLS identity: a self-signed ECDSA P-256 certificate ("CN=EasyRadioLink Server", valid for 20 years)
///     stored as <see cref="FileName" /> next to server.cfg. It is created on the first start and loaded on every start;
///     clients pin the SHA-256 of its SubjectPublicKeyInfo (<see cref="GetFingerprint" />, trust on first use, see
///     <see cref="KnownServersStore" />).
///     <para>
///         The file is protected by file permissions only (the PKCS#12 container has no password): on Linux/macOS it is
///         created with mode 0600 (and restricted to that on every start), on Windows with its own ACL - full control
///         for the account the server runs as, SYSTEM and Administrators, nothing inherited from the folder. On every
///         start <see cref="CheckPermissions" /> reports other accounts that can read it (Windows). It is never
///         regenerated silently - an unreadable file stops the server, because a new identity makes every client show the
///         "server identity changed" warning.
///     </para>
/// </summary>
public static class ServerIdentity
{
    public const string FileName = "server-identity.pfx";

    public const string SubjectName = "CN=EasyRadioLink Server";

    public const int ValidityYears = 20;

    /// <summary>Length of a fingerprint in bytes (SHA-256).</summary>
    public const int FingerprintLength = 32;

    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";

    private const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    ///     Loads the identity from <paramref name="directory" />, creating it first if the file does not exist.
    ///     Throws if an existing file can't be loaded (the server must not start with a different identity).
    /// </summary>
    /// <param name="directory">The configuration folder (next to server.cfg).</param>
    /// <param name="created">True if a new identity was created.</param>
    public static X509Certificate2 LoadOrCreate(string directory, out bool created)
    {
        var path = Path.Combine(directory, FileName);
        created = false;

        if (!File.Exists(path)) created = TryCreate(path);

        RestrictPermissions(path);

        return Load(path);
    }

    /// <summary>Loads an identity file. Throws <see cref="CryptographicException" /> if it has no usable private key.</summary>
    public static X509Certificate2 Load(string path)
    {
        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadPkcs12FromFile(path, null);
        }
        catch (CryptographicException)
        {
            // a PKCS#12 file written by another tool may use "" instead of "no password"
            certificate = X509CertificateLoader.LoadPkcs12FromFile(path, "");
        }

        var usable = false;
        try
        {
            using var key = certificate.GetECDsaPrivateKey();
            usable = key != null;
        }
        catch (CryptographicException)
        {
            // handled below
        }

        if (!usable)
        {
            certificate.Dispose();
            throw new CryptographicException($"{path} does not contain an ECDSA certificate with its private key.");
        }

        return certificate;
    }

    /// <summary>
    ///     Writes a new identity to <paramref name="path" />. Returns false if another process created the file in the
    ///     meantime (that identity is used).
    /// </summary>
    private static bool TryCreate(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        byte[] pkcs12 = null;
        var temporaryPath = path + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)) + ".tmp";
        try
        {
            pkcs12 = CreatePkcs12();

            // written completely (owner-only from the first byte: mode 0600 on Unix, an ACL of its own on Windows) before
            // it gets its final name, so a crash can never leave a half written identity behind. A move within the
            // folder keeps the ACL.
            using (var stream = CreateOwnerOnlyFile(temporaryPath))
            {
                stream.Write(pkcs12);
                stream.Flush(true);
            }

            try
            {
                File.Move(temporaryPath, path, false);
            }
            catch (IOException) when (File.Exists(path))
            {
                return false;
            }

            Logger.Info($"Created a new server identity {path}");
            return true;
        }
        finally
        {
            if (pkcs12 != null) CryptographicOperations.ZeroMemory(pkcs12);

            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"Unable to delete {temporaryPath}");
            }
        }
    }

    /// <summary>A new self-signed ECDSA P-256 server certificate with its private key as PKCS#12 (no password).</summary>
    internal static byte[] CreatePkcs12()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(SubjectName, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(ServerAuthenticationOid) }, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        using var certificate = request.CreateSelfSigned(notBefore, notBefore.AddYears(ValidityYears));

        return certificate.Export(X509ContentType.Pkcs12);
    }

    /// <summary>
    ///     A new file (<see cref="FileMode.CreateNew" />, write access) that only the server's account can read: mode
    ///     0600 on Linux/macOS; on Windows an explicit ACL without inheritance (<see cref="OwnerOnlySecurity" />).
    /// </summary>
    private static FileStream CreateOwnerOnlyFile(string path)
    {
        if (OperatingSystem.IsWindows())
            return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write | FileSystemRights.ReadData,
                FileShare.None, 4096, FileOptions.None, OwnerOnlySecurity());

        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = OwnerReadWrite
        });
    }

    /// <summary>
    ///     Windows: full control for the account the server runs as, SYSTEM and the Administrators group - and nothing
    ///     inherited from the folder (which may give "Users" or "Authenticated Users" read access).
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static FileSecurity OwnerOnlySecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);

        foreach (var principal in AllowedPrincipals())
            security.AddAccessRule(new FileSystemAccessRule(principal, FileSystemRights.FullControl,
                AccessControlType.Allow));

        return security;
    }

    [SupportedOSPlatform("windows")]
    private static List<SecurityIdentifier> AllowedPrincipals()
    {
        var principals = new List<SecurityIdentifier>
        {
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null)
        };

        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User != null) principals.Insert(0, identity.User);

        return principals;
    }

    /// <summary>
    ///     A warning if accounts other than the server's own account, SYSTEM and Administrators can read the identity
    ///     file (Windows: its ACL - e.g. inherited "Users" access for a file created by an older version or copied
    ///     there), null if it is private. On Linux/macOS null: the file is restricted to 0600 on every start.
    /// </summary>
    public static string CheckPermissions(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return null;

        try
        {
            var others = FindOtherReaders(path);
            if (others.Count == 0) return null;

            return $"{path} can be read by {string.Join(", ", others)} - anybody who can read it can impersonate this " +
                   "server. Allow only the account the server runs as, SYSTEM and Administrators (file Properties > " +
                   "Security > Advanced: disable inheritance and remove the other entries).";
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"Unable to check who can read {path}");
            return $"Unable to check who can read {path} ({ex.Message}) - make sure only the server's account can read it.";
        }
    }

    /// <summary>Names (or SIDs) of the accounts besides the allowed ones that may read <paramref name="path" />.</summary>
    [SupportedOSPlatform("windows")]
    internal static List<string> FindOtherReaders(string path)
    {
        // rights that let an account read the private key - directly, or by giving itself access
        const FileSystemRights readingRights = FileSystemRights.ReadData | FileSystemRights.ChangePermissions |
                                               FileSystemRights.TakeOwnership;

        var allowed = AllowedPrincipals();
        var others = new List<string>();

        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || (rule.FileSystemRights & readingRights) == 0)
                continue;
            if (rule.IdentityReference is not SecurityIdentifier sid || allowed.Contains(sid)) continue;

            var name = DisplayName(sid);
            if (!others.Contains(name)) others.Add(name);
        }

        return others;
    }

    [SupportedOSPlatform("windows")]
    private static string DisplayName(SecurityIdentifier sid)
    {
        try
        {
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch (Exception)
        {
            return sid.Value;
        }
    }

    /// <summary>On Linux/macOS: makes sure only the owner can read the identity (0600). Windows: see <see cref="CheckPermissions" />.</summary>
    private static void RestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path)) return;

        try
        {
            var mode = File.GetUnixFileMode(path);
            if ((mode & ~OwnerReadWrite) == 0) return;

            File.SetUnixFileMode(path, mode & OwnerReadWrite);
            Logger.Warn($"{path} was readable by other users - restricted it to the owner (0600)");
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"Unable to restrict the permissions of {path} - make sure only the server can read it");
        }
    }

    /// <summary>SHA-256 of the certificate's SubjectPublicKeyInfo (the value clients pin).</summary>
    public static byte[] ComputeFingerprint(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        if (certificate is X509Certificate2 certificate2) return ComputeFingerprint(certificate2);

        using var copy = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        return ComputeFingerprint(copy);
    }

    private static byte[] ComputeFingerprint(X509Certificate2 certificate)
    {
        return SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo());
    }

    /// <summary>The certificate's fingerprint formatted for people, e.g. "AB:CD:...:EF" (32 bytes).</summary>
    public static string GetFingerprint(X509Certificate certificate)
    {
        return FormatFingerprint(ComputeFingerprint(certificate));
    }

    /// <summary>Formats a SHA-256 fingerprint as upper case hex pairs separated by ':'.</summary>
    public static string FormatFingerprint(ReadOnlySpan<byte> fingerprint)
    {
        var builder = new StringBuilder(fingerprint.Length * 3);
        for (var i = 0; i < fingerprint.Length; i++)
        {
            if (i > 0) builder.Append(':');
            builder.Append(fingerprint[i].ToString("X2"));
        }

        return builder.ToString();
    }

    /// <summary>
    ///     Parses a fingerprint written as hex, with or without ':', '-' or space separators (any case). Returns false
    ///     unless it is exactly <see cref="FingerprintLength" /> bytes.
    /// </summary>
    public static bool TryParseFingerprint(string text, out byte[] fingerprint)
    {
        fingerprint = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var hex = new StringBuilder(FingerprintLength * 2);
        foreach (var c in text.Trim())
        {
            if (c is ':' or '-' or ' ') continue;
            if (!Uri.IsHexDigit(c)) return false;
            hex.Append(c);
        }

        if (hex.Length != FingerprintLength * 2) return false;

        fingerprint = Convert.FromHexString(hex.ToString());
        return true;
    }

    /// <summary>The canonical form ("AB:CD:...") of a fingerprint in any accepted notation, or null if invalid.</summary>
    public static string NormaliseFingerprint(string text)
    {
        return TryParseFingerprint(text, out var bytes) ? FormatFingerprint(bytes) : null;
    }

    /// <summary>True if both fingerprints are valid and equal (notation does not matter).</summary>
    public static bool FingerprintsEqual(string a, string b)
    {
        return TryParseFingerprint(a, out var left) && TryParseFingerprint(b, out var right) &&
               CryptographicOperations.FixedTimeEquals(left, right);
    }
}
