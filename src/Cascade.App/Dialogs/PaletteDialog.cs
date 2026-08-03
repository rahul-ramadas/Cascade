using System.Drawing;
using System.Windows.Forms;
using Cascade.Core.Model;

namespace Cascade.App;

/// <summary>Every colour pair still going spare, shown as what a matching line would look like. The point
/// of picking a colour here rather than from the system dialog is that the choice is already narrowed to
/// pairs that read well and that no other filter is wearing - so there is nothing to get wrong.</summary>
internal sealed class PaletteDialog : DialogBase
{
    private readonly Chips _chips;
    private readonly Quiet _scroller;
    private readonly Button _ok;

    internal LuckyColors.Pair Picked => _chips.Picked;

    /// <param name="sample">Drawn in every cell - the filter's own pattern where there is one, so the
    /// choice is made against the text it will actually colour.</param>
    internal PaletteDialog(IReadOnlyList<LuckyColors.Pair> pairs, string sample, LuckyColors.Pair? current,
                           int? visibleRows = null)
    {
        Text = "Choose a Color";

        const int columns = 5;
        int cellH = Dpi(28);
        _chips = new Chips(pairs, sample, Dpi(150), cellH, columns)
        {
            AccessibleName = "Color choices",
            Margin = Padding.Empty
        };
        if (current is { } c) _chips.SelectNearest(c);

        // Show the lot where the screen allows it - there are only a hundred or so once near-identical
        // colours are dropped, and a palette you can see all of needs no scrolling at all.
        int needed = Math.Max(1, (pairs.Count + columns - 1) / columns);
        int room = Math.Max(6, (Screen.PrimaryScreen?.WorkingArea.Height ?? Dpi(700)) * 55 / 100 / cellH);
        int rows = visibleRows ?? Math.Clamp(needed, 6, room);

        _scroller = new Quiet
        {
            AutoScroll = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, Dpi(4)),
            BorderStyle = BorderStyle.FixedSingle,
            Size = new Size(_chips.Width + SystemInformation.VerticalScrollBarWidth + Dpi(2), cellH * rows)
        };
        var scroller = _scroller;
        scroller.Controls.Add(_chips);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(Dpi(12), Dpi(10), Dpi(12), Dpi(10))
        };
        root.Controls.Add(scroller, 0, 0);

        var hint = new Label
        {
            Text = pairs.Count > 0
                 ? $"{pairs.Count:N0} colors no other filter is close to. Double-click one, or press Enter."
                 : "Every color is close to one already in use.",
            AutoSize = true,
            ForeColor = Color.Gray,
            Margin = new Padding(0, Dpi(8), 0, 0)
        };
        root.Controls.Add(hint, 0, 1);

        var buttons = OkCancelRow(out var ok, out _);
        ok.Enabled = pairs.Count > 0;
        root.Controls.Add(buttons, 0, 2);
        _ok = ok;

        Controls.Add(root);

        // Double-clicking a colour means "this one" - the same gesture that opens a filter from the list.
        _chips.Chosen += () => { DialogResult = DialogResult.OK; Close(); };
        ActiveControl = _chips;
    }

    internal int CountForTesting => _chips.Count;
    internal void MoveForTesting(Keys key) => _chips.MoveForTesting(key);
    internal Rectangle CellForTesting(int index) => _chips.CellAt(index);
    internal int ScrollForTesting => -_scroller.AutoScrollPosition.Y;
    internal int ViewportForTesting => _scroller.ClientSize.Height;
    internal void ScrollToForTesting(int y) => _scroller.AutoScrollPosition = new Point(0, y);
    internal void ClickForTesting(int index) => _chips.ClickForTesting(index);
    internal bool ScrollsForTesting => _chips.Height > _scroller.ClientSize.Height;
    /// <summary>Tab away and back, which is what makes a scrolling panel chase the focus.</summary>
    internal void CycleFocusForTesting()
    {
        ActiveControl = _ok;
        ActiveControl = _chips;
    }

    /// <summary>A scrolling panel that does NOT chase the focus. The grid is one tall control, so the stock
    /// behaviour - scroll the newly focused child fully into view - jumps the list every time it is clicked.
    /// Which row to show is the grid's business, and it says so itself.</summary>
    private sealed class Quiet : Panel
    {
        protected override Point ScrollToControl(Control activeControl) => DisplayRectangle.Location;
    }

    /// <summary>The grid itself. One control rather than a control per colour: there are hundreds of them,
    /// and a window handle each would cost far more than painting the few dozen actually on screen.</summary>
    private sealed class Chips : Control
    {
        private readonly IReadOnlyList<LuckyColors.Pair> _pairs;
        private readonly string _sample;
        private readonly int _cellW, _cellH, _columns;
        private int _index;

        internal event Action? Chosen;

        internal Chips(IReadOnlyList<LuckyColors.Pair> pairs, string sample, int cellW, int cellH, int columns)
        {
            _pairs = pairs;
            _sample = sample;
            _cellW = cellW;
            _cellH = cellH;
            _columns = columns;
            DoubleBuffered = true;
            TabStop = true;
            SetStyle(ControlStyles.Selectable | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            int rows = Math.Max(1, (pairs.Count + columns - 1) / columns);
            Size = new Size(cellW * columns, cellH * rows);
        }

        internal int Count => _pairs.Count;
        internal LuckyColors.Pair Picked => _pairs.Count == 0 ? default : _pairs[Math.Clamp(_index, 0, _pairs.Count - 1)];

        internal Rectangle CellAt(int index) =>
            new(index % _columns * _cellW, index / _columns * _cellH, _cellW, _cellH);

        /// <summary>Opens on the colour the filter already wears, so a dialog reopened shows where it stands
        /// rather than the top of the list.</summary>
        internal void SelectNearest(LuckyColors.Pair to)
        {
            double best = double.MaxValue;
            for (int i = 0; i < _pairs.Count; i++)
            {
                double d = LuckyColors.Distance(_pairs[i].Back, to.Back);
                if (d < best) { best = d; _index = i; }
            }
        }

        internal void MoveForTesting(Keys key) => Step(key);
        internal void ClickForTesting(int index)
        {
            var cell = CellAt(index);
            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, cell.Left + 2, cell.Top + 2, 0));
        }

        private void Step(Keys key)
        {
            int to = key switch
            {
                Keys.Left => _index - 1,
                Keys.Right => _index + 1,
                Keys.Up => _index - _columns,
                Keys.Down => _index + _columns,
                Keys.Home => 0,
                Keys.End => _pairs.Count - 1,
                _ => _index
            };
            SelectAt(to, scroll: true);
        }

        /// <param name="scroll">Only when the keyboard moved the selection. A click has already landed on a
        /// cell the user could see, and the bottom row is usually the one straddling the edge - bringing
        /// that fully into view slides the whole grid down under the pointer.</param>
        private void SelectAt(int index, bool scroll)
        {
            if (_pairs.Count == 0) return;
            index = Math.Clamp(index, 0, _pairs.Count - 1);
            if (index == _index) return;
            Invalidate(CellAt(_index));
            _index = index;
            Invalidate(CellAt(_index));
            if (scroll) ScrollIntoView();
        }

        /// <summary>The parent scrolls whole controls, not parts of one, so the row has to be brought into
        /// view by hand.</summary>
        private void ScrollIntoView()
        {
            if (Parent is not ScrollableControl panel) return;
            var cell = CellAt(_index);
            int top = -panel.AutoScrollPosition.Y, bottom = top + panel.ClientSize.Height;
            int want = cell.Top < top ? cell.Top
                     : cell.Bottom > bottom ? cell.Bottom - panel.ClientSize.Height
                     : top;
            if (want != top) panel.AutoScrollPosition = new Point(0, want);
        }

        protected override bool IsInputKey(Keys keyData) => true;

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Enter && _pairs.Count > 0) { Chosen?.Invoke(); e.Handled = true; return; }
            Step(e.KeyCode);
            base.OnKeyDown(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            int col = e.X / _cellW, row = e.Y / _cellH;
            if (col >= 0 && col < _columns) SelectAt(row * _columns + col, scroll: false);
            base.OnMouseDown(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (_pairs.Count > 0) Chosen?.Invoke();
            base.OnMouseDoubleClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(SystemColors.Window);
            int first = Math.Max(0, e.ClipRectangle.Top / _cellH * _columns);
            int last = Math.Min(_pairs.Count - 1, (e.ClipRectangle.Bottom / _cellH + 1) * _columns - 1);

            for (int i = first; i <= last; i++)
            {
                var pair = _pairs[i];
                var cell = CellAt(i);
                var paint = Rectangle.Inflate(cell, -1, -1);   // a hairline apart, or two near colours merge

                using (var back = new SolidBrush(ToColor(pair.Back))) e.Graphics.FillRectangle(back, paint);
                TextRenderer.DrawText(e.Graphics, _sample, Font, Rectangle.Inflate(paint, -6, 0),
                                      ToColor(pair.Fore),
                                      TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                                      TextFormatFlags.NoPrefix);

                if (i != _index) continue;
                // Two frames: the swatch's own text colour always reads against it, and the system highlight
                // outside says which of them is chosen whatever colours happen to be on screen.
                using var inner = new Pen(ToColor(pair.Fore));
                using var outer = new Pen(SystemColors.Highlight, 2);
                e.Graphics.DrawRectangle(inner, Rectangle.Inflate(paint, -1, -1));
                e.Graphics.DrawRectangle(outer, Rectangle.Inflate(cell, -1, -1));
            }
        }

        private static Color ToColor(RgbColor c) => Color.FromArgb(c.R, c.G, c.B);
    }
}
