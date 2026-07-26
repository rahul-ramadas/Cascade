using System.Drawing;
using System.Text;
using System.Windows.Forms;
using Cascade.Core.Columns;

namespace Cascade.App;

/// <summary>Configure how lines split into columns (single delimiter or <c>[name]</c> template),
/// with one-click auto-detect for leading <c>[...]</c> groups, and per-column visibility/width.</summary>
public sealed class ColumnsDialog : DialogBase
{
    private readonly ColumnSpec _working;
    private readonly string _sample;

    private readonly CheckBox _enabled = new() { Text = "Split lines into columns (display only)", AutoSize = true, Margin = new Padding(0, 0, 0, 6) };
    private readonly RadioButton _modeDelim = new() { Text = "Single delimiter", AutoSize = true, Checked = true, Margin = new Padding(0, 3, 16, 3) };
    private readonly RadioButton _modeTemplate = new() { Text = "Bracket template", AutoSize = true };
    private readonly ComboBox _delimPreset = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _delimiter = new();
    private readonly CheckBox _collapse = new() { Text = "Collapse consecutive", AutoSize = true, Margin = new Padding(12, 4, 12, 3) };
    private readonly NumericUpDown _maxSplits = new() { Minimum = 0, Maximum = 200 };
    private readonly TextBox _template = new() { Dock = DockStyle.Fill, Font = new Font("Consolas", 9.75f) };
    private readonly Button _autoDetect = new() { Text = "Auto-detect [ ] groups", AutoSize = true };
    private readonly Button _refresh = new() { Text = "Refresh columns from sample", AutoSize = true };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        RowHeadersVisible = false,
        AllowUserToResizeColumns = true,
        SelectionMode = DataGridViewSelectionMode.CellSelect
    };

    public ColumnsDialog(ColumnSpec spec, string sampleLine)
    {
        _working = spec.Clone();
        _sample = sampleLine ?? "";
        Text = "Columns";

        // This dialog hosts a grid, so it is resizable rather than auto-sized.
        AutoSize = false;
        AutoSizeMode = AutoSizeMode.GrowOnly;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        ClientSize = new Size(Dpi(680), Dpi(540));
        MinimumSize = new Size(Dpi(560), Dpi(420));

        _delimPreset.Items.AddRange(new object[] { "Tab", "Comma", "Space", "Pipe", "Semicolon", "Custom" });
        _delimPreset.Width = Dpi(96);
        _delimiter.Width = Dpi(70);
        _maxSplits.Width = Dpi(60);
        _delimPreset.SelectedIndexChanged += (_, _) =>
        {
            _delimiter.Text = _delimPreset.SelectedItem switch
            {
                "Tab" => "\\t", "Comma" => ",", "Space" => " ", "Pipe" => "|", "Semicolon" => ";", _ => _delimiter.Text
            };
        };

        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Column", Name = "name", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Visible", Name = "visible", Width = Dpi(70) });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Width (0=auto)", Name = "width", Width = Dpi(120) });

        var options = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2 };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Row(string label, Control field)
        {
            options.Controls.Add(FieldLabel(label));
            field.Margin = new Padding(0, 3, 0, 3);
            options.Controls.Add(field);
        }

        options.Controls.Add(_enabled);
        options.SetColumnSpan(_enabled, 2);

        var modeFlow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
        modeFlow.Controls.AddRange(new Control[] { _modeDelim, _modeTemplate });
        Row("Split by:", modeFlow);

        var delimFlow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
        delimFlow.Controls.AddRange(new Control[]
        {
            _delimPreset, _delimiter, _collapse,
            new Label { Text = "Max splits:", AutoSize = true, Margin = new Padding(0, 6, 6, 3) }, _maxSplits
        });
        Row("Delimiter:", delimFlow);

        var tmplFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
        _template.Width = Dpi(320);
        tmplFlow.Controls.AddRange(new Control[] { _template, _autoDetect });
        Row("Template:", tmplFlow);

        Row("", _refresh);

        var buttonsHost = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(Dpi(10)) };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(Dpi(84), Dpi(26)), Margin = new Padding(Dpi(6), 0, 0, 0) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(Dpi(84), Dpi(26)) };
        buttonsHost.Controls.Add(cancel);
        buttonsHost.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;

        var gridHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Dpi(12), Dpi(4), Dpi(12), Dpi(4)) };
        gridHost.Controls.Add(_grid);
        var optionsHost = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(Dpi(12), Dpi(12), Dpi(12), Dpi(4)) };
        optionsHost.Controls.Add(options);

        Controls.Add(gridHost);       // fill (added first → takes remaining space)
        Controls.Add(buttonsHost);    // bottom
        Controls.Add(optionsHost);    // top

        _modeDelim.CheckedChanged += (_, _) => UpdateEnabledState();
        _modeTemplate.CheckedChanged += (_, _) => UpdateEnabledState();
        _enabled.CheckedChanged += (_, _) => UpdateEnabledState();
        _autoDetect.Click += (_, _) => { _template.Text = DetectTemplate(_sample); _modeTemplate.Checked = true; RefreshColumns(); };
        _refresh.Click += (_, _) => RefreshColumns();
        ok.Click += (_, _) => Apply();

        LoadFromSpec();
    }

    public ColumnSpec Result => _working;

    private void LoadFromSpec()
    {
        _enabled.Checked = _working.Enabled;
        _modeTemplate.Checked = _working.Mode == ColumnSplitMode.Template;
        _modeDelim.Checked = !_modeTemplate.Checked;
        _delimiter.Text = _working.Delimiter == "\t" ? "\\t" : _working.Delimiter;
        _collapse.Checked = _working.CollapseConsecutive;
        _maxSplits.Value = Math.Clamp(_working.MaxSplits, 0, 200);
        _template.Text = _working.Template;
        PopulateGrid();
        UpdateEnabledState();
    }

    private void UpdateEnabledState()
    {
        bool on = _enabled.Checked;
        bool tmpl = _modeTemplate.Checked;
        _delimPreset.Enabled = _delimiter.Enabled = _collapse.Enabled = _maxSplits.Enabled = on && !tmpl;
        _template.Enabled = _autoDetect.Enabled = on && tmpl;
        _modeDelim.Enabled = _modeTemplate.Enabled = _refresh.Enabled = _grid.Enabled = on;
    }

    private void RefreshColumns()
    {
        CommitEditorsToWorking();
        if (_modeTemplate.Checked)
        {
            _working.SyncColumnsFromTemplate();
        }
        else
        {
            var splitter = new ColumnSplitter(_working);
            var values = new List<ColumnValue>();
            splitter.Split(_sample, values);
            var existing = _working.Columns.ToDictionary(c => c.Name, c => c);
            _working.Columns.Clear();
            for (int i = 0; i < values.Count; i++)
            {
                string name = string.IsNullOrEmpty(values[i].Name) ? $"Col {i + 1}" : values[i].Name;
                _working.Columns.Add(existing.TryGetValue(name, out var e) ? e : new ColumnDef { Name = name });
            }
        }
        PopulateGrid();
    }

    private void PopulateGrid()
    {
        _grid.Rows.Clear();
        foreach (var c in _working.Columns) _grid.Rows.Add(c.Name, c.Visible, c.Width);
    }

    private void CommitEditorsToWorking()
    {
        _working.Enabled = _enabled.Checked;
        _working.Mode = _modeTemplate.Checked ? ColumnSplitMode.Template : ColumnSplitMode.Delimiter;
        _working.Delimiter = _delimiter.Text.Replace("\\t", "\t");
        if (_working.Delimiter.Length == 0) _working.Delimiter = "\t";
        _working.CollapseConsecutive = _collapse.Checked;
        _working.MaxSplits = (int)_maxSplits.Value;
        _working.Template = _template.Text;
    }

    private void Apply()
    {
        CommitEditorsToWorking();
        for (int i = 0; i < _grid.Rows.Count && i < _working.Columns.Count; i++)
        {
            _working.Columns[i].Visible = Convert.ToBoolean(_grid.Rows[i].Cells["visible"].Value ?? true);
            _working.Columns[i].Width = int.TryParse(Convert.ToString(_grid.Rows[i].Cells["width"].Value), out int w) ? Math.Max(0, w) : 0;
        }
    }

    private static string DetectTemplate(string sample)
    {
        var sb = new StringBuilder();
        int i = 0, n = 0;
        while (i < sample.Length && sample[i] == '[')
        {
            int j = sample.IndexOf(']', i + 1);
            if (j < 0) break;
            n++;
            sb.Append("[[field").Append(n).Append("]]");
            i = j + 1;
        }
        if (n == 0) return "";
        if (i < sample.Length) sb.Append(" [message]");
        return sb.ToString();
    }
}
