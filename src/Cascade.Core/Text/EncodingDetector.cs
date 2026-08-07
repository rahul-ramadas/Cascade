using System.Text;

namespace Cascade.Core.Text;

/// <summary>Result of encoding detection: the encoding, its byte-order-mark length, and the
/// size (in bytes) and endianness of a single code unit (used for newline scanning).</summary>
public readonly record struct DetectedEncoding(Encoding Encoding, int PreambleLength, int UnitSize, bool BigEndian)
{
    public static DetectedEncoding Utf8NoBom => new(new UTF8Encoding(false), 0, 1, false);
}

/// <summary>
/// Works out how to read a file's bytes as text. A byte-order mark decides it outright; without one
/// there is nothing in the bytes to go on, so a caller-supplied fallback (UTF-8 by default) is used and
/// the reader is left to say otherwise via <see cref="ForEncoding"/>.
/// </summary>
public static class EncodingDetector
{
    public static DetectedEncoding Detect(ReadOnlySpan<byte> prefix, Encoding? fallback = null)
    {
        fallback ??= new UTF8Encoding(false);

        // UTF-32 must be tested before UTF-16 because the UTF-32 LE BOM starts with the UTF-16 LE BOM.
        if (prefix.Length >= 4 && prefix[0] == 0xFF && prefix[1] == 0xFE && prefix[2] == 0x00 && prefix[3] == 0x00)
            return new DetectedEncoding(new UTF32Encoding(bigEndian: false, byteOrderMark: true), 4, 4, false);
        if (prefix.Length >= 4 && prefix[0] == 0x00 && prefix[1] == 0x00 && prefix[2] == 0xFE && prefix[3] == 0xFF)
            return new DetectedEncoding(new UTF32Encoding(bigEndian: true, byteOrderMark: true), 4, 4, true);
        if (prefix.Length >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF)
            return new DetectedEncoding(new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 3, 1, false);
        if (prefix.Length >= 2 && prefix[0] == 0xFF && prefix[1] == 0xFE)
            return new DetectedEncoding(new UnicodeEncoding(bigEndian: false, byteOrderMark: true), 2, 2, false);
        if (prefix.Length >= 2 && prefix[0] == 0xFE && prefix[1] == 0xFF)
            return new DetectedEncoding(new UnicodeEncoding(bigEndian: true, byteOrderMark: true), 2, 2, true);

        // No BOM: use the fallback encoding. Byte-based newline scanning (unit size 1) is correct for
        // ASCII, UTF-8, and single-byte code pages such as Windows-1252.
        return new DetectedEncoding(fallback, 0, UnitSizeOf(fallback), IsBigEndian(fallback));
    }

    /// <summary>Builds a <see cref="DetectedEncoding"/> for a user-chosen encoding (View ▸ Encoding).</summary>
    public static DetectedEncoding ForEncoding(Encoding encoding, ReadOnlySpan<byte> prefix)
    {
        // The choice wins outright. Choosing an encoding is a reader saying the file is not what it looks
        // like, so deferring to a mark would leave the menu silently doing nothing for exactly the files
        // that carry one. A mark belonging to the SAME encoding is still skipped, so picking what was
        // detected anyway changes nothing; any other mark stays in the text, where it can be seen.
        var marked = Detect(prefix, fallback: encoding);
        int preamble = marked.Encoding.CodePage == encoding.CodePage ? marked.PreambleLength : 0;
        return new DetectedEncoding(encoding, preamble, UnitSizeOf(encoding), IsBigEndian(encoding));
    }

    /// <summary>Bytes per code unit, which is what the line scanner needs: UTF-16 and UTF-32 newlines are
    /// not single bytes, and a byte-wise scan would cut lines inside a character.</summary>
    private static int UnitSizeOf(Encoding encoding) => encoding.CodePage switch
    {
        1200 or 1201 => 2,
        12000 or 12001 => 4,
        _ => 1,
    };

    private static bool IsBigEndian(Encoding encoding)
        => encoding.CodePage is 1201 or 12001;

    /// <summary>What to call an encoding in the UI. <see cref="Encoding.EncodingName"/> says "Unicode" for
    /// UTF-16 LE and "Unicode (Big-Endian)" for UTF-16 BE, which is no help in a menu of Unicode encodings.</summary>
    public static string DisplayName(Encoding encoding) => encoding.CodePage switch
    {
        65001 => "UTF-8",
        1200 => "UTF-16 LE",
        1201 => "UTF-16 BE",
        12000 => "UTF-32 LE",
        12001 => "UTF-32 BE",
        20127 => "ASCII",
        _ => $"{encoding.EncodingName} ({encoding.CodePage})",
    };
}
