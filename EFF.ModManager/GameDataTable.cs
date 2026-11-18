using System.Buffers.Binary;
using System.Text;

namespace EffModManager;

// Endfield TableCfg files describe their own field layout. Read named fields,
// rather than pinning offsets to one game version or scraping string pools.
internal sealed class GameDataTable
{
    private sealed record Field(string Name, Kind Type);
    private sealed record Kind(byte Code, int Reference = 0, Kind? Element = null)
    {
        internal int Size => Code switch { 0 => 1, 2 or 4 or 6 or 7 or 8 or 9 => 4, 3 or 5 => 8,
            _ => throw new InvalidDataException($"Unsupported TableCfg type: {Code}") };
    }
    private readonly byte[] _bytes;
    private readonly Dictionary<int, Field[]> _structures = [];
    private readonly Kind _key;
    private readonly Kind _value;
    private readonly int _root;

    internal GameDataTable(byte[] bytes)
    {
        _bytes = bytes;
        var cursor = Int(0);
        var schemaEnd = Int(4);
        _root = Int(8);
        if (cursor != 12 || schemaEnd < cursor || schemaEnd >= _root || _root >= bytes.Length)
            throw new InvalidDataException("Invalid TableCfg header.");
        var count = Int(cursor); cursor += 4;
        if (count < 0 || count > (schemaEnd - cursor) / 12) throw new InvalidDataException("Invalid TableCfg schema count.");
        for (var i = 0; i < count; i++)
        {
            var kind = Byte(ref cursor);
            cursor = Align(cursor, 4);
            var identity = Int(cursor); cursor += 4;
            _ = String(ref cursor);
            cursor = Align(cursor, 4);
            var fields = Int(cursor); cursor += 4;
            if (fields < 0 || fields > schemaEnd - cursor) throw new InvalidDataException("Invalid TableCfg fields.");
            var members = new List<Field>();
            for (var f = 0; f < fields; f++)
            {
                var name = String(ref cursor);
                if (kind == 6) { cursor = Align(cursor, 4); _ = Int(cursor); cursor += 4; }
                else if (kind == 8) members.Add(new(name, ReadKind(ref cursor)));
                else throw new InvalidDataException("Unsupported TableCfg schema.");
            }
            if (kind == 8) _structures.Add(identity, members.ToArray());
        }
        if (cursor != schemaEnd || Byte(ref cursor) != 10) throw new InvalidDataException("Unsupported TableCfg root.");
        _ = String(ref cursor);
        _key = ReadKind(ref cursor);
        _value = ReadKind(ref cursor);
    }

    private Kind ReadKind(ref int cursor)
    {
        var code = Byte(ref cursor);
        if (code is 6 or 8)
        {
            cursor = Align(cursor, 4); var reference = Int(cursor); cursor += 4;
            return new(code, reference);
        }
        if (code == 9) return new(code, Element: ReadKind(ref cursor));
        var kind = new Kind(code); _ = kind.Size; return kind;
    }

    internal IEnumerable<(object Key, int Value)> Entries()
    {
        var count = Int(_root);
        if (count < 0 || count > (_bytes.Length - _root - 4) / 8) throw new InvalidDataException("Invalid TableCfg dictionary.");
        var valueOffset = Align(_key.Size, _value.Size);
        var stride = Align(valueOffset + _value.Size, Math.Max(_key.Size, _value.Size));
        long total = 0;
        for (var bucket = 0; bucket < count; bucket++)
        {
            var start = Int(_root + 4 + bucket * 8);
            var length = Int(_root + 8 + bucket * 8);
            if (length < 0 || (total += length) > count) throw new InvalidDataException("Invalid TableCfg bucket.");
            for (var i = 0; i < length; i++)
            {
                var entry = Align(checked(start + i * stride), _key.Size);
                yield return (Scalar(_key, entry), _value.Code == 8 ? Int(entry + valueOffset) : entry + valueOffset);
            }
        }
        if (total != count) throw new InvalidDataException("Incomplete TableCfg dictionary.");
    }

    internal string Text(int value) => (string)Scalar(new(7), value);
    internal string StringField(int row, string name) => (string)Scalar(FieldAt(row, name).Type, FieldAt(row, name).Offset);
    internal long LocalizedId(int row, string field)
    {
        var member = FieldAt(row, field);
        if (member.Type.Code != 8) throw new InvalidDataException("Expected an I18nText field: " + field);
        var nested = Int(member.Offset);
        var id = FieldAt(nested, "id", member.Type.Reference);
        return Convert.ToInt64(Scalar(id.Type, id.Offset));
    }

    private (Kind Type, int Offset) FieldAt(int row, string name, int? structure = null)
    {
        if (!_structures.TryGetValue(structure ?? _value.Reference, out var fields)) throw new InvalidDataException("Unknown TableCfg structure.");
        var offset = row;
        foreach (var field in fields)
        {
            offset = Align(offset, field.Type.Size);
            if (field.Name == name) return (field.Type, offset);
            offset = checked(offset + field.Type.Size);
        }
        throw new InvalidDataException("Missing TableCfg field: " + name);
    }

    private object Scalar(Kind kind, int offset) => kind.Code switch
    {
        0 => IntByte(offset) != 0,
        2 or 6 => Int(offset),
        3 => Long(offset),
        7 => StringAt(Int(offset)),
        _ => throw new InvalidDataException("Unsupported TableCfg value.")
    };
    private byte IntByte(int offset) { Check(offset, 1); return _bytes[offset]; }
    private byte Byte(ref int cursor) => IntByte(cursor++);
    private int Int(int offset) { Check(offset, 4); return BinaryPrimitives.ReadInt32LittleEndian(_bytes.AsSpan(offset, 4)); }
    private long Long(int offset) { Check(offset, 8); return BinaryPrimitives.ReadInt64LittleEndian(_bytes.AsSpan(offset, 8)); }
    private string StringAt(int offset) { if (offset == -1) return ""; return String(ref offset); }
    private string String(ref int cursor)
    {
        Check(cursor, 1); var end = Array.IndexOf(_bytes, (byte)0, cursor);
        if (end < 0) throw new InvalidDataException("Unterminated TableCfg string.");
        var result = new UTF8Encoding(false, true).GetString(_bytes, cursor, end - cursor); cursor = end + 1; return result;
    }
    private void Check(int offset, int size)
    {
        if (offset < 0 || offset > _bytes.Length - size) throw new InvalidDataException("TableCfg offset is outside its file.");
    }
    private static int Align(int offset, int alignment) => checked((offset + alignment - 1) & -alignment);
}
