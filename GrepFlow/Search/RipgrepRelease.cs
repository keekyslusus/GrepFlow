using System.Runtime.InteropServices;

namespace GrepFlow.Search;

public static class RipgrepRelease
{
    public const string Version = "15.2.0";

    private const string BaseUrl = "https://github.com/BurntSushi/ripgrep/releases/download/" + Version + "/";

    private const string Sha256X64 = "71b2fef860abe467217a538ff31de02f5258807c0129f771846f87bd029aafc5";
    private const string Sha256Arm64 = "e4abca10c3a64ebea742667dd7009449d49403db5460dd6873e389fa2945360f";
    private const string Sha256X86 = "9bf73bdb3fda9ad4b0235e1295b02c717031c986afa4d7c05dd0af8b74010a95";

    public static bool TryResolveAsset(out string triple, out string zipUrl, out string expectedSha256)
    {
        if (!TryMapTriple(RuntimeInformation.OSArchitecture, out triple))
        {
            zipUrl = string.Empty;
            expectedSha256 = string.Empty;
            return false;
        }

        var fileName = $"ripgrep-{Version}-{triple}.zip";
        zipUrl = BaseUrl + fileName;
        expectedSha256 = triple switch
        {
            "x86_64-pc-windows-msvc" => Sha256X64,
            "aarch64-pc-windows-msvc" => Sha256Arm64,
            "i686-pc-windows-msvc" => Sha256X86,
            _ => string.Empty,
        };
        return expectedSha256.Length > 0;
    }

    // OS architecture, not process: ARM64 Windows may host an x64-emulated Flow process.
    public static bool TryMapTriple(Architecture architecture, out string triple)
    {
        triple = architecture switch
        {
            Architecture.X64 => "x86_64-pc-windows-msvc",
            Architecture.Arm64 => "aarch64-pc-windows-msvc",
            Architecture.X86 => "i686-pc-windows-msvc",
            _ => string.Empty,
        };
        return triple.Length > 0;
    }
}
