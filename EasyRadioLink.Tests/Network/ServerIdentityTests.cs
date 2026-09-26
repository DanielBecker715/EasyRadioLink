using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using EasyRadioLink.Common.Network.Crypto;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EasyRadioLink.Common.Tests.Network;

/// <summary>The server's TLS identity (server-identity.pfx).</summary>
[TestClass]
public class ServerIdentityTests
{
    private string _directory;

    private string FilePath => Path.Combine(_directory, ServerIdentity.FileName);

    [TestInitialize]
    public void CreateDirectory()
    {
        _directory = Path.Combine(Path.GetTempPath(), "erl-identity-" + Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void DeleteDirectory()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    [TestMethod]
    public void IsCreatedOnceAndLoadedAfterwards()
    {
        string fingerprint;
        using (var created = ServerIdentity.LoadOrCreate(_directory, out var wasCreated))
        {
            Assert.IsTrue(wasCreated);
            Assert.IsTrue(File.Exists(FilePath));
            fingerprint = ServerIdentity.GetFingerprint(created);
        }

        using var loaded = ServerIdentity.LoadOrCreate(_directory, out var createdAgain);
        Assert.IsFalse(createdAgain, "an existing identity is never replaced");
        Assert.AreEqual(fingerprint, ServerIdentity.GetFingerprint(loaded));
        Assert.HasCount(1, Directory.GetFiles(_directory), "no temporary files are left behind");
    }

    [TestMethod]
    public void IsASelfSignedEcdsaP256ServerCertificate()
    {
        using var certificate = ServerIdentity.LoadOrCreate(_directory, out _);

        Assert.IsTrue(certificate.HasPrivateKey);
        Assert.AreEqual(ServerIdentity.SubjectName, certificate.Subject);
        Assert.AreEqual(certificate.Subject, certificate.Issuer);

        using var key = certificate.GetECDsaPublicKey();
        Assert.IsNotNull(key);
        Assert.AreEqual(256, key.KeySize);
        Assert.IsTrue(key.ExportParameters(false).Curve.IsNamed);

        var years = (certificate.NotAfter - certificate.NotBefore).TotalDays / 365.25;
        Assert.IsTrue(years is > 19.9 and < 20.1, $"valid for {years} years");
        Assert.IsTrue(certificate.NotBefore < DateTime.Now);

        var usage = certificate.Extensions["2.5.29.37"] as X509EnhancedKeyUsageExtension;
        Assert.IsNotNull(usage);
        Assert.AreEqual("1.3.6.1.5.5.7.3.1", usage.EnhancedKeyUsages[0].Value);
    }

    [TestMethod]
    public void FingerprintIsTheSha256OfTheSubjectPublicKeyInfo()
    {
        using var certificate = ServerIdentity.LoadOrCreate(_directory, out _);

        var expected = SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo());
        CollectionAssert.AreEqual(expected, ServerIdentity.ComputeFingerprint(certificate));
        Assert.AreEqual(ServerIdentity.FormatFingerprint(expected), ServerIdentity.GetFingerprint(certificate));

        // the public certificate alone (what a client sees) gives the same fingerprint
        using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.RawData);
        Assert.AreEqual(ServerIdentity.GetFingerprint(certificate), ServerIdentity.GetFingerprint(publicOnly));
    }

    [TestMethod]
    public void EveryNewIdentityIsDifferent()
    {
        using var first = X509CertificateLoader.LoadPkcs12(ServerIdentity.CreatePkcs12(), null);
        using var second = X509CertificateLoader.LoadPkcs12(ServerIdentity.CreatePkcs12(), null);

        Assert.AreNotEqual(ServerIdentity.GetFingerprint(first), ServerIdentity.GetFingerprint(second));
    }

    [TestMethod]
    public void DamagedIdentityStopsTheServerInsteadOfBeingReplaced()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(FilePath, [1, 2, 3, 4]);

        Assert.ThrowsExactly<CryptographicException>(() => ServerIdentity.LoadOrCreate(_directory, out _));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(FilePath), "the file is left alone");
    }

    [TestMethod]
    public void CertificateWithoutPrivateKeyIsRefused()
    {
        Directory.CreateDirectory(_directory);
        using (var full = X509CertificateLoader.LoadPkcs12(ServerIdentity.CreatePkcs12(), null))
        using (var publicOnly = X509CertificateLoader.LoadCertificate(full.RawData))
        {
            File.WriteAllBytes(FilePath, publicOnly.Export(X509ContentType.Pkcs12));
        }

        Assert.ThrowsExactly<CryptographicException>(() => ServerIdentity.LoadOrCreate(_directory, out _));
    }

    [TestMethod]
    public void OnlyTheOwnerCanReadTheIdentityOnUnix()
    {
        if (OperatingSystem.IsWindows()) return; // see OnlyTheServerAccountSystemAndAdministratorsCanReadItOnWindows

        using var identity = ServerIdentity.LoadOrCreate(_directory, out _);
        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(FilePath));
        Assert.IsNull(ServerIdentity.CheckPermissions(FilePath));
    }

    [TestMethod]
    public void OnlyTheServerAccountSystemAndAdministratorsCanReadItOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;

        // a folder that gives everybody read access: the identity must not inherit it
        Directory.CreateDirectory(_directory);
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var folder = new DirectoryInfo(_directory);
        var folderSecurity = folder.GetAccessControl();
        folderSecurity.AddAccessRule(new FileSystemAccessRule(everyone, FileSystemRights.Read,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None,
            AccessControlType.Allow));
        folder.SetAccessControl(folderSecurity);

        using (ServerIdentity.LoadOrCreate(_directory, out var created)) Assert.IsTrue(created);

        var security = new FileInfo(FilePath).GetAccessControl();
        Assert.IsTrue(security.AreAccessRulesProtected, "nothing inherited from the folder");

        using var current = WindowsIdentity.GetCurrent();
        var expected = new[]
        {
            current.User,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
        };
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        CollectionAssert.AreEquivalent(expected, rules.Select(rule => (SecurityIdentifier)rule.IdentityReference).ToArray());
        Assert.IsTrue(rules.All(rule => rule.AccessControlType == AccessControlType.Allow && !rule.IsInherited));
        Assert.IsNull(ServerIdentity.CheckPermissions(FilePath), "private: no warning");

        // somebody gave "Everyone" read access (or an older version created the file with the folder's ACL): warned
        security.AddAccessRule(new FileSystemAccessRule(everyone, FileSystemRights.Read, AccessControlType.Allow));
        new FileInfo(FilePath).SetAccessControl(security);

        var warning = ServerIdentity.CheckPermissions(FilePath);
        Assert.IsNotNull(warning);
        StringAssert.Contains(warning, FilePath);
        StringAssert.Contains(warning, everyone.Translate(typeof(NTAccount)).Value);

        // the server still loads it (the warning is only a warning)
        using var loaded = ServerIdentity.LoadOrCreate(_directory, out var createdAgain);
        Assert.IsFalse(createdAgain);
    }
}
