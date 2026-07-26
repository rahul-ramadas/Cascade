using System.Text;

namespace Cascade.Core.Text;

/// <summary>Result of encoding detection: the encoding, its byte-order-mark length, and the
/// size (in bytes) and endianness of a single code unit (used for newline scanning).</summary>
public readonly record struct DetectedEncoding(Encoding Encoding, int PreambleLength, int UnitSize, bool BigEndian)
{
    public static DetectedEncoding Utf8NoBom => new(new UTF8Encoding(false), 0, 1, false);
}

/// <summary>
/// Detects text encoding from a leading byte prefix using byte-order marks, following the same
/// precedence the original tool uses (BOM wins). Falls back to a caller-supplied encoding.
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
        int unit = fallback is UnicodeEncoding ? 2 : fallback is UTF32Encoding ? 4 : 1;
        bool be = (fallback as UnicodeEncoding)?.CodePage == 1201 || (fallback as UTF32Encoding)?.CodePage == 12001;
        return new DetectedEncoding(fallback, 0, unit, be);
    }

    /// <summary>Builds a <see cref="DetectedEncoding"/> for a user-chosen encoding (View ▸ Encoding),
    /// still honoring a BOM at the start of the file when present.</summary>
    public static DetectedEncoding ForEncoding(Encoding encoding, ReadOnlySpan<byte> prefix)
    {
        // A real BOM always wins over the chosen encoding, matching the original's precedence.
        var byBom = Detect(prefix, fallback: encoding);
        if (byBom.PreambleLength > 0) return byBom;
        int unit = encoding is UnicodeEncoding ? 2 : encoding is UTF32Encoding ? 4 : 1;
        bool be = (encoding as UnicodeEncoding)?.CodePage == 1201 || (encoding as UTF32Encoding)?.CodePage == 12001;
        return new DetectedEncoding(encoding, 0, unit, be);
    }
}
