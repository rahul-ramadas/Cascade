using System.Drawing;
using System.Windows.Forms;
using Cascade.Core.Document;
using Cascade.Core.Model;

namespace Cascade.App;

/// <summary>
/// The filter presets pane: named sets of filters to switch on together.
///
/// A preset's <b>tick</b> says it is in effect, and the enabled filters are the union of what is ticked - so
/// ticking one means "and this too", and unticking it takes its filters away again. The ticks are derived
/// from which filters are actually enabled, never from what was last clicked, so ticking a filter by hand in
/// the list next door lights the matching preset up (or clears it) exactly as applying it would.
///
/// The <b>selection</b> is the user's alone: it says which preset the commands act on, and nothing in the
/// model ever moves it. That separation is the whole point - while the two were one thing, any click that
/// aimed at a preset also switched its filters back on, so a preset could never be updated to drop one.
/// </summary>
public sealed class FilterPresetsControl : UserControl
{
    private readonly CheckedListBox _list = new()
    {
        Dock = DockStyle.Fill,
        SelectionMode = SelectionMode.One,
        IntegralHeight = false,
        BorderStyle = BorderStyle.None,
        CheckOnClick = false,
        AccessibleName = "Filter presets"
    };

    private readonly Label _empty = new()
    {
        Dock = DockStyle.Fill,
        Text = "No presets yet.\r\n\r\nTurn on the filters you want, then right-click here to save them as a preset.",
        TextAlign = ContentAlignment.TopLeft,
        ForeColor = SystemColors.GrayText,
        Padding = new Padding(8, 8, 8, 8),
        Visible = false
    };

    private readonly Label _header = new()
    {
        Dock = DockStyle.Top,
        Text = "  Presets",
        TextAlign = ContentAlignment.MiddleLeft,
        BackColor = SystemColors.Control,
        ForeColor = SystemColors.ControlText
    };

    private CascadeDocument? _doc;
    private bool _settingTicks;     // true only while we write a tick ourselves; everything else is vetoed
    private bool _applyQueued;      // collapses a burst of tick changes into one re-filter
    private bool _downOnTick;       // the last press landed on a tick box, so its double-click is not a rename

    /// <summary>The enabled filters changed, so the view has to be brought up to date.</summary>
    public event Action? PresetsApplied;

    /// <summary>The presets themselves changed, so the filter file is now dirty.</summary>
    public event Action? PresetsEdited;

    public FilterPresetsControl()
    {
        // The list is hidden until there is a preset to show, and the empty pane invites a right-click - so
        // the menu has to belong to the whole pane, not just to the list nobody can see yet.
        var menu = BuildContextMenu();
        ContextMenuStrip = menu;
        _list.ContextMenuStrip = menu;
        _empty.ContextMenuStrip = menu;
        _header.ContextMenuStrip = menu;
        // Windows toggles a tick for gestures of its own - clicking a row that is already selected, and the
        // second click of any double-click - which would switch a preset's filters on merely for aiming at
        // it. Vetoing every change we did not make ourselves is what keeps tick and selection apart.
        _list.ItemCheck += (_, e) => { if (!_settingTicks) e.NewValue = e.CurrentValue; };
        _list.MouseDown += (_, e) => HandleMouseDown(_list.IndexFromPoint(e.Location), e.X, e.Button);
        _list.KeyDown += OnKeyDown;
        _list.DoubleClick += (_, _) => { if (!_downOnTick) RenameSelected(); };

        Controls.Add(_list);
        Controls.Add(_empty);
        Controls.Add(_header);
        _header.Height = FontHeight + 6;
    }

    public void Attach(CascadeDocument doc)
    {
        _doc = doc;
        Rebuild();
    }

    private List<FilterPreset> Presets => _doc?.Filters.Presets ?? new List<FilterPreset>();

    private FilterPreset? Current => _list.SelectedIndex >= 0 && _list.SelectedIndex < Presets.Count
        ? Presets[_list.SelectedIndex] : null;

    /// <summary>The leading square of a row, where the tick box is drawn. Pressing there toggles the preset;
    /// anywhere else only aims at it.</summary>
    private int TickZoneWidth => _list.ItemHeight;

    /// <summary>Re-reads the presets and which of them are in effect. Cheap enough to call on any change.</summary>
    public void Rebuild()
    {
        if (_doc is null) return;
        var keep = Current;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var p in Presets) _list.Items.Add(Label(p));
        _list.EndUpdate();

        bool any = Presets.Count > 0;
        _list.Visible = any;
        _empty.Visible = !any;
        // Rebuilding is our doing, not the user's, so put their aim back where they left it.
        int was = keep is null ? -1 : Presets.IndexOf(keep);
        if (was >= 0) _list.SelectedIndex = was;
        RefreshActive();
    }

    /// <summary>Brings the ticks back in line with which filters are actually enabled. Never touches the
    /// selection: that belongs to the user.</summary>
    public void RefreshActive()
    {
        if (_doc is null) return;
        for (int i = 0; i < Presets.Count && i < _list.Items.Count; i++)
        {
            bool on = _doc.Filters.IsPresetActive(Presets[i]);
            if (_list.GetItemChecked(i) != on) SetTick(i, on);
        }
    }

    private string Label(FilterPreset p)
    {
        int missing = _doc?.Filters.MissingCount(p) ?? 0;
        return missing == 0 ? p.Name : $"{p.Name}   ({missing} missing)";
    }

    private void HandleMouseDown(int index, int x, MouseButtons button)
    {
        _downOnTick = false;
        if (index < 0 || index >= Presets.Count) return;
        if (button is not (MouseButtons.Left or MouseButtons.Right)) return;
        // Aim at the row under the pointer whichever button it was. Windows does this for the left button
        // and not at all for the right, and the menu has to act on the preset that was right-clicked.
        _list.SelectedIndex = index;
        if (button == MouseButtons.Right) return;
        _downOnTick = x < TickZoneWidth;
        if (_downOnTick) ToggleAt(index);
    }

    private void ToggleAt(int index)
    {
        if (index < 0 || index >= Presets.Count || index >= _list.Items.Count) return;
        SetTick(index, !_list.GetItemChecked(index));
        QueueApply();
    }

    private void SetTick(int index, bool on)
    {
        _settingTicks = true;
        try { _list.SetItemChecked(index, on); }
        finally { _settingTicks = false; }
    }

    /// <summary>Tick changes arrive one per click but several per command, and each one would otherwise
    /// re-run the filters. Applying once the message has been handled collapses a burst into a single pass.</summary>
    private void QueueApply()
    {
        if (_doc is null || _applyQueued || !IsHandleCreated) return;
        _applyQueued = true;
        BeginInvoke(() =>
        {
            _applyQueued = false;
            if (_doc is null) return;
            var ticked = _list.CheckedIndices.Cast<int>().Where(i => i < Presets.Count).Select(i => Presets[i]).ToList();
            if (_doc.Filters.ApplyPresets(ticked)) PresetsApplied?.Invoke();
        });
    }

    // ---- commands ----

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        var only = menu.Items.Add("Apply Only This Preset", null, (_, _) => ApplyOnlySelected());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Save Enabled Filters as Preset…", null, (_, _) => SaveCurrent());
        var update = menu.Items.Add("Update Preset from Enabled Filters", null, (_, _) => UpdateSelected());
        menu.Items.Add(new ToolStripSeparator());
        var rename = new ToolStripMenuItem("Rename Preset\u2026", null, (_, _) => RenameSelected()) { ShortcutKeyDisplayString = "F2" };
        menu.Items.Add(rename);
        var duplicate = menu.Items.Add("Duplicate Preset", null, (_, _) => DuplicateSelected());
        var delete = new ToolStripMenuItem("Delete Preset", null, (_, _) => DeleteSelected()) { ShortcutKeyDisplayString = "Del" };
        menu.Items.Add(delete);
        // Everything but saving needs a preset picked out; greyed says so, where doing nothing would not.
        menu.Opening += (_, _) =>
        {
            bool one = Current is not null;
            only.Enabled = update.Enabled = rename.Enabled = duplicate.Enabled = delete.Enabled = one;
        };
        return menu;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Space: ToggleAt(_list.SelectedIndex); break;
            case Keys.F2: RenameSelected(); break;
            case Keys.Delete: DeleteSelected(); break;
            default: return;
        }
        e.Handled = e.SuppressKeyPress = true;
    }

    /// <summary>Puts one preset in effect and takes every other out, which is what a single click used to
    /// mean before the tick took that job over.</summary>
    public void ApplyOnlySelected()
    {
        if (_doc is null || Current is not { } p) return;
        for (int i = 0; i < Presets.Count && i < _list.Items.Count; i++)
        {
            bool want = ReferenceEquals(Presets[i], p);
            if (_list.GetItemChecked(i) != want) SetTick(i, want);
        }
        QueueApply();
    }

    public void SaveCurrent()
    {
        if (_doc is null) return;
        if (TextInputDialog.Ask(this, "Save Preset", "Preset name:") is not { } name) return;
        _doc.Filters.Presets.Add(_doc.Filters.CaptureEnabled(name));
        Rebuild();
        PresetsEdited?.Invoke();
    }

    public void UpdateSelected()
    {
        if (_doc is null || Current is not { } p) return;
        p.FilterIds.Clear();
        p.FilterIds.AddRange(_doc.Filters.CaptureEnabled(p.Name).FilterIds);
        Rebuild();
        PresetsEdited?.Invoke();
    }

    public void RenameSelected()
    {
        if (Current is not { } p) return;
        if (TextInputDialog.Ask(this, "Rename Preset", "Preset name:", p.Name) is not { } name) return;
        p.Name = name;
        Rebuild();
        PresetsEdited?.Invoke();
    }

    public void DuplicateSelected()
    {
        if (_doc is null || Current is not { } p) return;
        _doc.Filters.Presets.Insert(_doc.Filters.Presets.IndexOf(p) + 1, new FilterPreset(p.Name + " copy", p.FilterIds));
        Rebuild();
        PresetsEdited?.Invoke();
    }

    public void DeleteSelected()
    {
        if (_doc is null || Current is not { } p) return;
        _doc.Filters.Presets.Remove(p);
        Rebuild();
        PresetsEdited?.Invoke();
    }

    public bool HasSelection => Current is not null;

    private int IndexOfPreset(string name) => Presets.FindIndex(p => p.Name == name);

    internal string[] LabelsForTesting => _list.Items.Cast<object>().Select(o => o.ToString() ?? "").ToArray();

    /// <summary>The presets in effect - which is what is ticked, and no longer what is selected.</summary>
    internal string[] ActiveForTesting
        => _list.CheckedIndices.Cast<int>().Where(i => i < Presets.Count).Select(i => Presets[i].Name).ToArray();

    internal string SelectedForTesting => Current?.Name ?? "";

    internal int TickZoneWidthForTesting => TickZoneWidth;

    internal void TickForTesting(params string[] names)
    {
        for (int i = 0; i < Presets.Count && i < _list.Items.Count; i++)
        {
            bool want = names.Contains(Presets[i].Name, StringComparer.Ordinal);
            if (_list.GetItemChecked(i) != want) SetTick(i, want);
        }
        QueueApply();
    }

    /// <summary>Drives the real mouse handler, so where in the row the press lands is part of what is
    /// checked. Injected keys do not set real modifier state, so the button has to be a parameter.</summary>
    internal void ClickForTesting(string name, MouseButtons button = MouseButtons.Left, bool onTick = false)
    {
        int i = IndexOfPreset(name);
        if (i >= 0) HandleMouseDown(i, onTick ? TickZoneWidth / 2 : TickZoneWidth + 8, button);
    }

    /// <summary>What Windows does off its own bat when an already-selected row is clicked, or on the second
    /// click of a double-click. It has to come to nothing.</summary>
    internal void NativeToggleForTesting(string name)
    {
        int i = IndexOfPreset(name);
        if (i >= 0) _list.SetItemChecked(i, !_list.GetItemChecked(i));
    }

    internal void SelectForTesting(string name)
    {
        int i = IndexOfPreset(name);
        if (i >= 0) _list.SelectedIndex = i;
    }

    internal Rectangle RowBoundsForTesting(int index) => _list.GetItemRectangle(index);

    internal Bitmap RenderRowsForTesting()
    {
        var bmp = new Bitmap(Math.Max(1, _list.Width), Math.Max(1, _list.Height));
        _list.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        return bmp;
    }
}
