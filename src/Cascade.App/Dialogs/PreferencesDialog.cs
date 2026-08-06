using System.Drawing;
using System.Windows.Forms;

namespace Cascade.App;

/// <summary>Edits <see cref="AppSettings"/> (font, colors, tab size, line numbers, markers).</summary>
public sealed class PreferencesDialog : DialogBase
{
    private readonly AppSettings _s;
    private readonly ComboBox _font = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly NumericUpDown _size = new() { Minimum = 6, Maximum = 48, DecimalPlaces = 1, Increment = 0.5m };
    private readonly NumericUpDown _tab = new() { Minimum = 1, Maximum = 16 };
    private readonly NumericUpDown _lineSpacing = new() { Minimum = 0, Maximum = 12 };
    private readonly CheckBox _lineNumbers = new() { Text = "Show line numbers", AutoSize = true };
    private readonly CheckBox _autoLoadFilters = new() { Text = "Load the last filter file automatically at startup", AutoSize = true };
    private readonly CheckBox _newFiltersAtTop = new() { Text = "Add new filters at the top of the list", AutoSize = true };
    private readonly ComboBox _markers = new() { DropDownStyle = ComboBoxStyle.DropDownList };

    private readonly Button _fg = new() { FlatStyle = FlatStyle.Flat };
    private readonly Button _bg = new() { FlatStyle = FlatStyle.Flat };
    private readonly Button _selBg = new() { FlatStyle = FlatStyle.Flat };
    private readonly Button _dim = new() { FlatStyle = FlatStyle.Flat };

    public PreferencesDialog(AppSettings settings)
    {
        _s = settings;
        Text = "Preferences";

        foreach (var f in FontFamily.Families) _font.Items.Add(f.Name);
        _markers.Items.AddRange(new object[] { "Always", "Never", "When in use" });
        _markers.Width = Dpi(150);
        _size.Width = Dpi(70);
        _tab.Width = Dpi(70);
        _lineSpacing.Width = Dpi(70);
        foreach (var b in new[] { _fg, _bg, _selBg, _dim }) b.Size = new Size(Dpi(70), Dpi(23));

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

        var fontRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0) };
        _font.Width = Dpi(200);
        fontRow.Controls.Add(_font);
        fontRow.Controls.Add(new Label { Text = "Size:", AutoSize = true, Margin = new Padding(Dpi(12), 6, 6, 3) });
        fontRow.Controls.Add(_size);
        Row("Font:", fontRow);

        var spacingRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0) };
        spacingRow.Controls.Add(_lineSpacing);
        spacingRow.Controls.Add(new Label { Text = "pixels added to each line; 0 uses the font's own spacing", AutoSize = true, Margin = new Padding(Dpi(8), 6, 0, 3) });
        Row("Line spacing:", spacingRow);

        Row("Foreground:", _fg);
        Row("Background:", _bg);
        Row("Selection:", _selBg);
        Row("Dimmed text:", _dim);
        Row("Tab size:", _tab);
        Row("Markers:", _markers);
        Row("", _lineNumbers);
        Row("", _autoLoadFilters);
        Row("", _newFiltersAtTop);

        var buttons = OkCancelRow(out var ok, out _);
        root.Controls.Add(buttons);
        root.SetColumnSpan(buttons, 2);

        Controls.Add(root);
        MinimumSize = new Size(Dpi(460), 0);

        Bind(_fg, () => _s.ForegroundArgb, v => _s.ForegroundArgb = v);
        Bind(_bg, () => _s.BackgroundArgb, v => _s.BackgroundArgb = v);
        Bind(_selBg, () => _s.SelectionBackArgb, v => _s.SelectionBackArgb = v);
        Bind(_dim, () => _s.DimForegroundArgb, v => _s.DimForegroundArgb = v);
        ok.Click += (_, _) => Apply();

        LoadSettings();
    }

    private void Bind(Button btn, Func<int> get, Action<int> set)
    {
        btn.BackColor = Color.FromArgb(get());
        btn.Click += (_, _) =>
        {
            using var dlg = new ColorDialog { FullOpen = true, Color = Color.FromArgb(get()) };
            if (dlg.ShowDialog(this) == DialogResult.OK) { set(dlg.Color.ToArgb()); btn.BackColor = dlg.Color; }
        };
    }

    private void LoadSettings()
    {
        _font.SelectedItem = _s.FontFamily;
        if (_font.SelectedIndex < 0) _font.Text = _s.FontFamily;
        _size.Value = (decimal)Math.Clamp(_s.FontSize, 6f, 48f);
        _tab.Value = Math.Clamp(_s.TabSize, 1, 16);
        _lineSpacing.Value = Math.Clamp(_s.ExtraLineSpacing, 0, 12);
        _lineNumbers.Checked = _s.ShowLineNumbers;
        _autoLoadFilters.Checked = _s.AutoLoadLastFilterFile;
        _newFiltersAtTop.Checked = _s.AddNewFiltersAtTop;
        _markers.SelectedIndex = (int)_s.MarkerVisibility;
    }

    private void Apply()
    {
        if (_font.SelectedItem is string f) _s.FontFamily = f;
        _s.FontSize = (float)_size.Value;
        _s.TabSize = (int)_tab.Value;
        _s.ExtraLineSpacing = (int)_lineSpacing.Value;
        _s.ShowLineNumbers = _lineNumbers.Checked;
        _s.AutoLoadLastFilterFile = _autoLoadFilters.Checked;
        _s.AddNewFiltersAtTop = _newFiltersAtTop.Checked;
        _s.MarkerVisibility = (MarkerVisibilityMode)_markers.SelectedIndex;
    }
}
