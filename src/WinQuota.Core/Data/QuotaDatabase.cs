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
                    reminder_minutes TEXT   NOT NULL DEFAULT '30,15,5,1',
                    max_extensions  INTEGER NOT NULL DEFAULT 0,
                    extension_minutes INTEGER NOT NULL DEFAULT 20,
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
                    extensions_used INTEGER NOT NULL DEFAULT 0,
                    UNIQUE (rule_id, usage_date)
                );

                CREATE INDEX IF NOT EXISTS idx_daily_usage_date ON daily_usage (usage_date);

                CREATE TABLE IF NOT EXISTS settings (
                    key   TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();

            // 旧库升级迁移：0.5.x 及之前没有 signer 列（签名者匹配，第四阶段）；
            // 0.7.x 及之前没有提醒阈值 / 延期配置列（v0.8.0）。
            var migrated = EnsureSignerColumn(connection) | EnsureV08Columns(connection);
            // 全新库 / 旧版本升级 / 完整性防护首次启用：写入基线签名。
            if (migrated || _integrity.GetStoredHmac(connection) is null)
            {
                _integrity.WriteBaseline(connection);
            }
        }
    }

    /// <summary>v0.8.0 迁移：提醒阈值与延期配置列、用量表的延期计数列。</summary>
    private static bool EnsureV08Columns(SqliteConnection connection)
    {
        var migrated = false;
        migrated |= AddColumnIfMissing(connection, "limit_rules", "reminder_minutes", "TEXT NOT NULL DEFAULT '30,15,5,1'");
        migrated |= AddColumnIfMissing(connection, "limit_rules", "max_extensions", "INTEGER NOT NULL DEFAULT 0");
        migrated |= AddColumnIfMissing(connection, "limit_rules", "extension_minutes", "INTEGER NOT NULL DEFAULT 20");
        migrated |= AddColumnIfMissing(connection, "daily_usage", "extensions_used", "INTEGER NOT NULL DEFAULT 0");
        return migrated;
    }

    private static bool AddColumnIfMissing(SqliteConnection connection, string table, string column, string definition)
    {
        using (var check = connection.CreateCommand())
        {
            check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
            if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
            {
                return false;
            }
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
        return true;
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
        string? signer = null,
        string? reminderMinutes = null,
        int maxExtensions = 0,
        int extensionMinutes = 20)
    {
        if (processNames.Count == 0)
        {
            throw new ArgumentException("至少需要一个进程名。", nameof(processNames));
        }

        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();

            var ruleId = InsertLimitRule(connection, name, RuleType.APPLICATION, weekdayLimitsMonToSun, reminderMinutes, maxExtensions, extensionMinutes);

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
    public long AddComputerRule(string name, IReadOnlyList<long> weekdayLimitsMonToSun,
        string? reminderMinutes = null, int maxExtensions = 0, int extensionMinutes = 20)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var ruleId = InsertLimitRule(connection, name, RuleType.COMPUTER, weekdayLimitsMonToSun, reminderMinutes, maxExtensions, extensionMinutes);
            _integrity.SignAfterWrite(connection);
            transaction.Commit();
            _integrity.NotifyCommitted();
            return ruleId;
        }
    }

    private static long InsertLimitRule(SqliteConnection connection, string name, RuleType type, IReadOnlyList<long> weekdayLimitsMonToSun,
        string? reminderMinutes, int maxExtensions, int extensionMinutes)
    {
        if (weekdayLimitsMonToSun.Count != 7)
        {
            throw new ArgumentException("必须提供周一到周日共 7 个额度值。", nameof(weekdayLimitsMonToSun));
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO limit_rules (name, type, enabled, monday_limit, tuesday_limit, wednesday_limit,
                                     thursday_limit, friday_limit, saturday_limit, sunday_limit,
                                     reminder_minutes, max_extensions, extension_minutes)
            VALUES (@name, @type, 1, @mon, @tue, @wed, @thu, @fri, @sat, @sun,
                    @remind, @maxExt, @extMin);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@type", (int)type);
        command.Parameters.AddWithValue("@remind", string.IsNullOrWhiteSpace(reminderMinutes) ? "30,15,5,1" : reminderMinutes.Trim());
        command.Parameters.AddWithValue("@maxExt", Math.Max(0, maxExtensions));
        command.Parameters.AddWithValue("@extMin", Math.Max(1, extensionMinutes));
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
                command.CommandText = "SELECT id, name, type, enabled, monday_limit, tuesday_limit, wednesday_limit, thursday_limit, friday_limit, saturday_limit, sunday_limit, reminder_minutes, max_extensions, extension_minutes FROM limit_rules ORDER BY id";
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
                        ReminderMinutes = reader.IsDBNull(11) ? "30,15,5,1" : reader.GetString(11),
                        MaxExtensions = (int)reader.GetInt64(12),
                        ExtensionMinutes = (int)reader.GetInt64(13),
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

    /// <summary>
    /// 原位编辑规则：改名并替换应用进程列表（额度与用量历史保持不变，
    /// 相比删除重建不会丢失当日已用时间）。name / processNames 传 null 表示不改。
    /// </summary>
    public bool UpdateRuleDetails(
        long ruleId,
        string? name,
        IReadOnlyList<string>? processNames,
        string? exePath = null,
        string? productName = null,
        string? publisher = null,
        string? signer = null,
        string? reminderMinutes = null,
        int? maxExtensions = null,
        int? extensionMinutes = null)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();

            long changed = 0;
            if (!string.IsNullOrWhiteSpace(name) ||
                !string.IsNullOrWhiteSpace(reminderMinutes) ||
                maxExtensions is not null ||
                extensionMinutes is not null)
            {
                var sets = new List<string>();
                using var command = connection.CreateCommand();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    sets.Add("name = @name");
                    command.Parameters.AddWithValue("@name", name.Trim());
                }

                if (!string.IsNullOrWhiteSpace(reminderMinutes))
                {
                    sets.Add("reminder_minutes = @remind");
                    command.Parameters.AddWithValue("@remind", reminderMinutes.Trim());
                }

                if (maxExtensions is not null)
                {
                    sets.Add("max_extensions = @maxExt");
                    command.Parameters.AddWithValue("@maxExt", Math.Max(0, maxExtensions.Value));
                }

                if (extensionMinutes is not null)
                {
                    sets.Add("extension_minutes = @extMin");
                    command.Parameters.AddWithValue("@extMin", Math.Max(1, extensionMinutes.Value));
                }

                command.CommandText = $"UPDATE limit_rules SET {string.Join(", ", sets)} WHERE id = @id";
                command.Parameters.AddWithValue("@id", ruleId);
                changed = command.ExecuteNonQuery();
            }

            if (processNames is { Count: > 0 } && GetRuleType(connection, ruleId) == (int)RuleType.APPLICATION)
            {
                using (var delete = connection.CreateCommand())
                {
                    delete.CommandText = "DELETE FROM application_rules WHERE rule_id = @id";
                    delete.Parameters.AddWithValue("@id", ruleId);
                    var removed = delete.ExecuteNonQuery();
                    if (changed == 0 && removed > 0)
                    {
                        changed = 1; // 规则存在（应用行被替换）
                    }
                }

                var applicationName = string.IsNullOrWhiteSpace(name)
                    ? GetRuleName(connection, ruleId) ?? string.Empty
                    : name.Trim();
                foreach (var processName in processNames)
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = """
                        INSERT INTO application_rules (rule_id, application_name, process_name, exe_path, product_name, publisher, signer, enabled)
                        VALUES (@ruleId, @appName, @processName, @exePath, @productName, @publisher, @signer, 1);
                        """;
                    command.Parameters.AddWithValue("@ruleId", ruleId);
                    command.Parameters.AddWithValue("@appName", applicationName);
                    command.Parameters.AddWithValue("@processName", processName);
                    command.Parameters.AddWithValue("@exePath", (object?)exePath ?? DBNull.Value);
                    command.Parameters.AddWithValue("@productName", (object?)productName ?? DBNull.Value);
                    command.Parameters.AddWithValue("@publisher", (object?)publisher ?? DBNull.Value);
                    command.Parameters.AddWithValue("@signer", (object?)signer ?? DBNull.Value);
                    command.ExecuteNonQuery();
                }
            }

            if (changed == 1)
            {
                _integrity.SignAfterWrite(connection);
                transaction.Commit();
                _integrity.NotifyCommitted();
            }

            return changed == 1;
        }
    }

    private static string? GetRuleName(SqliteConnection connection, long ruleId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM limit_rules WHERE id = @id";
        command.Parameters.AddWithValue("@id", ruleId);
        return command.ExecuteScalar() as string;
    }

    private static int? GetRuleType(SqliteConnection connection, long ruleId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type FROM limit_rules WHERE id = @id";
        command.Parameters.AddWithValue("@id", ruleId);
        return command.ExecuteScalar() is { } value ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : null;
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
            query.CommandText = "SELECT id, used_seconds, bonus_seconds, extensions_used FROM daily_usage WHERE rule_id = @ruleId AND usage_date = @date";
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
                    ExtensionsUsed = reader.GetInt64(3),
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

    /// <summary>
    /// 用户自助延期（无需管理员 PIN）：把规则的 extension_minutes 计入当天 bonus 并累计次数。
    /// 次数上限由本方法在事务内强制（规则 max_extensions 与当天已用次数比较），超限返回 false。
    /// </summary>
    public (bool Granted, long ExtensionsUsed, long MaxExtensions, long ExtensionSeconds) ExtendUsage(long ruleId, DateOnly date)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();

            long maxExtensions, extensionMinutes;
            using (var rule = connection.CreateCommand())
            {
                rule.CommandText = "SELECT max_extensions, extension_minutes FROM limit_rules WHERE id = @id";
                rule.Parameters.AddWithValue("@id", ruleId);
                using var reader = rule.ExecuteReader();
                if (!reader.Read())
                {
                    return (false, 0, 0, 0); // 规则不存在
                }

                maxExtensions = reader.GetInt64(0);
                extensionMinutes = reader.GetInt64(1);
            }

            var usage = GetOrCreateUsage(connection, ruleId, date, out var created);
            if (usage.ExtensionsUsed >= maxExtensions)
            {
                if (created)
                {
                    _integrity.SignAfterWrite(connection);
                    transaction.Commit();
                    _integrity.NotifyCommitted();
                }

                return (false, usage.ExtensionsUsed, maxExtensions, 0);
            }

            using (var update = connection.CreateCommand())
            {
                update.CommandText = """
                    UPDATE daily_usage
                    SET bonus_seconds = bonus_seconds + @minutes, extensions_used = extensions_used + 1
                    WHERE rule_id = @ruleId AND usage_date = @date
                    """;
                update.Parameters.AddWithValue("@minutes", extensionMinutes * 60);
                update.Parameters.AddWithValue("@ruleId", ruleId);
                update.Parameters.AddWithValue("@date", FormatDate(date));
                update.ExecuteNonQuery();
            }

            _integrity.SignAfterWrite(connection);
            transaction.Commit();
            _integrity.NotifyCommitted();
            return (true, usage.ExtensionsUsed + 1, maxExtensions, extensionMinutes * 60);
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
            command.CommandText = "SELECT id, rule_id, usage_date, used_seconds, bonus_seconds, extensions_used FROM daily_usage WHERE usage_date = @date ORDER BY rule_id";
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
                    ExtensionsUsed = reader.GetInt64(5),
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
            command.CommandText = "SELECT id, rule_id, usage_date, used_seconds, bonus_seconds, extensions_used FROM daily_usage WHERE usage_date BETWEEN @from AND @to ORDER BY usage_date, rule_id";
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
                    ExtensionsUsed = reader.GetInt64(5),
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
