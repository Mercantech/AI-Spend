using System.Security.Cryptography;
using System.Text;

namespace AiSpend.Api.Services;

/// <summary>AES encrypt/decrypt for provider credentials using CREDENTIAL_ENCRYPTION_KEY.</summary>
public sealed class CredentialProtector(IConfiguration config)
{
    private byte[] GetKey()
    {
        var raw = config["CREDENTIAL_ENCRYPTION_KEY"]
            ?? throw new InvalidOperationException("CREDENTIAL_ENCRYPTION_KEY er ikke sat");
        var bytes = Encoding.UTF8.GetBytes(raw);
        return SHA256.HashData(bytes);
    }

    public string Encrypt(string plaintext)
    {
        var key = GetKey();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        var payload = new byte[nonce.Length + tag.Length + cipher.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipher, 0, payload, nonce.Length + tag.Length, cipher.Length);
        return Convert.ToBase64String(payload);
    }

    public string Decrypt(string encrypted)
    {
        var key = GetKey();
        var payload = Convert.FromBase64String(encrypted);
        var nonce = payload.AsSpan(0, 12);
        var tag = payload.AsSpan(12, 16);
        var cipher = payload.AsSpan(28);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
