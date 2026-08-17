using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Windows.Forms;
using Cascade.Core.Document;
using Cascade.Core.Model;

namespace Cascade.App;

/// <summary>
/// The log seen from far enough away that a row is a pixel: one pixel row per display row, in exactly the
/// colour that row is painted in the text area. Nothing is invented here - a filter you have given no colour
/// shows as nothing, which is the honest answer.
///
/// A pixel per row would hold only as many rows as the map is tall - nine hundred rows of thirty million -
/// so rows are <b>compressed at a fixed rate</b>: one pixel stands for however many rows it takes to fit the
/// file, up to <see cref="MaxRowsPerPixel"/>. The rate is the same everywhere on the map, so distance down
/// the map stays distance through the file and a click lands where it was aimed.
///
/// Where a pixel covers several rows the <b>exception among them wins</b>. A lone error in a screenful of
/// ordinary lines is exactly what the map exists to show, so it must not be outvoted by its neighbours;
/// anything coloured beats a row nothing matched. Colours of comparable weight share the pixels out between
/// them in proportion, or a filter that is merely common would paint the whole map and hide the rest.
///
/// KNOWN LIMIT: a colour spread evenly enough to land in nearly every pixel, while staying under about a
/// fifth of the matched rows in each, is the exception everywhere and takes the whole map. Telling that
/// apart from a real exception needs a pass that knows how much of the map each colour reaches, which is
/// not worth its cost until using this says otherwise.
///
/// Past that rate the file is bigger than the map can reach and it shows a <b>window</b> centred on the view.
/// The scrollbar beside it keeps the whole-file scale.
/// </summary>
internal sealed class MiniMapControl : Control
{
    /// <summary>Logical width. Wide enough to hit with a mouse and to read a colour off.</summary>
    public const int LogicalWidth = 18;

    /// <summary>The most rows one pixel may stand for. Past about a screenful a pixel stops being something
    /// that can be aimed at: a click cannot land on the screen it meant, and the viewport rectangle has
    /// nothing left to say about how much of the file is on show.</summary>
    private const int MaxRowsPerPixel = 32;

    private const int EdgeLane = 3;           // logical; marks down the left, find hits down the right
    private const int MinViewportHeight = 8;  // a compressed stretch would otherwise leave it a hairline
    private const int HoverDelayMs = 400;
    private const int TipDurationMs = 8000;

    private readonly LineGridControl _grid;
    private readonly ToolTip _tips = new() { ShowAlways = true };
    private readonly System.Windows.Forms.Timer _tipTimer = new() { Interval = HoverDelayMs };

    private long[] _rowAt = Array.Empty<long>();   // the row behind each pixel row
    private int[] _colour = Array.Empty<int>();    // 0 where nothing is painted
    private int[] _scanline = Array.Empty<int>();
    private readonly List<(long From, long To)> _selectedRows = new();   // the selection, in rows, per paint
    private Bitmap? _picture;
    private int _rowPixels = 1;
    private int _step = 1;                         // rows behind one pixel
    private int _slots;                            // pixel rows in use
    private long _top;                             // first row the window shows
    private long _span = 1;                        // rows the window covered when it was last built

    // Resolving a colour decodes and matches a line, and the summary is rebuilt on every scroll, so the
    // answers are kept for the rows the map is over and slid along as it moves.
    private const int Unknown = 0;
    private const int Blank = 1;                   // a real colour is always opaque, so this cannot collide
    private int[] _cache = Array.Empty<int>();
    private ulong[] _words = Array.Empty<ulong>();
    private long[] _wantLines = Array.Empty<long>();
    private Filter?[] _wantFilters = Array.Empty<Filter?>();
    private readonly Dictionary<Filter, int> _colourOf = new();
    private long _cacheBase = -1;
    private long _cacheBaseLine = -1;
    private int _cacheGeneration = -1;
    private bool _cacheFilteredMode;
    private long _cacheRows = -1;

    private int _builtGeneration = -1;
    private long _builtRows = -1;
    private long _builtTop = -1;
    private int _builtHeight = -1;
    private int _builtWidth = -1;
    private bool _builtFilteredMode;
    private int _builtMarkers = -1;
    private long _builtFindHits = -1;
    private long _drawnSelection = -1;
    private (int Top, int Height) _drawnViewport = (-1, -1);

    private bool _dragging;
    private bool _hovering;
    private long _trackedView = -1;   // the view position the window was last placed for
    private int _grabOffset;         // where inside the rectangle the drag took hold of it
    private int _tipSlot = -1;
    private Point _tipPoint;
    private int _paints;
    private int _resolved;

    public MiniMapControl(LineGridControl grid)
    {
        _grid = grid;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Right;
        Width = LogicalToDeviceUnits(LogicalWidth);
        TabStop = false;
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "Minimap";
        _tipTimer.Tick += (_, _) => ShowTipNow();
    }

    protected override void Dispose(bool disposing)
    {
        // None of these is a child control, so nothing else would clean them up.
        if (disposing) { _tipTimer.Dispose(); _tips.Dispose(); _picture?.Dispose(); }
        base.Dispose(disposing);
    }

    /// <summary>Throws away the summary so the next paint rebuilds it.</summary>
    public void InvalidateSummary() { _builtGeneration = -1; _trackedView = -1; _cacheRows = -1; _blockRows = -1; Invalidate(); }

    /// <summary>The filters look different but match the same rows: work the colours out again without
    /// forgetting where the view was tracked, which would re-centre a window the user had dragged. Also how
    /// the two things the summary key does not cover - the settings, and which file is open - are
    /// reported.</summary>
    internal void InvalidateColors() { _builtGeneration = -1; _cacheRows = -1; _blockRows = -1; Invalidate(); }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Width = LogicalToDeviceUnits(LogicalWidth);
    }

    // ---- test seams: the summary, rather than a picture of it ----

    internal void RebuildForTesting() { _builtGeneration = -1; EnsureSummary(); }
    internal int SlotCountForTesting => _slots;
    internal int RowPixelsForTesting => _rowPixels;
    internal int RowsPerPixelForTesting => _step;
    internal long TopRowForTesting => _top;
    /// <summary>The view position the window was last placed for. Forgetting it re-centres the map, which
    /// would undo a drag - so a refresh that only means "the colours changed" must leave it alone.</summary>
    internal long TrackedViewForTesting => _trackedView;
    internal long SpanForTesting => _span;
    internal long RowAtForTesting(int slot) => slot >= 0 && slot < _slots ? _rowAt[slot] : -1;
    internal int ColourAtForTesting(int slot) => slot >= 0 && slot < _slots ? _colour[slot] : 0;
    internal int SlotOfForTesting(long row) => SlotOf(row);
    internal (long From, long To) RowsAtForTesting(int slot) => slot >= 0 && slot < _slots ? RowsAt(slot) : (-1, -1);
    internal long[] RowsForTesting() => _rowAt.Take(_slots).ToArray();
    internal string TipTextForTesting(int slot) => SlotTipText(slot);
    internal void ClickForTesting(int y) { Grab(y); DropForTesting(); LeaveForTesting(); }
    internal void GrabForTesting(int y) { _dragging = true; _hovering = true; Grab(y); }
    internal void DragToForTesting(int y) => ScrollToTop(y - _grabOffset);
    internal void DropForTesting() { _dragging = false; }
    internal void LeaveForTesting() { _hovering = false; }
    internal (int Top, int Height) ViewportForTesting => Geometry();
    internal Rectangle ContentForTesting => new(Divider, 0, Math.Max(1, ClientSize.Width - Divider), ClientSize.Height);

    /// <summary>How many times the map has actually painted. A picture of it cannot answer that: capturing a
    /// control draws it whether or not it was invalidated, so a screenshot always looks up to date even when
    /// the real window has been sitting stale for minutes.</summary>
    internal int PaintsForTesting => _paints;

    /// <summary>Rows whose colour a build had to read the file for. Everything else came from the last
    /// build, which is what keeps a drag affordable.</summary>
    internal int ColoursResolvedForTesting => _resolved;

    // ---- geometry ----

    private int MinRowPixels => Math.Max(1, LogicalToDeviceUnits(1));
    private int Divider => Math.Max(1, LogicalToDeviceUnits(1));
    private int EdgeWidth => Math.Max(2, LogicalToDeviceUnits(EdgeLane));

    /// <summary>Slots the map has room for at the size it is now.</summary>
    private int SlotCapacity => Math.Max(1, Math.Max(1, ClientSize.Height) / Math.Max(1, _rowPixels));

    /// <summary>Puts the view back in the middle of the window, but only when the view has moved for a
    /// reason of its own - a key, the wheel, the scrollbar. Returns whether it moved the window.
    ///
    /// Re-centring on every look would undo the map's own dragging: the drop position would spring back to
    /// the middle the moment the pointer left. So a scroll the map itself caused is recorded as accounted
    /// for and left alone, and the window only re-centres when something else moves the view.</summary>
    private bool TrackView(long rows, int slots)
    {
        long viewTop = _grid.FirstVisibleRow;
        if (_dragging || _hovering) { _trackedView = viewTop; return false; }
        if (viewTop == _trackedView) return false;
        _trackedView = viewTop;
        Recentre(rows, slots);
        return true;
    }

    /// <summary>Starts the window half a map above the view. The rate is the same the whole way down, so
    /// this is arithmetic rather than a walk.</summary>
    private void Recentre(long rows, int slots)
    {
        if (rows <= 0) return;
        long viewTop = Math.Clamp(_grid.FirstVisibleRow, 0, rows - 1);
        _top = Math.Max(0, viewTop - (long)(slots / 2) * _step);
    }

    private void EnsureSummary()
    {
        var doc = _grid.Document;
        if (doc is null) { _slots = 0; return; }

        long rows = doc.RowCount;
        int height = Math.Max(1, ClientSize.Height);
        int width = Math.Max(1, ClientSize.Width);
        long findHits = doc.FindHitCount;

        _rowPixels = MinRowPixels;
        int slots = Math.Max(1, height / _rowPixels);
        // Few enough rows to fit outright: give each one a taller band rather than leaving the map half empty.
        if (rows > 0 && rows <= slots)
        {
            _rowPixels = Math.Max(_rowPixels, height / (int)Math.Max(1, rows));
            slots = Math.Max(1, height / _rowPixels);
        }
        // The least compression that fits the file, and no more - so a file the map can hold is shown whole
        // and never scrolls, and only a file past its reach becomes a window.
        _step = RowsPerPixelFor(rows, slots);
        TrackView(rows, slots);

        long reach = (long)slots * _step;
        // Pinned to a fixed grid of the file, so scrolling slides the picture by whole pixels instead of
        // re-dividing every row between them - which is also what lets a pixel's colour be settled from the
        // rows behind it alone, and stay settled.
        _top = rows <= reach ? 0 : Math.Clamp(_top, 0, rows - reach) / _step * _step;

        // The selection is drawn over the picture rather than into it, so moving the caret needs a repaint
        // but not a rebuild - which matters, because holding an arrow key asks for one per keypress.
        if (_builtGeneration == doc.FilterGeneration && _builtRows == rows && _builtTop == _top &&
            _builtHeight == height && _builtWidth == width && _builtFilteredMode == doc.FilteredMode &&
            _builtMarkers == doc.Markers.Version && _builtFindHits == findHits)
            return;

        _builtGeneration = doc.FilterGeneration;
        _builtRows = rows;
        _builtHeight = height;
        _builtWidth = width;
        _builtFilteredMode = doc.FilteredMode;
        _builtMarkers = doc.Markers.Version;
        _builtFindHits = findHits;

        if (_rowAt.Length < slots) { _rowAt = new long[slots]; _colour = new int[slots]; }

        PrepareCache(doc, slots, rows);
        _slots = rows <= 0 ? 0 : Fill(doc, rows, slots);
        _span = Math.Max(1, Math.Min(rows, _top + (long)_slots * _step) - _top);
        _builtTop = _top;
        RedrawPicture(width, height);
    }

    /// <summary>Keeps the resolved colours for the rows the map is over, sliding them along as it moves so a
    /// scroll of a few rows costs a few lookups instead of a whole mapful.</summary>
    private void PrepareCache(CascadeDocument doc, int slots, long rows)
    {
        int want = slots * _step;
        if (_cache.Length < want) _cache = new int[want];
        // A row only means the same line while the filters and the visible set hold still. Checking the line
        // behind the base row catches a pass that shuffled rows without changing how many there are.
        long baseLine = _cacheBase >= 0 && _cacheBase < rows ? doc.RowToLine(_cacheBase) : -1;
        if (_cacheGeneration != doc.FilterGeneration || _cacheFilteredMode != doc.FilteredMode ||
            _cacheRows != rows || _cacheBaseLine != baseLine)
        {
            Array.Clear(_cache);
            _cacheGeneration = doc.FilterGeneration;
            _cacheFilteredMode = doc.FilteredMode;
            _cacheRows = rows;
            _cacheBase = _top;
            _cacheBaseLine = _top < rows ? doc.RowToLine(_top) : -1;
            return;
        }
        long delta = _top - _cacheBase;
        if (delta == 0) return;
        int len = _cache.Length;
        if (delta > 0 && delta < len)
        {
            Array.Copy(_cache, (int)delta, _cache, 0, len - (int)delta);
            Array.Clear(_cache, len - (int)delta, (int)delta);
        }
        else if (delta < 0 && -delta < len)
        {
            Array.Copy(_cache, 0, _cache, (int)-delta, len + (int)delta);
            Array.Clear(_cache, 0, (int)-delta);
        }
        else Array.Clear(_cache);
        _cacheBase = _top;
        _cacheBaseLine = _top < rows ? doc.RowToLine(_top) : -1;
    }

    /// <summary>Fills each pixel from the rows behind it. Returns how many were filled.</summary>
    private int Fill(CascadeDocument doc, long rows, int slots)
    {
        var settings = _grid.Settings;
        var defaults = Defaults(settings);
        Span<int> colours = stackalloc int[MaxRowsPerPixel];
        Span<int> counts = stackalloc int[MaxRowsPerPixel];
        // With nothing enabled no row can have a colour, so the whole file is blank without reading a line.
        bool anyColour = doc.CurrentSnapshot.HasAnyEnabled;
        long lastRow = Math.Min(rows, _top + (long)slots * _step);
        long firstWord = _top >> 6;
        var matched = anyColour ? ReadMatchedRows(doc, firstWord, lastRow) : ReadOnlySpan<ulong>.Empty;

        PrepareBlocks(doc, rows);
        if (anyColour) ResolveColours(doc, rows, lastRow, matched, firstWord, defaults, settings);

        int at = 0;
        for (; at < slots; at++)
        {
            long from = _top + (long)at * _step;
            if (from >= rows) break;
            long to = Math.Min(rows, from + _step);
            _rowAt[at] = from;
            _colour[at] = 0;
            if (!anyColour) continue;

            // A pixel this map has already worked out, in a stretch of the file it has been over before.
            // Reading a log is dragging up and down it, so this is most of them.
            int settled = KnownBlock(from);
            if (settled != Unknown) { _colour[at] = settled == Blank ? 0 : settled; continue; }

            int kinds = 0;
            for (long row = from; row < to; row++)
            {
                if (!matched.IsEmpty && (matched[(int)((row >> 6) - firstWord)] >> (int)(row & 63) & 1) == 0) continue;
                int argb = ColourOfRow(row);
                if (argb == 0) continue;
                int k = 0;
                while (k < kinds && colours[k] != argb) k++;
                if (k == kinds) { colours[kinds] = argb; counts[kinds] = 1; kinds++; }
                else counts[k]++;
            }

            // One pixel, one colour. The exception among the rows behind it is what the map exists to show,
            // so a colour far rarer than the rest takes the pixel outright; colours of comparable weight
            // share the pixels out between them in proportion instead, because always yielding to the same
            // one would paint a whole map in it and hide that the other was ever there.
            if (kinds == 1) _colour[at] = colours[0];
            else if (kinds > 1) _colour[at] = PickColour(colours, counts, kinds, from);
            RememberBlock(from, _colour[at]);
        }
        return at;
    }

    // ---- the colours this map has already worked out, for the whole file ----
    //
    // A pixel stands for a fixed block of rows: the window is pinned to a grid of the file, so the colour a
    // block comes out is the same wherever the window happens to sit. Keeping those makes going back over a
    // stretch free, and going back over a stretch is what reading a log is - the window slides by thousands
    // of rows on every mouse report of a drag, and each of those rows used to be read out of the file and
    // run past the filters again on the way back up.
    private int[] _blocks = [];
    private int _blockStep;
    private int _blockGeneration = -1;
    private bool _blockFilteredMode;
    private long _blockRows = -1;
    private long _blockBaseLine = -1;

    /// <summary>How many blocks are worth keeping. Four million of them is sixteen megabytes and covers a
    /// file of a hundred and twenty million lines; past that the map keeps only the window it is over, as it
    /// always did - a file that big is one the reader can never drag their way across anyway.</summary>
    private const int MaxBlocks = 4 << 20;

    private void PrepareBlocks(CascadeDocument doc, long rows)
    {
        long wanted = rows <= 0 ? 0 : (rows + _step - 1) / _step;
        long baseLine = rows > 0 ? doc.RowToLine(0) : -1;
        if (wanted is 0 or > MaxBlocks)
        {
            if (_blocks.Length > 0) _blocks = [];
            _blockRows = -1;
            return;
        }
        // Anything that can change what colour a row comes out, or which row a line is on, starts them again.
        if (_blockGeneration == doc.FilterGeneration && _blockFilteredMode == doc.FilteredMode &&
            _blockStep == _step && _blockRows == rows && _blockBaseLine == baseLine)
            return;

        if (_blocks.Length < wanted) _blocks = new int[wanted];
        else Array.Clear(_blocks, 0, (int)wanted);
        _blockGeneration = doc.FilterGeneration;
        _blockFilteredMode = doc.FilteredMode;
        _blockStep = _step;
        _blockRows = rows;
        _blockBaseLine = baseLine;
    }

    /// <summary>The colour settled for the block a row falls in, or <see cref="Unknown"/>.</summary>
    private int KnownBlock(long from)
    {
        long block = from / _step;
        return _blockRows < 0 || block >= _blocks.Length ? Unknown : _blocks[(int)block];
    }

    private void RememberBlock(long from, int colour)
    {
        long block = from / _step;
        if (_blockRows < 0 || block >= _blocks.Length) return;
        _blocks[(int)block] = colour == 0 ? Blank : colour;
    }

    /// <summary>
    /// Works out the colour of every row the map is over that it does not already know, in one go.
    /// <para>Asking line by line is what made dragging the scrollbar lag: a mapful is tens of thousands of
    /// rows, and running the filters over a line is about a microsecond, so a rebuild spent a fifth of a
    /// second on the thread that has to repaint. Gathering the unknown rows first lets the document share
    /// them out across the cores, and lets the ones carried over from the last build cost nothing at
    /// all.</para>
    /// </summary>
    private void ResolveColours(CascadeDocument doc, long rows, long lastRow, ReadOnlySpan<ulong> matched,
        long firstWord, ResolvedStyle defaults, AppSettings settings)
    {
        int want = (int)Math.Min(int.MaxValue, Math.Max(0, lastRow - _top));
        if (want <= 0) return;
        // Nothing to read when every pixel the window covers has been worked out before. Worth its own pass:
        // turning rows into lines is itself a walk of the visible set, and this is the common case on the way
        // back up a file.
        bool anythingUnknown = false;
        for (long from = _top; from < lastRow && !anythingUnknown; from += _step)
            anythingUnknown = KnownBlock(from) == Unknown;
        if (!anythingUnknown) return;

        if (_wantLines.Length < want) { _wantLines = new long[want]; _wantFilters = new Filter?[want]; }

        // Rows to lines against ONE snapshot of the visible set, rather than a lookup apiece - the same
        // reason the text view resolves a whole frame at a time.
        int found = doc.LinesForRows(_top, _wantLines.AsSpan(0, want));
        int asked = 0;
        for (int i = 0; i < found; i++)
        {
            long row = _top + i;
            bool skip = _cache[i] != Unknown || KnownBlock(row) != Unknown ||
                        (!matched.IsEmpty && (matched[(int)((row >> 6) - firstWord)] >> (int)(row & 63) & 1) == 0);
            if (skip) _wantLines[i] = -1; else asked++;
        }
        for (int i = found; i < want; i++) _wantLines[i] = -1;
        if (asked == 0) return;

        _resolved += asked;
        doc.ColouringFilters(_wantLines, found, _wantFilters);

        // Turning a filter into a colour is a walk up its parents, and a few filters colour thousands of
        // rows, so each one is worked out once per build.
        _colourOf.Clear();
        for (int i = 0; i < found; i++)
        {
            if (_wantLines[i] < 0) continue;
            var filter = _wantFilters[i];
            int argb = 0;
            if (filter is not null && !_colourOf.TryGetValue(filter, out argb))
                _colourOf[filter] = argb = ColourOf(filter, defaults, settings);
            _cache[i] = argb == 0 ? Blank : argb;
        }
    }

    private int ColourOfRow(long row)
    {
        int at = (int)(row - _cacheBase);
        if (at < 0 || at >= _cache.Length) return 0;
        int cached = _cache[at];
        return cached is Unknown or Blank ? 0 : cached;
    }

    /// <summary>How much rarer than everything else a colour must be to take a pixel on its own.</summary>
    private const int RareShare = 4;

    private static int PickColour(ReadOnlySpan<int> colours, ReadOnlySpan<int> counts, int kinds, long from)
    {
        int least = 0, second = -1, total = counts[0];
        for (int k = 1; k < kinds; k++)
        {
            total += counts[k];
            if (counts[k] < counts[least]) { second = least; least = k; }
            else if (second < 0 || counts[k] < counts[second]) second = k;
        }
        if ((long)counts[least] * RareShare <= counts[second]) return colours[least];

        // Keyed on the row rather than on the pixel's place in the map, so scrolling slides the pattern
        // along with the file instead of reshuffling it under the eye.
        int at = (int)(Scatter(from) % (uint)total);
        for (int k = 0; k < kinds; k++)
        {
            if (at < counts[k]) return colours[k];
            at -= counts[k];
        }
        return colours[least];
    }

    private static uint Scatter(long value)
    {
        ulong x = (ulong)value * 0x9E3779B97F4A7C15UL;
        x ^= x >> 29;
        x *= 0xBF58476D1CE4E5B9UL;
        return (uint)(x ^ (x >> 32));
    }

    /// <summary>Which of the rows the map is over the filters match, one bit each - read in one go, because
    /// asking a line at a time is a rank and a select apiece. Empty means every row matches, which is the
    /// answer in filtered mode and whenever nothing is being hidden.</summary>
    private ReadOnlySpan<ulong> ReadMatchedRows(CascadeDocument doc, long firstWord, long lastRow)
    {
        if (doc.FilteredMode || doc.MatchedWords is not { } read) return ReadOnlySpan<ulong>.Empty;
        int words = (int)(((lastRow + 63) >> 6) - firstWord);
        if (words <= 0) return ReadOnlySpan<ulong>.Empty;
        if (_words.Length < words) _words = new ulong[words];
        read(firstWord, _words.AsSpan(0, words));
        return _words.AsSpan(0, words);
    }

    private static ResolvedStyle Defaults(AppSettings settings) => new(
        new RgbColor(settings.Foreground.R, settings.Foreground.G, settings.Foreground.B),
        new RgbColor(settings.Background.R, settings.Background.G, settings.Background.B), false, false);

    /// <summary>The colour the text area paints a line's background in, or its text colour when the
    /// filter sets only that, or nothing at all. Resolved through the same evaluation the grid uses, so the
    /// map cannot come to a different answer than the row it stands for.</summary>
    private static int ColourOf(Filter filter, ResolvedStyle defaults, AppSettings settings)
    {
        var style = StyleResolver.Resolve(filter, defaults);
        var bg = Color.FromArgb(style.Background.R, style.Background.G, style.Background.B);
        if (bg.ToArgb() != settings.Background.ToArgb()) return bg.ToArgb();
        var fg = Color.FromArgb(style.Foreground.R, style.Foreground.G, style.Foreground.B);
        return fg.ToArgb() != settings.Foreground.ToArgb() ? fg.ToArgb() : 0;
    }

    /// <summary>Rows to a pixel: the least compression that fits <paramref name="rows"/> into
    /// <paramref name="slots"/> pixels, and never more than <see cref="MaxRowsPerPixel"/>.</summary>
    internal static int RowsPerPixelFor(long rows, int slots)
        => rows <= slots ? 1 : (int)Math.Min(MaxRowsPerPixel, (rows + slots - 1) / Math.Max(1, slots));

    /// <summary>The slot standing for a row. One rate the whole way down, so this is division.</summary>
    private int SlotOf(long row)
        => _slots <= 0 ? -1 : (int)Math.Clamp((row - _top) / _step, 0, _slots - 1);

    /// <summary>The rows a slot stands for.</summary>
    private (long From, long To) RowsAt(int slot)
    {
        long from = _rowAt[slot];
        long limit = _builtRows > 0 ? _builtRows : from + _step;
        return (from, Math.Max(from + 1, Math.Min(limit, from + _step)));
    }

    // ---- painting ----

    private void RedrawPicture(int width, int height)
    {
        if (_slots <= 0) { _picture?.Dispose(); _picture = null; return; }

        var settings = _grid.Settings;
        int backArgb = settings.GutterBack.ToArgb();
        int dividerArgb = Blend(settings.Foreground, settings.GutterBack, 0.30).ToArgb();
        int left = Divider;

        // Painted over rather than replaced: a drag rebuilds this many times a second, and a fresh bitmap
        // each time is a hundred kilobytes of garbage per frame.
        if (_picture is null || _picture.Width != width || _picture.Height != height)
        {
            _picture?.Dispose();
            _picture = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        }
        var picture = _picture;
        var data = picture.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            if (_scanline.Length < width) _scanline = new int[width];
            var row = _scanline;
            for (int y = 0; y < height; y++)
            {
                int slot = y / _rowPixels;
                int argb = slot < _slots && _colour[slot] != 0 ? _colour[slot] : backArgb;
                Array.Fill(row, argb, 0, width);
                // A rule down the left, or a coloured row runs straight into the text beside it and there is
                // no telling where one ends and the other starts.
                for (int x = 0; x < left && x < width; x++) row[x] = dividerArgb;
                System.Runtime.InteropServices.Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, width);
            }
        }
        finally { picture.UnlockBits(data); }
    }

    internal static Color Blend(Color c, Color back, double t) => Color.FromArgb(
        (int)Math.Round(back.R + (c.R - back.R) * t),
        (int)Math.Round(back.G + (c.G - back.G) * t),
        (int)Math.Round(back.B + (c.B - back.B) * t));

    /// <summary>The brush for a marker, made once. Marks are drawn a rectangle at a time down both the map
    /// and the scrollbar, so a brush per mark is a GDI+ object made and destroyed millions of times a
    /// repaint for eight fixed colours. These outlive every view on purpose - there are eight of them and
    /// the colours never change.</summary>
    internal static SolidBrush MarkerBrush(int index) =>
        MarkerBrushes[Math.Clamp(index, 0, MarkerBrushes.Length - 1)];

    private static readonly SolidBrush[] MarkerBrushes =
        Array.ConvertAll(AppSettings.MarkerColors, c => new SolidBrush(c));

    protected override void OnPaint(PaintEventArgs e)
    {
        _paints++;
        var g = e.Graphics;
        g.Clear(_grid.Settings.GutterBack);
        EnsureSummary();
        if (_grid.Document is not { } doc || _slots <= 0) return;

        if (_picture is { } picture) g.DrawImageUnscaled(picture, 0, 0);
        DrawMarks(g, doc);
        DrawViewport(g);
        DrawFrame(g);
    }

    /// <summary>Closes the map off at the top and bottom, so it reads as its own strip beside the scrollbar
    /// rather than running into whatever is above and below it. Drawn last: the viewport rectangle reaches
    /// the ends of the map and would otherwise paint over it.</summary>
    private void DrawFrame(Graphics g)
    {
        int rule = Divider;
        using var brush = new SolidBrush(Blend(_grid.Settings.Foreground, _grid.Settings.GutterBack, 0.30));
        g.FillRectangle(brush, 0, 0, ClientSize.Width, rule);
        g.FillRectangle(brush, 0, ClientSize.Height - rule, ClientSize.Width, rule);
    }

    private void DrawMarks(Graphics g, CascadeDocument doc)
    {
        int edge = EdgeWidth, left = Divider;
        long first = _rowAt[0], last = _rowAt[_slots - 1];
        _drawnSelection = _grid.SelectionVersion;

        // Selected rows and find hits share the map with the colours, so they take an edge each rather than
        // covering the row they belong to. The selection goes down first: a mark is deliberate and stays,
        // a selection is wherever you last clicked, and the two want the same few pixels.
        // It is held in file lines, so it is turned into rows once here rather than once per slot.
        _grid.FillSelectedRowRanges(_selectedRows);
        if (_selectedRows.Count > 0)
        {
            using var brush = new SolidBrush(_grid.Settings.SelectionBack);
            for (int s = 0; s < _slots; s++)
            {
                var (from, to) = RowsAt(s);
                foreach (var (a, b) in _selectedRows)
                    if (a < to && b > from)
                    {
                        g.FillRectangle(brush, left, s * _rowPixels, edge, Math.Max(2, _rowPixels));
                        break;
                    }
            }
        }

        foreach (var (line, mask) in doc.Markers.Snapshot())
        {
            long row = doc.FilteredMode ? doc.RowForLine(line) : line;
            if (row < first || row > last) continue;
            int index = System.Numerics.BitOperations.TrailingZeroCount(mask);
            g.FillRectangle(MarkerBrush(index), left, SlotOf(row) * _rowPixels, edge, Math.Max(2, _rowPixels));
        }

        if (doc.FindHitCount > 0)
        {
            using var brush = new SolidBrush(_grid.Settings.FindCurrent);
            int x = Math.Max(left, ClientSize.Width - edge);
            for (int s = 0; s < _slots; s++)
            {
                var (from, to) = RowsAt(s);
                long lineFrom = doc.RowToLine(from);
                long lineTo = doc.RowToLine(Math.Max(from, to - 1)) + 1;
                if (doc.FindHitsInRange(lineFrom, Math.Max(lineFrom + 1, lineTo)) > 0)
                    g.FillRectangle(brush, x, s * _rowPixels, edge, Math.Max(2, _rowPixels));
            }
        }
    }

    private (int Top, int Height) Geometry()
    {
        if (_slots <= 0) return (0, MinViewportHeight);
        long viewTop = _grid.FirstVisibleRow;
        long viewRows = Math.Max(1, _grid.VisibleRows);
        int top = SlotOf(viewTop) * _rowPixels;
        int bottom = (SlotOf(viewTop + viewRows - 1) + 1) * _rowPixels;
        int height = Math.Max(MinViewportHeight, bottom - top);
        int limit = Math.Max(1, ClientSize.Height);
        if (top + height > limit) top = Math.Max(0, limit - height);
        return (top, height);
    }

    private void DrawViewport(Graphics g)
    {
        var (top, height) = Geometry();
        _drawnViewport = (top, height);

        var settings = _grid.Settings;
        int left = Divider;
        int width = Math.Max(1, ClientSize.Width - left);
        using (var fill = new SolidBrush(Color.FromArgb(40, settings.SelectionBack)))
            g.FillRectangle(fill, left, top, width, height);
        using var pen = new Pen(Color.FromArgb(210, settings.SelectionBack));
        g.DrawRectangle(pen, left, top, width - 1, height - 1);
    }

    /// <summary>Repaints when anything the map draws has actually changed. The map is a child control, so the
    /// grid invalidating itself does not touch it - without this the picture would sit exactly as it was last
    /// painted while the text scrolled under it.
    /// <para><paramref name="repaint"/> is false on the moves of a drag that the screen is too slow to show.
    /// The window it is over still has to be tracked then - forgetting that is what makes a dragged map
    /// spring back to the middle - but drawing it would be drawing a frame nobody can see.</para></summary>
    internal void SyncToGrid(bool repaint = true)
    {
        if (!Visible || _grid.Document is not { } doc) return;
        if (_builtRows != doc.RowCount || _builtFilteredMode != doc.FilteredMode ||
            _builtGeneration != doc.FilterGeneration || _builtMarkers != doc.Markers.Version ||
            _builtFindHits != doc.FindHitCount || _drawnSelection != _grid.SelectionVersion)
        {
            if (repaint) { _owed = false; Invalidate(); } else _owed = true;
            return;
        }
        if (_slots <= 0) return;
        long rows = doc.RowCount;
        long before = _top;
        if (rows > 0) TrackView(rows, SlotCapacity);
        // Whether it is out of date, counting the times it was told so on a frame that was not drawn: the
        // window it is over is moved by the telling, so by the next one it looks as though nothing happened.
        bool stale = _owed || _top != before || Geometry() != _drawnViewport;
        if (!repaint) { _owed = stale; return; }
        if (stale) { _owed = false; Invalidate(); }
    }

    private bool _owed;

    // ---- interaction ----

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        Capture = true;
        HideTip();
        Grab(e.Y);
    }

    /// <summary>Takes hold at a pixel row. Inside the window that is a grab, and it stays exactly where it
    /// is under the pointer; outside it, the view jumps there first with the window centred on the
    /// pointer.</summary>
    private void Grab(int y)
    {
        var (top, height) = Geometry();
        if (y >= top && y < top + height) { _grabOffset = y - top; return; }
        _grabOffset = height / 2;
        ScrollToTop(y - _grabOffset);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _hovering = true;
        if (_dragging) { ScrollToTop(e.Y - _grabOffset); return; }
        TrackHover(e.Location);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Capture = false;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovering = true;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovering = false;
        HideTip();
        base.OnMouseLeave(e);
        Invalidate();   // frozen while the pointer was here; it has a window to catch up on
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _grid.ScrollByWheel(e.Delta);
    }

    /// <summary>Puts the top of the view at the pixel row <paramref name="y"/>, without letting it leave the
    /// stretch of file the map is showing - dragging past either end would otherwise carry the view on while
    /// the rectangle sat stuck against the edge, doing nothing you could see.</summary>
    private void ScrollToTop(int y)
    {
        if (_slots <= 0) return;
        int slot = Math.Clamp(y / _rowPixels, 0, _slots - 1);
        long lowest = _rowAt[0];
        long highest = Math.Max(lowest, _rowAt[_slots - 1] - _grid.VisibleRows + 1);
        _grid.ScrollToRow(Math.Clamp(_rowAt[slot], lowest, highest));
        // A held drag never lets the queue empty, and WM_PAINT only arrives when it does - so without this
        // the window under the pointer would not move until the mouse stopped. The view answers for its own
        // repaint, which it paces to the screen.
        Update();
    }

    // ---- what is under the pointer ----

    private void TrackHover(Point at)
    {
        int slot = at.Y / Math.Max(1, _rowPixels);
        if (slot < 0 || slot >= _slots) { HideTip(); return; }
        if (slot == _tipSlot) return;
        HideTip();
        _tipSlot = slot;
        _tipPoint = at;
        _tipTimer.Stop();
        _tipTimer.Start();
    }

    private void HideTip()
    {
        _tipTimer.Stop();
        if (_tipSlot >= 0) _tips.Hide(this);
        _tipSlot = -1;
    }

    private void ShowTipNow()
    {
        _tipTimer.Stop();
        string text = SlotTipText(_tipSlot);
        if (text.Length == 0) return;
        _tips.Show(text, this, _tipPoint.X - 8, _tipPoint.Y + 20, TipDurationMs);
    }

    /// <summary>The line a pixel stands for, and the filters that colour it.</summary>
    private string SlotTipText(int slot)
    {
        if (slot < 0 || slot >= _slots || _grid.Document is not { } doc) return "";
        var (from, to) = RowsAt(slot);
        long line = doc.RowToLine(from);
        var sb = new StringBuilder();
        sb.Append("Line ").Append((line + 1).ToString("N0"));
        if (to > from + 1) sb.Append('\u2013').Append((doc.RowToLine(to - 1) + 1).ToString("N0"));
        if (_colour[slot] == 0) return sb.Append("  (nothing matching)").ToString();
        string tip = FilterTipText.Build(doc.FiltersMatching(LineColoured(doc, from, to, _colour[slot])));
        return tip.Length == 0 ? sb.ToString() : sb.Append('\n').Append(tip).ToString();
    }

    /// <summary>The line the pixel took its colour from, so the tip names the filter it is actually showing
    /// rather than whichever filter happens to own the first row behind it.</summary>
    private long LineColoured(CascadeDocument doc, long from, long to, int argb)
    {
        for (long row = from; row < to; row++)
            if (ColourOfRow(row) == argb) return doc.RowToLine(row);
        return doc.RowToLine(from);
    }
}
