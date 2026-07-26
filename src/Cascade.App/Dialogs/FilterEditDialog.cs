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
    private readonly RadioButton _typeText = new() { Text = "Matches text", AutoSize = true, Checked = true, Margin = new Padding(0, 3, 16, 3) };
    private readonly RadioButton _typeMarker = new() { Text = "Marked by marker", AutoSize = true, Margin = new Padding(0, 3, 4, 3) };
    private readonly NumericUpDown _marker = new() { Minimum = 1, Maximum = 8 };
    private readonly TextBox _text = new() { Dock = DockStyle.Fill, Font = new Font("Consolas", 9.75f) };
    private readonly CheckBox _regex = new() { Text = "Regular expression", AutoSize = true, Margin = new Padding(0, 3, 20, 3) };
    private readonly CheckBox _caseSensitive = new() { Text = "Case sensitive", AutoSize = true };
    private readonly CheckBox _excluding = new() { Text = "Excluding filter (hides matching lines)", AutoSize = true };
    private readonly Label _regexError = new() { ForeColor = Color.Firebrick, AutoSize = true, Margin = new Padding(0, 2, 0, 2) };

    private readonly CheckBox _setFore = new() { Text = "Text color", AutoSize = true, Margin = new Padding(0, 4, 6, 3) };
    private readonly Button _foreBtn = new() { FlatStyle = FlatStyle.Flat };
    private readonly CheckBox _setBack = new() { Text = "Background", AutoSize = true, Margin = new Padding(24, 4, 6, 3) };
    private readonly Button _backBtn = new() { FlatStyle = FlatStyle.Flat };
    private readonly CheckBox _bold = new() { Text = "Bold", AutoSize = true, ThreeState = true, Margin = new Padding(0, 4, 20, 3) };
    private readonly CheckBox _italic = new() { Text = "Italic", AutoSize = true, ThreeState = true };

    private RgbColor _fore = new(0, 0, 0);
    private RgbColor _back = new(255, 255, 255);

    public FilterEditDialog(Filter filter, bool isNew)
    {
        _filter = filter;
        Text = isNew ? "Add Filter" : "Edit Filter";

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

        void Row(string label, Control field)
        {
            root.Controls.Add(FieldLabel(label));
            field.Margin = new Padding(0, 3, 0, 3);
            root.Controls.Add(field);
        }

        Row("Description:", _description);

        var typeRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0), WrapContents = false };
        typeRow.Controls.AddRange(new Control[] { _typeText, _typeMarker, _marker });
        Row("Type:", typeRow);

        Row("Text:", _text);

        var optRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0), WrapContents = false };
        optRow.Controls.AddRange(new Control[] { _regex, _caseSensitive });
        Row("Options:", optRow);
        Row("", _excluding);

        var colorRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0), WrapContents = false };
        colorRow.Controls.AddRange(new Control[] { _setFore, _foreBtn, _setBack, _backBtn });
        Row("Colors:", colorRow);

        var styleRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0), WrapContents = false };
        styleRow.Controls.AddRange(new Control[] { _bold, _italic, new Label { Text = "(unchecked colors / squares = inherit from parent)", AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(16, 4, 0, 3) } });
        Row("Style:", styleRow);

        Row("", _regexError);

        var buttons = OkCancelRow(out var ok, out _);
        root.Controls.Add(buttons);
        root.SetColumnSpan(buttons, 2);

        Controls.Add(root);
        MinimumSize = new Size(Dpi(540), 0);

        _foreBtn.Click += (_, _) => PickColor(ref _fore, _setFore);
        _backBtn.Click += (_, _) => PickColor(ref _back, _setBack);
        _setFore.CheckedChanged += (_, _) => UpdateColorButtons();
        _setBack.CheckedChanged += (_, _) => UpdateColorButtons();
        _typeText.CheckedChanged += (_, _) => UpdateTypeEnabled();
        _typeMarker.CheckedChanged += (_, _) => UpdateTypeEnabled();
        _text.TextChanged += (_, _) => ValidateRegex();
        _regex.CheckedChanged += (_, _) => ValidateRegex();
        ok.Click += (_, _) => Apply();

        LoadFromFilter();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);                    // DialogBase AutoSize has fit the form to its content
        Size naturalClient = ClientSize;   // content size, independent of the border style
        AutoSize = false;                  // allow a manual width and free resizing
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;

        // Default almost as wide as the app window so long filter text is fully visible.
        int clientW = Owner is { } o ? (int)(o.Width * 0.9) : naturalClient.Width;
        clientW = Math.Max(clientW, Dpi(520));
        ClientSize = new Size(clientW, naturalClient.Height);

        MinimumSize = new Size(Dpi(480), Height); // keep content from being clipped vertically
        if (Owner is { } owner)
            Location = new Point(owner.Left + Math.Max(0, (owner.Width - Width) / 2),
                                 owner.Top + Math.Max(0, (owner.Height - Height) / 2));
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

    private void ValidateRegex()
    {
        _regexError.Text = "";
        if (_typeText.Checked && _regex.Checked && _text.Text.Length > 0)
        {
            try { _ = System.Text.RegularExpressions.Regex.Match("", _text.Text); }
            catch (ArgumentException ex) { _regexError.Text = "Invalid regex: " + ex.Message; }
        }
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
