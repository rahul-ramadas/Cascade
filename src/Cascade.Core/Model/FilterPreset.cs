namespace Cascade.Core.Model;

/// <summary>
/// A named set of filters to switch on together - "triage", "payments deep-dive".
///
/// It records filter <b>ids</b>, not filters, so a preset survives its filters being edited, moved or
/// re-nested. Ids that no longer resolve are kept rather than pruned: deleting a filter is undoable, and
/// silently rewriting every preset that mentioned it would not be.
/// </summary>
public sealed class FilterPreset
{
    public FilterPreset() { }

    public FilterPreset(string name, IEnumerable<string> filterIds)
    {
        Name = name;
        FilterIds.AddRange(filterIds);
    }

    public string Name { get; set; } = "";

    public List<string> FilterIds { get; } = new();

    public FilterPreset Clone() => new(Name, FilterIds);
}
