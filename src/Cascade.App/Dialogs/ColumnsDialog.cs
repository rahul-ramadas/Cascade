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
        // would otherwise keep - and a header cut in half is the first thing a reader notices. The rows go
        // the same way, or the descender of a "g" is shaved off every name in the list.
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
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
        // Opened big enough to hold the whole thing at once. Everything in here is read together - the
        // template, what it does to the line, and the fields it found - so a dialog that starts small
        // enough to need scrolling or dragging before any of that can be seen is starting in the wrong
        // place. Trimmed to the screen in FitToContent, which is where the real room is known.
        ClientSize = new Size(Dpi(1120), Dpi(820));
        MinimumSize = new Size(Dpi(720), Dpi(540));

        _template.Font = TemplateFont;

        Controls.Add(BuildRoot());
        BuildList();
        Wire();

        _template.Text = _working.Template;
        _asInline.Checked = _working.Layout == FieldLayout.Inline;
        _asColumns.Checked = !_asInline.Checked;

        Reparse();
        FillList();
        Refresh0();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        FitToContent();
        CentreOnScreen();
        // A dialog hands focus to its first field and Windows selects the lot; the caret belongs at the end,
        // which is where typing carries on from.
        _template.Focus();
        _template.Select(_template.TextLength, 0);
    }

    /// <summary>Sizes the dialog to what it wants and to what the screen has. The list is given room for
    /// every field it holds AND a few rows beyond them, so that adding a field shows the new row where it
    /// lands instead of somewhere below the bottom edge - and no more than that, so a short template does
    /// not open a window two thirds of which is empty list.</summary>
    private void FitToContent()
    {
        var room = Screen.FromControl(Owner ?? this).WorkingArea;
        int most = Math.Max(Dpi(300), room.Height - Dpi(16));
        int widest = Math.Max(Dpi(320), room.Width - Dpi(16));
        // Before the width is touched: a form will not go narrower than its own minimum, and on a screen
        // narrower than that minimum the dialog would hang off the side rather than fit.
        MinimumSize = new Size(Math.Min(MinimumSize.Width, widest), MinimumSize.Height);
        Width = Math.Min(Width, widest);
        for (int pass = 0; pass < 5; pass++)
        {
            PerformLayout();
            // Everything but the list is as tall as it needs to be, so the whole window is only ever the
            // rest of it plus however much list is asked for - and never taller than the screen, which on a
            // short one is less than the rest of it needs. Held to that either way round, and NOT with
            // Math.Clamp: a floor above the ceiling is not a range, and Clamp throws rather than picking.
            int least = Math.Min(Height - _list.ClientSize.Height + LeastListHeight, most);
            int want = Math.Min(most, Math.Max(least, Height + WantedListHeight - _list.ClientSize.Height));
            if (want == Height) break;
            Height = want;
        }
        PerformLayout();
        int spare = Math.Max(0, _list.ClientSize.Height - LeastListHeight);
        MinimumSize = new Size(MinimumSize.Width, Math.Min(most, Math.Max(Dpi(420), Height - spare)));
    }

    /// <summary>Puts the dialog in the middle of the screen the window that opened it is on. The middle of
    /// the SCREEN, not of that window: this dialog is wider than a lot of windows people keep a log in, and
    /// centring it on a small window only pushes it against the edge of the screen. Windows centres a dialog
    /// when it is SHOWN, which here is before it has been sized, so left alone it grows downward out of the
    /// middle and, on a short screen, off the bottom.</summary>
    private void CentreOnScreen()
    {
        var area = Screen.FromControl(Owner ?? this).WorkingArea;
        Location = new Point(
            Math.Clamp(area.Left + (area.Width - Width) / 2, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(area.Top + (area.Height - Height) / 2, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }

    /// <summary>Every field the template found, and three rows of slack under them: a field added from the
    /// sample appears at the end of the list, and a list already full to the last row would put it out of
    /// sight at the moment it was created. Read off the list itself, because a row is as tall as the font
    /// makes it, and capped so that a template with forty fields does not ask for a dialog taller than the
    /// screen - FitToContent trims it to the screen in any case.</summary>
    private int WantedListHeight => ListHeightFor(Math.Clamp(_list.Rows.Count + 3, 6, 14));

    /// <summary>The least the list may be dragged down to and still read as a list: four rows and a header.</summary>
    private int LeastListHeight => ListHeightFor(4);

    private int ListHeightFor(int rows)
        => _list.ColumnHeadersHeight + Dpi(4) +
           rows * Math.Max(1, _list.Rows.Count > 0 ? _list.Rows[0].Height : _list.RowTemplate.Height);

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

        // A worked example, then the four rules in a grid so the symbols line up under one another. Written
        // out as one wrapping paragraph it read as a run-on sentence with punctuation samples embedded in it.
        Row(Example(), SizeType.AutoSize, 0, 8);
        Row(Rules(
                ("*", "the part that changes"),
                ("{ }", "one field \u2014 moved and hidden whole, punctuation and all"),
                ("space", "any run of spaces"),
                ("\\", "the next character, exactly")), SizeType.AutoSize, 0, 4);
        Row(_status, SizeType.AutoSize, 0, 8);

        var nav = Flow(_previous, _next, Centred(_which, 10), Centred(_fit, 20), _nextMisfit, _makeColumn);
        Row(nav, SizeType.AutoSize, 0, 10);

        // AutoSize, not a height measured here and then frozen: this control is built before the dialog has
        // said what font it is being read in, so a fixed height was right at 9pt and cut the sample in half
        // at 12 - taking the field list with it.
        Row(_preview, SizeType.AutoSize, 0, 6);

        Row(Heading("&Layout"), SizeType.AutoSize, 0, 12);
        Row(LayoutChoices(), SizeType.AutoSize, 0, 2);

        Row(Heading("&Fields"), SizeType.AutoSize, 0, 12);
        _list.Margin = new Padding(0);
        Row(_list, SizeType.Percent, 100, 2);

        // Below the list, not beside it: stacked at the side they need a fixed height the row cannot always
        // spare, and at a large font they were pushed off the bottom of the dialog. The note beside them is
        // what tells anyone the rows can simply be dragged - a grip says a row is draggable to whoever has
        // already guessed, and this says it to whoever has not.
        _up.Margin = new Padding(0, 0, Dpi(6), 0);
        _down.Margin = new Padding(0);
        var dragNote = new Label { Text = "\u2026or drag a field up and down the list.", AutoSize = true, ForeColor = SystemColors.GrayText };
        Row(Flow(_up, _down, Centred(dragNote, 10)), SizeType.AutoSize, 0, 6);

        var buttons = OkCancelRow(out var ok, out _);
        _ok = ok;
        ok.Click += (_, _) => Apply();
        Row(buttons, SizeType.AutoSize, 0, 4);

        return root;
    }

    private Button? _ok;

    /// <summary>The two layouts, each with what it does written beside it. Both are on show at once rather
    /// than one line that describes whichever is ticked: the reason to read either of them is to decide
    /// between them, and a description that only appears once you have chosen is no help in choosing.</summary>
    private TableLayoutPanel LayoutChoices()
    {
        var grid = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 2,
            Margin = new Padding(0)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        void Choice(RadioButton button, string says)
        {
            button.Margin = new Padding(0, Dpi(2), Dpi(12), Dpi(2));
            // Top, not centred: once the sentence beside it wraps onto a second line, a centred button sits
            // between the two lines instead of against the one it belongs to.
            button.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            var told = new Label
            {
                Text = says,
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Anchor = AnchorStyles.Top | AnchorStyles.Left,
                Margin = new Padding(0, Dpi(2), 0, Dpi(2))
            };
            _wrapping.Add(told);
            grid.Controls.Add(button);
            grid.Controls.Add(told);
        }

        Choice(_asColumns, "A table: every field gets a column, lined up under a header you can drag.");
        Choice(_asInline, "Each row stays a line, shortened by whatever you have hidden. Best when one field is much longer than the rest.");
        return grid;
    }

    /// <summary>The sentences beside the two layouts, which are the longest text in the dialog and the only
    /// text that has to WRAP. A label sizes itself to one line however long that line is, so at a large font
    /// - or in a window dragged narrow - the end of the sentence simply left the dialog. Telling it how much
    /// room there is turns the same label into a wrapping one, and the row it sits in grows to suit.</summary>
    private readonly List<Label> _wrapping = [];

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        FitWrappingText();
    }

    private void FitWrappingText()
    {
        int aside = Math.Max(_asColumns.Width, _asInline.Width) + Dpi(12);
        var room = new Size(Math.Max(Dpi(200), ClientSize.Width - Dpi(24) - aside), 0);
        foreach (var label in _wrapping) label.MaximumSize = room;
    }

    /// <summary>A section heading. Bold, and rebuilt when the window's font changes - a font assigned to a
    /// control is what stops that control following the window, so anything given one has to be given the
    /// next one too.</summary>
    private Label Heading(string text)
    {
        var label = new Label { Text = text, AutoSize = true, Font = BoldFont };
        _headings.Add(label);
        return label;
    }

    private readonly List<Label> _headings = [];
    private Font? _bold;
    private Font BoldFont => _bold ??= new Font(Font, FontStyle.Bold);

    /// <summary>The one thing a reader has to be told, shown rather than described: a line they recognise,
    /// and the template that reads it. No sentence around it - the arrow says what it is, and every mark in
    /// it is spelled out in the list underneath.</summary>
    private FlowLayoutPanel Example()
    {
        var flow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = new Padding(0) };
        flow.Controls.Add(Bit("[12:03][INFO] hello", SystemColors.GrayText, true));
        flow.Controls.Add(Bit("\u2192", SystemColors.GrayText, false));
        flow.Controls.Add(Bit("{[*]}{[*]} {*}", SystemColors.ControlText, true));
        return flow;
    }

    /// <summary>What each mark means, in a grid so that the marks line up in a column of their own instead
    /// of being buried mid-sentence.</summary>
    private TableLayoutPanel Rules(params (string Symbol, string Meaning)[] rules)
    {
        var grid = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = rules.Length,
            Margin = new Padding(Dpi(2), 0, 0, 0)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (var (symbol, meaning) in rules)
        {
            var mark = Bit(symbol, SystemColors.ControlText, true);
            mark.Margin = new Padding(0, Dpi(1), Dpi(10), Dpi(1));
            mark.Anchor = AnchorStyles.Right;
            var says = Bit(meaning, SystemColors.GrayText, false);
            says.Margin = new Padding(0, Dpi(1), 0, Dpi(1));
            says.Anchor = AnchorStyles.Left;
            grid.Controls.Add(mark);
            grid.Controls.Add(says);
        }
        return grid;
    }

    /// <summary>One piece of the help text. The marks are set in the same fixed pitch as the template box,
    /// so that <c>{ }</c> on the page and <c>{ }</c> in the box are plainly the same thing.</summary>
    private Label Bit(string text, Color colour, bool mono)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = colour,
            Margin = new Padding(0, Dpi(2), Dpi(7), Dpi(2))
        };
        // Everything else is left to INHERIT the dialog's font: assigning it, even the same one, is what
        // stops a control following the window when the window is read at another size.
        if (mono) { label.Font = MonoFont; _monoBits.Add(label); }
        return label;
    }

    private readonly List<Label> _monoBits = [];
    private Font? _legendMono, _templateFont;

    private Font MonoFont => _legendMono ??= new Font("Consolas", Font.SizeInPoints);

    /// <summary>The two faces this dialog makes for itself are made from its own size, so they have to be
    /// made again when that changes - which it does when the window is dragged to a screen at another
    /// scaling. Left alone, the template and the marks explaining it stayed at the size the dialog was
    /// built at while every label around them grew.</summary>
    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        var (staleMono, staleTemplate, staleBold) = (_legendMono, _templateFont, _bold);
        _legendMono = _templateFont = _bold = null;
        _template.Font = TemplateFont;
        foreach (var bit in _monoBits) bit.Font = MonoFont;
        foreach (var heading in _headings) heading.Font = BoldFont;
        FitWrappingText();
        staleMono?.Dispose();
        staleTemplate?.Dispose();
        staleBold?.Dispose();
    }

    /// <summary>A touch larger than the dialog's own text: the template is the thing being written here, and
    /// every character of it has to be told apart from every other.</summary>
    private Font TemplateFont => _templateFont ??= new Font("Consolas", Font.SizeInPoints + 1f);

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

    /// <summary>The list of fields. A grip in the first cell says a row can be dragged, and the two columns
    /// whose meaning is not in their heading say it on the HEADING - not on the cells, which a DataGridView
    /// pops up as the selection is walked with the arrow keys, tipping a reader who is only moving about.</summary>
    private void BuildList()
    {
        _list.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "swatch", HeaderText = "", Width = GripWidth + Dpi(30), ReadOnly = true,
            Resizable = DataGridViewTriState.False, SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _list.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "name", HeaderText = "Name", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _list.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "show", HeaderText = "Show", Width = HeaderRoom("Show", 34), SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _list.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "width", HeaderText = "Width", Width = HeaderRoom("Width", 34), SortMode = DataGridViewColumnSortMode.NotSortable
        });
        var align = new DataGridViewComboBoxColumn
        {
            Name = "align", HeaderText = "Align", Width = HeaderRoom("Center", 40), FlatStyle = FlatStyle.Flat,
            SortMode = DataGridViewColumnSortMode.NotSortable
        };
        align.Items.AddRange([nameof(ColumnAlign.Left), nameof(ColumnAlign.Right), nameof(ColumnAlign.Center)]);
        _list.Columns.Add(align);

        _list.Columns["show"]!.HeaderCell.ToolTipText = "Untick to leave a field out of the row. Its punctuation goes with it.";
        _list.Columns["width"]!.HeaderCell.ToolTipText = "Pixels, or blank to fit whatever is in it. Only used by the Columns layout.";
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

        _asColumns.CheckedChanged += (_, _) => { if (_filling) return; _working.Layout = _asInline.Checked ? FieldLayout.Inline : FieldLayout.Columns; UpdateListEnabled(); Refresh0(); };

        _list.CellValueChanged += (_, e) => { if (!_filling && e.RowIndex >= 0) { PullFromList(); KeepOneShown(e.RowIndex); Refresh0(); } };
        _list.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_list.IsCurrentCellDirty && _list.CurrentCell is DataGridViewCheckBoxCell or DataGridViewComboBoxCell)
                _list.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _list.SelectionChanged += (_, _) => UpdateHighlight();
        _list.CellPainting += PaintSwatch;
        _list.DataError += (_, e) => e.ThrowException = false;
        _list.KeyDown += OnListKeyDown;
        WireDragging();

        _up.Click += (_, _) => Reorder(-1);
        _down.Click += (_, _) => Reorder(+1);
        _preview.PartPicked += SelectField;

        // Two tips, and both say something no label does. Everything else here explains itself where it
        // stands - the marks under the template box, the description beside each layout, the words on the
        // buttons - and a tip that repeats the thing it is pointing at only gets in the way of it.
        _tips.SetToolTip(_detect, "Read the [ ] groups off the line below and write a template for them.");
        _tips.SetToolTip(_makeColumn, "Adds a field for what is picked out in the sample, with the punctuation around it.");
    }

    // ---- the template ----

    /// <summary>F2 opens a cell for typing OVER, not for typing into. Renaming a field almost always means
    /// a new name rather than an edit to the old one, so the old one arrives selected and the first
    /// keystroke replaces it - which is what double-clicking a chip over the log has always done, and what
    /// a reader who has done it there will expect here.
    ///
    /// <para>A DataGridView only selects the contents on F2 in the EditOnF2 mode, and that mode gives up
    /// starting an edit when a letter is typed - so the behaviour is taken rather than the mode.</para></summary>
    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.F2 || e.Modifiers != Keys.None || _list.IsCurrentCellInEditMode) return;
        if (_list.CurrentCell is not DataGridViewTextBoxCell cell || cell.ReadOnly) return;
        e.Handled = true;
        _list.BeginEdit(selectAll: true);
    }

    /// <summary>Escape belongs to the innermost thing it can close, and while a name is being typed over
    /// that is the name - not the dialog. Left to the base, a reader correcting a typo and thinking better
    /// of it threw away every other change they had made as well.
    ///
    /// <para>It has to be caught here rather than left to the grid: a dialog key is offered up the parent
    /// chain before the key ever reaches the grid as a keystroke, so the dialog answered first.</para></summary>
    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape && _list.IsCurrentCellInEditMode)
        {
            _list.CancelEdit();
            _list.EndEdit();
            return true;
        }
        return base.ProcessDialogKey(keyData);
    }

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

    /// <summary>Whether there is anything to add a field FOR. Parts are built left to right, so what is
    /// picked out has to lie beyond the last one - a stretch inside a field the template already reads
    /// cannot become a field of its own.</summary>
    private void UpdateMakeColumn()
    {
        var (from, _) = _preview.Selection;
        if (from < 0) { _makeColumn.Enabled = false; return; }

        var match = new TemplateMatch();
        _working.Compiled.Match(Current, match);
        _makeColumn.Enabled = from >= match.TailStart;
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
        // The new field goes on the end of the list, which on a long template is past the bottom of it -
        // and a field that appears somewhere you cannot see looks like nothing happened. Only if there is a
        // whole row's worth of list to scroll, though: with none, "the first row on show" is not a thing
        // the grid will be told.
        if (_list.Rows.Count > 0)
        {
            _list.CurrentCell = _list.Rows[^1].Cells["name"];
            int shown = _list.DisplayedRowCount(false);
            if (shown > 0) _list.FirstDisplayedScrollingRowIndex = Math.Max(0, _list.Rows.Count - shown);
        }
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

    // ---- dragging a row up and down the list ----

    private int _dragFrom = -1, _dropAt = -1;
    private Point _grabbed;

    /// <summary>Room in the first cell for the grip, left of the colour.</summary>
    private int GripWidth => Dpi(14);

    /// <summary>Reordering by dragging, which is what anyone tries first on a list whose order matters. The
    /// buttons stay: a row is dragged with a mouse, and the same move has to be there for a keyboard.
    ///
    /// <para>The drag only starts once the pointer has actually travelled, so that a plain click still just
    /// picks a row, and it is refused while a cell is being typed in - a drag begun mid-edit would carry the
    /// row out from under the editor.</para></summary>
    private void WireDragging()
    {
        _list.AllowDrop = true;

        _list.MouseDown += (_, e) =>
        {
            var hit = _list.HitTest(e.X, e.Y);
            // Only from the grip and the name. A drag begun on the tick or the dropdown would swallow the
            // click that was meant to work them, and those two cells are the ones a pointer goes to in order
            // to CHANGE something rather than to move the row.
            bool grabbable = hit.ColumnIndex == _list.Columns["swatch"]!.Index ||
                             hit.ColumnIndex == _list.Columns["name"]!.Index;
            _dragFrom = e.Button == MouseButtons.Left && hit.RowIndex >= 0 && grabbable ? hit.RowIndex : -1;
            _grabbed = e.Location;
        };
        _list.MouseMove += (_, e) =>
        {
            if (_dragFrom < 0 || e.Button != MouseButtons.Left) return;
            var slack = SystemInformation.DragSize;
            if (Math.Abs(e.X - _grabbed.X) < slack.Width && Math.Abs(e.Y - _grabbed.Y) < slack.Height) return;
            int from = _dragFrom;
            _dragFrom = -1;
            // Whatever was being edited is finished with first: dropping rebuilds the rows, and a row pulled
            // out from under an open editor takes the editor with it. A ticked box counts as an edit, so
            // asking on the way DOWN whether one was open refused the drag after every tick.
            _list.EndEdit();
            // The line is taken down whatever happened. Escape, or a release over a window that is not a
            // drop target, tells OLE to call nothing at all - and the line drawn where the row was going
            // would then stay on the list for the rest of the dialog.
            try { _list.DoDragDrop(from, DragDropEffects.Move); }
            finally { if (_dropAt >= 0) { _dropAt = -1; _list.Invalidate(); } }
        };
        _list.MouseUp += (_, _) => _dragFrom = -1;

        _list.DragOver += OnListDragOver;
        _list.DragLeave += (_, _) => { _dropAt = -1; _list.Invalidate(); };
        _list.DragDrop += OnListDragDrop;
        // Drawn per row rather than from the Paint event: a DataGridView paints its cells over anything the
        // event puts down, so the line would be laid and then covered.
        _list.RowPostPaint += (_, e) => DrawDropLine(e.Graphics, e.RowIndex, e.RowBounds);
    }

    private void OnListDragOver(object? sender, DragEventArgs e)
    {
        ScrollTowardEdge(e);
        int at = DropRow(e);
        e.Effect = at < 0 ? DragDropEffects.None : DragDropEffects.Move;
        if (at == _dropAt) return;
        _dropAt = at;
        _list.Invalidate();
    }

    private void OnListDragDrop(object? sender, DragEventArgs e)
    {
        int to = DropRow(e);
        int from = e.Data?.GetData(typeof(int)) as int? ?? -1;
        _dropAt = -1;
        _list.Invalidate();
        MoveRow(from, to);
    }

    /// <summary>Walks the list along while a row is held near its top or bottom edge, so that a field can be
    /// carried past the rows on screen to one that is not. Nothing to do when every row is already on show -
    /// nor when NONE of them is, which is not the same thing: a grid squeezed shorter than one row refuses
    /// to be told which row comes first, and this runs on every DragOver.</summary>
    private void ScrollTowardEdge(DragEventArgs e)
    {
        int shown = _list.DisplayedRowCount(false);
        if (shown <= 0 || shown >= _list.Rows.Count) return;
        var at = _list.PointToClient(new Point(e.X, e.Y));
        int edge = Math.Max(Dpi(12), _list.Rows.Count > 0 ? _list.Rows[0].Height : Dpi(20));
        int first = _list.FirstDisplayedScrollingRowIndex;
        if (at.Y < _list.ColumnHeadersHeight + edge && first > 0)
            _list.FirstDisplayedScrollingRowIndex = first - 1;
        else if (at.Y > _list.ClientSize.Height - edge && first < _list.Rows.Count - 1)
            _list.FirstDisplayedScrollingRowIndex = first + 1;
    }

    /// <summary>Which place in the list the pointer is offering the row to: the gap ABOVE the row it is in
    /// the top half of, below it otherwise, so that dropping on the bottom half of the last row puts the
    /// field at the end rather than one short of it.</summary>
    private int DropRow(DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(typeof(int)) != true) return -1;
        var at = _list.PointToClient(new Point(e.X, e.Y));
        var hit = _list.HitTest(at.X, at.Y);
        if (hit.RowIndex < 0)
            return at.Y > _list.ColumnHeadersHeight && _list.Rows.Count > 0 ? _list.Rows.Count : -1;
        var box = _list.GetRowDisplayRectangle(hit.RowIndex, false);
        return at.Y > box.Top + box.Height / 2 ? hit.RowIndex + 1 : hit.RowIndex;
    }

    /// <summary>Takes a field out of the list and puts it back at <paramref name="before"/>, which counts
    /// the GAPS between rows - so lifting a row out first would shift every gap below it by one.</summary>
    private void MoveRow(int from, int before)
    {
        if (from < 0 || from >= _working.Columns.Count || before < 0) return;
        int to = before > from ? before - 1 : before;
        if (to == from || to < 0 || to >= _working.Columns.Count) return;
        var moved = _working.Columns[from];
        _working.Columns.RemoveAt(from);
        _working.Columns.Insert(to, moved);
        FillList();
        _list.CurrentCell = _list.Rows[to].Cells["name"];
        Refresh0();
    }

    /// <summary>The line saying where a dragged row would land. Drawn inside the row it belongs against -
    /// above that row, or along the bottom of the last one when the field is being carried to the end -
    /// because a row post-paint may only mark its own row.</summary>
    private void DrawDropLine(Graphics g, int row, Rectangle bounds)
    {
        if (_dropAt < 0) return;
        bool last = _dropAt >= _list.Rows.Count;
        if (last ? row != _list.Rows.Count - 1 : row != _dropAt) return;
        int thick = Math.Max(2, Dpi(2));
        int y = last ? bounds.Bottom - thick / 2 - 1 : bounds.Top + thick / 2;
        using var pen = new Pen(SystemColors.Highlight, thick);
        g.DrawLine(pen, bounds.Left, y, bounds.Right, y);
    }

    private void UpdateHighlight()
    {
        int row = _list.CurrentRow?.Index ?? -1;
        _preview.Highlight = row >= 0 && row < _working.Columns.Count ? _working.Columns[row].Source : -1;
        _up.Enabled = row > 0;
        _down.Enabled = row >= 0 && row < _working.Columns.Count - 1;
    }

    /// <summary>The first cell of a row: a grip saying the row can be dragged, and the colour that ties the
    /// row to a band in the sample above. Greyed out when the field is not being shown, which is the same
    /// thing the sample does to a band it is leaving out.</summary>
    private void PaintSwatch(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != _list.Columns["swatch"]!.Index) return;
        e.PaintBackground(e.CellBounds, true);
        if (e.RowIndex < _working.Columns.Count)
        {
            var column = _working.Columns[e.RowIndex];
            DrawGrip(e.Graphics!, new Rectangle(e.CellBounds.Left, e.CellBounds.Top, GripWidth, e.CellBounds.Height));
            var box = new Rectangle(e.CellBounds.Left + GripWidth + Dpi(4), e.CellBounds.Top + Dpi(5),
                                    e.CellBounds.Width - GripWidth - Dpi(10), e.CellBounds.Height - Dpi(10));
            using var brush = new SolidBrush(column.Visible ? ColumnsPreview.BandOf(column.Source) : SystemColors.ControlLight);
            e.Graphics!.FillRectangle(brush, box);
            using var pen = new Pen(SystemColors.ControlDark);
            e.Graphics.DrawRectangle(pen, box);
        }
        e.Handled = true;
    }

    /// <summary>Six dots, the mark every list that can be reordered by hand uses. Drawn on every row rather
    /// than only the one under the pointer: it is there to be noticed before anyone thinks to point at it.</summary>
    private void DrawGrip(Graphics g, Rectangle cell)
    {
        int dot = Math.Max(1, Dpi(2));
        int gap = dot * 2;
        int left = cell.Left + (cell.Width - (dot * 2 + gap)) / 2;
        int top = cell.Top + (cell.Height - (dot * 3 + gap * 2)) / 2;
        using var brush = new SolidBrush(SystemColors.ControlDark);
        for (int row = 0; row < 3; row++)
            for (int side = 0; side < 2; side++)
                g.FillRectangle(brush, left + side * (dot + gap), top + row * (dot + gap), dot, dot);
    }

    /// <summary>Brings the row for a part forward, so that pointing at a field in the sample or the result
    /// says which of the listed fields it is - the same tie the colours make, followed the other way.</summary>
    private void SelectField(int part)
    {
        int row = -1;
        for (int i = 0; i < _working.Columns.Count; i++) if (_working.Columns[i].Source == part) { row = i; break; }
        if (row < 0 || row >= _list.Rows.Count) return;
        _list.CurrentCell = _list.Rows[row].Cells["name"];
        // Said outright rather than left to the list's own SelectionChanged: that fires while the grid is
        // still moving its current cell, so reading the row back from it there gave the row BEFORE the one
        // just picked - and the band drawn round the old field stayed drawn round it.
        UpdateHighlight();
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
        if (disposing) { _tips.Dispose(); _legendMono?.Dispose(); _templateFont?.Dispose(); _bold?.Dispose(); }
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

    /// <summary>F2 on the list, and what the editor it opens is offering to type over.</summary>
    internal void PressF2ForTesting() => OnListKeyDown(_list, new KeyEventArgs(Keys.F2));
    internal bool IsRenamingForTesting => _list.IsCurrentCellInEditMode;
    internal string SelectedInEditorForTesting => (_list.EditingControl as TextBox)?.SelectedText ?? "";
    internal void TypeInEditorForTesting(string text)
    {
        if (_list.EditingControl is not TextBox box) return;
        box.SelectedText = text;
        _list.NotifyCurrentCellDirty(true);
    }
    /// <summary>Drives the dialog's own key handling, which is where Escape is decided.</summary>
    internal bool PressDialogKeyForTesting(Keys keys) => ProcessDialogKey(keys);

    /// <summary>Drops a field into the gap before <paramref name="before"/>, as a drag onto that gap does.</summary>
    internal void DropRowForTesting(int from, int before) => MoveRow(from, before);

    /// <summary>The real drag-over and drop the grid raises, driven at a point in the list, so that a check
    /// can hold a row over a place a mouse would hold it - including the edges, where the list scrolls.</summary>
    internal void DragOverForTesting(int clientX, int clientY) => RaiseDrag(OnListDragOver, clientX, clientY);
    internal void DropAtForTesting(int from, int clientX, int clientY) => RaiseDrag(OnListDragDrop, clientX, clientY, from);

    private void RaiseDrag(Action<object?, DragEventArgs> to, int clientX, int clientY, int carried = 0)
    {
        var at = _list.PointToScreen(new Point(clientX, clientY));
        to(_list, new DragEventArgs(new DataObject(carried), 0, at.X, at.Y, DragDropEffects.Move, DragDropEffects.None));
    }
    /// <summary>Leaves the insertion line where a drag would be showing it, so it can be looked at.</summary>
    internal void ShowDropLineForTesting(int before) { _dropAt = before; _list.Invalidate(); }
    internal void PickFieldForTesting(int part) => SelectField(part);
    internal int SelectedRowForTesting => _list.CurrentRow?.Index ?? -1;
    internal int ListRoomInRowsForTesting
        => _list.ClientSize.Height / Math.Max(1, _list.Rows.Count > 0 ? _list.Rows[0].Height : _list.RowTemplate.Height);
    internal void DetectForTesting() => Detect();
    internal void ApplyForTesting() => Apply();
    internal void SetLayoutForTesting(FieldLayout layout) { _asInline.Checked = layout == FieldLayout.Inline; _asColumns.Checked = !_asInline.Checked; }
    internal int RowCountForTesting => _list.Rows.Count;
    internal bool WidthIsEditableForTesting => !_list.Columns["width"]!.ReadOnly;

    /// <summary>Where the longest sentence on the dialog ends up, in the dialog's own coordinates. It is the
    /// only text here that has to WRAP - a label sizes itself to one line however long that line is - so
    /// where its right-hand edge lands is the thing worth checking.</summary>
    internal Rectangle LongestHelpForTesting
    {
        get
        {
            var label = _wrapping[^1];
            return RectangleToClient(label.Parent!.RectangleToScreen(label.Bounds));
        }
    }

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
