using System.Buffers;
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

    /// <summary>How much decoding scratch a reader KEEPS of its own. A longer line is decoded into a pooled
    /// buffer instead, which is handed back as soon as the reader moves on. Without that split a reader ends
    /// up holding the longest line it ever saw - up to <see cref="MaxLineBytes"/>, so about 16 MB of chars -
    /// for the rest of its life, and readers are per thread: a search alone keeps one per core.</summary>
    internal const int KeptBufferChars = 256 * 1024;

    private readonly MemoryMappedTextSource _src;
    private readonly Encoding _encoding;
    private char[] _buffer = new char[256];
    private char[]? _large;

    /// <summary>Scratch this reader is holding on to, pooled or not.</summary>
    internal int HeldChars => _buffer.Length + (_large?.Length ?? 0);

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
        char[] buffer = Scratch(_encoding.GetMaxCharCount(byteLen));

        int n = _encoding.GetChars(bytes, buffer);
        var span = buffer.AsSpan(0, n);

        if (!truncated)
        {
            if (span.Length > 0 && span[^1] == '\n') span = span[..^1];
            if (span.Length > 0 && span[^1] == '\r') span = span[..^1];
        }
        return span;
    }

    public string GetString(long start, long endExclusive) => new string(GetChars(start, endExclusive));

    /// <summary>Room to decode one line into.
    /// <para>Ordinary lines use scratch the reader owns and keeps, so reading a screenful allocates nothing.
    /// A line too long for that is decoded into a POOLED buffer, handed back as soon as the reader moves on
    /// to ordinary lines again - so a file made of very long lines still costs no allocation per line, while
    /// one long line in an ordinary file does not leave every reader holding megabytes for good.</para>
    /// <para>The span from the previous call is invalidated by this one either way, which is what makes
    /// returning the pooled buffer here safe.</para></summary>
    private char[] Scratch(int maxChars)
    {
        if (maxChars > KeptBufferChars)
        {
            if (_large is null || _large.Length < maxChars)
            {
                if (_large is not null) ArrayPool<char>.Shared.Return(_large);
                _large = ArrayPool<char>.Shared.Rent(maxChars);
            }
            return _large;
        }

        if (_large is not null)
        {
            ArrayPool<char>.Shared.Return(_large);
            _large = null;
        }
        if (_buffer.Length < maxChars) _buffer = new char[Math.Max(maxChars, _buffer.Length * 2)];
        return _buffer;
    }

    public static bool IsTruncated(long start, long endExclusive) => (endExclusive - start) > MaxLineBytes;
}
