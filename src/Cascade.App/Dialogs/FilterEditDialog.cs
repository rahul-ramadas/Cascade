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

    private static readonly Font Mono = new("Consolas", 9.75f);

    /// <summary>The font the box is actually drawn in - not the field it is meant to come from, which would
    /// answer the same whatever the box had been given.</summary>
    internal Font FontForTesting => _text.Font;

    private readonly TextBox _text = new() { Dock = DockStyle.Fill, Font = Mono, MaxLength = MaxPatternLength };
    private readonly CheckBox _regex = new() { Text = "&Regular expression", AutoSize = true, Margin = new Padding(0, 3, 24, 3) };
    private readonly CheckBox _caseSensitive = new() { Text = "&Case sensitive", AutoSize = true, Margin = new Padding(0, 3, 24, 3) };
    private readonly CheckBox _excluding = new() { Text = "&Excluding filter (hides matching lines)", AutoSize = true, Margin = new Padding(0, 3, 0, 3) };
    private readonly Label _regexError = new() { ForeColor = Color.Firebrick, AutoSize = false, AutoEllipsis = true };

    private readonly CheckBox _setFore = new() { Text = "Text col&or", AutoSize = true, Margin = new Padding(0, 4, 6, 3) };
    private readonly Button _foreBtn = new() { FlatStyle = FlatStyle.Flat };
    private readonly CheckBox _setBack = new() { Text = "&Background", AutoSize = true, Margin = new Padding(28, 4, 6, 3) };
    private readonly Button _backBtn = new() { FlatStyle = FlatStyle.Flat };
    private readonly Button _luckyBtn = new() { Text = "I'm feeling luck&y", AutoSize = true, Margin = new Padding(24, 3, 0, 3) };
    private readonly CheckBox _bold = new() { Text = "Bo&ld", AutoSize = true, ThreeState = true, Margin = new Padding(0, 4, 24, 3) };
    private readonly CheckBox _italic = new() { Text = "&Italic", AutoSize = true, ThreeState = true };

    private readonly IReadOnlyList<Filter> _siblings;
    private int _lucky = -1;

    private RgbColor _fore = new(0, 0, 0);
    private RgbColor _back = new(255, 255, 255);

    public FilterEditDialog(Filter filter, bool isNew) : this(filter, isNew, Array.Empty<Filter>()) { }

    /// <summary><paramref name="siblings"/> is every filter in the set, so a suggested colour can avoid the
    /// ones already in use.</summary>
    public FilterEditDialog(Filter filter, bool isNew, IReadOnlyList<Filter> siblings)
    {
        _filter = filter;
        _siblings = siblings;
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

        // The field column only. Cells are given explicitly throughout: left to place things itself, a
        // TableLayoutPanel deals its cells out to the VISIBLE controls in order, so the error line appearing
        // would take the cell next to it and shift everything along.
        void NoteRow(Control field)
        {
            field.Margin = new Padding(0, Dpi(2), 0, Dpi(2));
            root.Controls.Add(field, 1, rows);
            rows++;
        }

        static FlowLayoutPanel Strip(params Control[] items)
        {
            var strip = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true };
            strip.Controls.AddRange(items);
            return strip;
        }

        Row("&Text:", _text);
        Row("Type:", Strip(_typeText, _typeMarker, _marker));
        Row("&Description:", _description);

        // All three options on one row: the excluding flag used to sit alone under a blank label, which
        // read as though it belonged to nothing.
        Row("Options:", Strip(_regex, _caseSensitive, _excluding));
        Row("Colors:", Strip(_setFore, _foreBtn, _setBack, _backBtn, _luckyBtn));
        Row("Style:", Strip(_bold, _italic));
        NoteRow(new Label
        {
            Text = "Unchecked colors and squares are inherited from the parent filter.",
            AutoSize = true,
            ForeColor = Color.Gray
        });

        // The error shares the button row rather than having one of its own: that row is as tall as the
        // buttons whatever else is in it, so a bad pattern cannot push them down the dialog.
        var buttons = OkCancelRow(out var ok, out _);
        buttons.Dock = DockStyle.None;
        buttons.Anchor = AnchorStyles.Right;    // centred against the error text, not pinned to the row's top
        buttons.Margin = new Padding(0);
        _regexError.Dock = DockStyle.Fill;
        _regexError.TextAlign = ContentAlignment.MiddleLeft;
        _regexError.Margin = new Padding(0, 0, Dpi(12), 0);
        _regexError.Height = Dpi(24);

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
        bottom.Controls.Add(_regexError, 0, 0);
        bottom.Controls.Add(buttons, 1, 0);

        root.Controls.Add(bottom, 0, rows);
        root.SetColumnSpan(bottom, 2);

        Controls.Add(root);
        MinimumSize = new Size(Dpi(560), 0);

        _foreBtn.Click += (_, _) => PickColor(ref _fore, _setFore);
        _backBtn.Click += (_, _) => PickColor(ref _back, _setBack);
        _luckyBtn.Click += (_, _) => FeelLucky();
        _setFore.CheckedChanged += (_, _) => UpdateColorButtons();
        _setBack.CheckedChanged += (_, _) => UpdateColorButtons();
        _typeText.CheckedChanged += (_, _) => UpdateTypeEnabled();
        _typeMarker.CheckedChanged += (_, _) => UpdateTypeEnabled();
        _text.TextChanged += (_, _) => ValidateRegex();
        _regex.CheckedChanged += (_, _) => ValidateRegex();
        ok.Click += (_, _) => Apply();

        LoadFromFilter();
    }

    /// <summary>Test seam: types into the pattern field, so a check can watch the error line appear.</summary>
    internal void SetTextForTesting(string text) => _text.Text = text;

    protected override void OnLoad(EventArgs e)    {
        base.OnLoad(e);                    // DialogBase AutoSize has fit the form to its content
        Size naturalClient = ClientSize;   // content size, independent of the border style
        AutoSize = false;                  // allow a manual width and free resizing
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;

        // Wide by default: a filter is often started from a whole log line, and the point is to see all of
        // it at once. Never wider than the screen it opens on, though.
        int clientW = Owner is { } o ? (int)(o.Width * 0.9) : naturalClient.Width;
        clientW = Math.Max(clientW, Dpi(640));
        clientW = Math.Min(clientW, Screen.FromControl(this).WorkingArea.Width - Dpi(64));
        ClientSize = new Size(clientW, naturalClient.Height);

        MinimumSize = new Size(Dpi(560), Height); // keep content from being clipped vertically
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
    }

    private void PickColor(ref RgbColor target, CheckBox set)
    {
        using var dlg = new ColorDialog { FullOpen = true, Color = Color.FromArgb(target.R, target.G, target.B) };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            target = new RgbColor(dlg.Color.R, dlg.Color.G, dlg.Color.B);
            set.Checked = true;
            UpdateColorButtons();
        }
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

    internal void FeelLuckyForTesting() => FeelLucky();
    internal (RgbColor Fore, RgbColor Back) ColorsForTesting => (_fore, _back);

    private void ValidateRegex()
    {
        string message = "";
        if (_typeText.Checked && _regex.Checked && _text.Text.Length > 0)
        {
            try { _ = System.Text.RegularExpressions.Regex.Match("", _text.Text); }
            catch (ArgumentException ex) { message = "Invalid regex: " + ex.Message; }
        }
        _regexError.Text = message;
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
