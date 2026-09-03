using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
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

    /// <summary>Asks the OS to bring a range of the file into memory, in its own large asynchronous reads.
    /// Purely a hint: it never changes what a later read returns, and if it fails the range is simply
    /// demand-paged as before. It matters because a scan that merely touches pages has exactly one fault
    /// outstanding at a time - MEASURED on a 19.3 GB log, that left the disk 51% idle and took 12.1 s,
    /// against 5.0 s once whole ranges were asked for up front. On a machine where a filter driver
    /// inspects every read, that serialisation is the entire cost.</summary>
    /// <summary>Bytes asked for through <see cref="Prefetch"/>. Read-ahead is only a hint, so losing it
    /// changes no result and no test would fail - it would just quietly cost a large file its speed.
    /// This is what lets a test say it is still happening.</summary>
    internal long PrefetchedBytes => Volatile.Read(ref _prefetched);
    private long _prefetched;

    public void Prefetch(long offset, long length)
    {
        if (_disposed || _ptr is null || _ownedBytes is not null) return;
        if (offset < 0 || offset >= Length || length <= 0) return;
        length = Math.Min(length, Length - offset);

        var range = new MemoryRangeEntry
        {
            VirtualAddress = (IntPtr)(_ptr + offset),
            NumberOfBytes = checked((IntPtr)length),
        };
        if (PrefetchVirtualMemory(GetCurrentProcess(), (UIntPtr)1, ref range, 0))
            Interlocked.Add(ref _prefetched, length);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryRangeEntry
    {
        public IntPtr VirtualAddress;
        public IntPtr NumberOfBytes;
    }

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool PrefetchVirtualMemory(IntPtr process, UIntPtr entryCount,
                                                     ref MemoryRangeEntry ranges, uint flags);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetCurrentProcess();

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
