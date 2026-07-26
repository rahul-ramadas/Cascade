using System.Text;
using Cascade.Core.IO;
using Cascade.Core.Indexing;
using Cascade.Core.Text;

namespace Cascade.Core.Tests;

/// <summary>Helpers to build an in-memory source + fully-built line index from a string, and to read
/// back all decoded lines. Used across the engine tests.</summary>
internal static class Harness
{
    public static (MemoryMappedTextSource Src, LineIndex Index, DetectedEncoding Enc) Build(string text, Encoding? encoding = null)
    {
        encoding ??= new UTF8Encoding(false);
        byte[] bytes = encoding.GetBytes(text);
        return BuildFromBytes(bytes, encoding);
    }

    public static (MemoryMappedTextSource Src, LineIndex Index, DetectedEncoding Enc) BuildFromBytes(byte[] bytes, Encoding? fallback = null)
    {
        var src = MemoryMappedTextSource.FromBytes(bytes);
        int prefixLen = Math.Min(64, bytes.Length);
        var det = EncodingDetector.Detect(bytes.AsSpan(0, prefixLen), fallback ?? new UTF8Encoding(false));
        var index = new LineIndex();
        new LineIndexer(src, index, det.PreambleLength, det.UnitSize, det.BigEndian).Run(null, CancellationToken.None);
        return (src, index, det);
    }

    public static List<string> ReadAll(MemoryMappedTextSource src, LineIndex index, DetectedEncoding det)
    {
        var reader = new LineReader(src, det.Encoding);
        var result = new List<string>();
        for (long i = 0; i < index.Count; i++)
        {
            long s = index.Get(i);
            long e = (i + 1 < index.Count) ? index.Get(i + 1) : src.Length;
            result.Add(reader.GetString(s, e));
        }
        return result;
    }

    public static List<string> Lines(string text, Encoding? encoding = null)
    {
        var (src, index, det) = Build(text, encoding);
        try { return ReadAll(src, index, det); }
        finally { src.Dispose(); }
    }

    /// <summary>Writes bytes to a unique temp file and returns its path (deleted by the caller/OS temp).</summary>
    public static string TempFile(byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), "cascade_test_" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
