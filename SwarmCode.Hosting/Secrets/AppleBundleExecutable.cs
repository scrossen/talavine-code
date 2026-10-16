using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SwarmCode.Hosting.Secrets;

/// <summary>Reads only the primary executable identity from bounded XML or binary Apple bundle metadata.</summary>
internal static class AppleBundleExecutable
{
    internal static string? Read(string path)
    {
        try
        {
            if (new FileInfo(path).Length is <= 0 or > 1024 * 1024) return null;
            var bytes = File.ReadAllBytes(path);
            if (bytes.AsSpan().StartsWith("bplist00"u8)) return ReadBinary(bytes);
            // XmlReader preserves indentation nodes unless explicitly told otherwise, even
            // when XDocument.Load uses its default options. Apple emits indented plists.
            using var reader = XmlReader.Create(new MemoryStream(bytes), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore, XmlResolver = null,
                IgnoreWhitespace = true, IgnoreComments = true,
            });
            var dict = XDocument.Load(reader).Root?.Element("dict");
            var keys = dict?.Elements("key").Where(k => k.Value == "CFBundleExecutable").ToArray();
            if (keys is not { Length: 1 } || keys[0].NextNode is not XElement { Name.LocalName: "string" } value) return null;
            return value.Value;
        }
        catch (Exception ex) when (ex is IOException or XmlException or InvalidDataException or OverflowException or ArgumentException or UnauthorizedAccessException)
        { return null; }
    }
    private static string? ReadBinary(byte[] bytes)
    {
        if (bytes.Length < 40) return null;
        int trailer = bytes.Length - 32, offsetSize = bytes[trailer + 6], referenceSize = bytes[trailer + 7];
        if (offsetSize is < 1 or > 8 || referenceSize is < 1 or > 8) return null;
        var count = Number(trailer + 8, 8); var top = Number(trailer + 16, 8); var table = Number(trailer + 24, 8);
        if (count is <= 0 or > 4096 || top >= count || table > (ulong)trailer || count * (ulong)offsetSize > (ulong)trailer - table) return null;
        int Offset(ulong id)
        {
            if (id >= count) throw new InvalidDataException();
            var offset = Number(checked((int)(table + id * (ulong)offsetSize)), offsetSize);
            if (offset < 8 || offset >= table) throw new InvalidDataException();
            return checked((int)offset);
        }
        ulong Number(int at, int size)
        {
            if (at < 0 || at > bytes.Length - size) throw new InvalidDataException();
            ulong result = 0; for (int i = 0; i < size; i++) result = (result << 8) | bytes[at + i]; return result;
        }
        int Length(ref int cursor, int marker)
        {
            var length = marker & 15;
            if (length != 15) return length;
            if ((ulong)cursor >= table) throw new InvalidDataException();
            var integer = bytes[cursor++];
            if ((integer >> 4) != 1 || (integer & 15) > 3) throw new InvalidDataException();
            var size = 1 << (integer & 15); var value = Number(cursor, size); cursor += size;
            return checked((int)value);
        }
        string? Text(ulong id)
        {
            int cursor = Offset(id), marker = bytes[cursor++], kind = marker >> 4;
            if (kind is not (5 or 6)) return null;
            var length = Length(ref cursor, marker); var byteCount = checked(length * (kind == 6 ? 2 : 1));
            if ((ulong)cursor + (ulong)byteCount > table) throw new InvalidDataException();
            return (kind == 6 ? Encoding.BigEndianUnicode : Encoding.ASCII).GetString(bytes, cursor, byteCount);
        }
        int cursor = Offset(top), marker = bytes[cursor++];
        if ((marker >> 4) != 13) return null;
        int entries = Length(ref cursor, marker);
        if (entries > 4096 || (ulong)cursor + (ulong)entries * (ulong)referenceSize * 2 > table) return null;
        string? found = null; bool matched = false;
        for (int i = 0; i < entries; i++)
        {
            if (Text(Number(cursor + i * referenceSize, referenceSize)) != "CFBundleExecutable") continue;
            if (matched) return null;
            matched = true;
            found = Text(Number(cursor + (entries + i) * referenceSize, referenceSize));
        }
        return found;
    }
}
