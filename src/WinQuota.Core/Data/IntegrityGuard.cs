using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace WinQuota.Core.Data;

/// <summary>完整性校验结果。</summary>
public enum IntegrityStatus
{
    /// <summary>校验通过。</summary>
    Ok,

    /// <summary>数据或签名被修改（含数据库文件被旧版本回滚）。</summary>
    Tampered,

    /// <summary>尚未写入基线（全新库或旧版本升级后首次启用）。</summary>
    NoBaseline,

    /// <summary>密钥文件丢失（数据库存在签名但 .key 文件不在了）。</summary>
    KeyMissing,
}

/// <summary>
/// 数据库完整性防护（第四阶段防绕过）：
/// 对规则、每日用量与设置全量做 HMAC-SHA256 签名，任何绕过 QuotaDatabase 的直改
/// （把 used_seconds 改小、调高额度、删除 PIN、删除当日用量行）都会使校验失败。
/// 签名同时绑定一个单调递增序号：序号存于数据库并镜像到密钥文件，
/// 把数据库文件整体回滚成旧副本（昨天的备份）会因序号倒退被检出。
/// 密钥文件位于数据库同目录（winquota.db.key），依靠安装脚本设置的 NTFS ACL
/// （仅 SYSTEM 与管理员可访问）保证受限用户无法读取或伪造。
/// 说明：对本地管理员级别的攻击者只能提高门槛，无法根绝（见 README）。
/// </summary>
public sealed class IntegrityGuard
{
    private const string HmacSettingKey = "db.integrity";
    private const string SeqSettingKey = "integrity.seq";

    private readonly string _keyFilePath;
    private byte[]? _key;
    private long _keyFileSeq;
    private long _pendingSeq;
    private bool _pendingKeyFileWrite;

    public IntegrityGuard(string databasePath)
    {
        _keyFilePath = databasePath + ".key";
        LoadKeyFile();
    }

    public bool HasKeyFile => _key is not null;

    /// <summary>读取数据库中存储的签名（无则为 null）。</summary>
    public string? GetStoredHmac(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = @key";
        command.Parameters.AddWithValue("@key", HmacSettingKey);
        return command.ExecuteScalar() as string;
    }

    /// <summary>校验当前数据库内容与签名、序号是否一致。</summary>
    public IntegrityStatus Verify(SqliteConnection connection)
    {
        var stored = GetStoredHmac(connection);
        if (string.IsNullOrEmpty(stored))
        {
            return IntegrityStatus.NoBaseline;
        }

        if (_key is null)
        {
            return IntegrityStatus.KeyMissing;
        }

        var computed = ComputeHmac(connection, _key, out var dbSeq);
        if (!CryptographicFixedTimeEquals(stored, computed))
        {
            return IntegrityStatus.Tampered;
        }

        // 序号倒退 = 数据库文件被旧副本回滚；前进则可能是签名后进程崩溃未及写回密钥文件，属可自愈范围。
        return dbSeq < _keyFileSeq ? IntegrityStatus.Tampered : IntegrityStatus.Ok;
    }

    /// <summary>
    /// 在写事务内（已写入业务数据、尚未提交）刷新签名。
    /// 提交成功后调用 <see cref="NotifyCommitted"/> 把新序号落盘到密钥文件。
    /// </summary>
    public void SignAfterWrite(SqliteConnection connection)
    {
        if (_key is null)
        {
            throw new InvalidOperationException("完整性密钥文件缺失，拒绝为本次写入签名。");
        }

        var seq = ReadSeq(connection) + 1;
        SetSettingRaw(connection, SeqSettingKey, seq.ToString(CultureInfo.InvariantCulture));
        var hmac = ComputeHmac(connection, _key, out _);
        SetSettingRaw(connection, HmacSettingKey, hmac);

        _pendingSeq = seq;
        _pendingKeyFileWrite = true;
    }

    /// <summary>写入基线（全新库、旧版本升级或密钥丢失后由管理员确认重建时调用）。必要时创建密钥文件。</summary>
    public void WriteBaseline(SqliteConnection connection)
    {
        if (_key is null)
        {
            _key = RandomNumberGenerator.GetBytes(32);
            _keyFileSeq = 0;
        }

        SignAfterWrite(connection);
        NotifyCommitted();
    }

    /// <summary>事务提交后调用：把序号写回密钥文件（单调，只升不降）。</summary>
    public void NotifyCommitted()
    {
        if (!_pendingKeyFileWrite || _key is null)
        {
            return;
        }

        if (_pendingSeq > _keyFileSeq)
        {
            _keyFileSeq = _pendingSeq;
        }

        try
        {
            var content = Convert.ToHexString(_key) + "\n" + _keyFileSeq.ToString(CultureInfo.InvariantCulture) + "\n";
            var tmp = _keyFilePath + ".tmp";
            File.WriteAllText(tmp, content);
            File.Move(tmp, _keyFilePath, overwrite: true);
        }
        catch (IOException)
        {
            // 密钥文件暂时写不进去（磁盘/权限问题）：序号已在内存中，
            // 校验按“数据库序号不小于密钥文件序号即通过”处理，下次成功写入时补齐。
        }

        _pendingKeyFileWrite = false;
    }

    private static long ReadSeq(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = @key";
        command.Parameters.AddWithValue("@key", SeqSettingKey);
        return long.TryParse(command.ExecuteScalar() as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq) ? seq : 0;
    }

    private static void SetSettingRaw(SqliteConnection connection, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = @value";
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", value);
        command.ExecuteNonQuery();
    }

    /// <summary>对全部规则、用量与设置做规范化序列化后计算 HMAC，同时输出当前序号。</summary>
    private static string ComputeHmac(SqliteConnection connection, byte[] key, out long seq)
    {
        var builder = new StringBuilder(4096);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, name, type, enabled, monday_limit, tuesday_limit, wednesday_limit, thursday_limit,
                       friday_limit, saturday_limit, sunday_limit
                FROM limit_rules ORDER BY id
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                builder.Append("R").Append(reader.GetInt64(0)).Append('|')
                    .Append(Escape(reader.GetString(1))).Append('|')
                    .Append(reader.GetInt64(2)).Append('|')
                    .Append(reader.GetInt64(3));
                for (var i = 4; i <= 10; i++)
                {
                    builder.Append('|').Append(reader.GetInt64(i));
                }

                builder.Append('\n');
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, rule_id, application_name, process_name, exe_path, product_name, publisher, signer, enabled
                FROM application_rules ORDER BY id
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                builder.Append("A").Append(reader.GetInt64(0)).Append('|')
                    .Append(reader.GetInt64(1)).Append('|')
                    .Append(Escape(reader.GetString(2))).Append('|')
                    .Append(Escape(reader.GetString(3))).Append('|')
                    .Append(Escape(reader.IsDBNull(4) ? null : reader.GetString(4))).Append('|')
                    .Append(Escape(reader.IsDBNull(5) ? null : reader.GetString(5))).Append('|')
                    .Append(Escape(reader.IsDBNull(6) ? null : reader.GetString(6))).Append('|')
                    .Append(Escape(reader.IsDBNull(7) ? null : reader.GetString(7))).Append('|')
                    .Append(reader.GetInt64(8)).Append('\n');
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, rule_id, usage_date, used_seconds, bonus_seconds FROM daily_usage ORDER BY rule_id, usage_date, id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                builder.Append("U").Append(reader.GetInt64(0)).Append('|')
                    .Append(reader.GetInt64(1)).Append('|')
                    .Append(Escape(reader.GetString(2))).Append('|')
                    .Append(reader.GetInt64(3)).Append('|')
                    .Append(reader.GetInt64(4)).Append('\n');
            }
        }

        seq = 0;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT key, value FROM settings WHERE key <> @hmacKey ORDER BY key";
            command.Parameters.AddWithValue("@hmacKey", HmacSettingKey);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var settingKey = reader.GetString(0);
                builder.Append("S").Append(Escape(settingKey)).Append('|')
                    .Append(Escape(reader.GetString(1))).Append('\n');
                if (settingKey == SeqSettingKey)
                {
                    seq = long.TryParse(reader.GetString(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
                }
            }
        }

        using var hmac = new HMACSHA256(key);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash);
    }

    private static string Escape(string? value) =>
        string.IsNullOrEmpty(value)
            ? "\\0"
            : value.Replace("\\", "\\\\").Replace("|", "\\p").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\0", "\\z");

    private static bool CryptographicFixedTimeEquals(string a, string b)
    {
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    }

    private void LoadKeyFile()
    {
        try
        {
            if (!File.Exists(_keyFilePath))
            {
                return;
            }

            var lines = File.ReadAllLines(_keyFilePath);
            if (lines.Length >= 2 &&
                lines[0].Length == 64 &&
                long.TryParse(lines[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq))
            {
                _key = Convert.FromHexString(lines[0]);
                _keyFileSeq = seq;
            }
        }
        catch (Exception ex) when (ex is IOException or FormatException)
        {
            // 密钥文件损坏视同丢失，由上层判定 KeyMissing
            _key = null;
            _keyFileSeq = 0;
        }
    }
}
