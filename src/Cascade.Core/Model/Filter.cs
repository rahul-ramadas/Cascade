namespace Cascade.Core.Model;

/// <summary>A single filter node in the (possibly nested) filter tree.</summary>
public sealed class Filter
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Optional user description; when empty, the pattern text is used as the display name.</summary>
    public string Description { get; set; } = "";

    public bool Enabled { get; set; }

    public FilterKind Kind { get; set; } = FilterKind.Include;

    public FilterMatch Match { get; set; } = new();

    public FilterStyle Style { get; set; } = new();

    public List<Filter> Children { get; } = new();

    public Filter? Parent { get; internal set; }

    /// <summary>0 for a root filter, incrementing per nesting level.</summary>
    public int Depth
    {
        get
        {
            int d = 0;
            for (Filter? n = Parent; n is not null; n = n.Parent) d++;
            return d;
        }
    }

    public string DisplayName =>
        !string.IsNullOrEmpty(Description) ? Description : Match.ToDisplayString();

    public bool IsAncestorOf(Filter other)
    {
        for (Filter? n = other.Parent; n is not null; n = n.Parent)
            if (ReferenceEquals(n, this)) return true;
        return false;
    }

    public Filter Clone(bool newIds = true)
    {
        var copy = new Filter
        {
            Id = newIds ? Guid.NewGuid().ToString("N") : Id,
            Description = Description,
            Enabled = Enabled,
            Kind = Kind,
            Match = Match.Clone(),
            Style = Style.Clone()
        };
        foreach (var child in Children)
        {
            var cc = child.Clone(newIds);
            cc.Parent = copy;
            copy.Children.Add(cc);
        }
        return copy;
    }
}
