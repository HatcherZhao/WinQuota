namespace WinQuota.Core.Engine;

/// <summary>进程 exe 的版本信息（来自 FileVersionInfo），用于产品级匹配。</summary>
public readonly record struct ProcessVersionInfo(string? ProductName, string? CompanyName, string? FilePath);

/// <summary>进程 exe 的数字签名验证结果（WinVerifyTrust + 签名证书 Subject），用于签名者匹配。</summary>
/// <param name="Trusted">WinVerifyTrust 校验通过（签名有效且文件未被修改）。未签名 / 校验失败 / 读取失败均为 false。</param>
/// <param name="SignerCn">签名证书 Subject 中的 CN 字段，作为规则匹配值。</param>
public readonly record struct SignatureInfo(bool Trusted, string? SignerCn);
