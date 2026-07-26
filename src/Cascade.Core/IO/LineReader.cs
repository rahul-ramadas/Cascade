using System.Text;

namespace Cascade.Core.IO;

/// <summary>
/// Decodes individual lines from a <see cref="MemoryMappedTextSource"/> on demand into a reusable
/// buffer. One instance per thread (the returned span is only valid until the next call). This is
/// how the viewer stays lazy: only lines actually needed (on screen / being filtered) are decoded.
/// </summary>
public sealed class LineReader
{
    /// <summary>Lines longer than this many bytes are truncated when decoded (display/filter safety).</summary>
    public const int MaxLineBytes = 8 * 1024 * 1024;

    private readonly MemoryMappedTextSource _src;
    private readonly Encoding _encoding;
    private char[] _buffer = new char[256];

    public LineReader(MemoryMappedTextSource src, Encoding encoding)
    {
        _src = src;
        _encoding = encoding;
    }

    /// <summary>Decodes the raw bytes in <c>[start, endExclusive)</c> minus a single trailing
    /// newline (and a preceding carriage return) into the shared buffer and returns it. The span is
    /// valid only until the next call on this reader.</summary>
    public ReadOnlySpan<char> GetChars(long start, long endExclusive)
    {
        long len = endExclusive - start;
        if (len <= 0) return ReadOnlySpan<char>.Empty;
        bool truncated = len > MaxLineBytes;
        int byteLen = (int)Math.Min(len, MaxLineBytes);

        ReadOnlySpan<byte> bytes = _src.Slice(start, byteLen);
        int maxChars = _encoding.GetMaxCharCount(byteLen);
        if (_buffer.Length < maxChars) _buffer = new char[Math.Max(maxChars, _buffer.Length * 2)];

        int n = _encoding.GetChars(bytes, _buffer);
        var span = _buffer.AsSpan(0, n);

        if (!truncated)
        {
            if (span.Length > 0 && span[^1] == '\n') span = span[..^1];
            if (span.Length > 0 && span[^1] == '\r') span = span[..^1];
        }
        return span;
    }

    public string GetString(long start, long endExclusive) => new string(GetChars(start, endExclusive));

    public bool IsTruncated(long start, long endExclusive) => (endExclusive - start) > MaxLineBytes;
}
