using System.Security.Cryptography;

namespace WinQuota.Service.Cli;

/// <summary>管理员 PIN 的哈希存储（SHA256 + 随机盐）。</summary>
public static class PinHasher
{
    private const string HashKey = "admin_pin_hash";
    private const string SaltKey = "admin_pin_salt";

    public static void SetPin(Core.Data.QuotaDatabase database, string pin)
    {
        var saltBytes = RandomNumberGenerator.GetBytes(16);
        var hash = ComputeHash(pin, saltBytes);
        database.SetSetting(SaltKey, Convert.ToHexString(saltBytes));
        database.SetSetting(HashKey, Convert.ToHexString(hash));
    }

    public static bool HasPin(Core.Data.QuotaDatabase database) => database.GetSetting(HashKey) is not null;

    public static bool VerifyPin(Core.Data.QuotaDatabase database, string pin)
    {
        var hashHex = database.GetSetting(HashKey);
        var saltHex = database.GetSetting(SaltKey);
        if (hashHex is null || saltHex is null)
        {
            return false;
        }

        var hash = Convert.FromHexString(hashHex);
        var salt = Convert.FromHexString(saltHex);
        return CryptographicOperations.FixedTimeEquals(ComputeHash(pin, salt), hash);
    }

    private static byte[] ComputeHash(string pin, byte[] salt)
    {
        var pinBytes = System.Text.Encoding.UTF8.GetBytes(pin);
        var input = new byte[salt.Length + pinBytes.Length];
        Buffer.BlockCopy(salt, 0, input, 0, salt.Length);
        Buffer.BlockCopy(pinBytes, 0, input, salt.Length, pinBytes.Length);
        return SHA256.HashData(input);
    }
}
