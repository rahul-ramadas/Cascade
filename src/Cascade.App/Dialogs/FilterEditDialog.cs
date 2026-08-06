using System.Drawing;
using System.Windows.Forms;
using Cascade.Core.Model;

namespace Cascade.App;

/// <summary>Create/edit a filter — laid out like the original TAT dialog: type, text, option flags,
/// text/background colors side by side, and bold/italic (each tri-state: on / off / inherit).</summary>
public sealed class FilterEditDialog : DialogBase
{
    private readonly Filter _filter;

    private readonly TextBox _description = new() { Dock = DockStyle.Fill };
    private readonly RadioButton _typeText = new() { Text = "&Matches text", AutoSize = true, Checked = true, Margin = new Padding(0, 3, 16, 3) };
    private readonly RadioButton _typeMarker = new() { Text = "Mar&ked by marker", AutoSize = true, Margin = new Padding(0, 3, 6, 3) };
    private readonly NumericUpDown _marker = new() { Minimum = 1, Maximum = 8 };
    /// <summary>As much of a pattern as the box will actually hold. A single-line TextBox silently drops
    /// anything past its MaxLength, so the limit is stated here rather than left to the default.</summary>
    internal const int MaxPatternLength = 32_000;

    private static readonly Font[] MonoFaces =
    [
        new("Consolas", 9.75f),
        new("Consolas", 9.75f, FontStyle.Bold),
        new("Consolas", 9.75f, FontStyle.Italic),
        new("Consolas", 9.75f, FontStyle.Bold | FontStyle.Italic),
    ];

    private static Font Mono => MonoFaces[0];

    /// <summary>The font the box is actually drawn in - not the field it is meant to come from, which would
    /// answer the same whatever the box had been given.</summary>
    internal Font FontForTesting => _text.Font;

    private readonly TextBox _text = new() { Dock = DockStyle.Fill, Font = Mono, MaxLength = MaxPatternLength };
    private readonly QuietCheckBox _regex = new() { Text = "&Regular expression", AutoSize = true, Margin = new Padding(0, 3, 24, 3) };
    private readonly QuietCheckBox _caseSensitive = new() { Text = "&Case sensitive", AutoSize = true, Margin = new Padding(0, 3, 24, 3) };
    private readonly QuietCheckBox _excluding = new() { Text = "&Excluding filter (hides matching lines)", AutoSize = true, Margin = new Padding(0, 3, 0, 3) };
    private readonly Label _note = new() { AutoSize = false, AutoEllipsis = true };

    /// <summary>What is inherited, in the place a bad pattern complains from. The two never both apply: a
    /// pattern that will not compile is the more urgent thing to say, and the note is always true anyway.
    /// </summary>
    private const string InheritNote =
        "Unchecked colors, and styles left neither on nor off, come from the parent filter.";

    // One scale across this row: 6 binds a tick to the swatch it owns, 24 separates one idea from the next.
    // The extra 6 on the tops is what centres a caption against the taller swatch buttons beside it.
    private readonly QuietCheckBox _setFore = new() { Text = "Text col&or", AutoSize = true, Margin = new Padding(0, 6, 6, 3) };
    private readonly Button _foreBtn = new() { FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 3, 0, 3) };
    private readonly QuietCheckBox _setBack = new() { Text = "&Background", AutoSize = true, Margin = new Padding(24, 6, 6, 3) };
    private readonly Button _backBtn = new() { FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 3, 0, 3) };
    private readonly Button _luckyBtn = new() { Text = "I'm feeling luck&y", AutoSize = true, Margin = new Padding(16, 3, 0, 3) };
    private readonly Button _chipsBtn = new() { Text = "&Paint chips\u2026", AutoSize = true, Margin = new Padding(6, 3, 0, 3) };
    private readonly QuietCheckBox _bold = new() { Text = "Bo&ld", AutoSize = true, ThreeState = true, Margin = new Padding(32, 6, 24, 3) };
    private readonly QuietCheckBox _italic = new() { Text = "&Italic", AutoSize = true, ThreeState = true, Margin = new Padding(0, 6, 0, 3) };

    private readonly IReadOnlyList<Filter> _siblings;
    /// <summary>Where this filter will hang, which decides what it inherits. A filter being added is not in
    /// the tree yet, so its own Parent is still null and the caller has to say.</summary>
    private readonly Filter? _parent;
    private readonly ResolvedStyle _defaults;
    private int _lucky = -1;

    private RgbColor _fore = new(0, 0, 0);
    private RgbColor _back = new(255, 255, 255);

    public FilterEditDialog(Filter filter, bool isNew) : this(filter, isNew, Array.Empty<Filter>()) { }

    /// <summary><paramref name="siblings"/> is every filter in the set, so a suggested colour can avoid the
    /// ones already in use. <paramref name="parent"/> and <paramref name="defaults"/> are what the preview
    /// falls back to for anything this filter does not set itself.</summary>
    public FilterEditDialog(Filter filter, bool isNew, IReadOnlyList<Filter> siblings,
                            Filter? parent = null, ResolvedStyle? defaults = null)
    {
        _filter = filter;
        _siblings = siblings;
        _parent = parent ?? filter.Parent;
        _defaults = defaults ?? new ResolvedStyle(ToRgb(SystemColors.WindowText), ToRgb(SystemColors.Window), false, false);
        Text = isNew ? "Add Filter" : "Edit Filter";

        // Accessible names so screen readers announce these fields (the visual labels aren't linked).
        _description.AccessibleName = "Filter description";
        _text.AccessibleName = "Filter text";

        _foreBtn.Size = new Size(Dpi(56), Dpi(23));
        _backBtn.Size = new Size(Dpi(56), Dpi(23));
        _marker.Width = Dpi(50);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Padding = new Padding(Dpi(14), Dpi(12), Dpi(14), Dpi(10))
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        int rows = 0;

        void Row(string label, Control field)
        {
            root.Controls.Add(FieldLabel(label), 0, rows);
            field.Margin = new Padding(0, Dpi(4), 0, Dpi(4));
            root.Controls.Add(field, 1, rows);
            rows++;
        }

        // Cells are given explicitly throughout: left to place things itself, a TableLayoutPanel deals its
        // cells out to the VISIBLE controls in order, so a control appearing would take the cell next to it
        // and shift everything along.
        static FlowLayoutPanel Strip(params Control[] items)
        {
            // Never wrapping: asked for its preferred height inside an auto-sizing column, a wrapping strip
            // answers for a narrower width than it is then given, so the row reserves space for lines that
            // are never drawn - which is where the dialog's dead band came from.
            var strip = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
            strip.Controls.AddRange(items);
            return strip;
        }

        Row("&Text:", _text);
        Row("Type:", Strip(_typeText, _typeMarker, _marker));
        Row("&Description:", _description);

        // All three options on one row: the excluding flag used to sit alone under a blank label, which
        // read as though it belonged to nothing.
        Row("Options:", Strip(_regex, _caseSensitive, _excluding));
        // Colour and style are one idea - what a matching line looks like - and the note below covers both.
        // Both buttons offer a colour, so they belong with the swatches rather than after the style ticks.
        Row("Appearance:", Strip(_setFore, _foreBtn, _setBack, _backBtn, _luckyBtn, _chipsBtn, _bold, _italic));

        // The error shares the button row rather than having one of its own: that row is as tall as the
        // buttons whatever else is in it, so a bad pattern cannot push them down the dialog.
        var buttons = OkCancelRow(out var ok, out _);
        buttons.Dock = DockStyle.None;
        buttons.Anchor = AnchorStyles.Right;    // centred against the error text, not pinned to the row's top
        buttons.Margin = new Padding(0);
        _note.Dock = DockStyle.Fill;
        _note.TextAlign = ContentAlignment.MiddleLeft;
        _note.Margin = new Padding(0, 0, Dpi(12), 0);
        _note.Height = Dpi(24);

        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, Dpi(12), 0, 0)
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(_note, 0, 0);
        bottom.Controls.Add(buttons, 1, 0);

        root.Controls.Add(bottom, 0, rows);
        root.SetColumnSpan(bottom, 2);

        Controls.Add(root);
        MinimumSize = new Size(Dpi(560), 0);

        _foreBtn.Click += (_, _) => PickColor(foreground: true);
        _backBtn.Click += (_, _) => PickColor(foreground: false);
        _luckyBtn.Click += (_, _) => FeelLucky();
        _chipsBtn.Click += (_, _) => ShowPalette();
        _setFore.CheckedChanged += (_, _) => UpdateColorButtons();
        _setBack.CheckedChanged += (_, _) => UpdateColorButtons();
        _bold.CheckStateChanged += (_, _) => UpdatePreview();
        _italic.CheckStateChanged += (_, _) => UpdatePreview();
        _typeText.CheckedChanged += (_, _) => UpdateTypeEnabled();
        _typeMarker.CheckedChanged += (_, _) => UpdateTypeEnabled();
        _text.TextChanged += (_, _) => ValidateRegex();
        _regex.CheckedChanged += (_, _) => ValidateRegex();
        ok.Click += (_, _) => Apply();

        LoadFromFilter();
    }

    /// <summary>Test seam: types into the pattern field, so a check can watch the error line appear.</summary>
    internal void SetTextForTesting(string text) => _text.Text = text;
    internal bool TextHasFocusForTesting => ActiveControl == _text;
    internal int PatternWidthForTesting => _text.Width;
    internal (int Start, int Length) TextSelectionForTesting => (_text.SelectionStart, _text.SelectionLength);
    internal string NoteForTesting => _note.Text;
    internal void FocusTextForTesting(int start, int length)
    {
        ActiveControl = _text;
        _text.SelectionStart = start;
        _text.SelectionLength = length;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);                    // DialogBase AutoSize has fit the form to its content
        Size naturalClient = ClientSize;   // content size, independent of the border style
        AutoSize = false;                  // AutoSize would pull the width straight back to the content

        // Wide by default: a filter is often started from a whole log line, and the point is to see all of
        // it at once. Never wider than the screen it opens on, though.
        int clientW = Owner is { } o ? (int)(o.Width * 0.9) : naturalClient.Width;
        clientW = Math.Max(clientW, Dpi(640));
        clientW = Math.Min(clientW, Screen.FromControl(this).WorkingArea.Width - Dpi(64));
        ClientSize = new Size(clientW, naturalClient.Height);

        if (Owner is { } owner)
            Location = new Point(owner.Left + Math.Max(0, (owner.Width - Width) / 2),
                                 owner.Top + Math.Max(0, (owner.Height - Height) / 2));

        ActiveControl = _text; // default focus in the (now first) Text field
    }

    private void LoadFromFilter()
    {
        _description.Text = _filter.Description;
        _typeMarker.Checked = _filter.Match.Type == FilterMatchType.Marker;
        _typeText.Checked = !_typeMarker.Checked;
        _marker.Value = Math.Clamp(_filter.Match.MarkerIndex + 1, 1, 8);
        _text.Text = _filter.Match.Text;
        _regex.Checked = _filter.Match.Regex;
        _caseSensitive.Checked = _filter.Match.CaseSensitive;
        _excluding.Checked = _filter.Kind == FilterKind.Exclude;

        if (_filter.Style.Foreground is { } fg) { _fore = fg; _setFore.Checked = true; }
        if (_filter.Style.Background is { } bg) { _back = bg; _setBack.Checked = true; }
        SeedLucky();
        _bold.CheckState = ToState(_filter.Style.Bold);
        _italic.CheckState = ToState(_filter.Style.Italic);

        UpdateColorButtons();
        UpdateTypeEnabled();
        ValidateRegex();
    }

    private static CheckState ToState(bool? v) => v is null ? CheckState.Indeterminate : v.Value ? CheckState.Checked : CheckState.Unchecked;
    private static bool? FromState(CheckState s) => s == CheckState.Indeterminate ? null : s == CheckState.Checked;

    private void UpdateTypeEnabled()
    {
        _marker.Enabled = _typeMarker.Checked;
        _text.Enabled = _regex.Enabled = _caseSensitive.Enabled = _typeText.Checked;
    }

    private void UpdateColorButtons()
    {
        _foreBtn.Enabled = _setFore.Checked;
        _backBtn.Enabled = _setBack.Checked;
        _foreBtn.BackColor = _setFore.Checked ? Color.FromArgb(_fore.R, _fore.G, _fore.B) : SystemColors.Control;
        _backBtn.BackColor = _setBack.Checked ? Color.FromArgb(_back.R, _back.G, _back.B) : SystemColors.Control;
        UpdatePreview();
    }

    /// <summary>Draws the pattern in the colours a line matching it would take, inheritance and all - which
    /// is the only place the effect of leaving a box unticked can actually be seen.</summary>
    private void UpdatePreview()
    {
        var inherited = _parent is null ? _defaults : StyleResolver.Resolve(_parent, _defaults);
        var fore = _setFore.Checked ? _fore : inherited.Foreground;
        var back = _setBack.Checked ? _back : inherited.Background;
        bool bold = _bold.CheckState is CheckState.Indeterminate ? inherited.Bold : _bold.Checked;
        bool italic = _italic.CheckState is CheckState.Indeterminate ? inherited.Italic : _italic.Checked;

        _text.ForeColor = Color.FromArgb(fore.R, fore.G, fore.B);
        _text.BackColor = Color.FromArgb(back.R, back.G, back.B);
        _text.Font = MonoFaces[(bold ? 1 : 0) | (italic ? 2 : 0)];
    }

    private static RgbColor ToRgb(Color c) => new(c.R, c.G, c.B);

    internal (Color Fore, Color Back, bool Bold, bool Italic) PreviewForTesting =>
        (_text.ForeColor, _text.BackColor, _text.Font.Bold, _text.Font.Italic);

    /// <summary>The system picker, with the pattern box following the colour as it is chosen. Cancelling
    /// puts back exactly what was there, including whether the box was ticked at all - picking a colour
    /// ticks it, so a cancelled pick must untick it again.</summary>
    private void PickColor(bool foreground)
    {
        var box = foreground ? _setFore : _setBack;
        RgbColor before = foreground ? _fore : _back;
        bool wasSet = box.Checked;

        void Show(RgbColor c)
        {
            if (foreground) _fore = c; else _back = c;
            box.Checked = true;
            UpdateColorButtons();
            _text.Update();   // the picker owns the message loop; nothing else will repaint this
        }

        using var dlg = new LiveColorDialog(Color.FromArgb(before.R, before.G, before.B));
        dlg.Previewing += c => Show(new RgbColor(c.R, c.G, c.B));

        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            Show(new RgbColor(dlg.Color.R, dlg.Color.G, dlg.Color.B));
            SeedLucky();
            return;
        }

        if (foreground) _fore = before; else _back = before;
        box.Checked = wasSet;
        UpdateColorButtons();
    }

    /// <summary>Test seam for the pick-and-revert path. The picker itself cannot be driven headlessly, but
    /// what surrounds it - preview while choosing, keep on OK, put back on cancel - is ordinary code.</summary>
    internal void PickColorForTesting(bool foreground, RgbColor previewed, RgbColor? accepted)
    {
        var box = foreground ? _setFore : _setBack;
        RgbColor before = foreground ? _fore : _back;
        bool wasSet = box.Checked;

        if (foreground) _fore = previewed; else _back = previewed;
        box.Checked = true;
        UpdateColorButtons();

        if (accepted is { } kept)
        {
            if (foreground) _fore = kept; else _back = kept;
            box.Checked = true;
        }
        else
        {
            if (foreground) _fore = before; else _back = before;
            box.Checked = wasSet;
        }
        UpdateColorButtons();
    }

    /// <summary>Offers a legible colour pair nobody else is using, and a different one on every press.</summary>
    private void FeelLucky()
    {
        _lucky = LuckyColors.Next(_lucky, _siblings, _filter);
        var pair = LuckyColors.At(_lucky);
        _back = pair.Back;
        _fore = pair.Fore;
        _setBack.Checked = true;
        _setFore.Checked = true;
        UpdateColorButtons();
    }

    /// <summary>Puts the walk where the colour on show already sits, so the next press moves off it. Called
    /// wherever a colour arrives from somewhere other than the button itself.</summary>
    private void SeedLucky() => _lucky = _setBack.Checked ? LuckyColors.IndexOf(_back) : -1;

    internal void FeelLuckyForTesting() => FeelLucky();
    internal void SaveForTesting() => Apply();
    internal (RgbColor Fore, RgbColor Back) ColorsForTesting => (_fore, _back);
    internal void SetStyleForTesting(bool? bold, bool? italic)
    {
        _bold.CheckState = ToState(bold);
        _italic.CheckState = ToState(italic);
    }
    internal void SetColorsForTesting(RgbColor? fore, RgbColor? back)
    {
        if (fore is { } f) _fore = f;
        if (back is { } b) _back = b;
        _setFore.Checked = fore is not null;
        _setBack.Checked = back is not null;
        UpdateColorButtons();
    }
    internal IReadOnlyList<LuckyColors.Pair> PaletteForTesting => LuckyColors.Free(_siblings, _filter);

    /// <summary>The whole ring of colours still going spare, shown as the filter's own text would look in
    /// each - the same offers the lucky button walks, but all at once and in any order.</summary>
    private void ShowPalette()
    {
        var free = LuckyColors.Free(_siblings, _filter);
        string pattern = _text.Text.Trim();
        var current = _setBack.Checked || _setFore.Checked ? new LuckyColors.Pair(_back, _fore) : (LuckyColors.Pair?)null;

        using var dlg = new PaletteDialog(free, pattern.Length > 0 ? pattern : "Sample text", current);
        if (dlg.ShowDialog(this) != DialogResult.OK || free.Count == 0) return;

        _back = dlg.Picked.Back;
        _fore = dlg.Picked.Fore;
        _setBack.Checked = true;
        _setFore.Checked = true;
        SeedLucky();
        UpdateColorButtons();
    }

    private void ValidateRegex()
    {
        string message = "";
        if (_typeText.Checked && _regex.Checked && _text.Text.Length > 0)
        {
            try { _ = System.Text.RegularExpressions.Regex.Match("", _text.Text); }
            catch (ArgumentException ex) { message = "Invalid regex: " + ex.Message; }
        }
        bool bad = message.Length > 0;
        _note.ForeColor = bad ? Color.Firebrick : Color.Gray;
        _note.Text = bad ? message : InheritNote;
    }

    private void Apply()
    {
        _filter.Description = _description.Text.Trim();
        if (_typeMarker.Checked)
        {
            _filter.Match.Type = FilterMatchType.Marker;
            _filter.Match.MarkerIndex = (int)_marker.Value - 1;
        }
        else
        {
            _filter.Match.Type = FilterMatchType.Text;
            _filter.Match.Text = _text.Text;
            _filter.Match.Regex = _regex.Checked;
            _filter.Match.CaseSensitive = _caseSensitive.Checked;
        }
        _filter.Kind = _excluding.Checked ? FilterKind.Exclude : FilterKind.Include;
        _filter.Style.Foreground = _setFore.Checked ? _fore : null;
        _filter.Style.Background = _setBack.Checked ? _back : null;
        _filter.Style.Bold = FromState(_bold.CheckState);
        _filter.Style.Italic = FromState(_italic.CheckState);
    }
}
