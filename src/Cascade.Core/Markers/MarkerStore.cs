using System.Collections.Concurrent;

namespace Cascade.Core.Markers;

/// <summary>
/// Stores up to 8 marker flags per line. Only marked lines consume memory. Lookups are lock-free
/// (used during filtering); mutations and ordered navigation are synchronized.
/// </summary>
public sealed class MarkerStore
{
    public const int MarkerCount = 8;

    private readonly ConcurrentDictionary<long, byte> _mask = new();
    private readonly SortedSet<long>[] _byMarker;
    private readonly object _lock = new();

    public event Action? Changed;

    public MarkerStore()
    {
        _byMarker = new SortedSet<long>[MarkerCount];
        for (int i = 0; i < MarkerCount; i++) _byMarker[i] = new SortedSet<long>();
    }

    public bool Has(long line, int index)
        => _mask.TryGetValue(line, out var m) && (m & (1 << index)) != 0;

    public byte MaskOf(long line) => _mask.TryGetValue(line, out var m) ? m : (byte)0;

    public bool AnyInUse => !_mask.IsEmpty;

    /// <summary>Steps on every change. Anything holding a <see cref="Snapshot"/> can tell whether it is still
    /// the current one without comparing the marks themselves.</summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>Steps whenever marker <paramref name="index"/> alone changes, so an answer worked out from
    /// its marks can name <b>which</b> marks it was worked out from. A filter's cached results are keyed by
    /// the predicates behind them, and a marker predicate means nothing without this: the filter is still
    /// "marked by 3" after the marks move, while its answer is not. Per marker rather than one number for
    /// all eight, so marking a line with marker 5 does not strand what is known about marker 3.</summary>
    public int VersionOf(int index) => Volatile.Read(ref _markerVersions[index]);

    private int _version;
    private readonly int[] _markerVersions = new int[MarkerCount];

    /// <summary>Steps the global version and that of each marker in <paramref name="touched"/> (a bitmask).
    /// Called under <see cref="_lock"/>, so a version and the marks it labels always move together.</summary>
    private void Bump(int touched)
    {
        Interlocked.Increment(ref _version);
        for (int i = 0; i < MarkerCount; i++)
            if ((touched & (1 << i)) != 0) Interlocked.Increment(ref _markerVersions[i]);
    }

    /// <summary>Every marked line with its mask, in line order. Marked lines are hand-picked, so this stays
    /// small however large the file - which is what lets a whole-file summary just walk it.
    /// <para>Kept until something changes: the minimap and the scrollbar both ask for this while PAINTING,
    /// and sorting the marks afresh every repaint is fine for a handful and quite another thing for the two
    /// million a select-all and Ctrl+1 can make.</para>
    /// <para>It is held rather than rebuilt, so it COSTS 16 bytes a mark - MEASURED at 32 MB for two
    /// million, against the 64 MB a frame it was throwing away, and against the ~200 MB the dictionary and
    /// the sorted sets already spend on the same marks. At any ordinary number of marks it is kilobytes.</para></summary>
    public IReadOnlyList<(long Line, byte Mask)> Snapshot()
    {
        lock (_lock)
        {
            if (_snapshot is { } cached && _snapshotVersion == _version) return cached;
            var all = _mask.ToArray();
            Array.Sort(all, static (a, b) => a.Key.CompareTo(b.Key));
            _snapshot = Array.ConvertAll(all, kv => (kv.Key, kv.Value));
            _snapshotVersion = _version;
            return _snapshot;
        }
    }

    private (long Line, byte Mask)[]? _snapshot;
    private int _snapshotVersion = -1;

    /// <summary>Bitmask of which of the 8 markers are currently used on at least one line.</summary>
    public int UsedMarkers
    {
        get
        {
            lock (_lock)
            {
                int used = 0;
                for (int i = 0; i < MarkerCount; i++)
                    if (_byMarker[i].Count > 0) used |= 1 << i;
                return used;
            }
        }
    }

    /// <summary>Toggles a marker on a single line; returns the new state.</summary>
    public bool Toggle(long line, int index)
    {
        bool set;
        lock (_lock)
        {
            byte m = _mask.TryGetValue(line, out var cur) ? cur : (byte)0;
            int bit = 1 << index;
            set = (m & bit) == 0;
            if (set) { m |= (byte)bit; _byMarker[index].Add(line); }
            else { m &= (byte)~bit; _byMarker[index].Remove(line); }
            if (m == 0) _mask.TryRemove(line, out _);
            else _mask[line] = m;
            // Stepped under the lock that made the change, so the version and the marks it labels are
            // always set together. Callers see the bump before Toggle returns either way; keeping it here
            // just means a reader overlapping a change can't pair new marks with the old number.
            Bump(bit);
        }
        Changed?.Invoke();
        return set;
    }

    public void Set(long line, int index, bool value)
    {
        lock (_lock)
        {
            byte m = _mask.TryGetValue(line, out var cur) ? cur : (byte)0;
            int bit = 1 << index;
            bool has = (m & bit) != 0;
            if (value == has) return;
            if (value) { m |= (byte)bit; _byMarker[index].Add(line); }
            else { m &= (byte)~bit; _byMarker[index].Remove(line); }
            if (m == 0) _mask.TryRemove(line, out _);
            else _mask[line] = m;
            Bump(bit);
        }
        Changed?.Invoke();
    }

    /// <summary>Smallest marked line &gt; <paramref name="afterLine"/>, or -1 if none.</summary>
    public long Next(long afterLine, int index)
    {
        lock (_lock)
        {
            foreach (var l in _byMarker[index].GetViewBetween(afterLine + 1, long.MaxValue))
                return l;
            return -1;
        }
    }

    /// <summary>Largest marked line &lt; <paramref name="beforeLine"/>, or -1 if none.</summary>
    public long Previous(long beforeLine, int index)
    {
        lock (_lock)
        {
            long result = -1;
            foreach (var l in _byMarker[index].GetViewBetween(long.MinValue, beforeLine - 1))
                result = l;
            return result;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _mask.Clear();
            foreach (var s in _byMarker) s.Clear();
            Bump(~0);
        }
        Changed?.Invoke();
    }
}
