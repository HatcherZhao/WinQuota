using WinQuota.Core.Data;
using WinQuota.Core.Engine;

namespace WinQuota.Core.Tests;

public class IntegrityGuardTests : IDisposable
{
    private readonly string _databasePath;

    public IntegrityGuardTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"winquota-integrity-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(_databasePath)!, Path.GetFileName(_databasePath) + "*"))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void FreshDatabase_HasBaselineAndVerifiesOk()
    {
        var database = new QuotaDatabase(_databasePath);
        Assert.True(database.HasIntegrityKey);
        Assert.Equal(IntegrityStatus.Ok, database.VerifyIntegrity());
    }

    [Fact]
    public void LegitimateWrites_KeepIntegrityOk()
    {
        var database = new QuotaDatabase(_databasePath);
        var ruleId = database.AddApplicationRule("野狐围棋", [7200, 7200, 7200, 7200, 7200, 7200, 7200], ["foxwq.exe"]);
        database.AddUsedSeconds(ruleId, new DateOnly(2026, 8, 15), 300);
        database.AddBonusSeconds(ruleId, new DateOnly(2026, 8, 15), 600);
        database.SetSetting("pin.hash", "abc");
        Assert.Equal(IntegrityStatus.Ok, database.VerifyIntegrity());
    }

    [Fact]
    public void DirectSqlEditOfUsedSeconds_IsDetectedAsTampered()
    {
        var database = new QuotaDatabase(_databasePath);
        var ruleId = database.AddComputerRule("电脑", [7200, 7200, 7200, 7200, 7200, 7200, 7200]);
        database.AddUsedSeconds(ruleId, new DateOnly(2026, 8, 15), 3600);
        Assert.Equal(IntegrityStatus.Ok, database.VerifyIntegrity());

        // 模拟绕过 QuotaDatabase 的直改：把已用秒数改小以重置额度
        ExecuteRaw($"UPDATE daily_usage SET used_seconds = 10 WHERE rule_id = {ruleId}");

        Assert.Equal(IntegrityStatus.Tampered, database.VerifyIntegrity());
    }

    [Fact]
    public void DirectSqlEditOfRuleQuota_IsDetectedAsTampered()
    {
        var database = new QuotaDatabase(_databasePath);
        database.AddComputerRule("电脑", [7200, 7200, 7200, 7200, 7200, 7200, 7200]);
        ExecuteRaw("UPDATE limit_rules SET monday_limit = 999999");

        Assert.Equal(IntegrityStatus.Tampered, database.VerifyIntegrity());
    }

    [Fact]
    public void DeletingPinRow_IsDetectedAsTampered()
    {
        var database = new QuotaDatabase(_databasePath);
        database.SetSetting("pin.hash", "secret");
        ExecuteRaw("DELETE FROM settings WHERE key = 'pin.hash'");

        Assert.Equal(IntegrityStatus.Tampered, database.VerifyIntegrity());
    }

    [Fact]
    public void DeletingUsageRow_IsDetectedAsTampered()
    {
        var database = new QuotaDatabase(_databasePath);
        var ruleId = database.AddComputerRule("电脑", [7200, 7200, 7200, 7200, 7200, 7200, 7200]);
        database.AddUsedSeconds(ruleId, new DateOnly(2026, 8, 15), 3600);
        ExecuteRaw($"DELETE FROM daily_usage WHERE rule_id = {ruleId}");

        Assert.Equal(IntegrityStatus.Tampered, database.VerifyIntegrity());
    }

    [Fact]
    public void DeletingBaselineRow_IsNotSilentlyRebaselined()
    {
        var database = new QuotaDatabase(_databasePath);
        database.AddComputerRule("电脑", [7200, 7200, 7200, 7200, 7200, 7200, 7200]);
        ExecuteRaw("DELETE FROM settings WHERE key = 'db.integrity'");

        // 基线行被删而密钥仍在：不能当作“全新库”静默重建基线，否则删行即可绕过校验
        Assert.Equal(IntegrityStatus.NoBaseline, database.VerifyIntegrity());
        Assert.True(database.HasIntegrityKey);
    }

    [Fact]
    public void MissingKeyFile_IsReportedAndCanBeReinitialized()
    {
        var database = new QuotaDatabase(_databasePath);
        database.AddComputerRule("电脑", [7200, 7200, 7200, 7200, 7200, 7200, 7200]);
        Assert.Equal(IntegrityStatus.Ok, database.VerifyIntegrity());

        File.Delete(_databasePath + ".key");
        var reloaded = new QuotaDatabase(_databasePath);
        {
            Assert.False(reloaded.HasIntegrityKey);
            Assert.Equal(IntegrityStatus.KeyMissing, reloaded.VerifyIntegrity());

            // 管理员确认后重建基线，恢复正常
            reloaded.ReinitializeIntegrity();
            Assert.Equal(IntegrityStatus.Ok, reloaded.VerifyIntegrity());
        }
    }

    [Fact]
    public void DatabaseFileRollbackToOlderCopy_IsDetected()
    {
        var date = new DateOnly(2026, 8, 15);
        long ruleId;
        string backupPath;
        var database = new QuotaDatabase(_databasePath);
        {
            ruleId = database.AddComputerRule("电脑", [7200, 7200, 7200, 7200, 7200, 7200, 7200]);
            database.AddUsedSeconds(ruleId, date, 3600);
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        backupPath = _databasePath + ".backup";
        File.Copy(_databasePath, backupPath);

        // 备份之后再继续累计使用（序号前进）
        var database2 = new QuotaDatabase(_databasePath);
        {
            database2.AddUsedSeconds(ruleId, date, 1800);
            Assert.Equal(IntegrityStatus.Ok, database2.VerifyIntegrity());
        }

        // 攻击：用旧副本覆盖当前数据库（密钥文件不动）
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(_databasePath + suffix))
            {
                File.Delete(_databasePath + suffix);
            }
        }

        File.Copy(backupPath, _databasePath, overwrite: true);

        var database3 = new QuotaDatabase(_databasePath);
        Assert.Equal(IntegrityStatus.Tampered, database3.VerifyIntegrity());
    }

    [Fact]
    public void LegacyDatabaseWithoutSignerColumn_MigratesAndKeepsIntegrity()
    {
        // 手工构造 0.5.x 的旧库结构（无 signer 列）
        var legacyPath = Path.Combine(Path.GetTempPath(), $"winquota-legacy-{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={legacyPath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE limit_rules (
                        id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, type INTEGER NOT NULL,
                        enabled INTEGER NOT NULL DEFAULT 1,
                        monday_limit INTEGER NOT NULL DEFAULT 0, tuesday_limit INTEGER NOT NULL DEFAULT 0,
                        wednesday_limit INTEGER NOT NULL DEFAULT 0, thursday_limit INTEGER NOT NULL DEFAULT 0,
                        friday_limit INTEGER NOT NULL DEFAULT 0, saturday_limit INTEGER NOT NULL DEFAULT 0,
                        sunday_limit INTEGER NOT NULL DEFAULT 0,
                        created_at TEXT NOT NULL DEFAULT (datetime('now', 'localtime')));
                    CREATE TABLE application_rules (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        rule_id INTEGER NOT NULL REFERENCES limit_rules(id) ON DELETE CASCADE,
                        application_name TEXT NOT NULL, process_name TEXT NOT NULL,
                        exe_path TEXT, product_name TEXT, publisher TEXT,
                        enabled INTEGER NOT NULL DEFAULT 1);
                    CREATE TABLE daily_usage (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        rule_id INTEGER NOT NULL REFERENCES limit_rules(id) ON DELETE CASCADE,
                        usage_date TEXT NOT NULL, used_seconds INTEGER NOT NULL DEFAULT 0,
                        bonus_seconds INTEGER NOT NULL DEFAULT 0, UNIQUE (rule_id, usage_date));
                    CREATE INDEX IF NOT EXISTS idx_daily_usage_date ON daily_usage (usage_date);
                    CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                    INSERT INTO limit_rules (name, type) VALUES ('野狐围棋', 1);
                    INSERT INTO application_rules (rule_id, application_name, process_name) VALUES (1, '野狐围棋', 'foxwq.exe');
                    """;
                command.ExecuteNonQuery();
            }

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var database = new QuotaDatabase(legacyPath);
            Assert.Equal(IntegrityStatus.Ok, database.VerifyIntegrity());

            // 迁移后可以正常写入带签名者的规则
            database.AddApplicationRule("新规则", [60, 60, 60, 60, 60, 60, 60], ["a.exe"], signer: "Tencent Technology(Shenzhen) Company Limited");
            Assert.Equal("Tencent Technology(Shenzhen) Company Limited", Assert.Single(database.GetRules(), r => r.Rule.Name == "新规则").Apps[0].Signer);
            Assert.Equal(IntegrityStatus.Ok, database.VerifyIntegrity());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(legacyPath)!, Path.GetFileName(legacyPath) + "*"))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private void ExecuteRaw(string sql)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

public class UsageGuardTests
{
    [Fact]
    public void UsedSecondsDecrease_IsTampered()
    {
        Assert.True(UsageGuard.IsTampered(observedUsed: 100, rememberedMaxUsed: 600, observedBonus: 0, rememberedMaxBonus: 0));
    }

    [Fact]
    public void BonusSecondsDecrease_IsTampered()
    {
        Assert.True(UsageGuard.IsTampered(observedUsed: 600, rememberedMaxUsed: 600, observedBonus: 0, rememberedMaxBonus: 900));
    }

    [Fact]
    public void GrowthOrSmallRounding_IsNotTampered()
    {
        Assert.False(UsageGuard.IsTampered(observedUsed: 600, rememberedMaxUsed: 600, observedBonus: 900, rememberedMaxBonus: 900));
        Assert.False(UsageGuard.IsTampered(observedUsed: 599, rememberedMaxUsed: 600, observedBonus: 0, rememberedMaxBonus: 0));
    }
}

public class SignatureMatchingTests
{
    [Fact]
    public void TrustedMatchingSigner_Matches()
    {
        var rule = new Models.ApplicationRule { ProcessName = "foxwq.exe", Signer = "Tencent Technology(Shenzhen) Company Limited" };
        Assert.True(AppMatcher.MatchesBySignature(
            new SignatureInfo(true, "Tencent Technology(Shenzhen) Company Limited"), rule));
    }

    [Fact]
    public void UntrustedSignature_NeverMatches()
    {
        var rule = new Models.ApplicationRule { ProcessName = "foxwq.exe", Signer = "Tencent Technology(Shenzhen) Company Limited" };
        // 签名校验失败（文件被篡改/未签名/读取失败）时即使 CN 相同也不命中
        Assert.False(AppMatcher.MatchesBySignature(new SignatureInfo(false, "Tencent Technology(Shenzhen) Company Limited"), rule));
    }

    [Fact]
    public void DifferentSigner_DoesNotMatch()
    {
        var rule = new Models.ApplicationRule { ProcessName = "foxwq.exe", Signer = "Tencent Technology(Shenzhen) Company Limited" };
        Assert.False(AppMatcher.MatchesBySignature(new SignatureInfo(true, "Some Other Company"), rule));
    }

    [Fact]
    public void RuleWithoutSigner_NeverMatchesBySignature()
    {
        var rule = new Models.ApplicationRule { ProcessName = "foxwq.exe" };
        Assert.False(AppMatcher.MatchesBySignature(new SignatureInfo(true, "Anyone"), rule));
    }

    [Fact]
    public void SignerComparison_IsCaseInsensitive()
    {
        var rule = new Models.ApplicationRule { ProcessName = "foxwq.exe", Signer = "tencent technology(shenzhen) company limited" };
        Assert.True(AppMatcher.MatchesBySignature(new SignatureInfo(true, "Tencent Technology(Shenzhen) Company Limited"), rule));
    }
}
