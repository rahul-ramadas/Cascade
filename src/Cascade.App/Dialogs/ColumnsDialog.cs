using System.Drawing;
using System.Windows.Forms;
using Cascade.Core.Columns;
using Cascade.Core.Timing;

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
        // Tab belongs to the DIALOG. Left alone a grid takes it for itself and walks the current cell along,
        // so tabbing out of a four-by-five list meant twenty presses; the cells are walked with the arrow
        // keys, which is what anyone reaches for in a grid anyway.
        StandardTab = true,
        // The header is as tall as its own text needs, which at a large font is taller than the default it
        // would otherwise keep - and a header cut in half is the first thing a reader notices. The rows go
        // the same way, or the descender of a "g" is shaved off every name in the list.
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
        ShowCellToolTips = false,
        AutoGenerateColumns = false
    };

    private readonly Button _up = new() { Text = "Move &up", AutoSize = true };
    private readonly Button _down = new() { Text = "Move d&own", AutoSize = true };
    private readonly ToolTip _tips = new() { AutoPopDelay = 20000, InitialDelay = 400, ReshowDelay = 100 };

    // No Alt key of its own: F, I, E and L are all claimed elsewhere in this dialog, and the "Tim&e"
    // heading directly above already lands on this box - a label's mnemonic moves focus to the next stop.
    private readonly Label _timeFieldLabel = new() { Text = "Field:", AutoSize = true };
    private readonly ComboBox _timeField = new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Time field" };
    private readonly Label _timeLabel = new() { Text = "Format:", AutoSize = true };
    private readonly ComboBox _timeFormat = new() { DropDownStyle = ComboBoxStyle.DropDown, AccessibleName = "Time format" };
    private readonly Button _guess = new() { Text = "&Guess", AutoSize = true };
    private readonly Label _timeStatus = new() { AutoSize = true };

    /// <summary>Everything on the dialog, in one column, inside the panel that scrolls it. Kept because how
    /// short the content may be drawn is what decides whether the dialog scrolls at all; see
    /// <see cref="GiveTheContentAFloor"/>.</summary>
    private readonly TableLayoutPanel _root;

    /// <summary>What the content sits in, so that a screen with less room than the dialog needs can be
    /// scrolled rather than losing the rows at the bottom - the OK button among them. A form will not
    /// scroll over a child docked to fill it, which is what the content otherwise is.</summary>
    private readonly Panel _scroll = new() { Dock = DockStyle.Fill, AutoScroll = true };

    /// <summary>The least the content may be drawn in and still show every row.</summary>
    private int _contentFloor;

    /// <summary>Set while the content is being resized, so that the panel laying itself out again does not
    /// come straight back round and do it a second time.</summary>
    private bool _sizingContent;

    public ColumnSpec Result => _working;

    /// <summary>What the log's own clock was found to be, when nobody has named a field. Shown so that the
    /// figures already on screen can be accounted for from here rather than taken on trust.</summary>
    private readonly LogClock? _found;

    /// <summary>The template and the switch as they were when the dialog opened, so that OK can tell a
    /// reader who wrote a template from one who came for something else - see <see cref="Apply"/>.</summary>
    private readonly string _templateAsOpened;
    private readonly bool _openedEnabled;

    public ColumnsDialog(ColumnSpec spec, IReadOnlyList<string> samples, int startSample = 0, LogClock? found = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        _working = spec.Clone();
        _found = found;
        _templateAsOpened = spec.Template;
        _openedEnabled = spec.Enabled;
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

        _root = BuildRoot();
        _root.Dock = DockStyle.Top;
        _scroll.Controls.Add(_root);
        _scroll.ClientSizeChanged += (_, _) => GiveTheContentAFloor();
        Controls.Add(_scroll);
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
        GiveTheContentAFloor();
    }

    /// <summary>Works out the least room the content can be drawn in and still show every row of itself,
    /// then gives it that much whatever the window has.
    ///
    /// <para>On a screen too short for everything on here, the window is the screen's height and the list -
    /// the one row that gives way - is squeezed to nothing, which leaves the rows BELOW it, the OK button
    /// among them, pushed off the bottom edge with no way to reach them: a dialog that cannot be answered.
    /// Held to what it needs instead, the content keeps its height and the panel around it scrolls.</para>
    ///
    /// <para>Added up row by row rather than read off the panel, because the panel is whatever height it was
    /// last given - and a figure worked out from that would grow every time it was applied. Every row but
    /// the list is as tall as it needs to be whatever room there is, so this is the same number at any window
    /// size. Twice, since a scroll bar takes width off the wrapping text, which may then want another line
    /// for it.</para></summary>
    private void GiveTheContentAFloor()
    {
        if (_sizingContent) return;
        _sizingContent = true;
        try
        {
            for (int pass = 0; pass < 3; pass++)
            {
                ApplyContentRoom();
                PerformLayout();
                int stack = _root.Padding.Vertical + _list.Margin.Vertical + LeastListHeight;
                foreach (Control child in _root.Controls)
                    if (child != _list) stack += child.Height + child.Margin.Vertical;
                if (stack == _contentFloor) return;
                _contentFloor = stack;
            }
            ApplyContentRoom();
        }
        finally { _sizingContent = false; }
    }

    /// <summary>Gives the content the room the window has, or the least it needs, whichever is more. More
    /// than the window has is what puts the scroll bar there.
    ///
    /// <para>The wrapping text is re-fitted first, because a scroll bar takes width off the content without
    /// the WINDOW changing size at all - and text measured before that appeared runs on under the bar.</para>
    ///
    /// <para>The panel is told to lay out again afterwards because it works out how far it scrolls from
    /// where its child ends, and it does not notice that on its own when the child is simply resized: left
    /// to itself it keeps the room the dialog wanted when it opened, and shows a scroll bar over content
    /// that has since been made to fit.</para></summary>
    private void ApplyContentRoom()
    {
        FitWrappingText();
        int height = Math.Max(_scroll.ClientSize.Height, _contentFloor);
        if (_root.Height != height) _root.Height = height;
        _scroll.PerformLayout();
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
        // Anchored sideways only, so the cell centres each of them: a button is several pixels taller than
        // the box beside it, and left to the default Top|Left they sit on visibly different lines.
        _template.Dock = DockStyle.None;
        _template.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _template.Margin = new Padding(0, 0, Dpi(6), 0);
        _detect.Anchor = AnchorStyles.Left;
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

        var nav = Flow(_previous, _next, Centred(_which, 10), Centred(_fit, 20), _nextMisfit);
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
        var dragNote = new Label { Text = "\u2026or drag a field up and down the list, or press Alt+\u2191 / Alt+\u2193.", AutoSize = true, ForeColor = SystemColors.GrayText };
        Row(Flow(_up, _down, Centred(dragNote, 10)), SizeType.AutoSize, 0, 6);

        Row(Heading("Tim&e"), SizeType.AutoSize, 0, 12);
        Row(TimeRow(), SizeType.AutoSize, 0, 2);
        Row(_timeStatus, SizeType.AutoSize, 0, 4);

        var buttons = OkCancelRow(out var ok, out _);
        _ok = ok;
        ok.Click += (_, _) => Apply();
        Row(buttons, SizeType.AutoSize, 0, 4);

        return root;
    }

    private Button? _ok;

    /// <summary>Which field holds the stamp, and how it is written. A LIST rather than a tick beside every
    /// field: only one field can be the time, and a column of tick boxes says the opposite of that.
    ///
    /// <para>The controls are always here and greyed when no field is named, rather than appearing with the
    /// choice: a row that came and went would shift the OK button under the pointer that had just used it.
    /// </para></summary>
    private TableLayoutPanel TimeRow()
    {
        _timeFormat.Items.AddRange(CommonFormats);
        foreach (var c in new Control[] { _timeFieldLabel, _timeField, _timeLabel, _timeFormat })
        {
            // Neither Top nor Bottom, so the cell centres it: a button is taller than the box beside it,
            // and top-anchored they sit on different lines.
            c.Anchor = AnchorStyles.Left;
            c.Margin = new Padding(0, 0, Dpi(6), 0);
        }
        _timeField.Anchor = AnchorStyles.Left;
        _timeField.Width = Dpi(260);
        _timeFormat.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _guess.Anchor = AnchorStyles.Left;
        _guess.Margin = new Padding(0);

        var row = new TableLayoutPanel { ColumnCount = 5, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        // A field name is a word; a format is a line of one. The rest of the row goes to the longer of them.
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        row.Controls.Add(_timeFieldLabel, 0, 0);
        row.Controls.Add(_timeField, 1, 0);
        row.Controls.Add(_timeLabel, 2, 0);
        row.Controls.Add(_timeFormat, 3, 0);
        row.Controls.Add(_guess, 4, 0);
        return row;
    }

    /// <summary>The shapes worth offering ready-made. Everything else is written in the same language, which
    /// is .NET's own - the one <c>DateTime.ToString</c> takes - so there is nothing new to learn.</summary>
    private static readonly string[] CommonFormats =
    [
        "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd'T'HH:mm:ss.fffffff", "yyyy-MM-dd HH:mm:ss",
        "HH:mm:ss.fff", "HH:mm:ss", "yyyy/MM/dd HH:mm:ss", "MM/dd/yyyy HH:mm:ss.fff",
        "dd/MM/yyyy HH:mm:ss.fff", "MMM d HH:mm:ss",
        "epoch:s", "epoch:ms", "epoch:us", "epoch:ns", "elapsed:s", "elapsed:ms"
    ];

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
        // The room inside the panel that scrolls, not the window: once a scroll bar is there it is the
        // panel that is narrower, and text measured against the window would wrap under the bar.
        int width = _scroll.ClientSize.Width > 0 ? _scroll.ClientSize.Width : ClientSize.Width;
        var room = new Size(Math.Max(Dpi(200), width - Dpi(24) - aside), 0);
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
        if (_list.Columns.Count > 0) MeasureListColumns();
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

    /// <summary>The list of fields. A grip in the first cell says a row can be dragged, and a width left
    /// empty says "auto" in the cell itself rather than in a tip - a tip on a cell is popped up as the
    /// selection is walked with the arrow keys, tipping a reader who is only moving about, and it is
    /// switched off here for that reason.</summary>
    private void BuildList()
    {
        _list.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "swatch", HeaderText = "", ReadOnly = true,
            Resizable = DataGridViewTriState.False, SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _list.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "name", HeaderText = "Name", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _list.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "show", HeaderText = "Show", SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _list.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "width", HeaderText = "Width (px)", SortMode = DataGridViewColumnSortMode.NotSortable
        });
        var align = new DataGridViewComboBoxColumn
        {
            Name = "align", HeaderText = "Align", FlatStyle = FlatStyle.Flat,
            SortMode = DataGridViewColumnSortMode.NotSortable
        };
        align.Items.AddRange([nameof(ColumnAlign.Left), nameof(ColumnAlign.Right), nameof(ColumnAlign.Center)]);
        _list.Columns.Add(align);
        MeasureListColumns();
    }

    /// <summary>How wide the fixed columns have to be for their own headings to fit. Measured again whenever
    /// the window's font changes, not once when it was built: a column sized for 9pt has its heading wrapped
    /// onto two lines at 16, and the dialog is read at whatever size the screen it is dragged to says.</summary>
    private void MeasureListColumns()
    {
        _list.Columns["swatch"]!.Width = GripWidth + Dpi(30);
        _list.Columns["show"]!.Width = HeaderRoom("Show", 34);
        _list.Columns["width"]!.Width = HeaderRoom("Width (px)", 34);
        _list.Columns["align"]!.Width = HeaderRoom("Center", 40);
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

        _asColumns.CheckedChanged += (_, _) => { if (_filling) return; _working.Layout = _asInline.Checked ? FieldLayout.Inline : FieldLayout.Columns; UpdateListEnabled(); Refresh0(); };

        _list.CellValueChanged += (_, e) =>
        {
            if (_filling || e.RowIndex < 0) return;
            PullFromList();
            KeepOneShown(e.RowIndex);
            // The first cell draws the field's colour, greyed when the field is not being shown - and the
            // grid has no reason to know that ticking one cell changed what another one draws.
            _list.InvalidateCell(_list.Columns["swatch"]!.Index, e.RowIndex);
            Refresh0();
        };
        _list.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_list.IsCurrentCellDirty && _list.CurrentCell is DataGridViewCheckBoxCell or DataGridViewComboBoxCell)
                _list.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _list.SelectionChanged += (_, _) => UpdateHighlight();
        _list.CellPainting += PaintCell;
        _list.DataError += (_, e) => e.ThrowException = false;
        _list.KeyDown += OnListKeyDown;
        WireDragging();

        _up.Click += (_, _) => Reorder(-1);
        _down.Click += (_, _) => Reorder(+1);
        _preview.PartPicked += SelectField;

        _timeField.SelectedIndexChanged += (_, _) => { if (!_filling) PickTimeField(_timeField.SelectedIndex - 1); };
        _timeFormat.TextChanged += (_, _) => { if (_filling) return; _working.TimeFormat = _timeFormat.Text.Trim(); ShowTimeStatus(); };
        _guess.Click += (_, _) => GuessTimeFormat(announce: true);

        // One tip, and it says something no label does: "Detect" alone gives no clue what it will read or
        // what it will do with it. Everything else here explains itself where it stands - the marks under
        // the template box, the description beside each layout, the words on the buttons - and a tip that
        // repeats the thing it is pointing at only gets in the way of it.
        _tips.SetToolTip(_detect, DetectSays);
    }

    // ---- the template ----

    /// <summary>F2 opens a cell for typing OVER, not for typing into. Renaming a field almost always means
    /// a new name rather than an edit to the old one, so the old one arrives selected and the first
    /// keystroke replaces it - which is what double-clicking a chip over the log has always done, and what
    /// a reader who has done it there will expect here. Alt with an arrow carries the row, as it does in the
    /// filter list.
    ///
    /// <para>A DataGridView only selects the contents on F2 in the EditOnF2 mode, and that mode gives up
    /// starting an edit when a letter is typed - so the behaviour is taken rather than the mode.</para></summary>
    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        // The same keys the filter list is reordered with, so the one gesture is learned once. The buttons
        // say "Move up" and "Move down" with Alt keys of their own; these are for hands already in the list.
        if (e.Alt && e.KeyCode is Keys.Up or Keys.Down)
        {
            e.Handled = e.SuppressKeyPress = true;
            Reorder(e.KeyCode == Keys.Up ? -1 : +1);
            return;
        }
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
        // What the time field holds changes with the template even when the number of fields does not -
        // "{[*]}{[*]}{*}" edited to "{(*)}{[*]}{*}" reads a different stretch of the line out of the same
        // part - so what the format makes of it has to be worked out again rather than left as it was.
        ShowTimeStatus();
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
            _status.Text = "Nothing is being split yet. Press Detect, or write the template above.";
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

    /// <summary>What Detect will and will not read, said on the button rather than found out by pressing
    /// it: a header held together by brackets is a reading, and one held together by spaces is a guess.</summary>
    private const string DetectSays =
        "Reads a header of bracketed groups - [ ], ( ) or < > - off the start of the line below, "
        + "with anything before the first bracket as a field of its own. A header separated by nothing but "
        + "spaces has to be written by hand.";

    private void Detect()
    {
        string found = LineTemplate.Detect(Current);
        if (found.Length == 0)
        {
            _status.ForeColor = Color.FromArgb(180, 120, 0);
            _status.Text = "There are no bracketed groups at the start of this line to read. Step to another "
                         + "line, or write the template above - the marks it is made of are listed there.";
            return;
        }
        WriteTemplate(found, found.Length);
        // A header nearly always begins with the time, and a reader who pressed Detect wants the whole
        // reading rather than the fields alone. Proposed, not imposed: it lands in the field list and the
        // format box where it can be seen and changed.
        if (ClockDetector.GuessField(_samples, _working.Compiled) is { } time)
        {
            _working.TimePart = time.Part;
            _working.TimeFormat = time.Format;
        }
        FillList();
        Refresh0();
    }

    // ---- the time field ----

    /// <summary>Proposes what reads the ticked field, judged across every sample line rather than the one
    /// on show. Silent when it is filling the box on the reader's behalf, and outspoken when they asked.
    /// </summary>
    private void GuessTimeFormat(bool announce)
    {
        string? found = _working.TimePart < 0
            ? null
            : ClockDetector.GuessFormat(_samples, _working.Compiled, _working.TimePart);

        if (found is not null)
        {
            _working.TimeFormat = found;
            _filling = true;
            try { _timeFormat.Text = found; } finally { _filling = false; }
        }
        else if (announce)
        {
            _timeStatus.ForeColor = Color.FromArgb(180, 120, 0);
            _timeStatus.Text = "That field is not written in a shape this recognises. Write the format above "
                             + "- it is the same language as DateTime.ToString, e.g. yyyy-MM-dd HH:mm:ss.fff.";
            return;
        }
        ShowTimeStatus();
    }

    /// <summary>What the format makes of the reader's own log, in their own words: how many of the sampled
    /// lines it read, and one of them echoed back as a moment. That echo is the whole safety of the thing -
    /// a proposal you can see reading your log correctly is not a guess you have to trust.</summary>
    private void ShowTimeStatus()
    {
        if (_working.TimePart < 0)
        {
            _timeStatus.ForeColor = SystemColors.GrayText;
            // Where the figures on screen are coming from when nobody has said, which is the answer to
            // "what is that column measuring" without having to go looking for it.
            _timeStatus.Text = _found is null
                ? "No field is the timestamp, and none could be found at the start of the line - so there "
                  + "are no elapsed times. Name the field the stamp is in."
                : $"No field is the timestamp. Elapsed times are being read from the start of the line as "
                  + $"\u201c{_found.Format.Source}\u201d; name a field to say otherwise.";
            return;
        }

        if (_working.TimeFormat.Length == 0)
        {
            _timeStatus.ForeColor = SystemColors.GrayText;
            _timeStatus.Text = "Press Guess, or choose a format above.";
            return;
        }

        if (ClockFormat.Compile(_working.TimeFormat) is null)
        {
            _timeStatus.ForeColor = Color.FromArgb(192, 32, 32);
            _timeStatus.Text = $"\u2715   \u201c{_working.TimeFormat}\u201d is not a format anything could be read with.";
            return;
        }

        var clock = LogClock.From(_working);
        if (clock is null)
        {
            _timeStatus.ForeColor = Color.FromArgb(192, 32, 32);
            _timeStatus.Text = "\u2715   That field holds no text of its own to read a time out of.";
            return;
        }

        var (read, total) = ClockDetector.Coverage(clock, _samples);
        if (read == 0)
        {
            _timeStatus.ForeColor = Color.FromArgb(192, 32, 32);
            _timeStatus.Text = $"\u2715   None of the {total} lines below could be read with that format.";
            return;
        }

        clock.TryRead(FirstReadable(clock), out long ticks);
        _timeStatus.ForeColor = read == total ? Color.FromArgb(24, 112, 48) : Color.FromArgb(180, 120, 0);
        _timeStatus.Text = $"\u2713   {read:N0} of {total:N0} lines read \u00b7 {Canonical(clock, ticks)}";
    }

    private string FirstReadable(LogClock clock)
    {
        foreach (string line in _samples) if (clock.TryRead(line, out _)) return line;
        return "";
    }

    /// <summary>A moment written out plainly, so that a stamp read wrongly - a day where a month was meant,
    /// a fraction taken for seconds - is obvious rather than merely numeric. To the log's own precision and
    /// no further, or the echo claims accuracy the log never had.</summary>
    private static string Canonical(LogClock clock, long ticks)
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        if (clock.Format.Source.StartsWith("elapsed", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.FromTicks(ticks).ToString("g", culture) + " in";

        string fraction = clock.FractionDigits > 0 ? "." + new string('f', clock.FractionDigits) : "";
        string shape = (clock.Format.WrapsAtMidnight ? "HH:mm:ss" : "dd MMM yyyy HH:mm:ss") + fraction;
        return new DateTime(ticks, DateTimeKind.Utc).ToString(shape, culture);
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
        FillTimeField();
        _timeFormat.Text = _working.TimeFormat;
        _filling = false;
        UpdateListEnabled();
        UpdateHighlight();
        ShowTimeStatus();
    }

    /// <summary>The fields on offer as the timestamp, by the names they are listed under. Caller holds
    /// <see cref="_filling"/>: refilling a ComboBox resets its selection, which would read as a choice.
    /// </summary>
    private void FillTimeField()
    {
        _timeField.Items.Clear();
        _timeField.Items.Add(NoTimeField);
        foreach (var column in _working.Columns) _timeField.Items.Add(column.Name);

        int at = -1;
        for (int i = 0; i < _working.Columns.Count; i++)
            if (_working.Columns[i].Source == _working.TimePart) { at = i; break; }
        _timeField.SelectedIndex = at + 1;
    }

    private const string NoTimeField = "(none)";

    /// <summary>Width and alignment only mean anything to the Columns layout, so they are greyed rather
    /// than left looking as though they do nothing. The format goes the same way when no field is the time.
    /// </summary>
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

        bool timed = _working.TimePart >= 0;
        _timeLabel.Enabled = _timeFormat.Enabled = _guess.Enabled = timed;
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

    /// <summary>Naming a field as the timestamp, by its row in the list. Naming a NEW one also proposes a
    /// format for it: a field named as the time with nothing that reads it is a half-finished answer.
    /// </summary>
    private void PickTimeField(int row)
    {
        int now = row >= 0 && row < _working.Columns.Count ? _working.Columns[row].Source : -1;
        if (now == _working.TimePart) return;

        _working.TimePart = now;
        if (now < 0) _working.TimeFormat = "";
        else GuessTimeFormat(announce: false);
        FillList();
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
    /// <summary>The cells this dialog draws itself: the first one, which carries the grip and the colour,
    /// and a width nobody has set, which says "auto" rather than sitting empty.</summary>
    private void PaintCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0) return;
        if (e.ColumnIndex == _list.Columns["width"]!.Index) { PaintWidth(e); return; }
        if (e.ColumnIndex != _list.Columns["swatch"]!.Index) return;
        _swatchPaints.Add(e.RowIndex);
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

    /// <summary>A width nobody has set says so, in the cell, in grey: "auto". What an empty box means is
    /// exactly the sort of thing that used to be explained in a tip nobody reads until they are already
    /// puzzled - written where the answer is wanted, it needs no explaining at all. Typing over it works as
    /// it always did, because there is nothing there to type over.
    ///
    /// <para>Drawn half way between the ink and the paper of whatever state the cell is in, so it reads as
    /// a placeholder rather than a value on a plain row, on the selected row, and while the column is greyed
    /// out for the Inline layout alike.</para></summary>
    private void PaintWidth(DataGridViewCellPaintingEventArgs e)
    {
        if (Convert.ToString(e.Value)?.Trim() is { Length: > 0 }) return;
        e.PaintBackground(e.CellBounds, true);
        var style = e.CellStyle!;
        bool picked = e.State.HasFlag(DataGridViewElementStates.Selected);
        var ink = picked ? style.SelectionForeColor : style.ForeColor;
        var paper = picked ? style.SelectionBackColor : style.BackColor;
        TextRenderer.DrawText(e.Graphics!, "auto", style.Font ?? Font, e.CellBounds,
            Color.FromArgb((ink.R + paper.R) / 2, (ink.G + paper.G) / 2, (ink.B + paper.B) / 2),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
            TextFormatFlags.EndEllipsis | TextFormatFlags.LeftAndRightPadding);
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
        // Writing a template and pressing OK plainly means "do it"; there is no separate switch to find.
        // Clearing it means the opposite, and turning it off again is Ctrl+Shift+C or the menu.
        // But only the TEMPLATE says that. Someone who came here to say where the timestamp is has asked
        // for elapsed times, not for their log to be laid out as a table - and the time is read either way.
        bool wrote = !string.Equals(_template.Text, _templateAsOpened, StringComparison.Ordinal);
        _working.Enabled = (_openedEnabled || wrote)
                        && _working.Compiled.IsValid && _working.Compiled.PartCount > 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tips.Dispose(); _legendMono?.Dispose(); _templateFont?.Dispose(); _bold?.Dispose(); }
        base.Dispose(disposing);
    }

    // ---- harness seams ----

    /// <summary>Names a listed field as the timestamp through the same handler a choice goes through, so
    /// what a check exercises is the wiring rather than the field behind it.</summary>
    internal void PickTimeFieldForTesting(int row) => _timeField.SelectedIndex = row + 1;

    /// <summary>Which rows have had their first cell drawn since a check last asked. Whether the COLOUR is
    /// right can be read off a render, but a render forces the whole grid to paint - so the only way to see
    /// that a cell was left stale is to watch what the grid was asked to draw.</summary>
    private readonly List<int> _swatchPaints = [];

    internal void ForgetSwatchPaintsForTesting() => _swatchPaints.Clear();

    internal int[] SwatchPaintsForTesting()
    {
        _list.Update();   // whatever is invalid, and nothing else
        return [.. _swatchPaints];
    }

    internal string TimeFieldForTesting => Convert.ToString(_timeField.SelectedItem) ?? "";
    internal string TimeStatusForTesting => _timeStatus.Text;
    internal string TimeFormatForTesting => _timeFormat.Text;

    // ---- seams, so the dialog can be driven without a mouse ----

    internal void SetTemplateForTesting(string text) => WriteTemplate(text, text.Length);

    /// <summary>The panel the content scrolls inside, so a check can confirm that whatever a short screen
    /// leaves off the bottom can still be reached rather than being lost.</summary>
    internal ScrollableControl ContentScrollForTesting => _scroll;

    internal string ContentFloorForTesting
        => $"floor {_contentFloor}, root {_root.Height}, list {_list.Height}, least {LeastListHeight}, " +
           $"rows {string.Join('+', _root.Controls.Cast<Control>().Select(c => c == _list ? $"[list {c.Height}]" : $"{c.Height}"))}";

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

    /// <summary>Drives a key at the field list, as a hand already in it would.</summary>
    internal void PressListKeyForTesting(Keys keys) => OnListKeyDown(_list, new KeyEventArgs(keys));

    /// <summary>Whether Tab walks out of the field list rather than along its cells. The walk below cannot
    /// see this - a grid reports itself as one control either way, and then takes the key at run time.</summary>
    internal bool TabLeavesListForTesting => _list.StandardTab;

    /// <summary>Everywhere Tab stops, in the order it reaches them and named as a reader would name them.
    /// Anything disabled or hidden is left out, because Tab passes over it.</summary>
    internal string[] TabStopsForTesting
    {
        get
        {
            var stops = new List<string>();
            Control? at = this;
            for (int i = 0; i < 200; i++)
            {
                at = GetNextControl(at, true);
                if (at is null) break;
                if (at.TabStop && at.CanSelect) stops.Add(NameOf(at));
            }
            return [.. stops];
        }
    }

    private string NameOf(Control c)
        => ReferenceEquals(c, _template) ? "template"
         : ReferenceEquals(c, _list) ? "fields"
         : c.AccessibleName is { Length: > 0 } named ? named
         : (c.Text ?? "").Replace("&", "");

    /// <summary>Every Alt key the dialog claims, so that no two of them claim the same letter.</summary>
    internal string[] MnemonicsForTesting
    {
        get
        {
            var claimed = new List<string>();
            void Walk(Control parent)
            {
                foreach (Control c in parent.Controls)
                {
                    string text = c.Text ?? "";
                    int at = text.IndexOf('&', StringComparison.Ordinal);
                    if (at >= 0 && at + 1 < text.Length && text[at + 1] != '&')
                        claimed.Add($"{char.ToLowerInvariant(text[at + 1])}:{text.Replace("&", "")}");
                    Walk(c);
                }
            }
            Walk(this);
            return [.. claimed];
        }
    }

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

    /// <summary>The two rows that mix a button with a box beside it, which is where they can end up on
    /// different lines: a button is several pixels taller, and a cell anchored Top puts them both at the
    /// top of it rather than through the middle.</summary>
    internal Control[] MixedRowsForTesting => [_detect.Parent!, _guess.Parent!];
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

    internal void SelectSampleForTesting(int index) => _preview.ClickSampleForTesting(index);
    internal string ResultForTesting => _preview.ResultForTesting();
    internal void StepSampleForTesting(int by) => StepSample(by);
    internal void StepToMisfitForTesting() => StepToMisfit();
    internal string WhichSampleForTesting => _which.Text;
    internal ColumnsPreview PreviewForTesting => _preview;
    internal Control ListForTesting => _list;
}
