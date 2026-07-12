using M3Undle.Web.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Security.Cryptography;
using System.Text;

namespace M3Undle.Web.Tests.Application;

// Tests in this class mutate process-wide M3UNDLE_ENCRYPTION_KEY(S) environment variables
// (see EnvScope below); the assembly parallelizes at method level (GlobalTestSettings.cs),
// so these must run sequentially relative to each other to avoid cross-test races.
[TestClass]
[DoNotParallelize]
public sealed class SecretEncryptionServiceTests
{
    [TestMethod]
    public void LegacySingleKey_RoundTrip_Succeeds()
    {
        // M3UNDLE_ENCRYPTION_KEY alone (no M3UNDLE_ENCRYPTION_KEYS) is wrapped as a one-entry
        // ring with the synthetic id "legacy". New writes use the current v2 tagged format —
        // only *pre-existing* untagged data needs the legacy trial-decrypt fallback (see
        // LegacyFormatValue_* tests below), so a fresh Encrypt/Decrypt round trip here is
        // expected to produce v2 output.
        using var env = new EnvScope(key: RandomKey());
        var encryption = CreateService();

        var ciphertext = encryption.Encrypt("hunter2");

        Assert.AreEqual("legacy", encryption.ActiveKeyId);
        Assert.IsTrue(ciphertext.StartsWith("m3e:v2:legacy:", StringComparison.Ordinal));
        Assert.AreEqual("hunter2", encryption.Decrypt(ciphertext));
    }

    [TestMethod]
    public void V2RingKey_RoundTrip_ProducesTaggedFormat()
    {
        using var env = new EnvScope(keys: $"k1:{RandomKey()}");
        var encryption = CreateService();

        var ciphertext = encryption.Encrypt("hunter2");

        Assert.IsTrue(ciphertext.StartsWith("m3e:v2:k1:", StringComparison.Ordinal));
        Assert.AreEqual("k1", encryption.ActiveKeyId);
        Assert.AreEqual("hunter2", encryption.Decrypt(ciphertext));
    }

    [TestMethod]
    public void MultiKeyRing_EncryptUsesFirstEntryAsActive()
    {
        using var env = new EnvScope(keys: $"k2:{RandomKey()},k1:{RandomKey()}");
        var encryption = CreateService();

        var ciphertext = encryption.Encrypt("hunter2");

        Assert.AreEqual("k2", encryption.ActiveKeyId);
        Assert.IsTrue(ciphertext.StartsWith("m3e:v2:k2:", StringComparison.Ordinal));
        CollectionAssert.AreEqual(new[] { "k2", "k1" }, encryption.KnownKeyIds.ToArray());
    }

    [TestMethod]
    public void MultiKeyRing_DecryptsValueEncryptedUnderNonActiveRingKey()
    {
        var keyA = RandomKey();
        var keyB = RandomKey();

        using var encryptedUnderA = new EnvScope(keys: $"a:{keyA}");
        var ciphertext = CreateService().Encrypt("hunter2");
        encryptedUnderA.Dispose();

        using var env = new EnvScope(keys: $"b:{keyB},a:{keyA}");
        var encryption = CreateService();

        Assert.AreEqual("b", encryption.ActiveKeyId);
        Assert.AreEqual("hunter2", encryption.Decrypt(ciphertext));
    }

    [TestMethod]
    public void LegacyFormatValue_DecryptsViaRingFallback_EvenWhenNotFirst()
    {
        // Simulates data written before this key-ring feature existed: a bare, untagged
        // ciphertext with no "m3e:v2:" prefix. It must still decrypt via ring trial-decrypt
        // even when its key isn't the active (first) ring entry.
        var legacyKey = RandomKey();
        var legacyCiphertext = EncryptLegacyFormat("hunter2", legacyKey);

        using var env = new EnvScope(keys: $"new:{RandomKey()},old:{legacyKey}");
        var encryption = CreateService();

        Assert.AreEqual("hunter2", encryption.Decrypt(legacyCiphertext));
    }

    [TestMethod]
    public void LegacyFormatValue_KeyNotInRing_ThrowsInvalidOperationException()
    {
        var legacyCiphertext = EncryptLegacyFormat("hunter2", RandomKey());

        using var env = new EnvScope(keys: $"other:{RandomKey()}");
        var encryption = CreateService();

        Assert.ThrowsExactly<InvalidOperationException>(() => encryption.Decrypt(legacyCiphertext));
    }

    [TestMethod]
    public void V2Value_AadTamper_ThrowsInvalidOperationException()
    {
        var k1 = RandomKey();
        var k9 = RandomKey();

        using var env = new EnvScope(keys: $"k1:{k1},k9:{k9}");
        var encryption = CreateService();
        var ciphertext = encryption.Encrypt("hunter2");
        Assert.IsTrue(ciphertext.StartsWith("m3e:v2:k1:", StringComparison.Ordinal));

        // Swap the declared key id in the prefix without re-encrypting — the AES-GCM tag
        // was computed over the original AAD ("m3e:v2:k1"), so this must fail to authenticate
        // even though "k9" is still a valid, present ring key.
        var tampered = ciphertext.Replace("m3e:v2:k1:", "m3e:v2:k9:", StringComparison.Ordinal);

        Assert.ThrowsExactly<InvalidOperationException>(() => encryption.Decrypt(tampered));
    }

    [TestMethod]
    public void V2Value_UnknownKeyId_ThrowsInvalidOperationException()
    {
        using var env = new EnvScope(keys: $"k1:{RandomKey()}");
        var encryption = CreateService();
        var ciphertext = encryption.Encrypt("hunter2");

        using var env2 = new EnvScope(keys: $"k2:{RandomKey()}");
        var encryption2 = CreateService();

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => encryption2.Decrypt(ciphertext));
        StringAssert.Contains(ex.Message, "k1");
    }

    [TestMethod]
    public void Peek_LegacyValue_ReportsLegacyFormatWithNoKeyId()
    {
        var ciphertext = EncryptLegacyFormat("hunter2", RandomKey());
        var encryption = CreateService();

        var info = encryption.Peek(ciphertext);

        Assert.IsTrue(info.IsLegacyFormat);
        Assert.IsNull(info.KeyId);
    }

    [TestMethod]
    public void Peek_V2Value_ReportsKeyIdWithoutDecrypting()
    {
        using var env = new EnvScope(keys: $"k1:{RandomKey()}");
        var encryption = CreateService();
        var ciphertext = encryption.Encrypt("hunter2");

        // A ring containing no keys at all still lets Peek parse the prefix — it never decrypts.
        using var emptyEnv = new EnvScope(keys: null, key: null);
        var noKeyEncryption = CreateService();

        var info = noKeyEncryption.Peek(ciphertext);

        Assert.IsFalse(info.IsLegacyFormat);
        Assert.AreEqual("k1", info.KeyId);
    }

    [TestMethod]
    public void NoKeyConfigured_IsUnavailable_AndThrowsOnUse()
    {
        using var env = new EnvScope(keys: null, key: null);
        var encryption = CreateService();

        Assert.IsFalse(encryption.IsAvailable);
        Assert.IsNull(encryption.ActiveKeyId);
        Assert.ThrowsExactly<InvalidOperationException>(() => encryption.Encrypt("hunter2"));
        Assert.ThrowsExactly<InvalidOperationException>(() => encryption.Decrypt("anything"));
    }

    [TestMethod]
    public void RingWithMalformedEntries_SkipsThem_KeepsValidOnes()
    {
        // "bad" (no colon), "empty:" (no key material), "short:AAAA" (not 32 bytes decoded)
        // must all be skipped without throwing; "k1" remains active.
        using var env = new EnvScope(keys: $"bad,empty:,short:AAAA,k1:{RandomKey()}");
        var encryption = CreateService();

        Assert.IsTrue(encryption.IsAvailable);
        Assert.AreEqual("k1", encryption.ActiveKeyId);
        CollectionAssert.AreEqual(new[] { "k1" }, encryption.KnownKeyIds.ToArray());
    }

    [TestMethod]
    public void RingWithWhitespaceAroundEntries_TrimsKeyIdAndKeyMaterial()
    {
        // Operators commonly hand-edit env files as "keyId: base64key, keyId2: base64key2" —
        // a stray space on either side of the colon must not turn into a distinct/mismatched
        // key id, nor break Base64 decoding (Convert.FromBase64String already tolerates
        // surrounding whitespace in the key material itself; the key id needs its own trim).
        var key1 = RandomKey();
        var key2 = RandomKey();
        using var env = new EnvScope(keys: $" k1 : {key1} , k2: {key2}");
        var encryption = CreateService();

        CollectionAssert.AreEqual(new[] { "k1", "k2" }, encryption.KnownKeyIds.ToArray());
        Assert.AreEqual("k1", encryption.ActiveKeyId);

        var ciphertext = encryption.Encrypt("hunter2");
        Assert.IsTrue(ciphertext.StartsWith("m3e:v2:k1:", StringComparison.Ordinal),
            "keyId must be exactly 'k1' with no trailing space from the ring entry.");
    }

    [TestMethod]
    public void RingWithDuplicateKeyId_KeepsOnlyFirstOccurrence()
    {
        var firstK1 = RandomKey();
        var secondK1 = RandomKey();

        // "k1" appears twice with different key material — the second occurrence must be
        // ignored so the ring can't silently end up with ambiguous/overridden key material.
        using var env = new EnvScope(keys: $"k1:{firstK1},k1:{secondK1},k2:{RandomKey()}");
        var encryption = CreateService();

        CollectionAssert.AreEqual(new[] { "k1", "k2" }, encryption.KnownKeyIds.ToArray());

        var ciphertextUnderFirstK1 = EncryptLegacyFormat("hunter2", firstK1);
        Assert.AreEqual("hunter2", encryption.Decrypt(ciphertextUnderFirstK1),
            "The first occurrence of a duplicated key id must be the one kept in the ring.");
    }

    private static SecretEncryptionService CreateService()
    {
        var envVars = new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance);
        return new SecretEncryptionService(envVars);
    }

    /// <summary>
    /// Replicates today's pre-key-ring wire format (bare Base64 of nonce+tag+ciphertext, no
    /// prefix, no AAD) independent of SecretEncryptionService, to simulate data written before
    /// this feature existed.
    /// </summary>
    private static string EncryptLegacyFormat(string plaintext, string base64Key)
    {
        var key = Convert.FromBase64String(base64Key);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = new byte[AesGcm.NonceByteSizes.MaxSize];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];

        using var aes = new AesGcm(key, AesGcm.TagByteSizes.MaxSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        var payload = new byte[nonce.Length + tag.Length + ciphertext.Length];
        nonce.CopyTo(payload, 0);
        tag.CopyTo(payload, nonce.Length);
        ciphertext.CopyTo(payload, nonce.Length + tag.Length);
        return Convert.ToBase64String(payload);
    }

    private static string RandomKey() => Convert.ToBase64String(RandomNumberGeneratorFill());

    private static byte[] RandomNumberGeneratorFill()
    {
        var bytes = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    /// <summary>
    /// Saves/restores M3UNDLE_ENCRYPTION_KEY and M3UNDLE_ENCRYPTION_KEYS around a test,
    /// matching the try/finally env-var pattern used elsewhere in this suite
    /// (see Stream/StreamTimeoutTests.cs).
    /// </summary>
    private sealed class EnvScope : IDisposable
    {
        private readonly string? _previousKey;
        private readonly string? _previousKeys;

        public EnvScope(string? keys = null, string? key = null)
        {
            _previousKey = Environment.GetEnvironmentVariable("M3UNDLE_ENCRYPTION_KEY");
            _previousKeys = Environment.GetEnvironmentVariable("M3UNDLE_ENCRYPTION_KEYS");

            Environment.SetEnvironmentVariable("M3UNDLE_ENCRYPTION_KEY", key);
            Environment.SetEnvironmentVariable("M3UNDLE_ENCRYPTION_KEYS", keys);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("M3UNDLE_ENCRYPTION_KEY", _previousKey);
            Environment.SetEnvironmentVariable("M3UNDLE_ENCRYPTION_KEYS", _previousKeys);
        }
    }
}
