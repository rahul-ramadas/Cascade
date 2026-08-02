using System.IO.MemoryMappedFiles;
using Microsoft.Win32.SafeHandles;

namespace Cascade.Core.IO;

/// <summary>
/// Zero-copy, read-only access to a file via a whole-file memory mapping. On 64-bit the entire
/// file is mapped and paged in on demand by the OS, so opening is instant regardless of size and
/// the file is never copied into managed memory. Safe for concurrent readers.
/// </summary>
public sealed unsafe class MemoryMappedTextSource : IDisposable
{
    private readonly MemoryMappedFile? _mmf;
    private readonly MemoryMappedViewAccessor? _view;
    private readonly SafeMemoryMappedViewHandle? _handle;
    private byte* _ptr;
    private bool _disposed;

    public long Length { get; }
    public string FilePath { get; }

    public MemoryMappedTextSource(string path)
    {
        FilePath = path;
        Length = new FileInfo(path).Length;
        if (Length == 0) return;

        // Share ReadWrite|Delete so we can open logs that are actively being written or replaced.
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.None);
        _mmf = MemoryMappedFile.CreateFromFile(fs, mapName: null, capacity: 0,
            MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
        _view = _mmf.CreateViewAccessor(0, Length, MemoryMappedFileAccess.Read);
        _handle = _view.SafeMemoryMappedViewHandle;
        byte* p = null;
        _handle.AcquirePointer(ref p);
        _ptr = p + _view.PointerOffset;
    }

    /// <summary>Creates an in-memory source from raw bytes (used for clipboard/paste input and tests).</summary>
    public static MemoryMappedTextSource FromBytes(byte[] bytes, string label = "<memory>")
        => new(bytes, label);

    private readonly byte[]? _ownedBytes;
    private System.Runtime.InteropServices.GCHandle _pin;

    private MemoryMappedTextSource(byte[] bytes, string label)
    {
        FilePath = label;
        Length = bytes.Length;
        _ownedBytes = bytes;
        if (Length == 0) return;
        _pin = System.Runtime.InteropServices.GCHandle.Alloc(bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
        _ptr = (byte*)_pin.AddrOfPinnedObject();
    }

    /// <summary>Returns a zero-copy read-only span of file bytes. <paramref name="length"/> must fit in Int32.</summary>
    public ReadOnlySpan<byte> Slice(long offset, int length)
    {
        if (length == 0) return ReadOnlySpan<byte>.Empty;
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (offset < 0 || length < 0 || offset + length > Length)
            throw new ArgumentOutOfRangeException(nameof(offset), "Requested slice is outside the file.");
        return new ReadOnlySpan<byte>(_ptr + offset, length);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownedBytes != null)
        {
            if (_pin.IsAllocated) _pin.Free();
            _ptr = null;
            return;
        }
        if (_handle != null && _ptr != null) _handle.ReleasePointer();
        _ptr = null;
        _view?.Dispose();
        _mmf?.Dispose();
    }
}
