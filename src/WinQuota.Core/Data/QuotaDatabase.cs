using System.Globalization;
using Microsoft.Data.Sqlite;
using WinQuota.Core.Models;

namespace WinQuota.Core.Data;

/// <summary>
/// WinQuota 的 SQLite 持久化层：规则、每日用量与设置。
/// 所有时间额度一律以秒为单位存储。
/// </summary>
public sealed class QuotaDatabase
{
    private readonly string _connectionString;
    private readonly IntegrityGuard _integrity;
    private readonly object _gate = new();

    public QuotaDatabase(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
        _integrity = new IntegrityGuard(databasePath);

        EnsureCreated();
    }

    public string DatabasePath { get; }

    /// <summary>完整性密钥文件是否存在（丢失时需要管理员重建基线）。</summary>
    public bool HasIntegrityKey => _integrity.HasKeyFile;

    /// <summary>校验数据库完整性：外部直改 / 数据库文件回滚 / 基线缺失分别见 <see cref="IntegrityStatus"/>。</summary>
    public IntegrityStatus VerifyIntegrity()
    {
        lock (_gate)
        {
            using var connection = Open();
            return _integrity.Verify(connection);
        }
    }

    /// <summary>密钥文件丢失后由管理员确认调用：生成新密钥并把当前数据写入新基线。</summary>
    public void ReinitializeIntegrity()
    {
        lock (_gate)
        {
            using var connection = Open();
            _integrity.WriteBaseline(connection);
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    private void EnsureCreated()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode = WAL;";
            pragma.ExecuteNonQuery();

            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS limit_rules (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    name            TEXT    NOT NULL,
                    type            INTEGER NOT NULL,
                    enabled         INTEGER NOT NULL DEFAULT 1,
                    monday_limit    INTEGER NOT NULL DEFAULT 0,
                    tuesday_limit   INTEGER NOT NULL DEFAULT 0,
                    wednesday_limit INTEGER NOT NULL DEFAULT 0,
                    thursday_limit  INTEGER NOT NULL DEFAULT 0,
                    friday_limit    INTEGER NOT NULL DEFAULT 0,
                    saturday_limit  INTEGER NOT NULL DEFAULT 0,
                    sunday_limit    INTEGER NOT NULL DEFAULT 0,
                    created_at      TEXT    NOT NULL DEFAULT (datetime('now', 'localtime'))
                );

                CREATE TABLE IF NOT EXISTS application_rules (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    rule_id         INTEGER NOT NULL REFERENCES limit_rules(id) ON DELETE CASCADE,
                    application_name TEXT   NOT NULL,
                    process_name    TEXT    NOT NULL,
                    exe_path        TEXT,
                    product_name    TEXT,
                    publisher       TEXT,
                    enabled         INTEGER NOT NULL DEFAULT 1
                );

                CREATE TABLE IF NOT EXISTS daily_usage (
                    id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    rule_id       INTEGER NOT NULL REFERENCES limit_rules(id) ON DELETE CASCADE,
                    usage_date    TEXT    NOT NULL,
                    used_seconds  INTEGER NOT NULL DEFAULT 0,
                    bonus_seconds INTEGER NOT NULL DEFAULT 0,
                    UNIQUE (rule_id, usage_date)
                );

                CREATE INDEX IF NOT EXISTS idx_daily_usage_date ON daily_usage (usage_date);

                CREATE TABLE IF NOT EXISTS settings (
                    key   TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();

            // 旧库升级迁移：0.5.x 及之前没有 signer 列（签名者匹配，第四阶段）。
            var migrated = EnsureSignerColumn(connection);
            // 全新库 / 旧版本升级 / 完整性防护首次启用：写入基线签名。
            if (migrated || _integrity.GetStoredHmac(connection) is null)
            {
                _integrity.WriteBaseline(connection);
            }
        }
    }

    private static bool EnsureSignerColumn(SqliteConnection connection)
    {
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('application_rules') WHERE name = 'signer'";
            if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
            {
                return false;
            }
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE application_rules ADD COLUMN signer TEXT";
        alter.ExecuteNonQuery();
        return true;
    }

    #region 规则

    /// <summary>
    /// 创建一条应用限制规则：一个应用组（多个进程名共享一份额度）。
    /// weekdayLimitsMonToSun 依次为周一到周日的额度秒数。
    /// </summary>
    public long AddApplicationRule(
        string name,
        IReadOnlyList<long> weekdayLimitsMonToSun,
        IReadOnlyList<string> processNames,
        string? exePath = null,
        string? productName = null,
        string? publisher = null,
        string? signer = null)
    {
        if (processNames.Count == 0)
        {
            throw new ArgumentException("至少需要一个进程名。", nameof(processNames));
        }

        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();

            var ruleId = InsertLimitRule(connection, name, RuleType.APPLICATION, weekdayLimitsMonToSun);

            foreach (var processName in processNames)
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO application_rules (rule_id, application_name, process_name, exe_path, product_name, publisher, signer, enabled)
                    VALUES (@ruleId, @appName, @processName, @exePath, @productName, @publisher, @signer, 1);
                    """;
                command.Parameters.AddWithValue("@ruleId", ruleId);
                command.Parameters.AddWithValue("@appName", name);
                command.Parameters.AddWithValue("@processName", processName);
                command.Parameters.AddWithValue("@exePath", (object?)exePath ?? DBNull.Value);
                command.Parameters.AddWithValue("@productName", (object?)productName ?? DBNull.Value);
                command.Parameters.AddWithValue("@publisher", (object?)publisher ?? DBNull.Value);
                command.Parameters.AddWithValue("@signer", (object?)signer ?? DBNull.Value);
                command.ExecuteNonQuery();
            }

            _integrity.SignAfterWrite(connection);
            transaction.Commit();
            _integrity.NotifyCommitted();
            return ruleId;
        }
    }

    /// <summary>创建一条整机使用时长限制规则。</summary>
    public long AddComputerRule(string name, IReadOnlyList<long> weekdayLimitsMonToSun)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var ruleId = InsertLimitRule(connection, name, RuleType.COMPUTER, weekdayLimitsMonToSun);
            _integrity.SignAfterWrite(connection);
            transaction.Commit();
            _integrity.NotifyCommitted();
            return ruleId;
        }
    }

    private static long InsertLimitRule(SqliteConnection connection, string name, RuleType type, IReadOnlyList<long> weekdayLimitsMonToSun)
    {
        if (weekdayLimitsMonToSun.Count != 7)
        {
            throw new ArgumentException("必须提供周一到周日共 7 个额度值。", nameof(weekdayLimitsMonToSun));
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO limit_rules (name, type, enabled, monday_limit, tuesday_limit, wednesday_limit,
                                     thursday_limit, friday_limit, saturday_limit, sunday_limit)
            VALUES (@name, @type, 1, @mon, @tue, @wed, @thu, @fri, @sat, @sun);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@type", (int)type);
        AddWeekdayParams(command, weekdayLimitsMonToSun);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public IReadOnlyList<(LimitRule Rule, IReadOnlyList<ApplicationRule> Apps)> GetRules(bool? enabledFilter = null)
    {
        lock (_gate)
        {
            using var connection = Open();

            var rules = new List<(LimitRule, IReadOnlyList<ApplicationRule>)>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT id, name, type, enabled, monday_limit, tuesday_limit, wednesday_limit, thursday_limit, friday_limit, saturday_limit, sunday_limit FROM limit_rules ORDER BY id";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var rule = new LimitRule
                    {
                        Id = reader.GetInt64(0),
                        Name = reader.GetString(1),
                        Type = (RuleType)reader.GetInt64(2),
                        Enabled = reader.GetInt64(3) != 0,
                        MondayLimitSeconds = reader.GetInt64(4),
                        TuesdayLimitSeconds = reader.GetInt64(5),
                        WednesdayLimitSeconds = reader.GetInt64(6),
                        ThursdayLimitSeconds = reader.GetInt64(7),
                        FridayLimitSeconds = reader.GetInt64(8),
                        SaturdayLimitSeconds = reader.GetInt64(9),
                        SundayLimitSeconds = reader.GetInt64(10),
                    };
                    if (enabledFilter is { } filter && rule.Enabled != filter)
                    {
                        continue;
                    }

                    rules.Add((rule, new List<ApplicationRule>()));
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT id, rule_id, application_name, process_name, exe_path, product_name, publisher, signer, enabled FROM application_rules ORDER BY id";
                using var reader = command.ExecuteReader();
                var byRuleId = rules.ToDictionary(entry => entry.Item1.Id, entry => (List<ApplicationRule>)entry.Item2);
                while (reader.Read())
                {
                    if (!byRuleId.TryGetValue(reader.GetInt64(1), out var apps))
                    {
                        continue;
                    }

                    apps.Add(new ApplicationRule
                    {
                        Id = reader.GetInt64(0),
                        RuleId = reader.GetInt64(1),
                        ApplicationName = reader.GetString(2),
                        ProcessName = reader.GetString(3),
                        ExePath = reader.IsDBNull(4) ? null : reader.GetString(4),
                        ProductName = reader.IsDBNull(5) ? null : reader.GetString(5),
                        Publisher = reader.IsDBNull(6) ? null : reader.GetString(6),
                        Signer = reader.IsDBNull(7) ? null : reader.GetString(7),
                        Enabled = reader.GetInt64(8) != 0,
                    });
                }
            }

            return rules;
        }
    }

    /// <summary>更新规则周一到周日的额度（秒）。</summary>
    public bool UpdateRuleQuotas(long ruleId, IReadOnlyList<long> weekdayLimitsMonToSun)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE limit_rules
                SET monday_limit = @mon, tuesday_limit = @tue, wednesday_limit = @wed, thursday_limit = @thu,
                    friday_limit = @fri, saturday_limit = @sat, sunday_limit = @sun
                WHERE id = @id
                """;
            AddWeekdayParams(command, weekdayLimitsMonToSun);
            command.Parameters.AddWithValue("@id", ruleId);
            var changed = command.ExecuteNonQuery() == 1;
            if (changed)
            {
                _integrity.SignAfterWrite(connection);
                transaction.Commit();
                _integrity.NotifyCommitted();
            }

            return changed;
        }
    }

    public bool SetRuleEnabled(long ruleId, bool enabled)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE limit_rules SET enabled = @enabled WHERE id = @id";
            command.Parameters.AddWithValue("@enabled", enabled ? 1 : 0);
            command.Parameters.AddWithValue("@id", ruleId);
            var changed = command.ExecuteNonQuery() == 1;
            if (changed)
            {
                _integrity.SignAfterWrite(connection);
                transaction.Commit();
                _integrity.NotifyCommitted();
            }

            return changed;
        }
    }

    public bool RemoveRule(long ruleId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM limit_rules WHERE id = @id";
            command.Parameters.AddWithValue("@id", ruleId);
            var changed = command.ExecuteNonQuery();
            if (changed == 1)
            {
                _integrity.SignAfterWrite(connection);
                transaction.Commit();
                _integrity.NotifyCommitted();
                return true;
            }

            return false;
        }
    }

    #endregion

    #region 每日用量

    /// <summary>
    /// 获取指定规则某天的用量记录，不存在则创建（惰性跨天重置的核心：
    /// 每次读取时按当天日期取记录，日期变化自然产生新的一行，不依赖定时任务）。
    /// </summary>
    public DailyUsage GetOrCreateUsage(long ruleId, DateOnly date)
    {
        lock (_gate)
        {
            using var connection = Open();
            var usage = GetOrCreateUsage(connection, ruleId, date, out var created);
            if (created)
            {
                _integrity.SignAfterWrite(connection);
                _integrity.NotifyCommitted();
            }

            return usage;
        }
    }

    private static DailyUsage GetOrCreateUsage(SqliteConnection connection, long ruleId, DateOnly date, out bool created)
    {
        var dateText = FormatDate(date);
        using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT id, used_seconds, bonus_seconds FROM daily_usage WHERE rule_id = @ruleId AND usage_date = @date";
            query.Parameters.AddWithValue("@ruleId", ruleId);
            query.Parameters.AddWithValue("@date", dateText);
            using var reader = query.ExecuteReader();
            if (reader.Read())
            {
                created = false;
                return new DailyUsage
                {
                    Id = reader.GetInt64(0),
                    RuleId = ruleId,
                    UsageDate = date,
                    UsedSeconds = reader.GetInt64(1),
                    BonusSeconds = reader.GetInt64(2),
                };
            }
        }

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO daily_usage (rule_id, usage_date, used_seconds, bonus_seconds)
                VALUES (@ruleId, @date, 0, 0);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("@ruleId", ruleId);
            insert.Parameters.AddWithValue("@date", dateText);
            var id = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
            created = true;
            return new DailyUsage { Id = id, RuleId = ruleId, UsageDate = date, UsedSeconds = 0, BonusSeconds = 0 };
        }
    }

    /// <summary>增量累计已用秒数（进程运行中的周期性落盘）。</summary>
    public void AddUsedSeconds(long ruleId, DateOnly date, long deltaSeconds)
    {
        if (deltaSeconds <= 0)
        {
            return;
        }

        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            GetOrCreateUsage(connection, ruleId, date, out _);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE daily_usage SET used_seconds = used_seconds + @delta WHERE rule_id = @ruleId AND usage_date = @date";
            command.Parameters.AddWithValue("@delta", deltaSeconds);
            command.Parameters.AddWithValue("@ruleId", ruleId);
            command.Parameters.AddWithValue("@date", FormatDate(date));
            command.ExecuteNonQuery();
            _integrity.SignAfterWrite(connection);
            transaction.Commit();
            _integrity.NotifyCommitted();
        }
    }

    /// <summary>为当天增加临时奖励秒数，仅影响当天。</summary>
    public long AddBonusSeconds(long ruleId, DateOnly date, long seconds)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            GetOrCreateUsage(connection, ruleId, date, out _);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE daily_usage SET bonus_seconds = bonus_seconds + @seconds WHERE rule_id = @ruleId AND usage_date = @date; SELECT bonus_seconds FROM daily_usage WHERE rule_id = @ruleId AND usage_date = @date";
            command.Parameters.AddWithValue("@seconds", seconds);
            command.Parameters.AddWithValue("@ruleId", ruleId);
            command.Parameters.AddWithValue("@date", FormatDate(date));
            var result = command.ExecuteScalar();
            _integrity.SignAfterWrite(connection);
            transaction.Commit();
            _integrity.NotifyCommitted();
            return Convert.ToInt64(result ?? 0L, CultureInfo.InvariantCulture);
        }
    }

    public IReadOnlyList<DailyUsage> GetUsageForDate(DateOnly date)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, rule_id, usage_date, used_seconds, bonus_seconds FROM daily_usage WHERE usage_date = @date ORDER BY rule_id";
            command.Parameters.AddWithValue("@date", FormatDate(date));
            using var reader = command.ExecuteReader();
            var list = new List<DailyUsage>();
            while (reader.Read())
            {
                list.Add(new DailyUsage
                {
                    Id = reader.GetInt64(0),
                    RuleId = reader.GetInt64(1),
                    UsageDate = ParseDate(reader.GetString(2)),
                    UsedSeconds = reader.GetInt64(3),
                    BonusSeconds = reader.GetInt64(4),
                });
            }

            return list;
        }
    }

    public IReadOnlyList<DailyUsage> GetRecentUsage(DateOnly fromDate, DateOnly toDate)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, rule_id, usage_date, used_seconds, bonus_seconds FROM daily_usage WHERE usage_date BETWEEN @from AND @to ORDER BY usage_date, rule_id";
            command.Parameters.AddWithValue("@from", FormatDate(fromDate));
            command.Parameters.AddWithValue("@to", FormatDate(toDate));
            using var reader = command.ExecuteReader();
            var list = new List<DailyUsage>();
            while (reader.Read())
            {
                list.Add(new DailyUsage
                {
                    Id = reader.GetInt64(0),
                    RuleId = reader.GetInt64(1),
                    UsageDate = ParseDate(reader.GetString(2)),
                    UsedSeconds = reader.GetInt64(3),
                    BonusSeconds = reader.GetInt64(4),
                });
            }

            return list;
        }
    }

    #endregion

    #region 设置

    public string? GetSetting(string key)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM settings WHERE key = @key";
            command.Parameters.AddWithValue("@key", key);
            return command.ExecuteScalar() as string;
        }
    }

    public void SetSetting(string key, string value)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = @value";
            command.Parameters.AddWithValue("@key", key);
            command.Parameters.AddWithValue("@value", value);
            command.ExecuteNonQuery();
            _integrity.SignAfterWrite(connection);
            transaction.Commit();
            _integrity.NotifyCommitted();
        }
    }

    #endregion

    private static void AddWeekdayParams(SqliteCommand command, IReadOnlyList<long> limits)
    {
        var names = new[] { "@mon", "@tue", "@wed", "@thu", "@fri", "@sat", "@sun" };
        for (var i = 0; i < 7; i++)
        {
            command.Parameters.AddWithValue(names[i], limits[i]);
        }
    }

    internal static string FormatDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static DateOnly ParseDate(string text) => DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
