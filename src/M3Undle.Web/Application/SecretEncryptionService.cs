using System.Security.Cryptography;
using System.Text;

namespace M3Undle.Web.Application;

/// <summary>
/// Encrypts and decrypts short secret values (passwords) using AES-256-GCM.
/// The key is read from the M3UNDLE_ENCRYPTION_KEY environment variable, which must be
/// a Base64-encoded 32-byte (256-bit) value. If the variable is missing or invalid,
/// IsAvailable returns false and Encrypt/Decrypt throw InvalidOperationException.
///
/// Ciphertext format (Base64 of): [12-byte nonce][16-byte tag][ciphertext bytes]
/// </summary>
public sealed class SecretEncryptionService
{
    private readonly byte[]? _key;

    public SecretEncryptionService()
    {
        var raw = Environment.GetEnvironmentVariable("M3UNDLE_ENCRYPTION_KEY");
        if (string.IsNullOrWhiteSpace(raw))
            return;

        try
        {
            var decoded = Convert.FromBase64String(raw.Trim());
            if (decoded.Length == 32)
                _key = decoded;
        }
        catch (FormatException) { }
    }

    public bool IsAvailable => _key is not null;

    public string Encrypt(string plaintext)
    {
        if (_key is null)
            throw new InvalidOperationException("M3UNDLE_ENCRYPTION_KEY is not set or is invalid. Set it to a Base64-encoded 32-byte value.");

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = new byte[AesGcm.NonceByteSizes.MaxSize]; // 12 bytes
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize]; // 16 bytes

        using var aes = new AesGcm(_key, AesGcm.TagByteSizes.MaxSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // Layout: nonce (12) + tag (16) + ciphertext
        var output = new byte[nonce.Length + tag.Length + ciphertext.Length];
        nonce.CopyTo(output, 0);
        tag.CopyTo(output, nonce.Length);
        ciphertext.CopyTo(output, nonce.Length + tag.Length);

        return Convert.ToBase64String(output);
    }

    public string Decrypt(string encryptedBase64)
    {
        if (_key is null)
            throw new InvalidOperationException("M3UNDLE_ENCRYPTION_KEY is not set or is invalid.");

        byte[] input;
        try
        {
            input = Convert.FromBase64String(encryptedBase64);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Encrypted value is corrupt (invalid Base64).", ex);
        }

        const int nonceLen = 12;
        const int tagLen = 16;
        if (input.Length < nonceLen + tagLen)
            throw new InvalidOperationException("Encrypted value is corrupt (too short).");

        var nonce = input[..nonceLen];
        var tag = input[nonceLen..(nonceLen + tagLen)];
        var ciphertext = input[(nonceLen + tagLen)..];
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(_key, AesGcm.TagByteSizes.MaxSize);
        try
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new InvalidOperationException("Encrypted value is corrupt or was encrypted with a different key.", ex);
        }

        return Encoding.UTF8.GetString(plaintext);
    }
}
