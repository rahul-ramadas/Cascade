using System.Drawing;
using System.Windows.Forms;
using Cascade.Core.Model;

namespace Cascade.App;

/// <summary>Sets the appearance of several filters at once.
///
/// Its own dialog rather than the filter editor with most of itself greyed out: a pattern, a type and a
/// description belong to one filter, so there is nothing for those fields to show and nothing useful for
/// them to do. What is left needs a state the single-filter editor has no use for - <i>leave each filter
/// as it is</i> - which is why bold and italic are lists here and tick boxes there.</summary>
public sealed class AppearanceDialog : DialogBase
{
    private readonly IReadOnlyList<Filter> _filters;
    private readonly IReadOnlyList<Filter> _all;
    private readonly ResolvedStyle _defaults;

    private readonly QuietCheckBox _setFore = new() { Text = "Text col&or", AutoSize = true, ThreeState = true, Margin = new Padding(0, 6, 6, 3) };
    private readonly Button _foreBtn = new() { FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 3, 0, 3) };
    private readonly QuietCheckBox _setBack = new() { Text = "&Background", AutoSize = true, ThreeState = true, Margin = new Padding(24, 6, 6, 3) };
    private readonly Button _backBtn = new() { FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 3, 0, 3) };
    private readonly Button _luckyBtn = new() { Text = "I'm feeling luck&y", AutoSize = true, Margin = new Padding(16, 3, 0, 3) };
    private readonly Button _chipsBtn = new() { Text = "&Paint chips\u2026", AutoSize = true, Margin = new Padding(6, 3, 0, 3) };

    private readonly ComboBox _bold = new() { DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 3, 24, 3) };
    private readonly ComboBox _italic = new() { DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 3, 0, 3) };

    private readonly TextBox _preview = new() { Dock = DockStyle.Fill, ReadOnly = true, TabStop = false, Font = Mono };
    private readonly Label _note = new() { AutoSize = false, AutoEllipsis = true, ForeColor = Color.Gray };

    private static readonly Font[] MonoFaces =
    [
        new("Consolas", 9.75f),
        new("Consolas", 9.75f, FontStyle.Bold),
        new("Consolas", 9.75f, FontStyle.Italic),
        new("Consolas", 9.75f, FontStyle.Bold | FontStyle.Italic),
    ];

    private static Font Mono => MonoFaces[0];

    private const string VariesText = "varies";

    /// <summary>What the sample says. Deliberately not the filters' own patterns: those are usually regular
    /// expressions and far too long for the box, and none of that tells you whether the colours being
    /// chosen can be read. A plain sentence with a mix of letter shapes does.</summary>
    private const string SampleText = "The quick brown fox jumps over the lazy dog \u2014 0123456789";

    /// <summary>What the flag lists offer, in order. Index 0 is the one a set of filters that disagree
    /// starts on, so pressing OK without touching anything changes nothing.</summary>
    private static readonly (string Text, StyleEdit Edit, bool Value)[] FlagChoices =
    [
        ("(leave unchanged)", StyleEdit.Leave, false),
        ("On", StyleEdit.Set, true),
        ("Off", StyleEdit.Set, false),
        ("Inherit from parent", StyleEdit.Inherit, false),
    ];

    private RgbColor _fore;
    private RgbColor _back;
    private int _lucky = -1;

    /// <summary>What the user asked for. Attributes they left alone come back as
    /// <see cref="StyleEdit.Leave"/> and must not be written to anything.</summary>
    public StyleChange Change { get; private set; } = StyleChange.Nothing;

    /// <param name="filters">The filters being changed.</param>
    /// <param name="all">Every filter in the set, so a suggested colour can avoid the ones in use.</param>
    /// <param name="defaults">What the log view draws with when nothing says otherwise.</param>
    public AppearanceDialog(IReadOnlyList<Filter> filters, IReadOnlyList<Filter> all, ResolvedStyle defaults)
    {
        _filters = filters;
        _all = all;
        _defaults = defaults;
        Text = filters.Count == 1 ? "Appearance" : $"Appearance of {filters.Count} Filters";

        _fore = defaults.Foreground;
        _back = defaults.Background;
        _foreBtn.Size = new Size(Dpi(72), Dpi(23));
        _backBtn.Size = new Size(Dpi(72), Dpi(23));
        _bold.Width = _italic.Width = Dpi(150);
        foreach (var choice in FlagChoices) { _bold.Items.Add(choice.Text); _italic.Items.Add(choice.Text); }
        _preview.AccessibleName = "Appearance preview";
        _setFore.AccessibleName = "Set text color";
        _setBack.AccessibleName = "Set background";
        _bold.AccessibleName = "Bold";
        _italic.AccessibleName = "Italic";

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

        // Never wrapping: asked for its preferred height inside an auto-sizing column, a wrapping strip
        // answers for a narrower width than it is then given and reserves lines it never draws.
        static FlowLayoutPanel Strip(params Control[] items)
        {
            var strip = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
            strip.Controls.AddRange(items);
            return strip;
        }

        Row("Preview:", _preview);
        Row("Color:", Strip(_setFore, _foreBtn, _setBack, _backBtn, _luckyBtn, _chipsBtn));
        Row("Style:", Strip(Caption("Bo&ld"), _bold, Caption("&Italic"), _italic));

        var buttons = OkCancelRow(out var ok, out _);
        buttons.Dock = DockStyle.None;
        buttons.Anchor = AnchorStyles.Right;
        buttons.Margin = new Padding(0);
        _note.Dock = DockStyle.Fill;
        _note.TextAlign = ContentAlignment.MiddleLeft;
        _note.Margin = new Padding(0, 0, Dpi(12), 0);
        _note.Height = Dpi(24);
        _note.Text = filters.Count == 1
            ? "Anything left inherited comes from the parent filter."
            : "Anything left unchanged keeps whatever each filter already has.";

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
        _setFore.CheckStateChanged += (_, _) => UpdatePreview();
        _setBack.CheckStateChanged += (_, _) => UpdatePreview();
        _bold.SelectedIndexChanged += (_, _) => UpdatePreview();
        _italic.SelectedIndexChanged += (_, _) => UpdatePreview();
        ok.Click += (_, _) => Apply();

        LoadFromFilters();
        ActiveControl = _setFore;   // not the sample, which shows its text selected once it has the caret
    }

    private static Label Caption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 6, 6, 3),
        TextAlign = ContentAlignment.MiddleLeft
    };

    private void LoadFromFilters()
    {
        var common = StyleChange.Describe(_filters);
        _setFore.CheckState = ToState(common.Foreground);
        _setBack.CheckState = ToState(common.Background);
        if (common.Foreground == StyleEdit.Set) _fore = common.ForegroundValue;
        if (common.Background == StyleEdit.Set) _back = common.BackgroundValue;
        _bold.SelectedIndex = ChoiceFor(common.Bold, common.BoldValue);
        _italic.SelectedIndex = ChoiceFor(common.Italic, common.ItalicValue);
        UpdatePreview();
    }

    private static CheckState ToState(StyleEdit edit) => edit switch
    {
        StyleEdit.Set => CheckState.Checked,
        StyleEdit.Inherit => CheckState.Unchecked,
        _ => CheckState.Indeterminate
    };

    private static StyleEdit FromState(CheckState state) => state switch
    {
        CheckState.Checked => StyleEdit.Set,
        CheckState.Unchecked => StyleEdit.Inherit,
        _ => StyleEdit.Leave
    };

    private static int ChoiceFor(StyleEdit edit, bool value)
    {
        for (int i = 0; i < FlagChoices.Length; i++)
            if (FlagChoices[i].Edit == edit && (edit != StyleEdit.Set || FlagChoices[i].Value == value)) return i;
        return 0;
    }

    /// <summary>Repaints the swatches and the sample. Not called Refresh: that is already a Control method
    /// and means something else entirely.</summary>
    private void UpdatePreview()
    {
        _foreBtn.Text = _setFore.CheckState == CheckState.Indeterminate ? VariesText : "";
        _backBtn.Text = _setBack.CheckState == CheckState.Indeterminate ? VariesText : "";
        _foreBtn.BackColor = _setFore.CheckState == CheckState.Checked ? ToColor(_fore) : SystemColors.Control;
        _backBtn.BackColor = _setBack.CheckState == CheckState.Checked ? ToColor(_back) : SystemColors.Control;

        // Unset attributes are shown as the view draws them. The filters can have different parents, so
        // there is no one chain to resolve against - which is what the note under the buttons says.
        var change = Read();
        var fore = change.Foreground == StyleEdit.Set ? change.ForegroundValue : _defaults.Foreground;
        var back = change.Background == StyleEdit.Set ? change.BackgroundValue : _defaults.Background;
        bool bold = change.Bold == StyleEdit.Set && change.BoldValue;
        bool italic = change.Italic == StyleEdit.Set && change.ItalicValue;

        _preview.ForeColor = ToColor(fore);
        _preview.BackColor = ToColor(back);
        _preview.Font = MonoFaces[(bold ? 1 : 0) | (italic ? 2 : 0)];
        _preview.Text = SampleText;
    }

    private StyleChange Read() => new(
        FromState(_setFore.CheckState), _fore,
        FromState(_setBack.CheckState), _back,
        FlagChoices[Math.Max(0, _bold.SelectedIndex)].Edit, FlagChoices[Math.Max(0, _bold.SelectedIndex)].Value,
        FlagChoices[Math.Max(0, _italic.SelectedIndex)].Edit, FlagChoices[Math.Max(0, _italic.SelectedIndex)].Value);

    private void Apply() => Change = Read();

    private static Color ToColor(RgbColor c) => Color.FromArgb(c.R, c.G, c.B);

    /// <summary>The system picker, with the sample following the colour as it is chosen. Cancelling puts
    /// back exactly what was there, including which of the three states the tick box was in.</summary>
    private void PickColor(bool foreground)
    {
        var box = foreground ? _setFore : _setBack;
        RgbColor before = foreground ? _fore : _back;
        var wasState = box.CheckState;

        void Show(RgbColor c)
        {
            if (foreground) _fore = c; else _back = c;
            box.CheckState = CheckState.Checked;
            UpdatePreview();
            _preview.Update();   // the picker owns the message loop; nothing else will repaint this
        }

        using var dlg = new LiveColorDialog(ToColor(before));
        dlg.Previewing += c => Show(new RgbColor(c.R, c.G, c.B));

        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            Show(new RgbColor(dlg.Color.R, dlg.Color.G, dlg.Color.B));
            return;
        }

        if (foreground) _fore = before; else _back = before;
        box.CheckState = wasState;
        UpdatePreview();
    }

    private void FeelLucky()
    {
        _lucky = LuckyColors.Next(_lucky, _all, _filters);
        var pair = LuckyColors.At(_lucky);
        _back = pair.Back;
        _fore = pair.Fore;
        _setBack.CheckState = CheckState.Checked;
        _setFore.CheckState = CheckState.Checked;
        UpdatePreview();
    }

    private void ShowPalette()
    {
        var free = LuckyColors.Free(_all, _filters);
        var current = _setBack.CheckState == CheckState.Checked || _setFore.CheckState == CheckState.Checked
            ? new LuckyColors.Pair(_back, _fore) : (LuckyColors.Pair?)null;

        using var dlg = new PaletteDialog(free, SampleText, current);
        if (dlg.ShowDialog(this) != DialogResult.OK || free.Count == 0) return;

        _back = dlg.Picked.Back;
        _fore = dlg.Picked.Fore;
        _setBack.CheckState = CheckState.Checked;
        _setFore.CheckState = CheckState.Checked;
        UpdatePreview();
    }

    // ---- test seams ----

    internal void SetColorStateForTesting(bool foreground, CheckState state, RgbColor? value)
    {
        if (value is { } c) { if (foreground) _fore = c; else _back = c; }
        (foreground ? _setFore : _setBack).CheckState = state;
        UpdatePreview();
    }

    internal void SetFlagForTesting(bool bold, StyleEdit edit, bool value)
        => (bold ? _bold : _italic).SelectedIndex = ChoiceFor(edit, value);

    internal (CheckState Fore, CheckState Back, int Bold, int Italic) StateForTesting =>
        (_setFore.CheckState, _setBack.CheckState, _bold.SelectedIndex, _italic.SelectedIndex);

    internal (string Fore, string Back) SwatchTextForTesting => (_foreBtn.Text, _backBtn.Text);

    internal StyleChange ReadForTesting() => Read();

    internal void ApplyForTesting() => Apply();

    internal void FeelLuckyForTesting() => FeelLucky();
}
