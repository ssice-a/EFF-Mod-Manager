using System.IO;
using System.Text;

namespace EffRepair;

internal enum CompiledModCacheKind
{
    Missing,
    CurrentBlockStore,
    LegacyOrUnknown
}

internal static class CompiledModCache
{
    private static readonly byte[] CurrentBlockStore = Encoding.ASCII.GetBytes("EFF_MOD_BLOCKS/2");

    internal static CompiledModCacheKind Detect(string mod)
    {
        var path = Path.Combine(mod, "compiled.bin");
        if (!File.Exists(path)) return CompiledModCacheKind.Missing;

        using var input = File.OpenRead(path);
        Span<byte> prefix = stackalloc byte[CurrentBlockStore.Length];
        var read = input.Read(prefix);
        return read == CurrentBlockStore.Length && prefix.SequenceEqual(CurrentBlockStore)
            ? CompiledModCacheKind.CurrentBlockStore
            : CompiledModCacheKind.LegacyOrUnknown;
    }

    internal static void ClearDerived(string mod)
    {
        foreach (var name in new[] { "compiled.bin", "source-inputs.bin" })
        {
            var path = Path.Combine(mod, name);
            if (File.Exists(path)) File.Delete(path);
        }

        var blocks = Path.Combine(mod, "compiled.blocks");
        if (Directory.Exists(blocks)) Directory.Delete(blocks, recursive: true);
    }
}
