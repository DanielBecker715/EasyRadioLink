using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using EasyRadioLink.Common.Settings;
using NLog;

namespace EasyRadioLink.Common.Network.Crypto;

/// <summary>Result of checking a server's identity against the pinned one.</summary>
public enum ServerPinStatus
{
    /// <summary>Never connected to this server before - trust on first use.</summary>
    FirstUse,

    /// <summary>The server presented the pinned identity.</summary>
    Match,

    /// <summary>The server presented a different identity than the pinned one - do not connect.</summary>
    Mismatch,

    /// <summary>
    ///     The pin of this server in known-servers.json is unreadable (not a fingerprint): its identity is unknown. Never
    ///     pinned silently - do not connect unless the user checks the fingerprint and trusts it.
    /// </summary>
    Unknown,

    /// <summary>
    ///     known-servers.json can't be read (damaged, locked by another program, no permission): no identity can be
    ///     checked - do not connect. The file is never overwritten while it is damaged.
    /// </summary>
    StoreUnreadable
}

/// <summary>known-servers.json can't be read, or is damaged and must not be overwritten.</summary>
public class KnownServersStoreException : IOException
{
    public KnownServersStoreException(string message, Exception innerException = null) : base(message, innerException)
    {
    }
}

/// <summary>
///     Trust-on-first-use pins of server identities (like SSH's known_hosts): <c>known-servers.json</c> in the client's
///     settings folder maps <c>"host:port"</c> (as the user typed it, see <see cref="ServerKey" />) to the SHA-256
///     fingerprint of the server's public key (<see cref="ServerIdentity.GetFingerprint" />).
///     <para>
///         Fail closed: only a missing file or a missing entry means "first use". A file that can't be read or parsed
///         gives <see cref="ServerPinStatus.StoreUnreadable" /> for every server and is never written (repair or delete
///         it); an entry that is not a valid fingerprint gives <see cref="ServerPinStatus.Unknown" /> for its server.
///         When the file is rewritten, entries this version does not understand are kept exactly as they were.
///     </para>
///     <para>
///         The file is re-read for every check, so several client instances share it. Changes are read-modify-write
///         under an exclusive lock file (<c>known-servers.json.lock</c>, shared with other processes, retried for a
///         few seconds) and written atomically (temporary file, then renamed).
///     </para>
/// </summary>
public class KnownServersStore
{
    public const string FileName = "known-servers.json";

    private const string LockFileSuffix = ".lock";

    // how long a change waits for another process that holds the lock, and how often reads/renames are retried
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);
    private const int IoAttempts = 8;

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private static readonly JsonWriterOptions WriteOptions = new() { Indented = true };

    // one lock for every store instance of this process: all of them may point at the same file
    private static readonly object FileLock = new();

    public KnownServersStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("A file path is required", nameof(filePath));

        FilePath = Path.GetFullPath(filePath);
    }

    /// <summary>The store in the client's settings folder (<see cref="GlobalSettingsStore.Path" />).</summary>
    public static KnownServersStore Default => new(Path.Combine(GlobalSettingsStore.Path, FileName));

    public string FilePath { get; }

    /// <summary>
    ///     The pin key of a server: <c>"host:port"</c> with the host in lower case (IPv6 addresses in brackets). The host
    ///     is the name the user connects to, not the resolved address.
    /// </summary>
    public static string ServerKey(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("A host is required", nameof(host));
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));

        var normalised = host.Trim().TrimStart('[').TrimEnd(']').TrimEnd('.').ToLowerInvariant();
        if (normalised.Contains(':')) normalised = "[" + normalised + "]";

        return normalised + ":" + port.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Compares <paramref name="presentedFingerprint" /> with the pin of <paramref name="serverKey" />.</summary>
    /// <param name="pinnedFingerprint">The pinned fingerprint (null on first use, unknown or unreadable).</param>
    public ServerPinStatus Check(string serverKey, string presentedFingerprint, out string pinnedFingerprint)
    {
        return Check(serverKey, presentedFingerprint, out pinnedFingerprint, out _);
    }

    /// <summary>Compares <paramref name="presentedFingerprint" /> with the pin of <paramref name="serverKey" />.</summary>
    /// <param name="pinnedFingerprint">The pinned fingerprint (null on first use, unknown or unreadable).</param>
    /// <param name="problem">
    ///     For <see cref="ServerPinStatus.StoreUnreadable" /> and <see cref="ServerPinStatus.Unknown" />: what is wrong
    ///     (for the user; never contains fingerprints).
    /// </param>
    public ServerPinStatus Check(string serverKey, string presentedFingerprint, out string pinnedFingerprint,
        out string problem)
    {
        pinnedFingerprint = null;
        problem = null;

        PinFile file;
        lock (FileLock)
        {
            file = Read();
        }

        if (!file.IsReadable)
        {
            problem = file.Problem;
            return ServerPinStatus.StoreUnreadable;
        }

        var entry = file.Find(serverKey);
        if (entry == null) return ServerPinStatus.FirstUse;

        if (entry.Fingerprint == null)
        {
            problem = $"the saved identity of {serverKey} in {FilePath} is not a valid fingerprint";
            return ServerPinStatus.Unknown;
        }

        pinnedFingerprint = entry.Fingerprint;
        return ServerIdentity.FingerprintsEqual(pinnedFingerprint, presentedFingerprint)
            ? ServerPinStatus.Match
            : ServerPinStatus.Mismatch;
    }

    /// <summary>
    ///     The pinned fingerprint of <paramref name="serverKey" />, or null (none, not valid, or the file can't be
    ///     read).
    /// </summary>
    public string GetPinned(string serverKey)
    {
        if (string.IsNullOrEmpty(serverKey)) return null;

        lock (FileLock)
        {
            return Read().Find(serverKey)?.Fingerprint;
        }
    }

    /// <summary>
    ///     Pins <paramref name="fingerprint" /> for <paramref name="serverKey" /> (first use, or the user accepted a new
    ///     identity), keeping every other entry. Throws <see cref="ArgumentException" /> if the fingerprint is invalid
    ///     and <see cref="IOException" /> (<see cref="KnownServersStoreException" /> if the file is damaged - it is never
    ///     overwritten) if the file can't be changed.
    /// </summary>
    public void Trust(string serverKey, string fingerprint)
    {
        if (string.IsNullOrEmpty(serverKey)) throw new ArgumentException("A server key is required", nameof(serverKey));

        var normalised = ServerIdentity.NormaliseFingerprint(fingerprint) ??
                         throw new ArgumentException("Invalid fingerprint", nameof(fingerprint));

        Change(file => file.Set(serverKey, normalised));
    }

    /// <summary>
    ///     Trust on first use, atomically: pins <paramref name="fingerprint" /> only if <paramref name="serverKey" /> still
    ///     has no entry (checked again under the lock - another client instance may have pinned something meanwhile).
    ///     Returns <see cref="ServerPinStatus.FirstUse" /> if it was pinned now, <see cref="ServerPinStatus.Match" /> if
    ///     the same fingerprint is pinned already, otherwise the status that forbids the connection
    ///     (<see cref="ServerPinStatus.Mismatch" />, <see cref="ServerPinStatus.Unknown" />,
    ///     <see cref="ServerPinStatus.StoreUnreadable" />) - nothing is written then. Throws <see cref="IOException" /> if
    ///     the file can't be written.
    /// </summary>
    public ServerPinStatus TrustFirstUse(string serverKey, string fingerprint, out string pinnedFingerprint)
    {
        if (string.IsNullOrEmpty(serverKey)) throw new ArgumentException("A server key is required", nameof(serverKey));

        var normalised = ServerIdentity.NormaliseFingerprint(fingerprint) ??
                         throw new ArgumentException("Invalid fingerprint", nameof(fingerprint));

        string pinned = null;
        var status = ServerPinStatus.StoreUnreadable;
        lock (FileLock)
        {
            using var crossProcessLock = AcquireLockFile();

            var file = Read();
            if (file.IsReadable)
            {
                var entry = file.Find(serverKey);
                if (entry == null)
                {
                    file.Set(serverKey, normalised);
                    Write(file);
                    status = ServerPinStatus.FirstUse;
                }
                else if (entry.Fingerprint == null)
                {
                    status = ServerPinStatus.Unknown;
                }
                else
                {
                    pinned = entry.Fingerprint;
                    status = ServerIdentity.FingerprintsEqual(pinned, normalised)
                        ? ServerPinStatus.Match
                        : ServerPinStatus.Mismatch;
                }
            }
        }

        pinnedFingerprint = pinned;
        return status;
    }

    /// <summary>Removes the pin of <paramref name="serverKey" /> (the next connection is a first use again).</summary>
    public bool Forget(string serverKey)
    {
        var removed = false;
        Change(file => removed = file.Remove(serverKey));
        return removed;
    }

    /// <summary>Read-modify-write under the in-process and the cross-process lock; a damaged file is never written.</summary>
    private void Change(Func<PinFile, bool> change)
    {
        lock (FileLock)
        {
            using var crossProcessLock = AcquireLockFile();

            var file = Read();
            if (!file.IsReadable)
                throw new KnownServersStoreException(
                    $"{FilePath} can't be read ({file.Problem}) - it is left unchanged. Repair or delete it " +
                    "(deleting it forgets every saved server identity).");

            if (change(file)) Write(file);
        }
    }

    /// <summary>Exclusive lock file next to the store, shared with other processes (other client instances).</summary>
    private FileStream AcquireLockFile()
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var deadline = DateTime.UtcNow + LockTimeout;
        while (true)
            try
            {
                return new FileStream(FilePath + LockFileSuffix, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.None);
            }
            catch (IOException ex) when (DateTime.UtcNow < deadline && ex is not FileNotFoundException &&
                                         ex is not DirectoryNotFoundException)
            {
                // held by another client instance for a moment
                Thread.Sleep(RetryDelay);
            }
    }

    private PinFile Read()
    {
        if (!File.Exists(FilePath)) return PinFile.Empty();

        string json = null;
        Exception readError = null;
        for (var attempt = 1; attempt <= IoAttempts && json == null; attempt++)
            try
            {
                // other instances replace the file atomically; a sharing violation only lasts a moment
                using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
                json = reader.ReadToEnd();
            }
            catch (FileNotFoundException)
            {
                return PinFile.Empty();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                readError = ex;
                if (ex is DecoderFallbackException || attempt == IoAttempts) break;

                Thread.Sleep(RetryDelay);
            }

        if (json == null)
        {
            Logger.Error(readError, $"Unable to read {FilePath} - no server identity can be checked");
            return PinFile.Unreadable(readError is DecoderFallbackException
                ? "it is not valid UTF-8 text"
                : $"it can't be read: {readError?.Message}");
        }

        try
        {
            return PinFile.Parse(json);
        }
        catch (JsonException ex)
        {
            Logger.Error($"{FilePath} is damaged ({ex.Message}) - no server identity can be checked until it is " +
                         "repaired or deleted; it is left unchanged");
            return PinFile.Unreadable($"it is damaged: {ex.Message}");
        }
    }

    private void Write(PinFile file)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temporaryPath = FilePath + ".tmp";
        using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using (var writer = new Utf8JsonWriter(stream, WriteOptions))
            {
                file.WriteTo(writer);
            }

            stream.Flush(true);
        }

        for (var attempt = 1;; attempt++)
            try
            {
                File.Move(temporaryPath, FilePath, true);
                return;
            }
            catch (Exception ex) when (attempt < IoAttempts && ex is IOException or UnauthorizedAccessException)
            {
                // a reader of another instance has it open for a moment
                Thread.Sleep(RetryDelay);
            }
    }

    /// <summary>One entry of the file: its raw JSON value, and the fingerprint if the value is a valid one.</summary>
    private sealed class PinEntry
    {
        public PinEntry(string key, string rawValue, string fingerprint)
        {
            Key = key;
            RawValue = rawValue;
            Fingerprint = fingerprint;
        }

        public string Key { get; }
        public string RawValue { get; }

        /// <summary>Canonical fingerprint; null if the value is not one (the entry is kept but never trusted).</summary>
        public string Fingerprint { get; private set; }

        public void MarkInvalid()
        {
            Fingerprint = null;
        }
    }

    /// <summary>The parsed file: the entries in file order (invalid ones included), or why it can't be used.</summary>
    private sealed class PinFile
    {
        private readonly List<PinEntry> _entries = new();

        private PinFile(string problem)
        {
            Problem = problem;
        }

        public string Problem { get; }
        public bool IsReadable => Problem == null;

        public static PinFile Empty()
        {
            return new PinFile(null);
        }

        public static PinFile Unreadable(string problem)
        {
            return new PinFile(problem ?? "unknown error");
        }

        /// <summary>Throws <see cref="JsonException" /> unless the text is one JSON object.</summary>
        public static PinFile Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException($"expected a JSON object, found {document.RootElement.ValueKind}");

            var file = new PinFile(null);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var value = property.Value;
                var fingerprint = value.ValueKind == JsonValueKind.String
                    ? ServerIdentity.NormaliseFingerprint(value.GetString())
                    : null;

                var entry = new PinEntry(property.Name, value.GetRawText(), fingerprint);

                // the same server twice: ambiguous, neither entry is trusted
                foreach (var existing in file._entries)
                    if (existing.Key == entry.Key)
                    {
                        existing.MarkInvalid();
                        entry.MarkInvalid();
                    }

                file._entries.Add(entry);
            }

            var invalid = file._entries.Count(entry => entry.Fingerprint == null);
            if (invalid > 0)
                Logger.Warn($"{invalid} entries of {FileName} are not valid fingerprints (or appear twice): the " +
                            "identity of their servers counts as unknown until the user trusts it again");

            return file;
        }

        public PinEntry Find(string serverKey)
        {
            if (serverKey == null) return null;

            foreach (var entry in _entries)
                if (entry.Key == serverKey)
                    return entry;

            return null;
        }

        /// <summary>Replaces every entry of <paramref name="serverKey" /> with a valid pin.</summary>
        public bool Set(string serverKey, string fingerprint)
        {
            Remove(serverKey);
            _entries.Add(new PinEntry(serverKey, null, fingerprint));
            return true;
        }

        public bool Remove(string serverKey)
        {
            return _entries.RemoveAll(entry => entry.Key == serverKey) > 0;
        }

        /// <summary>Sorted by key; valid pins in canonical form, everything else exactly as it was read.</summary>
        public void WriteTo(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            foreach (var entry in _entries.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(entry.Key);
                if (entry.Fingerprint != null) writer.WriteStringValue(entry.Fingerprint);
                else writer.WriteRawValue(entry.RawValue, true);
            }

            writer.WriteEndObject();
        }
    }
}
