using System.Drawing;
using System.Windows.Forms;
using Cascade.Core.Columns;

namespace Cascade.App;

/// <summary>
/// Where a line is taught to split. The template is written as a picture of the line, and everything about
/// it is answered on the spot: which stretch of the line each part takes, what the row will look like once
/// the hidden parts are gone, how many of the lines on screen fit, and - when one does not - the character
/// at which it stopped fitting.
///
/// <para>Built as one root <see cref="TableLayoutPanel"/> with every child docked into its cell. No fixed
/// client size and no Dock mixed with AutoSize, so it lays out the same at any DPI and at any font size.</para>
/// </summary>
public sealed class ColumnsDialog : DialogBase
{
    private readonly ColumnSpec _working;
    private readonly IReadOnlyList<string> _samples;
    private int _sample;
    private bool _filling;

    private readonly TextBox _template = new() { Dock = DockStyle.Fill };
    private readonly Button _detect = new() { Text = "&Detect", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true };

    private readonly Button _previous = new() { Text = "◀", AutoSize = true, AccessibleName = "Previous sample line" };
    private readonly Button _next = new() { Text = "▶", AutoSize = true, AccessibleName = "Next sample line" };
    private readonly Label _which = new() { AutoSize = true };
    private readonly Label _fit = new() { AutoSize = true };
    private readonly Button _nextMisfit = new() { Text = "Next line that does not &match", AutoSize = true };
    private readonly Button _makeColumn = new() { Text = "&Add field", AutoSize = true, Enabled = false };

    private readonly ColumnsPreview _preview = new() { Dock = DockStyle.Fill };

    private readonly RadioButton _asColumns = new() { Text = "&Columns", AutoSize = true };
    private readonly RadioButton _asInline = new() { Text = "&Inline", AutoSize = true };
    private readonly Label _layoutHelp = new() { AutoSize = true, ForeColor = SystemColors.GrayText };

    private readonly DataGridView _list = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        RowHeadersVisible = false,
        BackgroundColor = SystemColors.Window,
        BorderStyle = BorderStyle.FixedSingle,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
        // The header is as tall as its own text needs, which at a large font is taller than the default it
        // would otherwise keep - and a header cut in half is the first thing a reader notices.
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
        AutoGenerateColumns = false
    };

    private readonly Button _up = new() { Text = "Move &up", AutoSize = true };
    private readonly Button _down = new() { Text = "Move d&own", AutoSize = true };
    private readonly ToolTip _tips = new() { AutoPopDelay = 20000, InitialDelay = 400, ReshowDelay = 100 };

    public ColumnSpec Result => _working;

    public ColumnsDialog(ColumnSpec spec, IReadOnlyList<string> samples, int startSample = 0)
    {
        ArgumentNullException.ThrowIfNull(spec);
        _working = spec.Clone();
        _samples = samples is { Count: > 0 } ? samples : [""];
        _sample = Math.Clamp(startSample, 0, _samples.Count - 1);

        Text = "Fields";
        AutoSize = false;
        AutoSizeMode = AutoSizeMode.GrowOnly;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        ClientSize = new Size(Dpi(940), Dpi(560));
        MinimumSize = new Size(Dpi(720), Dpi(540));

        _template.Font = new Font("Consolas", Font.SizeInPoints + 1f);

        Controls.Add(BuildRoot());
        BuildList();
        Wire();

        _template.Text = _working.Template;
        _asInline.Checked = _working.Layout == FieldLayout.Inline;
        _asColumns.Checked = !_asInline.Checked;
        UpdateLayoutHelp();

        Reparse();
        FillList();
        Refresh0();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        FitToContent();
        // A dialog hands focus to its first field and Windows selects the lot; the caret belongs at the end,
        // which is where typing carries on from.
        _template.Focus();
        _template.Select(_template.TextLength, 0);
    }

    /// <summary>Grows the dialog until the field list has room for a few rows, and makes that the smallest
    /// it can be dragged to. Worked out from what the list ACTUALLY got once laid out, so it holds at any
    /// font size and any DPI - a figure written down here would only ever be right at one of them.</summary>
    private void FitToContent()
    {
        int room = Screen.FromControl(this).WorkingArea.Height;
        for (int pass = 0; pass < 4; pass++)
        {
            PerformLayout();
            int wanted = WantedListHeight - _list.ClientSize.Height;
            if (wanted <= 0 || Height >= room) break;
            Height = Math.Min(Height + wanted, room);
        }
        PerformLayout();
        MinimumSize = new Size(MinimumSize.Width,
                               Height - Math.Max(0, _list.ClientSize.Height - WantedListHeight));
    }

    /// <summary>Four rows and the header: enough of the list to see that it IS a list and to drag a row
    /// about in it. Read off the list itself, because a row is as tall as the font makes it.</summary>
    private int WantedListHeight
        => _list.ColumnHeadersHeight + Dpi(4) +
           4 * Math.Max(1, _list.Rows.Count > 0 ? _list.Rows[0].Height : _list.RowTemplate.Height);

    // ---- layout ----

    private TableLayoutPanel BuildRoot()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(Dpi(12)),
            AutoSize = false
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Row(Control child, SizeType type, float value, int gapAbove = 8)
        {
            root.RowStyles.Add(new RowStyle(type, value));
            child.Dock = DockStyle.Fill;
            child.Margin = new Padding(0, Dpi(gapAbove), 0, 0);
            root.Controls.Add(child);
        }

        Row(Heading("&Template"), SizeType.AutoSize, 0, 0);

        var templateRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        templateRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        templateRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        templateRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _template.Margin = new Padding(0, 0, Dpi(6), 0);
        _detect.Margin = new Padding(0);
        templateRow.Controls.Add(_template, 0, 0);
        templateRow.Controls.Add(_detect, 1, 0);
        Row(templateRow, SizeType.AutoSize, 0, 3);

        // Each part of the legend is its own label in a wrapping row, so that a narrow dialog - or a large
        // font - breaks it BETWEEN the rules rather than through the middle of one.
        Row(Legend(SystemColors.ControlDarkDark,
                   "[12:03][INFO] hello   is   {[*]}{[*]} {*}",
                   "*  the text that changes, up to whatever you wrote next",
                   "{ }  one field, punctuation and all"), SizeType.AutoSize, 0, 6);
        Row(Legend(SystemColors.GrayText,
                   "A run of spaces matches any run of spaces.",
                   "\\{  \\}  \\*  \\\\  match those characters themselves."), SizeType.AutoSize, 0, 1);
        Row(_status, SizeType.AutoSize, 0, 6);

        var nav = Flow(_previous, _next, Centred(_which, 10), Centred(_fit, 20), _nextMisfit, _makeColumn);
        Row(nav, SizeType.AutoSize, 0, 10);

        // AutoSize, not a height measured here and then frozen: this control is built before the dialog has
        // said what font it is being read in, so a fixed height was right at 9pt and cut the sample in half
        // at 12 - taking the field list with it.
        Row(_preview, SizeType.AutoSize, 0, 6);

        Row(Heading("&Layout"), SizeType.AutoSize, 0, 12);
        Row(Flow(_asColumns, _asInline), SizeType.AutoSize, 0, 2);
        Row(_layoutHelp, SizeType.AutoSize, 0, 2);

        Row(Heading("&Fields"), SizeType.AutoSize, 0, 12);
        _list.Margin = new Padding(0);
        Row(_list, SizeType.Percent, 100, 2);

        // Below the list, not beside it: stacked at the side they need a fixed height the row cannot always
        // spare, and at a large font they were pushed off the bottom of the dialog.
        _up.Margin = new Padding(0, 0, Dpi(6), 0);
        _down.Margin = new Padding(0);
        Row(Flow(_up, _down), SizeType.AutoSize, 0, 6);

        var buttons = OkCancelRow(out var ok, out _);
        _ok = ok;
        ok.Click += (_, _) => Apply();
        Row(buttons, SizeType.AutoSize, 0, 4);

        return root;
    }

    private Button? _ok;

    private Label Heading(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font(Font, FontStyle.Bold)
    };

    /// <summary>A row of the syntax legend: one label per rule, wrapping between them.</summary>
    private FlowLayoutPanel Legend(Color colour, params string[] parts)
    {
        var flow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = new Padding(0) };
        foreach (string part in parts)
            flow.Controls.Add(new Label
            {
                Text = part,
                AutoSize = true,
                ForeColor = colour,
                Margin = new Padding(0, 0, Dpi(26), 0)
            });
        return flow;
    }

    /// <summary>Puts a label on the same line as the buttons beside it, whatever the font: anchored to
    /// nothing, a flow layout centres it in the row rather than hanging it from the top.</summary>
    private static Control Centred(Control child, int left)
    {
        child.Anchor = AnchorStyles.None;
        child.Margin = new Padding(child.Margin.Left + left, 0, child.Margin.Right, 0);
        return child;
    }

    private FlowLayoutPanel Flow(params Control[] children)
    {
        // Wrapping, so that a larger font pushes a row onto a second line instead of pushing its last
        // control off the side of the dialog.
        var flow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = new Padding(0) };
        foreach (var child in children)
        {
            if (child.Margin == new Padding(3)) child.Margin = new Padding(0, 0, Dpi(8), 0);
            flow.Controls.Add(child);
        }
        return flow;
    }

    private void BuildList()
    {
        _list.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "swatch", HeaderText = "", Width = Dpi(30), ReadOnly = true,
            Resizable = DataGridViewTriState.False, SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _list.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "name", HeaderText = "Name", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            ToolTipText = "What this field is called. Shown as the column header, and on the chip above the log."
        });
        _list.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "show", HeaderText = "Show", Width = HeaderRoom("Show", 34), SortMode = DataGridViewColumnSortMode.NotSortable,
            ToolTipText = "Untick to leave this field out. Its punctuation goes with it."
        });
        _list.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "width", HeaderText = "Width", Width = HeaderRoom("Width", 34), SortMode = DataGridViewColumnSortMode.NotSortable,
            ToolTipText = "Width in pixels for the Columns layout, or blank to size it to what is in it."
        });
        var align = new DataGridViewComboBoxColumn
        {
            Name = "align", HeaderText = "Align", Width = HeaderRoom("Center", 40), FlatStyle = FlatStyle.Flat,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            ToolTipText = "Which way the text sits in its column."
        };
        align.Items.AddRange([nameof(ColumnAlign.Left), nameof(ColumnAlign.Right), nameof(ColumnAlign.Center)]);
        _list.Columns.Add(align);
    }

    /// <summary>How wide a column has to be for its own heading to fit, with room for the tick or the
    /// dropdown arrow beside it. Measured rather than written down, because at 16pt "Width" is half again
    /// as wide as the figure that suited 9.</summary>
    private int HeaderRoom(string text, int extra)
        => TextRenderer.MeasureText(text, Font).Width + Dpi(extra);

    private void Wire()
    {
        _template.TextChanged += (_, _) => { Reparse(); FillListIfPartsChanged(); Refresh0(); };
        _detect.Click += (_, _) => Detect();

        _previous.Click += (_, _) => StepSample(-1);
        _next.Click += (_, _) => StepSample(+1);
        _nextMisfit.Click += (_, _) => StepToMisfit();
        _makeColumn.Click += (_, _) => MakeColumnFromSelection();
        _preview.SelectionChanged += UpdateMakeColumn;

        _asColumns.CheckedChanged += (_, _) => { if (_filling) return; _working.Layout = _asInline.Checked ? FieldLayout.Inline : FieldLayout.Columns; UpdateListEnabled(); UpdateLayoutHelp(); Refresh0(); };

        _list.CellValueChanged += (_, e) => { if (!_filling && e.RowIndex >= 0) { PullFromList(); KeepOneShown(e.RowIndex); Refresh0(); } };
        _list.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_list.IsCurrentCellDirty && _list.CurrentCell is DataGridViewCheckBoxCell or DataGridViewComboBoxCell)
                _list.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _list.SelectionChanged += (_, _) => UpdateHighlight();
        _list.CellPainting += PaintSwatch;
        _list.DataError += (_, e) => e.ThrowException = false;

        _up.Click += (_, _) => Reorder(-1);
        _down.Click += (_, _) => Reorder(+1);

        _tips.SetToolTip(_template, "A picture of your line. Replace what changes with *, and wrap each field in { }.");
        _tips.SetToolTip(_detect, "Read the [ ] groups off the line below and write a template for them.");
        _tips.SetToolTip(_nextMisfit, "Step to the next sampled line the template does not match.");
        _tips.SetToolTip(_makeColumn, "Drag across the sample line to pick out a field, then press this to add it.");
        _tips.SetToolTip(_asColumns, "A table: every field gets a column, lined up under a header you can drag.");
        _tips.SetToolTip(_asInline, "Each row stays a line, shortened by whatever you have hidden. Best when one field is much longer than the rest.");
        _tips.SetToolTip(_up, "Draw this field earlier in the row.");
        _tips.SetToolTip(_down, "Draw this field later in the row.");
    }

    // ---- the template ----

    /// <summary>Says what the chosen layout will do, in the place the choice was made - so the radio labels
    /// can stay short enough to sit on one line at any font size.</summary>
    private void UpdateLayoutHelp()
        => _layoutHelp.Text = _asInline.Checked
            ? "Each row stays a line, shortened by whatever you have hidden. Best when one field is much longer than the rest."
            : "A table: every field gets a column, lined up under a header you can drag.";

    private string Current => _samples[Math.Clamp(_sample, 0, _samples.Count - 1)];

    /// <summary>Re-reads the template. The caret says WHERE the edit was, so a part typed into the middle
    /// takes a new row at that place rather than shoving every name along by one.</summary>
    private void Reparse()
    {
        // Where the caret WILL be, when the dialog is the one writing: assigning Text puts the caret back to
        // the start before this is raised, and a part added at the end would then be taken for one added at
        // the front - handing every existing name to the part next door.
        int caret = _caretAfterEdit >= 0 ? _caretAfterEdit : _template.SelectionStart;
        _working.Template = _template.Text;
        _working.Sync(_working.Compiled.PartIndexAtOffset(caret));
        UpdateStatus();
    }

    private int _caretAfterEdit = -1;

    /// <summary>Writes the template on the reader's behalf, leaving the caret where they would have left it.</summary>
    private void WriteTemplate(string text, int caret)
    {
        _caretAfterEdit = Math.Clamp(caret, 0, text.Length);
        try { _template.Text = text; }
        finally { _caretAfterEdit = -1; }
        _template.SelectionStart = Math.Clamp(caret, 0, _template.TextLength);
    }

    private void UpdateStatus()
    {
        var template = _working.Compiled;
        if (template.Issues.Count > 0)
        {
            var first = template.Issues[0];
            _status.ForeColor = Color.FromArgb(192, 32, 32);
            _status.Text = $"✕   {first.Message}   (at character {first.Position})";
        }
        else if (template.PartCount == 0)
        {
            _status.ForeColor = SystemColors.GrayText;
            _status.Text = "Nothing is being split yet. Press Detect, or drag across the sample below and press "
                         + "\u201cAdd field\u201d for each field in turn.";
        }
        else
        {
            _status.ForeColor = Color.FromArgb(24, 112, 48);
            int quiet = template.PartCount - template.ValueCount;
            _status.Text = quiet == 0
                ? $"✓   {Count(template.PartCount, "field")}"
                : $"✓   {Count(template.ValueCount, "field")}, and {Count(quiet, "piece")} of fixed text";
        }
    }

    private static string Count(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";

    private void Detect()
    {
        string found = LineTemplate.Detect(Current);
        if (found.Length == 0)
        {
            _status.ForeColor = Color.FromArgb(180, 120, 0);
            _status.Text = "This line has no [ ] groups at its start to read. Step to another line, or drag "
                         + "across the sample below and press \u201cAdd field\u201d for each field in turn.";
            return;
        }
        WriteTemplate(found, found.Length);
        FillList();
        Refresh0();
    }

    // ---- the sample ----

    private void StepSample(int by)
    {
        _sample = (_sample + by + _samples.Count) % _samples.Count;
        Refresh0();
    }

    private void StepToMisfit()
    {
        var match = new TemplateMatch();
        for (int i = 1; i <= _samples.Count; i++)
        {
            int at = (_sample + i) % _samples.Count;
            if (!_working.Compiled.Match(_samples[at], match)) { _sample = at; Refresh0(); return; }
        }
    }

    private void UpdateMakeColumn()
    {
        var (from, to) = _preview.Selection;
        if (from < 0) { _makeColumn.Enabled = false; return; }

        var match = new TemplateMatch();
        _working.Compiled.Match(Current, match);
        // Parts are built left to right, so what is picked out has to lie beyond the last one.
        _makeColumn.Enabled = from >= match.TailStart;
        _tips.SetToolTip(_makeColumn, _makeColumn.Enabled
            ? "Add a field for what is picked out, with the punctuation around it."
            : "Pick out something after the last field the template reaches, and this will add a field for it.");
    }

    /// <summary>Adds a part for the stretch picked out in the sample: the text before it becomes the part's
    /// lead-in, and any closing punctuation straight after it comes along too - which is what makes hiding
    /// the part take its brackets with it. Whatever spaces stand in front of that lead-in are a SEPARATOR
    /// and are written outside the braces, so that they stay behind when the field is carried elsewhere.</summary>
    private void MakeColumnFromSelection()
    {
        var (from, to) = _preview.Selection;
        if (from < 0) return;

        string line = Current;
        var match = new TemplateMatch();
        _working.Compiled.Match(line, match);
        if (from < match.TailStart) return;

        const string closing = ")]}>\"'";
        int after = to;
        while (after < line.Length && closing.Contains(line[after], StringComparison.Ordinal)) after++;

        int leadFrom = match.TailStart;
        int leadIn = leadFrom;
        while (leadIn < from && line[leadIn] == ' ') leadIn++;

        string between = LineTemplate.Escape(line[leadFrom..leadIn]);
        string lead = LineTemplate.Escape(line[leadIn..from]);
        string trail = LineTemplate.Escape(line[to..after]);
        string added = $"{between}{{{lead}*{trail}}}";
        WriteTemplate(_template.Text + added, _template.TextLength + added.Length);
        _preview.ClearSelection();
        FillList();
        Refresh0();
    }

    // ---- the list ----

    private void FillListIfPartsChanged()
    {
        if (_list.Rows.Count != _working.Columns.Count) FillList();
    }

    private void FillList()
    {
        _filling = true;
        int keep = _list.CurrentRow?.Index ?? 0;
        _list.Rows.Clear();
        foreach (var column in _working.Columns)
            _list.Rows.Add("", column.Name, column.Visible,
                           column.Width == 0 ? "" : column.Width.ToString(), column.Align.ToString());
        if (_list.Rows.Count > 0)
            _list.CurrentCell = _list.Rows[Math.Clamp(keep, 0, _list.Rows.Count - 1)].Cells["name"];
        _filling = false;
        UpdateListEnabled();
        UpdateHighlight();
    }

    /// <summary>Width and alignment only mean anything to the Columns layout, so they are greyed rather
    /// than left looking as though they do nothing.</summary>
    private void UpdateListEnabled()
    {
        bool grid = !_asInline.Checked;
        _list.Columns["width"]!.ReadOnly = !grid;
        _list.Columns["align"]!.ReadOnly = !grid;
        var style = grid ? SystemColors.WindowText : SystemColors.GrayText;
        _list.Columns["width"]!.DefaultCellStyle.ForeColor = style;
        _list.Columns["align"]!.DefaultCellStyle.ForeColor = style;
        _list.Columns["width"]!.HeaderCell.Style.ForeColor = style;
        _list.Columns["align"]!.HeaderCell.Style.ForeColor = style;
        _list.Invalidate();
    }

    private void PullFromList()
    {
        for (int i = 0; i < _list.Rows.Count && i < _working.Columns.Count; i++)
        {
            var column = _working.Columns[i];
            var row = _list.Rows[i];
            string name = Convert.ToString(row.Cells["name"].Value)?.Trim() ?? "";
            if (name.Length > 0) column.Name = name;
            column.Visible = Convert.ToBoolean(row.Cells["show"].Value ?? true);

            string width = Convert.ToString(row.Cells["width"].Value)?.Trim() ?? "";
            int parsed = int.TryParse(width, out int w) && w > 0 ? w : 0;
            // Only when it was actually typed over: a width dragged in the header is kept in characters,
            // and rewriting it from the pixel figure shown would quietly undo that on every OK.
            if (parsed != column.Width) { column.Width = parsed; column.WidthChars = 0; }

            column.Align = Enum.TryParse<ColumnAlign>(Convert.ToString(row.Cells["align"].Value), out var a) ? a : ColumnAlign.Left;
        }
    }

    /// <summary>The last field standing cannot be put away: a row with nothing in it says nothing, and the
    /// chips above the log refuse it too. The tick springs back, and the reason is said out loud rather than
    /// left as a tick that would not take.</summary>
    private void KeepOneShown(int row)
    {
        if (_working.Columns.Count == 0 || _working.Columns.Any(c => c.Visible)) return;
        if (row < 0 || row >= _working.Columns.Count) return;

        _working.Columns[row].Visible = true;
        FillList();
        _status.ForeColor = Color.FromArgb(180, 120, 0);
        _status.Text = $"\u201c{_working.Columns[row].Name}\u201d is the only field left, so it cannot be "
                     + "left out too - a row has to show something.";
    }

    private void Reorder(int by)
    {
        int row = _list.CurrentRow?.Index ?? -1;
        int to = row + by;
        if (row < 0 || to < 0 || to >= _working.Columns.Count) return;
        (_working.Columns[row], _working.Columns[to]) = (_working.Columns[to], _working.Columns[row]);
        FillList();
        _list.CurrentCell = _list.Rows[to].Cells["name"];
        Refresh0();
    }

    private void UpdateHighlight()
    {
        int row = _list.CurrentRow?.Index ?? -1;
        _preview.Highlight = row >= 0 && row < _working.Columns.Count ? _working.Columns[row].Source : -1;
        _up.Enabled = row > 0;
        _down.Enabled = row >= 0 && row < _working.Columns.Count - 1;
    }

    private void PaintSwatch(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != _list.Columns["swatch"]!.Index) return;
        e.PaintBackground(e.CellBounds, true);
        if (e.RowIndex < _working.Columns.Count)
        {
            var column = _working.Columns[e.RowIndex];
            var box = new Rectangle(e.CellBounds.Left + Dpi(6), e.CellBounds.Top + Dpi(5),
                                    e.CellBounds.Width - Dpi(12), e.CellBounds.Height - Dpi(10));
            using var brush = new SolidBrush(column.Visible ? ColumnsPreview.BandOf(column.Source) : SystemColors.ControlLight);
            e.Graphics!.FillRectangle(brush, box);
            using var pen = new Pen(SystemColors.ControlDark);
            e.Graphics.DrawRectangle(pen, box);
        }
        e.Handled = true;
    }

    // ---- everything that has to be redone when anything changes ----

    private void Refresh0()
    {
        MeasureColumnWidths();
        _preview.ShowLine(Current, _working);

        var template = _working.Compiled;
        var match = new TemplateMatch();
        int fit = 0;
        foreach (var line in _samples) if (template.Match(line, match)) fit++;

        _which.Text = $"line {_sample + 1} of {_samples.Count}";
        // Nothing worth counting until the template reads: "0 of 200 match" would send the reader looking
        // at their log when the trouble is a missing brace.
        bool usable = template.IsValid && template.PartCount > 0;
        _fit.Text = !usable ? ""
            : fit == _samples.Count ? $"all {_samples.Count} sampled lines match"
            : $"{fit} of {_samples.Count} sampled lines match";
        _fit.ForeColor = fit == _samples.Count ? Color.FromArgb(24, 112, 48)
                       : fit == 0 ? Color.FromArgb(192, 32, 32) : Color.FromArgb(180, 120, 0);
        _nextMisfit.Enabled = usable && fit < _samples.Count;
        _previous.Enabled = _next.Enabled = _samples.Count > 1;
        if (_ok is not null) _ok.Enabled = template.IsValid;
        UpdateMakeColumn();
        UpdateHighlight();
    }

    /// <summary>How wide each field's column runs, in characters, so the Columns preview lines up the way
    /// the real table will: a width set by hand is what that column gets - shown in characters, since that
    /// is what the preview is laid out in - and one left to itself takes what the sampled lines need.</summary>
    private void MeasureColumnWidths()
    {
        _preview.ColumnWidths.Clear();
        _preview.FixedWidths.Clear();
        if (_working.Layout != FieldLayout.Columns) return;

        var template = _working.Compiled;
        if (!template.IsValid) return;
        var match = new TemplateMatch();
        foreach (var line in _samples)
        {
            if (!template.Match(line, match)) continue;
            foreach (var column in _working.Columns)
            {
                if (column.Source < 0 || column.Source >= template.PartCount) continue;
                int value = template.PartAt(column.Source).Value;
                if (value < 0) continue;
                int want = Math.Min(match.Value(value).Length, 60);
                _preview.ColumnWidths[column.Source] =
                    Math.Max(_preview.ColumnWidths.GetValueOrDefault(column.Source), want);
            }
        }

        // A width typed in pixels is shown as the characters it comes to, because the preview is laid out
        // in characters - which is also how a width is kept once dragged in a fixed-pitch font.
        foreach (var column in _working.Columns)
        {
            int chars = column.WidthChars > 0 ? column.WidthChars
                      : column.Width > 0 ? Math.Max(1, (int)Math.Round(column.Width / (double)_preview.CharWidth))
                      : 0;
            if (chars > 0) _preview.FixedWidths[column.Source] = Math.Min(chars, 120);
        }
    }

    private void Apply()
    {
        _list.EndEdit();
        PullFromList();
        _working.Template = _template.Text;
        _working.Layout = _asInline.Checked ? FieldLayout.Inline : FieldLayout.Columns;
        // Setting a template up and pressing OK plainly means "do it"; there is no separate switch to find.
        // Clearing it means the opposite, and turning it off again is Ctrl+Shift+C or the menu.
        _working.Enabled = _working.Compiled.IsValid && _working.Compiled.PartCount > 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }

    // ---- seams, so the dialog can be driven without a mouse ----

    internal void SetTemplateForTesting(string text) => WriteTemplate(text, text.Length);
    internal string TemplateForTesting => _template.Text;
    internal string StatusForTesting => _status.Text;
    internal string FitForTesting => _fit.Text;
    internal void SetCellForTesting(int row, string column, object? value) => _list.Rows[row].Cells[column].Value = value;
    internal void SelectRowForTesting(int row) => _list.CurrentCell = _list.Rows[row].Cells["name"];
    internal void MoveForTesting(int by) => Reorder(by);
    internal void DetectForTesting() => Detect();
    internal void ApplyForTesting() => Apply();
    internal void SetLayoutForTesting(FieldLayout layout) { _asInline.Checked = layout == FieldLayout.Inline; _asColumns.Checked = !_asInline.Checked; }
    internal int RowCountForTesting => _list.Rows.Count;
    internal bool WidthIsEditableForTesting => !_list.Columns["width"]!.ReadOnly;

    // ---- the sample, which is drawn rather than built out of controls ----

    internal void SelectSampleForTesting(int from, int to) => _preview.SelectForTesting(from, to);
    internal bool AddFieldEnabledForTesting => _makeColumn.Enabled;
    internal void AddFieldForTesting() => MakeColumnFromSelection();
    internal string ResultForTesting => _preview.ResultForTesting();
    internal void StepSampleForTesting(int by) => StepSample(by);
    internal void StepToMisfitForTesting() => StepToMisfit();
    internal string WhichSampleForTesting => _which.Text;
    internal ColumnsPreview PreviewForTesting => _preview;
    internal Control ListForTesting => _list;
}
