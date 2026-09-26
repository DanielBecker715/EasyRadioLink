namespace EasyRadioLink.Common.Network.Crypto;

/// <summary>
///     A server presented another identity than the one pinned in <see cref="KnownServersStore" />, or its pin is
///     unreadable (<see cref="ServerPinStatus.Unknown" />) - the client did not connect (nothing, not even the password,
///     was sent).
/// </summary>
/// <param name="ServerKey">The pin key ("host:port", <see cref="KnownServersStore.ServerKey" />).</param>
/// <param name="PinnedFingerprint">The fingerprint known from earlier connections; null if the pin is unreadable.</param>
/// <param name="PresentedFingerprint">The fingerprint the server presented now.</param>
public sealed record ServerIdentityMismatch(string ServerKey, string PinnedFingerprint, string PresentedFingerprint);
