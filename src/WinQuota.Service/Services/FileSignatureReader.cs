using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using WinQuota.Core.Engine;

namespace WinQuota.Service.Services;

/// <summary>
/// 文件数字签名验证（第四阶段防绕过）：
/// WinVerifyTrust 校验 Authenticode 签名（签名有效且文件未被修改），
/// 再从签名证书中提取 Subject 的 CN 作为“签名者”用于规则匹配。
/// 未签名 / 签名无效 / 读取失败统一返回 Trusted=false。
/// </summary>
public partial class FileSignatureReader
{
    /// <summary>读取 exe 的签名验证结果。任何失败都视为不可信，不抛异常。</summary>
    public static SignatureInfo Read(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return new SignatureInfo(false, null);
        }

        try
        {
            // .NET 10 尚无 X509CertificateLoader 的签名文件加载 API，CreateFromSignedFile 仍是唯一途径
#pragma warning disable SYSLIB0057
            using var certificate = X509Certificate.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
            var signerCn = ExtractCommonName(certificate.Subject);
            var trusted = VerifyTrust(filePath);
            return new SignatureInfo(trusted, signerCn);
        }
        catch
        {
            // 未签名文件加载签名证书会抛 CryptographicException
            return new SignatureInfo(false, null);
        }
    }

    [GeneratedRegex(@"(?:^|,)\s*CN\s*=\s*((?:\\.|[^,""\\])+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommonNameRegex();

    /// <summary>从证书 Subject（如 "CN=Tencent Technology..., O=..."）中提取 CN 字段值。</summary>
    internal static string? ExtractCommonName(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        var match = CommonNameRegex().Match(subject);
        if (!match.Success)
        {
            return null;
        }

        // RFC4514 转义：\, 表示字面逗号等；规则匹配与界面展示都应使用解码后的值
        return match.Groups[1].Value.Trim().Replace("\\,", ",").Replace("\\\"", "\"").Replace("\\\\", "\\");
    }

    private static bool VerifyTrust(string filePath)
    {
        try
        {
            var fileInfo = new NativeMethods.WinTrustFileInfo(filePath);
            var data = new NativeMethods.WinTrustData
            {
                dwStructSize = (uint)Marshal.SizeOf<NativeMethods.WinTrustData>(),
                dwUIChoice = NativeMethods.WtdUiNone,
                fdwRevocationChecks = NativeMethods.WtdRevokeNone,
                dwUnionChoice = NativeMethods.WtdChoiceFile,
                pFile = Marshal.AllocHGlobal(Marshal.SizeOf(fileInfo)),
                dwStateAction = NativeMethods.WtdStateActionVerify,
            };

            try
            {
                Marshal.StructureToPtr(fileInfo, data.pFile, fDeleteOld: false);
                var action = NativeMethods.WinTrustActionGenericVerifyV2;
                return NativeMethods.WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
            }
            finally
            {
                if (data.pFile != IntPtr.Zero)
                {
                    // 先释放 WinVerifyTrust 内部为本次校验缓存的状态
                    data.dwStateAction = NativeMethods.WtdStateActionClose;
                    var action = NativeMethods.WinTrustActionGenericVerifyV2;
                    _ = NativeMethods.WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                    Marshal.DestroyStructure<NativeMethods.WinTrustFileInfo>(data.pFile);
                    Marshal.FreeHGlobal(data.pFile);
                }
            }
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static class NativeMethods
    {
        public const uint WtdUiNone = 2;
        public const uint WtdRevokeNone = 0;
        public const uint WtdChoiceFile = 1;
        public const uint WtdStateActionVerify = 1;
        public const uint WtdStateActionClose = 2;

        public static readonly Guid WinTrustActionGenericVerifyV2 = new("00aac56b-cd44-11d0-8cc2-00c04fc295ee");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WinTrustFileInfo
        {
            public WinTrustFileInfo(string path)
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>();
                pcwszFilePath = path;
                hFile = IntPtr.Zero;
                pgKnownSubject = IntPtr.Zero;
            }

            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WinTrustData
        {
            public uint dwStructSize;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWvtStateData;
            public string? pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
        public static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionId, ref WinTrustData pWvtData);
    }
}
