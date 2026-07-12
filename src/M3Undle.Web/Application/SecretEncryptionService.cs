using System.Security.Cryptography;
using System.Text;

namespace M3Undle.Web.Application;

/// <summary>
/// Encrypts and decrypts short secret values (passwords, API keys) using AES-256-GCM,
/// with support for a rotatable key ring.
///
/// Key configuration:
/// - Preferred: M3UNDLE_ENCRYPTION_KEYS = "keyId1:base64key1,keyId2:base64key2,...".
///   The first entry is the active key (used for all new Encrypt calls); every entry
///   in the ring is usable for Decrypt. Each key must be a Base64-encoded 32-byte value.
/// - Legacy fallback: if M3UNDLE_ENCRYPTION_KEYS is unset, M3UNDLE_ENCRYPTION_KEY (a single
///   Base64-encoded 32-byte value) is used as a one-entry ring with the synthetic id "legacy".
///
/// Ciphertext formats:
/// - Current (v2): "m3e:v2:{keyId}:{base64(nonce[12]+tag[16]+ciphertext)}". The literal
///   prefix "m3e:v2:{keyId}" is passed as AES-GCM associated data, cryptographically binding
///   the ciphertext to its declared key id.
/// - Legacy (v1, implicit): a bare Base64 blob of nonce[12]+tag[16]+ciphertext with no prefix
///   and no associated data. Decrypt auto-detects this and trial-decrypts it against every
///   key currently in the ring (safe: AES-GCM's auth tag rejects the wrong key cleanly).
/// </summary>
public sealed class SecretEncryptionService
{
    private const string FormatPrefix = "m3e:v2:";
    private const string LegacyKeyId = "legacy";
    private const int NonceLength = 12;
    private const int TagLength = 16;

    private readonly IReadOnlyList<KeyRingEntry> _ring;
    private readonly IReadOnlyDictionary<string, byte[]> _byId;

    public SecretEncryptionService(EnvironmentVariableService envVarService)
    {
        _ring = ParseRing(envVarService);
        _byId = _ring.ToDictionary(e => e.KeyId, e => e.Key, StringComparer.Ordinal);
        KnownKeyIds = _ring.Select(e => e.KeyId).ToList();
    }

    public bool IsAvailable => _ring.Count > 0;

    public string? ActiveKeyId => _ring.Count > 0 ? _ring[0].KeyId : null;

    public IReadOnlyList<string> KnownKeyIds { get; }

    public string Encrypt(string plaintext)
    {
        if (_ring.Count == 0)
            throw new InvalidOperationException("No encryption key is configured. Set M3UNDLE_ENCRYPTION_KEYS (or M3UNDLE_ENCRYPTION_KEY) to a Base64-encoded 32-byte value.");

        var active = _ring[0];
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = new byte[AesGcm.NonceByteSizes.MaxSize];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];

        var aad = Encoding.ASCII.GetBytes(FormatPrefix + active.KeyId);

        using var aes = new AesGcm(active.Key, AesGcm.TagByteSizes.MaxSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag, aad);

        var payload = new byte[nonce.Length + tag.Length + ciphertext.Length];
        nonce.CopyTo(payload, 0);
        tag.CopyTo(payload, nonce.Length);
        ciphertext.CopyTo(payload, nonce.Length + tag.Length);

        return FormatPrefix + active.KeyId + ":" + Convert.ToBase64String(payload);
    }

    public string Decrypt(string encryptedValue)
    {
        if (_ring.Count == 0)
            throw new InvalidOperationException("No encryption key is configured. Set M3UNDLE_ENCRYPTION_KEYS (or M3UNDLE_ENCRYPTION_KEY) to a Base64-encoded 32-byte value.");

        var info = Peek(encryptedValue);
        if (!info.IsLegacyFormat)
        {
            var keyId = info.KeyId!;
            if (!_byId.TryGetValue(keyId, out var key))
                throw new InvalidOperationException($"Encrypted value references key id '{keyId}', which is not present in M3UNDLE_ENCRYPTION_KEYS. Add it back (as a decrypt-only entry) to read this value.");

            var payload = DecodePayload(encryptedValue[(FormatPrefix.Length + keyId.Length + 1)..]);
            var aad = Encoding.ASCII.GetBytes(FormatPrefix + keyId);
            try
            {
                return DecryptPayload(payload, key, aad);
            }
            catch (AuthenticationTagMismatchException ex)
            {
                throw new InvalidOperationException("Encrypted value is corrupt or was encrypted with a different key.", ex);
            }
        }

        var legacyPayload = DecodePayload(encryptedValue);
        foreach (var entry in _ring)
        {
            try
            {
                return DecryptPayload(legacyPayload, entry.Key, associatedData: null);
            }
            catch (AuthenticationTagMismatchException)
            {
                // Wrong key for this legacy value — try the next one in the ring.
            }
        }

        throw new InvalidOperationException("Encrypted value is corrupt or was encrypted with a key that is not present in the current key ring.");
    }

    /// <summary>
    /// Inspects an encrypted value's format/key id without decrypting it. Cheap — pure
    /// string parsing, used for status reporting across many rows.
    /// </summary>
    public EncryptedValueInfo Peek(string encryptedValue)
    {
        if (encryptedValue.StartsWith(FormatPrefix, StringComparison.Ordinal))
        {
            var rest = encryptedValue[FormatPrefix.Length..];
            var separator = rest.IndexOf(':');
            if (separator > 0)
                return new EncryptedValueInfo(IsLegacyFormat: false, KeyId: rest[..separator]);
        }

        return new EncryptedValueInfo(IsLegacyFormat: true, KeyId: null);
    }

    private static byte[] DecodePayload(string base64)
    {
        byte[] input;
        try
        {
            input = Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Encrypted value is corrupt (invalid Base64).", ex);
        }

        if (input.Length < NonceLength + TagLength)
            throw new InvalidOperationException("Encrypted value is corrupt (too short).");

        return input;
    }

    private static string DecryptPayload(byte[] payload, byte[] key, byte[]? associatedData)
    {
        var nonce = payload[..NonceLength];
        var tag = payload[NonceLength..(NonceLength + TagLength)];
        var ciphertext = payload[(NonceLength + TagLength)..];
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, AesGcm.TagByteSizes.MaxSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);

        return Encoding.UTF8.GetString(plaintext);
    }

    private static List<KeyRingEntry> ParseRing(EnvironmentVariableService envVarService)
    {
        var ringValue = envVarService.GetValue("M3UNDLE_ENCRYPTION_KEYS");
        if (!string.IsNullOrWhiteSpace(ringValue))
            return ParseRingValue(ringValue);

        var singleKey = envVarService.GetValue("M3UNDLE_ENCRYPTION_KEY");
        if (string.IsNullOrWhiteSpace(singleKey))
            return [];

        var key = TryDecodeKey(singleKey.Trim());
        return key is null ? [] : [new KeyRingEntry(LegacyKeyId, key)];
    }

    private static List<KeyRingEntry> ParseRingValue(string ringValue)
    {
        var entries = new List<KeyRingEntry>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rawEntry in ringValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = rawEntry.IndexOf(':');
            if (separator <= 0 || separator == rawEntry.Length - 1)
                continue;

            // Trim each side of the colon independently — operators commonly write
            // "keyId: base64key" (with a space after the colon) when hand-editing env files,
            // and a stray space must not silently turn into a distinct/mismatched key id.
            var keyId = rawEntry[..separator].Trim();
            var base64Key = rawEntry[(separator + 1)..].Trim();

            if (keyId.Length == 0)
                continue;

            if (!seenIds.Add(keyId))
                continue;

            var key = TryDecodeKey(base64Key);
            if (key is not null)
                entries.Add(new KeyRingEntry(keyId, key));
        }

        return entries;
    }

    private static byte[]? TryDecodeKey(string base64Key)
    {
        try
        {
            var decoded = Convert.FromBase64String(base64Key);
            return decoded.Length == 32 ? decoded : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private readonly record struct KeyRingEntry(string KeyId, byte[] Key);
}

public readonly record struct EncryptedValueInfo(bool IsLegacyFormat, string? KeyId);
