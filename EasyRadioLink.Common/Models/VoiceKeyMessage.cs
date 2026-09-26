using System;
using System.Collections.Generic;
using EasyRadioLink.Common.Models.Player;
using EasyRadioLink.Common.Network.Crypto;

namespace EasyRadioLink.Common.Models;

/// <summary>
///     Body of a <see cref="NetworkMessage.MessageType.VOICE_KEY" /> message: the key of one transmission, wrapped for
///     each listener (see <see cref="E2EVoiceCrypto" />).
///     <code>
///     {"SenderGuid":"...","TxId":"&lt;base64, 8 B&gt;","Frequency":27185000,"Modulation":0,
///      "Keys":{"&lt;recipient guid&gt;":"&lt;base64, 48 B wrapped key&gt;", ...}}
///     </code>
///     Client -> server: one entry per listener. Server -> client: only the recipient's own entry. The server never sees
///     the transmission key itself, only the wrapped blobs.
/// </summary>
public class VoiceKeyMessage
{
    /// <summary>Most entries a sender may list in one message (the server holds at most 1000 connections).</summary>
    public const int MaxKeys = 1000;

    public string SenderGuid { get; set; }

    /// <summary>Base64 of the <see cref="E2EVoiceCrypto.TxIdLength" /> byte transmission id.</summary>
    public string TxId { get; set; }

    /// <summary>Frequency of the transmission in Hz (the server forwards a key only to clients that can hear it).</summary>
    public double Frequency { get; set; }

    public Modulation Modulation { get; set; }

    /// <summary>Recipient client id -> base64 of its <see cref="E2EVoiceCrypto.WrappedKeyLength" /> byte wrapped key.</summary>
    public Dictionary<string, string> Keys { get; set; }

    /// <summary>
    ///     Strict format check of everything except the wrapped keys' content (which only their recipient can check):
    ///     client ids, a <see cref="E2EVoiceCrypto.TxIdLength" /> byte id, a finite positive frequency, a known
    ///     modulation, 1..<see cref="MaxKeys" /> entries of exactly <see cref="E2EVoiceCrypto.WrappedKeyLength" /> bytes.
    /// </summary>
    /// <param name="reason">Why the message is invalid (for logs; never contains key material).</param>
    public bool IsValid(out ulong txId, out string reason)
    {
        txId = 0;

        if (!ShortGuid.IsWellFormed(SenderGuid))
        {
            reason = "invalid sender id";
            return false;
        }

        if (!E2EVoiceCrypto.TryParseTxId(TxId, out txId))
        {
            reason = "invalid transmission id";
            return false;
        }

        if (!double.IsFinite(Frequency) || Frequency <= 0)
        {
            reason = "invalid frequency";
            return false;
        }

        if (!Enum.IsDefined(Modulation) || Modulation == Modulation.DISABLED)
        {
            reason = "invalid modulation";
            return false;
        }

        if (Keys == null || Keys.Count == 0 || Keys.Count > MaxKeys)
        {
            reason = $"{Keys?.Count ?? 0} keys (1..{MaxKeys} allowed)";
            return false;
        }

        foreach (var (recipient, wrapped) in Keys)
            if (!ShortGuid.IsWellFormed(recipient) || !E2EVoiceCrypto.IsValidWrappedKey(wrapped))
            {
                reason = "invalid key entry";
                return false;
            }

        reason = null;
        return true;
    }
}
