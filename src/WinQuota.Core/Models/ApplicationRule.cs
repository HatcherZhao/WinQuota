namespace WinQuota.Core.Models;

public class ApplicationRule
{
    public long Id { get; set; }

    public long RuleId { get; set; }

    public string ApplicationName { get; set; } = string.Empty;

    /// <summary>进程名，含 .exe 扩展名，比较时不区分大小写。</summary>
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>可选的完整路径匹配，配置后按路径精确匹配（不区分大小写），否则按进程名匹配。</summary>
    public string? ExePath { get; set; }

    public string? ProductName { get; set; }

    public string? Publisher { get; set; }

    /// <summary>可选的数字签名者匹配（证书 Subject 的 CN，如 "Tencent Technology(Shenzhen) Company Limited"）。
    /// 配置后，任何由该签名者有效签名且未被篡改的 exe 都会命中规则——重命名、复制、换目录均无法绕过。</summary>
    public string? Signer { get; set; }

    public bool Enabled { get; set; } = true;
}
