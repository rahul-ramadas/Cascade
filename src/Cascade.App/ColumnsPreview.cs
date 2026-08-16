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

    private readonly HScrollBar _scroll = new() { Dock = DockStyle.Bottom, SmallChange = 1, LargeChange = 20, Minimum = 0 };
    private readonly TemplateMatch _match = new();
    private readonly LineProjection _projection = new();

    private Font _mono = null!, _name = null!, _small = null!;
    private int _charWidth, _lineHeight, _nameHeight, _smallHeight;

    private string _line = "";
    private ColumnSpec? _spec;
    private bool _fits;
    private int _selectFrom = -1, _selectTo = -1;
    private bool _dragging;

    public ColumnsPreview()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = SystemColors.Window;
        TabStop = false;
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

    /// <summary>What is picked out in the sample line, as indices into it, or (-1,-1) for nothing.</summary>
    public (int From, int To) Selection => _selectFrom < 0 || _selectTo <= _selectFrom ? (-1, -1) : (_selectFrom, _selectTo);

    public event Action? SelectionChanged;

    private void BuildFonts()
    {
        _mono?.Dispose();
        _name?.Dispose();
        _small?.Dispose();
        _mono = new Font("Consolas", Font.SizeInPoints + 0.5f, FontStyle.Regular, GraphicsUnit.Point);
        _name = new Font(Font.FontFamily, Math.Max(6.5f, Font.SizeInPoints - 1.5f), FontStyle.Bold, GraphicsUnit.Point);
        _small = new Font(Font.FontFamily, Math.Max(7f, Font.SizeInPoints - 1f), GraphicsUnit.Point);

        var big = new Size(int.MaxValue, int.MaxValue);
        _charWidth = Math.Max(1, TextRenderer.MeasureText("0000000000", _mono, big, TextFormatFlags.NoPadding).Width / 10);
        _lineHeight = TextRenderer.MeasureText("Xg", _mono, big, TextFormatFlags.NoPadding).Height + Dpi(4);
        _nameHeight = TextRenderer.MeasureText("Xg", _name, big, TextFormatFlags.NoPadding).Height + Dpi(2);
        _smallHeight = TextRenderer.MeasureText("Xg", _small, big, TextFormatFlags.NoPadding).Height + Dpi(2);
    }

    private int Dpi(int logical) => LogicalToDeviceUnits(logical);
    private int Gutter => Dpi(48);
    private int Pad => Dpi(6);

    /// <summary>Room the scrollbar is taking, which is none while there is nothing to scroll.</summary>
    private int ScrollSpace => _scroll.Visible ? _scroll.Height : 0;

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        BuildFonts();
        Invalidate();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        BuildFonts();
        Invalidate();
    }

    /// <summary>How tall this wants to be: the names, the two lines of text, and the row that carries the
    /// reason when a line does not fit. That row is kept even when everything fits, so the dialog does not
    /// jump about as the reader steps through the sample.</summary>
    public int PreferredHeight => Pad + _nameHeight + _lineHeight + Dpi(4) + _lineHeight + _smallHeight + Pad + _scroll.Height;

    public void ShowLine(string line, ColumnSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!ReferenceEquals(_line, line)) ClearSelection();
        _line = line ?? "";
        _spec = spec;
        _fits = spec.Compiled.IsValid && spec.Compiled.PartCount > 0 && spec.Compiled.Match(_line, _match);
        if (spec.Layout == FieldLayout.Columns) BuildTable(); else _projection.Build(_line, spec, _match);
        UpdateScroll();
        Invalidate();
    }

    /// <summary>The Columns layout does NOT rebuild the line - it draws the captured values, padded to line
    /// up. Showing the reconstructed line here instead would promise a row the table will never draw:
    /// the real one leaves the punctuation behind, because the punctuation is not in any value.</summary>
    private void BuildTable()
    {
        _table.Clear();
        _tableSpans.Clear();
        if (!_fits) return;

        var template = _spec!.Compiled;
        bool first = true;
        foreach (var column in _spec.Columns)
        {
            if (!column.Visible || column.Source < 0 || column.Source >= template.PartCount) continue;
            int value = template.PartAt(column.Source).Value;
            if (value < 0) continue;

            if (!first) _table.Append("  ");
            first = false;

            var (start, length) = _match.Value(value);
            int want = ColumnWidths.TryGetValue(column.Source, out int w) ? Math.Max(w, length) : length;
            int slack = Math.Max(0, want - length);
            int before = column.Align switch
            {
                ColumnAlign.Right => slack,
                ColumnAlign.Center => slack / 2,
                _ => 0
            };

            _table.Append(' ', before);
            _tableSpans.Add((_table.Length, length, column.Source));
            _table.Append(_line, start, length);
            _table.Append(' ', slack - before);
        }
    }

    private readonly System.Text.StringBuilder _table = new();
    private readonly List<(int Start, int Length, int Part)> _tableSpans = [];
    private bool AsTable => _spec is not null && _spec.Layout == FieldLayout.Columns;
    private string Result => AsTable ? _table.ToString() : _projection.Text;

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
        int widest = Math.Max(_line.Length, Result.Length);
        int over = Math.Max(0, widest - VisibleChars);
        _scroll.LargeChange = Math.Max(1, VisibleChars / 2);
        _scroll.Maximum = over + _scroll.LargeChange - 1;
        // Out of the way entirely when there is nothing to scroll: a dead scrollbar is one more thing on a
        // dialog that already has plenty to look at.
        _scroll.Visible = over > 0;
        if (_scroll.Value > over) _scroll.Value = over;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateScroll();
    }

    // ---- picking a stretch of the sample out, to make a column of it ----

    private int CharAt(int x) => Math.Clamp((x - Gutter + _scroll.Value * _charWidth + _charWidth / 2) / _charWidth, 0, _line.Length);

    private int SampleTop => Pad + _nameHeight;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || e.Y < SampleTop || e.Y > SampleTop + _lineHeight) return;
        _dragging = true;
        _selectFrom = _selectTo = CharAt(e.X);
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = e.Y >= SampleTop && e.Y <= SampleTop + _lineHeight ? Cursors.IBeam : Cursors.Default;
        if (!_dragging) return;
        _selectTo = CharAt(e.X);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging) return;
        _dragging = false;
        Capture = false;
        if (_selectTo < _selectFrom) (_selectFrom, _selectTo) = (_selectTo, _selectFrom);
        SelectionChanged?.Invoke();
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (_scroll.Enabled)
        {
            int step = e.Delta > 0 ? -3 : 3;
            int most = Math.Max(_scroll.Minimum, _scroll.Maximum - _scroll.LargeChange + 1);
            _scroll.Value = Math.Clamp(_scroll.Value + step, _scroll.Minimum, most);
        }
        base.OnMouseWheel(e);
    }

    // ---- drawing ----

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        using (var pen = new Pen(SystemColors.ControlDark))
            g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - ScrollSpace - 1);

        if (_spec is null) return;

        int nameY = Pad;
        int sampleY = SampleTop;
        int resultY = sampleY + _lineHeight + Dpi(4);
        int noteY = resultY + _lineHeight;

        TextRenderer.DrawText(g, "sample", _small, new Point(Pad, sampleY + Dpi(2)), SystemColors.GrayText);
        if (_fits) TextRenderer.DrawText(g, "result", _small, new Point(Pad, resultY + Dpi(2)), SystemColors.GrayText);

        var saved = g.Clip;
        g.SetClip(new Rectangle(Gutter, 0, Math.Max(0, ClientSize.Width - Gutter - Pad), ClientSize.Height));
        int x0 = Gutter - _scroll.Value * _charWidth;

        DrawSample(g, x0, nameY, sampleY);
        if (_fits) DrawResult(g, x0, resultY);
        g.Clip = saved;
        saved.Dispose();

        if (!_fits) DrawWhyNot(g, x0, sampleY, noteY);
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
                var rect = new Rectangle(x0 + start * _charWidth, y, length * _charWidth, _lineHeight);
                if (rect.Right < Gutter || rect.Left > ClientSize.Width) continue;

                using (var brush = new SolidBrush(off ? SystemColors.ControlLight : BandOf(part)))
                    g.FillRectangle(brush, rect);

                if (part == Highlight)
                    using (var pen = new Pen(SystemColors.WindowText, Dpi(2)))
                        g.DrawRectangle(pen, rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2);

                if (names.TryGetValue(part, out var name) && name.Length > 0)
                {
                    var size = TextRenderer.MeasureText(name, _name, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
                    if (size.Width <= rect.Width)
                        TextRenderer.DrawText(g, name, _name, new Rectangle(rect.X, nameY, rect.Width, _nameHeight),
                            off ? SystemColors.GrayText : SystemColors.ControlDarkDark,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                }
            }
        }

        var (selectFrom, selectTo) = Selection;
        if (selectFrom >= 0)
        {
            var rect = new Rectangle(x0 + selectFrom * _charWidth, y, (selectTo - selectFrom) * _charWidth, _lineHeight);
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

    private void DrawText(Graphics g, int x0, int y, int start, int length, Color colour, bool struck)
    {
        if (length <= 0 || start >= _line.Length) return;
        length = Math.Min(length, _line.Length - start);
        int x = x0 + start * _charWidth;
        if (x + length * _charWidth < Gutter || x > ClientSize.Width) return;

        TextRenderer.DrawText(g, _line.AsSpan(start, length), _mono, new Point(x, y + Dpi(2)), colour,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

        if (struck)
            using (var pen = new Pen(SystemColors.GrayText))
                g.DrawLine(pen, x, y + _lineHeight / 2, x + length * _charWidth, y + _lineHeight / 2);
    }

    private void DrawResult(Graphics g, int x0, int y)
    {
        if (AsTable)
        {
            string table = _table.ToString();
            foreach (var (start, length, part) in _tableSpans)
            {
                var cell = new Rectangle(x0 + start * _charWidth, y, length * _charWidth, _lineHeight);
                if (cell.Right < Gutter || cell.Left > ClientSize.Width) continue;
                using var brush = new SolidBrush(BandOf(part));
                g.FillRectangle(brush, cell);
                if (part == Highlight)
                    using (var pen = new Pen(SystemColors.WindowText, Dpi(2)))
                        g.DrawRectangle(pen, cell.X + 1, cell.Y + 1, cell.Width - 2, cell.Height - 2);
            }
            TextRenderer.DrawText(g, table, _mono, new Point(x0, y + Dpi(2)), SystemColors.WindowText,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            return;
        }

        foreach (var span in _projection.Spans)
        {
            var rect = new Rectangle(x0 + span.Start * _charWidth, y, span.Length * _charWidth, _lineHeight);
            if (rect.Right < Gutter || rect.Left > ClientSize.Width) continue;

            if (span.Part >= 0)
            {
                using var brush = new SolidBrush(BandOf(span.Part));
                g.FillRectangle(brush, rect);
                if (span.Part == Highlight)
                    using (var pen = new Pen(SystemColors.WindowText, Dpi(2)))
                        g.DrawRectangle(pen, rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2);
            }
            else if (span.Invented)
            {
                // Text the projection had to put in, marked so it is never taken for the log's own.
                using var brush = new HatchBrush(HatchStyle.LightUpwardDiagonal, SystemColors.ControlDark, SystemColors.Window);
                g.FillRectangle(brush, rect);
            }

            TextRenderer.DrawText(g, _projection.Text.AsSpan(span.Start, span.Length), _mono,
                new Point(rect.X, y + Dpi(2)), SystemColors.WindowText,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
    }

    private void DrawWhyNot(Graphics g, int x0, int sampleY, int noteY)
    {
        int at = _match.FailurePosition;
        int x = x0 + at * _charWidth;
        var red = Color.FromArgb(192, 32, 32);

        if (x >= Gutter && x <= ClientSize.Width)
            using (var pen = new Pen(red, Dpi(2)))
                g.DrawLine(pen, x, sampleY, x, sampleY + _lineHeight - 1);

        string wanted = _match.FailureExpected == " " ? "a space" : $"\u201c{_match.FailureExpected}\u201d";
        TextRenderer.DrawText(g, $"This line does not match: expected {wanted} at character {at}.", _small,
            new Point(Gutter, noteY), red, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _mono?.Dispose(); _name?.Dispose(); _small?.Dispose(); }
        base.Dispose(disposing);
    }
}
