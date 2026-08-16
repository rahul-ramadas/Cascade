using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Cascade.Core.Columns;

namespace Cascade.App;

/// <summary>
/// The live picture of one line: the sample with a coloured band behind every part, and beneath it the
/// line as the layout would actually show it. Colour is what ties a band here to a row in the list - names
/// are drawn inside a band only when they fit, so nothing ever collides and it still reads at fifteen
/// parts.
///
/// <para>When the template does not fit the line, the result gives way to a mark under the exact character
/// where it stopped fitting - which the literal scanner knows for nothing.</para>
/// </summary>
public sealed class ColumnsPreview : Control
{
    /// <summary>Muted enough to read black text on, and far enough apart to tell one from the next.</summary>
    private static readonly Color[] Bands =
    [
        Color.FromArgb(255, 224, 178), Color.FromArgb(178, 223, 219), Color.FromArgb(209, 196, 233),
        Color.FromArgb(200, 230, 201), Color.FromArgb(255, 205, 210), Color.FromArgb(187, 222, 251),
        Color.FromArgb(255, 245, 157), Color.FromArgb(215, 204, 200), Color.FromArgb(244, 204, 234),
        Color.FromArgb(178, 235, 242)
    ];

    public static Color BandOf(int part) => Bands[(part % Bands.Length + Bands.Length) % Bands.Length];

    private readonly HScrollBar _scroll = new() { SmallChange = 1, LargeChange = 20, Minimum = 0 };
    private readonly TemplateMatch _match = new();
    private readonly LineProjection _projection = new();

    private Font _mono = null!, _name = null!, _small = null!;
    private int _charWidth, _lineHeight, _nameHeight, _smallHeight;

    private string _line = "";
    private ColumnSpec? _spec;
    private bool _fits;
    private bool _asked;
    private int _selectFrom = -1, _selectTo = -1;
    private bool _dragging;

    public ColumnsPreview()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = SystemColors.Window;
        TabStop = false;
        // Its height follows the font, and a table layout only asks a child what it wants when the child
        // says its size is its own business.
        AutoSize = true;
        Controls.Add(_scroll);
        _scroll.ValueChanged += (_, _) => Invalidate();
        _scroll.AccessibleName = "Scroll the sample line sideways";
        BuildFonts();
    }

    private int _highlight = -1;

    /// <summary>Which part to pick out, so that hovering a row in the list says which part it is.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Highlight
    {
        get => _highlight;
        set { if (_highlight == value) return; _highlight = value; Invalidate(); }
    }

    /// <summary>How wide each field's value runs across the whole sample, so the Columns preview lines up
    /// the way the real table will rather than fitting itself to whichever line happens to be showing.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Dictionary<int, int> ColumnWidths { get; } = [];

    /// <summary>The columns whose width the reader has set by hand, in characters. Those are held to - and
    /// a value too long for one is cut, exactly as the table cuts it - while the rest grow to fit.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Dictionary<int, int> FixedWidths { get; } = [];

    /// <summary>What is picked out in the sample line, as indices into it, or (-1,-1) for nothing.</summary>
    public (int From, int To) Selection => _selectFrom < 0 || _selectTo <= _selectFrom ? (-1, -1) : (_selectFrom, _selectTo);

    public event Action? SelectionChanged;

    private void BuildFonts()
    {
        _mono?.Dispose();
        _name?.Dispose();
        _small?.Dispose();
        // Measured in the old face, so worth nothing in the new one.
        _cellsForChar.Clear();
        _mono = new Font("Consolas", Font.SizeInPoints + 0.5f, FontStyle.Regular, GraphicsUnit.Point);
        _name = new Font(Font.FontFamily, Math.Max(6.5f, Font.SizeInPoints - 1.5f), FontStyle.Bold, GraphicsUnit.Point);
        _small = new Font(Font.FontFamily, Math.Max(7f, Font.SizeInPoints - 1f), GraphicsUnit.Point);

        var big = new Size(int.MaxValue, int.MaxValue);
        _charWidth = Math.Max(1, TextRenderer.MeasureText("0000000000", _mono, big, TextFormatFlags.NoPadding).Width / 10);
        _lineHeight = TextRenderer.MeasureText("Xg", _mono, big, TextFormatFlags.NoPadding).Height + Dpi(4);
        _nameHeight = TextRenderer.MeasureText("Xg", _name, big, TextFormatFlags.NoPadding).Height + Dpi(2);
        _smallHeight = TextRenderer.MeasureText("Xg", _small, big, TextFormatFlags.NoPadding).Height + Dpi(2);
        // Wide enough for the words that name the two rows, whatever font the dialog is being read in.
        // Fixed at 48 they were cut in half the moment anyone raised the font size.
        _gutter = Pad + Math.Max(TextRenderer.MeasureText(SampleLabel, _small, big, TextFormatFlags.NoPadding).Width,
                                 TextRenderer.MeasureText(ResultLabel, _small, big, TextFormatFlags.NoPadding).Width) + Pad;
    }

    private const string SampleLabel = "sample";
    private const string ResultLabel = "result";

    private int Dpi(int logical) => LogicalToDeviceUnits(logical);
    private int Gutter => _gutter;
    private int Pad => Dpi(6);
    private int _gutter;

    /// <summary>How wide one character of the sample is drawn, so the dialog can say what a width in pixels
    /// comes to in characters - which is what the preview lays its columns out in.</summary>
    public int CharWidth => Math.Max(1, _charWidth);

    /// <summary>Room the scrollbar is taking, which is none while there is nothing to scroll.</summary>
    private int ScrollSpace => _scroll.Visible ? _scroll.Height + Dpi(2) : 0;

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        BuildFonts();
        // The maps were built in cells of the old face; the line is redrawn from them.
        Remeasure();
        Invalidate();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        BuildFonts();
        Remeasure();
        Invalidate();
    }

    /// <summary>Works the line out again in the face now in use, without asking the dialog for it.</summary>
    private void Remeasure()
    {
        if (_spec is not null) ShowLine(_line, _spec);
        else UpdateScroll();
    }

    /// <summary>How tall this wants to be: a row of names over the sample, the sample, a row of names over
    /// the result, and the result. The row that carries the reason a line does not fit takes the result's
    /// place rather than a row of its own, so that stepping through the sample does not make the dialog
    /// jump about.</summary>
    public int PreferredHeight
        => Pad + _nameHeight + _lineHeight + Dpi(10) + _nameHeight + _lineHeight + Pad + _scroll.Height + Dpi(2);

    /// <summary>What a layout panel asks for, so the row this sits in follows the font instead of being
    /// measured once, at the default font, before the dialog has even said what font it is using.</summary>
    public override Size GetPreferredSize(Size proposedSize)
        => new(Math.Max(proposedSize.Width, Dpi(200)), PreferredHeight);

    public void ShowLine(string line, ColumnSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!ReferenceEquals(_line, line)) ClearSelection();
        _line = line ?? "";
        _spec = spec;
        _sampleCells.Build(_line, CellsFor);
        // Whether the template is in a state to be asked about the line at all. Without this, a template
        // that is empty or half-written reported every line as one that "does not match", pointing at
        // character 0 and naming nothing - an error where the reader has simply not finished typing.
        _asked = spec.Compiled.IsValid && spec.Compiled.PartCount > 0;
        _fits = _asked && spec.Compiled.Match(_line, _match);
        if (spec.Layout == FieldLayout.Columns)
        {
            BuildTable();
            _resultCells.Build(_result, CellsFor);
        }
        else
        {
            _projection.Build(_line, spec, _match);
            _result = _projection.Text;
            _resultCells.Build(_result, CellsFor);
        }
        UpdateScroll();
        Invalidate();
    }

    /// <summary>How many cells of the fixed-pitch grid one character takes. One, for the ASCII most logs
    /// are; two for a CJK glyph or an emoji; and a tab is given a cell rather than a tab stop, because the
    /// point here is that the text sits in the cells the coloured bands were drawn for.</summary>
    private int CellsFor(char c)
    {
        if (c is >= ' ' and <= '~') return 1;
        if (_cellsForChar.TryGetValue(c, out int cells)) return cells;
        int width = TextRenderer.MeasureText(c.ToString(), _mono, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding).Width;
        cells = Math.Clamp((width + _charWidth - 1) / Math.Max(1, _charWidth), 1, 4);
        _cellsForChar[c] = cells;
        return cells;
    }

    private readonly Dictionary<char, int> _cellsForChar = [];
    private readonly CellMap _sampleCells = new();
    private readonly CellMap _resultCells = new();
    private string _result = "";

    /// <summary>
    /// Where each character of a string sits, counted in cells of the fixed-pitch grid the preview is laid
    /// out on. Ordinary text is a character to a cell and the map is the index itself; a tab, a CJK glyph or
    /// an emoji is not, and without this the text walks away from the bands drawn behind it.
    /// </summary>
    private sealed class CellMap
    {
        /// <summary>Long enough for any line worth looking at one character at a time, short enough that
        /// building the map for a line of megabytes is never attempted.</summary>
        private const int Most = 64 * 1024;

        private int[] _cells = [];
        private bool _plain = true;
        private int _length;

        /// <summary>How many cells the whole string takes.</summary>
        public int Total { get; private set; }

        public void Build(string text, Func<char, int> cellsFor)
        {
            _plain = true;
            _length = text.Length;
            Total = text.Length;
            if (text.Length == 0 || text.Length > Most) return;

            bool wide = false;
            foreach (char c in text) if (c is < ' ' or > '~') { wide = true; break; }
            if (!wide) return;

            if (_cells.Length < text.Length + 1) _cells = new int[Math.Max(text.Length + 1, 256)];
            int at = 0;
            for (int i = 0; i < text.Length; i++)
            {
                _cells[i] = at;
                at += cellsFor(text[i]);
            }
            _cells[text.Length] = at;
            _plain = false;
            Total = at;
        }

        /// <summary>The cell a character starts in.</summary>
        public int Of(int index)
        {
            index = Math.Clamp(index, 0, _length);
            return _plain ? index : _cells[index];
        }

        /// <summary>The character in a cell - which is what the pointer is over.</summary>
        public int IndexAt(int cell)
        {
            if (_plain) return Math.Clamp(cell, 0, _length);
            int lo = 0, hi = _length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (_cells[mid] <= cell) lo = mid; else hi = mid - 1;
            }
            return lo;
        }
    }

    /// <summary>The Columns layout does NOT rebuild the line - it draws the captured values in cells, each
    /// as wide as that column will be, so what is on show here is a row of the table and not a line with the
    /// punctuation left in. A value too long for its cell is cut with an ellipsis exactly as the table cuts
    /// it, which is the whole point of setting a width by hand.
    ///
    /// <para>Everything here is counted in CELLS rather than characters, so that a value with a wide glyph
    /// in it still ends where the column does.</para></summary>
    private void BuildTable()
    {
        _table.Clear();
        _tableSpans.Clear();
        _result = "";
        if (!_fits) return;

        var template = _spec!.Compiled;
        bool first = true;
        foreach (var column in _spec.Columns)
        {
            if (!column.Visible || column.Source < 0 || column.Source >= template.PartCount) continue;
            int value = template.PartAt(column.Source).Value;
            if (value < 0) continue;

            // One cell between columns, as the table leaves a little room either side of a cell's text.
            if (!first) _table.Append(' ');
            first = false;

            var (start, length) = _match.Value(value);
            int valueCells = _sampleCells.Of(start + length) - _sampleCells.Of(start);
            // A width set by hand is held to, and the value cut to fit it as the table would; a column left
            // to itself is as wide as the sample needs, so nothing is ever cut that the table would show.
            int cell = FixedWidths.TryGetValue(column.Source, out int fixedWidth)
                ? Math.Max(1, fixedWidth)
                : Math.Max(1, Math.Max(ColumnWidths.GetValueOrDefault(column.Source), valueCells));

            bool cut = valueCells > cell;
            int room = cut ? cell - 1 : cell;           // a cell is kept for the ellipsis
            int take = 0, taken = 0;
            while (take < length && taken + CellsFor(_line[start + take]) <= room)
                taken += CellsFor(_line[start + take++]);

            int slack = Math.Max(0, cell - taken - (cut ? 1 : 0));
            int before = column.Align switch
            {
                ColumnAlign.Right => slack,
                ColumnAlign.Center => slack / 2,
                _ => 0
            };

            int cellStart = _table.Length;
            _table.Append(' ', before);
            int textAt = _table.Length;
            if (take > 0) _table.Append(_line, start, take);
            if (cut) _table.Append('\u2026');
            int textLength = _table.Length - textAt;
            _table.Append(' ', slack - before);
            _tableSpans.Add((cellStart, cell, textAt, textLength, column.Source));
        }
        _result = _table.ToString();
    }

    private readonly System.Text.StringBuilder _table = new();

    /// <summary>Each cell of the Columns preview: where the cell begins in the built row and how wide it is
    /// (which is what makes the columns line up), and where its text sits inside it. Both are indices into
    /// the row's characters; the cells they fall in come from the map.</summary>
    private readonly List<(int Start, int Width, int TextAt, int TextLength, int Part)> _tableSpans = [];
    private bool AsTable => _spec is not null && _spec.Layout == FieldLayout.Columns;

    public void ClearSelection()
    {
        if (_selectFrom < 0) return;
        _selectFrom = _selectTo = -1;
        SelectionChanged?.Invoke();
        Invalidate();
    }

    private int VisibleChars => Math.Max(1, (ClientSize.Width - Gutter - Pad) / Math.Max(1, _charWidth));

    private void UpdateScroll()
    {
        int widest = Math.Max(_sampleCells.Total, _resultCells.Total);
        int over = Math.Max(0, widest - VisibleChars);
        int large = Math.Max(1, VisibleChars / 2);
        // Maximum first, and read back what the scrollbar actually took: LargeChange is clamped to the
        // range, so setting it against the OLD maximum and then working the new one out from what came back
        // could leave a bar that is on screen with nowhere to go.
        _scroll.Maximum = over + large - 1;
        _scroll.LargeChange = large;
        _scroll.Maximum = over + _scroll.LargeChange - 1;
        // Out of the way entirely when there is nothing to scroll: a dead scrollbar is one more thing on a
        // dialog that already has plenty to look at.
        _scroll.Visible = over > 0;
        if (_scroll.Value > over) _scroll.Value = over;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        PlaceScrollBar();
        UpdateScroll();
    }

    /// <summary>Puts the scrollbar just inside the border rather than docked across the bottom of the
    /// control, where its own background cut the box in two - the box is meant to read as one panel.</summary>
    private void PlaceScrollBar()
        => _scroll.SetBounds(1, Math.Max(0, ClientSize.Height - 1 - _scroll.Height),
                             Math.Max(0, ClientSize.Width - 2), _scroll.Height);

    // ---- picking a stretch of the sample out, to make a column of it ----

    /// <summary>Which character of the sample the pointer is over: the cell it is in, and then the character
    /// that sits in that cell.</summary>
    private int CharAt(int x)
    {
        int cell = (x - Gutter + _scroll.Value * _charWidth + _charWidth / 2) / Math.Max(1, _charWidth);
        return _sampleCells.IndexAt(Math.Max(0, cell));
    }

    private int SampleTop => Pad + _nameHeight;

    /// <summary>Where the names over the RESULT are drawn, and where the result itself is. The result gets
    /// names of its own because it is not the sample with pieces greyed out - the fields can be in another
    /// order entirely, so one row of headings over the sample would be a heading over the wrong thing.</summary>
    private int ResultNameTop => SampleTop + _lineHeight + Dpi(10);
    private int ResultTop => ResultNameTop + _nameHeight;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e is null || e.Button != MouseButtons.Left) return;
        if (InSample(e.Y))
        {
            _dragging = true;
            _selectFrom = _selectTo = CharAt(e.X);
            Capture = true;
            Invalidate();
            return;
        }
        // A press on the result is not a drag - the result cannot be picked out of, only pointed at - so it
        // is remembered and answered on the way up, where a click properly is one.
        if (InResult(e.Y)) _pressedResult = e.X;
    }

    private int _pressedResult = int.MinValue;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e is null) return;
        Cursor = InSample(e.Y) ? Cursors.IBeam
               : InResult(e.Y) && PartAtResult(e.X) >= 0 ? Cursors.Hand
               : Cursors.Default;
        if (!_dragging) return;
        _selectTo = CharAt(e.X);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e is not null && _pressedResult != int.MinValue)
        {
            int was = _pressedResult;
            _pressedResult = int.MinValue;
            if (InResult(e.Y) && Math.Abs(e.X - was) <= SystemInformation.DragSize.Width)
                Pick(PartAtResult(e.X));
            return;
        }
        if (!_dragging || e is null) return;
        _dragging = false;
        Capture = false;
        if (_selectTo < _selectFrom) (_selectFrom, _selectTo) = (_selectTo, _selectFrom);
        // A press and release in the same place picked nothing out, so it was a click: it says which field
        // was pointed at instead, which is how the sample and the list are tied together in both directions.
        // Only over the text, though - the words naming the rows sit to the left of it, and pointing at
        // those is pointing at nothing.
        if (_selectTo == _selectFrom && e.X >= Gutter) Pick(PartAtSample(_selectFrom));
        SelectionChanged?.Invoke();
        Invalidate();
    }

    /// <summary>Raised with the part a click landed on, so the list can bring that field's row forward.</summary>
    public event Action<int>? PartPicked;

    private void Pick(int part)
    {
        if (part >= 0) PartPicked?.Invoke(part);
    }

    private bool InSample(int y) => y >= SampleTop && y <= SampleTop + _lineHeight;
    private bool InResult(int y) => _fits && y >= ResultTop && y <= ResultTop + _lineHeight;

    /// <summary>Which part of the template covers a character of the sample, or -1 for the text between
    /// parts and the tail beyond the last of them.</summary>
    private int PartAtSample(int index)
    {
        if (!_fits) return -1;
        for (int part = 0; part < _match.PartCount; part++)
        {
            var (start, length) = _match.Part(part);
            if (length > 0 && index >= start && index < start + length) return part;
        }
        return -1;
    }

    /// <summary>Which part the result draws at a point - a whole cell in the table, a field's own text
    /// inline - or -1 where nothing of the log's is drawn there.</summary>
    private int PartAtResult(int x)
    {
        if (!_fits || x < Gutter) return -1;
        int cell = (x - Gutter + _scroll.Value * _charWidth) / Math.Max(1, _charWidth);
        if (cell < 0) return -1;
        if (AsTable)
        {
            foreach (var (start, width, _, _, part) in _tableSpans)
            {
                int from = _resultCells.Of(start);
                if (cell >= from && cell < from + width) return part;
            }
            return -1;
        }
        foreach (var span in _projection.Spans)
        {
            if (span.Part < 0) continue;
            int from = _resultCells.Of(span.Start), to = _resultCells.Of(span.Start + span.Length);
            if (cell >= from && cell < to) return span.Part;
        }
        return -1;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (_scroll.Visible)
        {
            int step = e.Delta > 0 ? -3 : 3;
            int most = Math.Max(_scroll.Minimum, _scroll.Maximum - _scroll.LargeChange + 1);
            _scroll.Value = Math.Clamp(_scroll.Value + step, _scroll.Minimum, most);
        }
        base.OnMouseWheel(e);
    }

    // ---- drawing ----

    /// <summary>
    /// How every piece of the scrolled text is drawn.
    ///
    /// <see cref="TextFormatFlags.PreserveGraphicsClipping"/> is the load-bearing one: TextRenderer draws
    /// through GDI, which ignores the GDI+ clip region set on the Graphics unless it is asked not to.
    /// Without it, a sample scrolled sideways starts at a negative x and is painted straight over the
    /// words that name the two rows - the SetClip around the call looks like it should prevent that,
    /// and does not.
    /// </summary>
    private const TextFormatFlags ScrolledText =
        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        using (var pen = new Pen(SystemColors.ControlDark))
            g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

        if (_spec is null) return;

        int nameY = Pad;
        int sampleY = SampleTop;
        int resultY = ResultTop;

        TextRenderer.DrawText(g, SampleLabel, _small, new Point(Pad, sampleY + Dpi(2)), SystemColors.GrayText,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        if (_fits)
            TextRenderer.DrawText(g, ResultLabel, _small, new Point(Pad, resultY + Dpi(2)), SystemColors.GrayText,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

        var saved = g.Clip;
        g.SetClip(new Rectangle(Gutter, 0, Math.Max(0, ClientSize.Width - Gutter - Pad),
                                Math.Max(0, ClientSize.Height - ScrollSpace)));
        int x0 = Gutter - _scroll.Value * _charWidth;

        DrawSample(g, x0, nameY, sampleY);
        if (_fits) DrawResult(g, x0, ResultNameTop, resultY);
        g.Clip = saved;
        saved.Dispose();

        // In the place the result would have taken, not a row below it: a reader who has just been told the
        // line does not fit should not have to hunt down the page for the reason. Only when there is a
        // template to fail, though - "expected nothing at character 0" is no way to greet an empty box.
        if (_asked && !_fits) DrawWhyNot(g, x0, sampleY, resultY + Dpi(2));
    }

    private void DrawSample(Graphics g, int x0, int nameY, int y)
    {
        var hidden = new HashSet<int>();
        var names = new Dictionary<int, string>();
        foreach (var column in _spec!.Columns)
        {
            if (!column.Visible) hidden.Add(column.Source);
            names[column.Source] = column.Name;
        }

        if (_fits)
        {
            for (int part = 0; part < _match.PartCount; part++)
            {
                var (start, length) = _match.Part(part);
                if (length <= 0) continue;
                bool off = hidden.Contains(part);
                int from = _sampleCells.Of(start), to = _sampleCells.Of(start + length);
                var rect = new Rectangle(x0 + from * _charWidth, y, (to - from) * _charWidth, _lineHeight);
                if (rect.Right < Gutter || rect.Left > ClientSize.Width) continue;

                using (var brush = new SolidBrush(off ? SystemColors.ControlLight : BandOf(part)))
                    g.FillRectangle(brush, rect);

                if (part == Highlight)
                    using (var pen = new Pen(SystemColors.WindowText, Dpi(2)))
                        g.DrawRectangle(pen, rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2);

                DrawName(g, names.GetValueOrDefault(part), rect, nameY, off);
            }
        }

        var (selectFrom, selectTo) = Selection;
        if (selectFrom >= 0)
        {
            int from = _sampleCells.Of(selectFrom), to = _sampleCells.Of(selectTo);
            var rect = new Rectangle(x0 + from * _charWidth, y, (to - from) * _charWidth, _lineHeight);
            using var brush = new SolidBrush(Color.FromArgb(90, SystemColors.Highlight));
            g.FillRectangle(brush, rect);
        }

        // The text in stretches, so a hidden part can be drawn differently without being drawn over.
        int at = 0;
        if (_fits)
        {
            for (int part = 0; part < _match.PartCount; part++)
            {
                var (start, length) = _match.Part(part);
                if (length <= 0) continue;
                if (start > at) DrawText(g, x0, y, at, start - at, SystemColors.WindowText, false);
                DrawText(g, x0, y, start, length, hidden.Contains(part) ? SystemColors.GrayText : SystemColors.WindowText,
                         hidden.Contains(part));
                at = start + length;
            }
        }
        if (at < _line.Length)
            DrawText(g, x0, y, at, _line.Length - at,
                _fits ? SystemColors.WindowText : SystemColors.GrayText, false);
    }

    /// <summary>Puts a field's name over the band drawn for it, centred, and only where the band is wide
    /// enough to hold it with room to spare on both sides - so two names over neighbouring bands are told
    /// apart by the gap between them rather than running into one another.</summary>
    private void DrawName(Graphics g, string? name, Rectangle band, int y, bool off)
    {
        if (string.IsNullOrEmpty(name)) return;
        var size = TextRenderer.MeasureText(name, _name, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
        if (size.Width + Dpi(8) > band.Width) return;
        TextRenderer.DrawText(g, name, _name, new Rectangle(band.X, y, band.Width, _nameHeight),
            off ? SystemColors.GrayText : SystemColors.ControlDarkDark,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
            TextFormatFlags.PreserveGraphicsClipping);
    }

    private void DrawText(Graphics g, int x0, int y, int start, int length, Color colour, bool struck)
    {
        if (length <= 0 || start >= _line.Length) return;
        length = Math.Min(length, _line.Length - start);
        int from = _sampleCells.Of(start), to = _sampleCells.Of(start + length);
        int x = x0 + from * _charWidth;
        if (x + (to - from) * _charWidth < Gutter || x > ClientSize.Width) return;

        DrawCells(g, _line.AsSpan(start, length), x, y + Dpi(2), colour);

        if (struck)
            using (var pen = new Pen(SystemColors.GrayText))
                g.DrawLine(pen, x, y + _lineHeight / 2, x + (to - from) * _charWidth, y + _lineHeight / 2);
    }

    /// <summary>Draws a stretch of text into the cells the bands were drawn for. Everything here is laid out
    /// a character to a cell, which one call to the renderer gives you free for ordinary text - but a tab, a
    /// CJK glyph or an emoji is not one cell wide, and left to itself the text walks away from the colours
    /// behind it. Those are placed cell by cell instead, and only for the line actually on show.</summary>
    private void DrawCells(Graphics g, ReadOnlySpan<char> text, int x, int y, Color colour)
    {
        if (AllOneCell(text))
        {
            TextRenderer.DrawText(g, text, _mono, new Point(x, y), colour, ScrolledText);
            return;
        }

        int cell = 0;
        for (int i = 0; i < text.Length; i++)
        {
            int cx = x + cell * _charWidth;
            if (cx > ClientSize.Width) break;      // and everything after it is further right still
            // A surrogate pair is one glyph over two cells: drawn as a pair, or it comes out as two boxes.
            int take = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
            int cells = take == 2 ? CellsFor(text[i]) + CellsFor(text[i + 1]) : CellsFor(text[i]);
            if (cx + cells * _charWidth >= Gutter)
                TextRenderer.DrawText(g, text.Slice(i, take), _mono, new Point(cx, y), colour, ScrolledText);
            cell += cells;
            i += take - 1;
        }
    }

    /// <summary>Whether every character is one cell wide in a fixed-pitch face, which is true of the plain
    /// ASCII most logs are and lets the whole stretch go out in one call.</summary>
    private static bool AllOneCell(ReadOnlySpan<char> text)
    {
        foreach (char c in text) if (c < ' ' || c > '~') return false;
        return true;
    }

    /// <summary>The result, with a row of names over it. The names are what makes a reordered row readable:
    /// the sample's headings sit over the fields where the LINE has them, and once the fields have been
    /// moved about those headings answer for the wrong things - a table whose header row does not match its
    /// body is worse than no header at all.</summary>
    private void DrawResult(Graphics g, int x0, int nameY, int y)
    {
        var names = new Dictionary<int, string>();
        foreach (var column in _spec!.Columns) names[column.Source] = column.Name;

        if (AsTable)
        {
            // Cell by cell rather than one long string: the cell is the thing that has to line up, and a
            // band drawn round the value alone says nothing about where the column ends.
            foreach (var (start, width, textAt, textLength, part) in _tableSpans)
            {
                int from = _resultCells.Of(start);
                var cell = new Rectangle(x0 + from * _charWidth, y, width * _charWidth, _lineHeight);
                if (cell.Right < Gutter || cell.Left > ClientSize.Width) continue;
                using (var brush = new SolidBrush(BandOf(part)))
                    g.FillRectangle(brush, cell);
                if (part == Highlight)
                    using (var pen = new Pen(SystemColors.WindowText, Dpi(2)))
                        g.DrawRectangle(pen, cell.X + 1, cell.Y + 1, cell.Width - 2, cell.Height - 2);
                if (textLength > 0)
                    DrawCells(g, _result.AsSpan(textAt, textLength), x0 + _resultCells.Of(textAt) * _charWidth,
                              y + Dpi(2), SystemColors.WindowText);
                DrawName(g, names.GetValueOrDefault(part), cell, nameY, false);
            }
            return;
        }

        foreach (var span in _projection.Spans)
        {
            int from = _resultCells.Of(span.Start), to = _resultCells.Of(span.Start + span.Length);
            var rect = new Rectangle(x0 + from * _charWidth, y, (to - from) * _charWidth, _lineHeight);
            if (rect.Right < Gutter || rect.Left > ClientSize.Width) continue;

            if (span.Part >= 0)
            {
                using var brush = new SolidBrush(BandOf(span.Part));
                g.FillRectangle(brush, rect);
                if (span.Part == Highlight)
                    using (var pen = new Pen(SystemColors.WindowText, Dpi(2)))
                        g.DrawRectangle(pen, rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2);
                DrawName(g, names.GetValueOrDefault(span.Part), rect, nameY, false);
            }
            else if (span.Invented)
            {
                // Text the projection had to put in, marked so it is never taken for the log's own.
                using var brush = new HatchBrush(HatchStyle.LightUpwardDiagonal, SystemColors.ControlDark, SystemColors.Window);
                g.FillRectangle(brush, rect);
            }

            DrawCells(g, _result.AsSpan(span.Start, span.Length), rect.X, y + Dpi(2), SystemColors.WindowText);
        }
    }

    private void DrawWhyNot(Graphics g, int x0, int sampleY, int noteY)
    {
        int at = _match.FailurePosition;
        int x = x0 + _sampleCells.Of(at) * _charWidth;
        var red = Color.FromArgb(192, 32, 32);

        if (x >= Gutter && x <= ClientSize.Width)
            using (var pen = new Pen(red, Dpi(2)))
                g.DrawLine(pen, x, sampleY, x, sampleY + _lineHeight - 1);

        string wanted = _match.FailureExpected == " " ? "a space" : $"\u201c{_match.FailureExpected}\u201d";
        TextRenderer.DrawText(g, $"This line does not match: expected {wanted} at character {at}.", _small,
            new Rectangle(Gutter, noteY, Math.Max(0, ClientSize.Width - Gutter - Pad), _smallHeight + _lineHeight),
            red, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordEllipsis);
    }

    /// <summary>Picks a stretch of the sample out, as a drag across it does, so the dialog can be driven
    /// without a mouse.</summary>
    internal void SelectForTesting(int from, int to)
    {
        _selectFrom = Math.Clamp(from, 0, _line.Length);
        _selectTo = Math.Clamp(to, 0, _line.Length);
        SelectionChanged?.Invoke();
        Invalidate();
    }

    internal int ScrollValueForTesting() => _scroll.Value;

    internal void ScrollToForTesting(int value)
        => _scroll.Value = Math.Clamp(value, _scroll.Minimum, Math.Max(_scroll.Minimum, _scroll.Maximum - _scroll.LargeChange + 1));

    /// <summary>Points at a character of the sample, or a cell of the result, as a click on it does.</summary>
    internal void ClickSampleForTesting(int index) => Pick(PartAtSample(index));
    internal void ClickResultForTesting(int cell) => Pick(PartAtResult(Gutter + (cell - _scroll.Value) * _charWidth));

    internal bool CanScrollForTesting() => _scroll.Visible;
    internal int FurthestScrollForTesting() => Math.Max(_scroll.Minimum, _scroll.Maximum - _scroll.LargeChange + 1);
    internal int GutterForTesting() => Gutter;
    internal string ResultForTesting() => _result;

    /// <summary>Whether the box is telling the reader the line does not fit the template.</summary>
    internal bool SaysWhyNotForTesting => _asked && !_fits;

    internal int ScrollBarHeightForTesting => _scroll.Visible ? _scroll.Height : 0;

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _mono?.Dispose(); _name?.Dispose(); _small?.Dispose(); }
        base.Dispose(disposing);
    }
}
