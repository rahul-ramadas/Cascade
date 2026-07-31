using System.Drawing;
using System.Windows.Forms;
using Cascade.Core.Document;
using Cascade.Core.Model;

namespace Cascade.App;

/// <summary>
/// The filter presets pane: named sets of filters to switch on together.
///
/// The list's selection <b>is</b> the set of presets in effect, and the enabled filters are the union of
/// what is selected - so clicking one preset means "just this", and Ctrl+clicking a second means "both",
/// with no separate apply step. The selection is always derived from which filters are actually enabled,
/// never from what was last clicked, so ticking a filter by hand in the list next door lights the matching
/// preset up (or clears it) exactly as applying it would.
/// </summary>
public sealed class FilterPresetsControl : UserControl
{
    private readonly ListBox _list = new()
    {
        Dock = DockStyle.Fill,
        SelectionMode = SelectionMode.MultiExtended,
        IntegralHeight = false,
        BorderStyle = BorderStyle.None,
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
    private bool _syncing;          // true while the selection is being written from the model
    private bool _applyQueued;      // collapses a burst of selection changes into one re-filter

    /// <summary>The enabled filters changed, so the view has to be brought up to date.</summary>
    public event Action? PresetsApplied;

    /// <summary>The presets themselves changed, so the filter file is now dirty.</summary>
    public event Action? PresetsEdited;

    public FilterPresetsControl()
    {
        _list.ContextMenuStrip = BuildContextMenu();
        _list.SelectedIndexChanged += (_, _) => QueueApply();
        _list.KeyDown += OnKeyDown;
        _list.DoubleClick += (_, _) => RenameSelected();

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

    /// <summary>Re-reads the presets and which of them are in effect. Cheap enough to call on any change.</summary>
    public void Rebuild()
    {
        if (_doc is null) return;
        _syncing = true;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var p in Presets) _list.Items.Add(Label(p));
        _list.EndUpdate();
        _syncing = false;

        bool any = Presets.Count > 0;
        _list.Visible = any;
        _empty.Visible = !any;
        RefreshActive();
    }

    /// <summary>Brings the selection back in line with which filters are actually enabled.</summary>
    public void RefreshActive()
    {
        if (_doc is null || _syncing) return;
        var wanted = new List<int>();
        for (int i = 0; i < Presets.Count; i++)
            if (_doc.Filters.IsPresetActive(Presets[i])) wanted.Add(i);

        var current = _list.SelectedIndices.Cast<int>().ToList();
        if (current.SequenceEqual(wanted)) return;

        _syncing = true;
        _list.BeginUpdate();
        _list.ClearSelected();
        foreach (int i in wanted) _list.SetSelected(i, true);
        _list.EndUpdate();
        _syncing = false;
    }

    private string Label(FilterPreset p)
    {
        int missing = _doc?.Filters.MissingCount(p) ?? 0;
        return missing == 0 ? p.Name : $"{p.Name}   ({missing} missing)";
    }

    /// <summary>Selection changes arrive one per click but several per drag, and each one would otherwise
    /// re-run the filters. Applying once the message has been handled collapses a burst into a single pass.</summary>
    private void QueueApply()
    {
        if (_syncing || _doc is null || _applyQueued || !IsHandleCreated) return;
        _applyQueued = true;
        BeginInvoke(() =>
        {
            _applyQueued = false;
            if (_doc is null) return;
            var selected = _list.SelectedIndices.Cast<int>().Where(i => i < Presets.Count).Select(i => Presets[i]).ToList();
            _doc.Filters.ApplyPresets(selected);
            PresetsApplied?.Invoke();
        });
    }

    // ---- commands ----

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Save Enabled Filters as Preset…", null, (_, _) => SaveCurrent());
        menu.Items.Add("Update Preset from Enabled Filters", null, (_, _) => UpdateSelected());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Rename Preset…", null, (_, _) => RenameSelected()) { ShortcutKeyDisplayString = "F2" });
        menu.Items.Add("Duplicate Preset", null, (_, _) => DuplicateSelected());
        menu.Items.Add(new ToolStripMenuItem("Delete Preset", null, (_, _) => DeleteSelected()) { ShortcutKeyDisplayString = "Del" });
        return menu;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.F2: RenameSelected(); break;
            case Keys.Delete: DeleteSelected(); break;
            default: return;
        }
        e.Handled = e.SuppressKeyPress = true;
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

    internal string[] LabelsForTesting => _list.Items.Cast<object>().Select(o => o.ToString() ?? "").ToArray();

    internal string[] ActiveForTesting => _list.SelectedIndices.Cast<int>().Select(i => Presets[i].Name).ToArray();

    internal void SelectForTesting(params string[] names)
    {
        _list.ClearSelected();
        for (int i = 0; i < Presets.Count; i++)
            if (names.Contains(Presets[i].Name)) _list.SetSelected(i, true);
    }
}
