using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AnimeStudio.GUI;

internal static class IndexFixture
{
    private delegate void Cipher(Span<byte> bytes, ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, uint counter);

    internal static void WriteVfs(string root)
    {
        var bytes = Bundle();
        var chunk = MD5.HashData(bytes);
        File.WriteAllBytes(Path.Combine(root, Convert.ToHexString(chunk) + ".chk"), bytes);
        using var block = new MemoryStream();
        using var writer = new BinaryWriter(block, Encoding.ASCII, true);
        void Name(string text) { var encoded = Encoding.ASCII.GetBytes(text); writer.Write((ushort)encoded.Length); writer.Write(encoded); }
        writer.Write(4); writer.Write(100); Name("Data/Bundles/Windows/main"); writer.Write(1UL);
        writer.Write(1); writer.Write((long)bytes.Length); writer.Write((byte)0); writer.Write(1);
        writer.Write(chunk); writer.Write(chunk); writer.Write((long)bytes.Length); writer.Write((byte)0); writer.Write(0); writer.Write(1);
        Name("Data/Bundles/Windows/main/offline.ab"); writer.Write(0L); writer.Write(chunk); writer.Write(chunk);
        writer.Write(0L); writer.Write((long)bytes.Length); writer.Write((byte)0); writer.Write((byte)0); writer.Write(0); writer.Write(0);
        writer.Flush();
        var plain = block.ToArray();
        var nonce = new byte[12];
        var cipher = typeof(EndfieldVfsArchive).GetMethod("ChaCha20Xor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.CreateDelegate<Cipher>();
        cipher(plain, Convert.FromHexString("E95B317AC4F828569D23A86BF271DCB53E846FA75C924D671DBA8E38F4CA52E1"), nonce, 1);
        File.WriteAllBytes(Path.Combine(root, "fixture.blc"), nonce.Concat(plain).ToArray());
    }

    private static byte[] Bundle()
    {
        using var data = new MemoryStream();
        using (var writer = new BinaryWriter(data, Encoding.UTF8, true))
        {
            var name = Encoding.UTF8.GetBytes("OfflineFixture");
            writer.Write(name.Length); writer.Write(name); while (data.Position % 4 != 0) writer.Write((byte)0);
            writer.Write(0); // Empty TextAsset script.
        }
        using var serialized = new MemoryStream();
        using (var writer = new BinaryWriter(serialized, Encoding.UTF8, true))
        {
            writer.Write(new byte[20]);
            writer.Write(Encoding.ASCII.GetBytes("2022.3.0f1\0"));
            writer.Write(19); writer.Write(false); writer.Write(1);
            writer.Write(49); writer.Write(false); writer.Write((short)-1); writer.Write(new byte[16]);
            writer.Write(1); while (serialized.Position % 4 != 0) writer.Write((byte)0);
            writer.Write(1L); writer.Write(0U); writer.Write((uint)data.Length); writer.Write(0);
            writer.Write(0); writer.Write(0); writer.Write((byte)0);
            var metadata = checked((uint)(serialized.Position - 20));
            while (serialized.Position % 16 != 0) writer.Write((byte)0);
            var offset = checked((uint)serialized.Position);
            writer.Write(data.ToArray());
            var size = checked((uint)serialized.Length);
            var header = new byte[16];
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0), metadata);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), size);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), 17);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), offset);
            serialized.Position = 0; writer.Write(header);
        }
        var payload = serialized.ToArray();
        using var info = new MemoryStream();
        using (var writer = new BinaryWriter(info, Encoding.ASCII, true))
        {
            writer.Write(new byte[16]); Be(writer, 1U); Be(writer, (uint)payload.Length); Be(writer, (uint)payload.Length);
            writer.Write(new byte[2]); Be(writer, 1U); Be(writer, 0L); Be(writer, (long)payload.Length); Be(writer, 4U);
            writer.Write(Encoding.ASCII.GetBytes("CAB-offline-fixture\0"));
        }
        using var output = new MemoryStream();
        using (var writer = new BinaryWriter(output, Encoding.ASCII, true))
        {
            writer.Write(Encoding.ASCII.GetBytes("UnityFS\0")); Be(writer, 6U);
            writer.Write(Encoding.ASCII.GetBytes("5.x.x\0")); writer.Write(Encoding.ASCII.GetBytes("2022.3.0f1\0"));
            var sizeOffset = output.Position; Be(writer, 0L); Be(writer, (uint)info.Length); Be(writer, (uint)info.Length); Be(writer, 0x40U);
            writer.Write(info.ToArray()); writer.Write(payload); var length = output.Length;
            output.Position = sizeOffset; Be(writer, length);
        }
        return output.ToArray();
    }

    private static void Be(BinaryWriter writer, uint value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); writer.Write(bytes); }
    private static void Be(BinaryWriter writer, long value) { Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(bytes, value); writer.Write(bytes); }
}
