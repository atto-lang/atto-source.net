using System.Text;

namespace Ores.Net.Compiler;

// Unicode scalar scanning retains existing UTF-16 source offsets and ASCII
// numeric syntax. Runtime names are ordinal; no implicit normalization.
internal static class PortableUnicodeIdentifier
{
    internal static int StartWidth(string text, int offset) =>
        offset >= 0 && offset < text.Length && Rune.TryGetRuneAt(text, offset, out var r) &&
        (Rune.IsLetter(r) || r.Value is '_' or 0x3007) ? r.Utf16SequenceLength : 0;

    internal static int PartWidth(string text, int offset) =>
        offset >= 0 && offset < text.Length && text[offset] is >= '0' and <= '9' ? 1 :
        StartWidth(text, offset);

    internal static int End(string text, int offset)
    {
        int width;
        while ((width = PartWidth(text, offset)) != 0) offset += width;
        return offset;
    }

    internal static bool IsName(string name) =>
        StartWidth(name, 0) != 0 && End(name, 0) == name.Length;
}
