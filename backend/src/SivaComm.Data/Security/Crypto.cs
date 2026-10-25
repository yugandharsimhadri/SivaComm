using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace SivaComm.Data.Security;

/// <summary>
/// The one place key material is handled. Two purpose-specific keys are
/// derived from the master key with HKDF, so the key that encrypts phone
/// numbers is never the key that HMACs OTPs.
/// </summary>
public sealed class Crypto
{
    private readonly byte[] _encryptionKey;
    private readonly byte[] _otpKey;

    public Crypto(IOptions<SecurityOptions> options)
    {
        var master = Decode(options.Value.MasterKey);
        _encryptionKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: "sivacomm/encryption/v1"u8.ToArray());
        _otpKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: "sivacomm/otp-hmac/v1"u8.ToArray());
    }

    private static byte[] Decode(string masterKey)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(masterKey ?? ""); }
        catch (FormatException) { throw new InvalidOperationException("Security:MasterKey is not valid base64."); }

        if (bytes.Length < 32)
            throw new InvalidOperationException(
                "Security:MasterKey must be at least 32 random bytes, base64-encoded. " +
                "Generate one with: [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))");
        return bytes;
    }

    // ── AES-GCM: nonce(12) | tag(16) | ciphertext ───────────────────────────

    public byte[] Encrypt(string plaintext)
    {
        var data = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[12 + 16 + data.Length];
        var nonce = output.AsSpan(0, 12);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_encryptionKey, 16);
        aes.Encrypt(nonce, data, output.AsSpan(28), output.AsSpan(12, 16));
        return output;
    }

    public string Decrypt(byte[] blob)
    {
        if (blob.Length < 28) throw new CryptographicException("Ciphertext is too short.");
        var plain = new byte[blob.Length - 28];

        using var aes = new AesGcm(_encryptionKey, 16);
        aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), plain);
        return Encoding.UTF8.GetString(plain);
    }

    // ── OTP ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Keyed with the challenge id as well as the code, so the same six digits
    /// on two challenges produce unrelated values and a row cannot be checked
    /// against a precomputed table even by someone holding another row's code.
    /// </summary>
    public byte[] OtpHmac(Guid challengeId, string otp)
    {
        var input = Encoding.UTF8.GetBytes($"{challengeId:N}:{otp}");
        return HMACSHA256.HashData(_otpKey, input);
    }

    public bool OtpMatches(Guid challengeId, string otp, byte[] stored) =>
        CryptographicOperations.FixedTimeEquals(OtpHmac(challengeId, otp), stored);

    public static string NewOtp(int length)
    {
        // RandomNumberGenerator, never Random: this is a credential.
        var max = (int)Math.Pow(10, length);
        return RandomNumberGenerator.GetInt32(0, max).ToString($"D{length}");
    }

    // ── Grants and secrets ──────────────────────────────────────────────────

    /// <summary>256 random bits, base64url. Unguessable, so a plain SHA-256 is
    /// a sufficient stored form.</summary>
    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static byte[] Sha256(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>PBKDF2-SHA512 for the dashboard's admin passwords.</summary>
public static class PasswordHasher
{
    private const int Iterations = 210_000;

    public static (string Hash, string Salt) Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA512, 32);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public static bool Verify(string password, string hash, string salt)
    {
        try
        {
            var expected = Convert.FromBase64String(hash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                password, Convert.FromBase64String(salt), Iterations, HashAlgorithmName.SHA512, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
