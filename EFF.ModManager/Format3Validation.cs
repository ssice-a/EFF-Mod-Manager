using System.Runtime.InteropServices;
using System.Text;

namespace EffModManager;

// Loads the validator shipped with Manager, never a downloaded cache compiler.
internal static class Format3Validation
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate int ValidateManifest(string root, byte[] ini, nuint size, byte[] response, nuint capacity);

    private static readonly Lazy<ValidateManifest> Validator = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "native", "eff_author_assets_format3.dll");
        if (!File.Exists(path)) throw new IOException("管理器安装缺少 Format 3 校验器，请重新安装完整管理器；不会调用旧缓存编译器。");
        var library = NativeLibrary.Load(path);
        try
        {
            return Marshal.GetDelegateForFunctionPointer<ValidateManifest>(
                NativeLibrary.GetExport(library, "EFF_AuthorAssets_ValidateManifestW"));
        }
        catch
        {
            NativeLibrary.Free(library);
            throw new IOException("管理器附带的 Format 3 校验器版本不匹配，请重新安装完整管理器。");
        }
    });

    internal static string Validate(string mod, CancellationToken token)
    {
        ModService.RequireFormat3(mod);
        var entry = ModService.ScanTree(mod).Single();
        if (new FileInfo(entry.IniPath).Length > 16 * 1024 * 1024)
            throw new InvalidDataException("mod.ini exceeds the 16 MiB validation limit.");
        token.ThrowIfCancellationRequested();
        var ini = File.ReadAllBytes(entry.IniPath);
        var response = new byte[1024 * 1024];
        var status = Validator.Value(entry.Folder, ini, (nuint)ini.Length, response, (nuint)response.Length);
        token.ThrowIfCancellationRequested();
        var length = Array.IndexOf(response, (byte)0);
        var report = Encoding.UTF8.GetString(response, 0, length < 0 ? response.Length : length);
        if (status != 0)
            throw new InvalidDataException(report + "；请根据错误修正 INI 声明，或恢复缺失的作者资源后重试。此检查不会重建作者资源，原 Mod 未更改。");
        return Path.GetFileName(entry.Folder) + "：INI 装配、控制表达式和声明文件路径校验通过。资源内容与当前游戏逻辑命中在请求时按需校验；无需离线重编译，未更改文件。 " + report;
    }
}
