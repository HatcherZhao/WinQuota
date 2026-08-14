namespace WinQuota.Core.Engine;

/// <summary>进程 exe 的版本信息（来自 FileVersionInfo），用于产品级匹配。</summary>
public readonly record struct ProcessVersionInfo(string? ProductName, string? CompanyName, string? FilePath);
