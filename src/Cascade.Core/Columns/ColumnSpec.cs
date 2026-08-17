namespace Cascade.Core.Columns;

/// <summary>How the parts of a line are laid out on screen.</summary>
public enum FieldLayout
{
    /// <summary>Aligned columns under a header, as a table.</summary>
    Columns,

    /// <summary>Still a line, with the hidden parts left out of it.</summary>
    Inline
}

public enum ColumnAlign { Left, Right, Center }

/// <summary>One part of the template as the reader manages it: what it is called, whether it is shown, and
/// how wide it is drawn.</summary>
public sealed class ColumnDef
{
    public string Name { get; set; } = "";
    public bool Visible { get; set; } = true;
    public int Width { get; set; }              // display width in pixels; 0 = size to content

    /// <summary>Which PART of the template this shows, counted from 0 in template order. Kept apart from
    /// the column's place in the list, because that place is only where the column is DRAWN - carrying a
    /// header about has to move the data with it, not relabel the parts. -1 means "not settled yet";
    /// see <see cref="ColumnSpec.NormalizeSources"/>.</summary>
    public int Source { get; set; } = -1;

    /// <summary>Width in characters, which is what a width means when the log is being read in a
    /// fixed-pitch font: zooming then keeps the same fields visible instead of clipping them. Takes
    /// precedence over <see cref="Width"/> while such a font is in use; 0 leaves the pixel width to speak.</summary>
    public int WidthChars { get; set; }

    public ColumnAlign Align { get; set; } = ColumnAlign.Left;

    public ColumnDef Clone() => new()
    {
        Name = Name,
        Visible = Visible,
        Width = Width,
        WidthChars = WidthChars,
        Align = Align,
        Source = Source
    };
}

/// <summary>
/// How each line is split for display: a <see cref="LineTemplate"/>, and what to do with the parts it
/// finds. Splitting is display-only and never affects filtering, which always runs on the whole raw line -
/// so turning this on can shorten a line but can never hide one.
/// </summary>
public sealed class ColumnSpec
{
    private string _template = "";
    private LineTemplate? _compiled;

    public bool Enabled { get; set; }

    public FieldLayout Layout { get; set; } = FieldLayout.Columns;

    public string Template
    {
        get => _template;
        set
        {
            string next = value ?? "";
            if (_template == next) return;
            _template = next;
            _compiled = null;
        }
    }

    /// <summary>The parsed template, built once and kept until the text changes. Read on the UI thread
    /// only, which is where every user of the spec lives.</summary>
    public LineTemplate Compiled => _compiled ??= new LineTemplate(_template);

    /// <summary>The columns, in the order they are DRAWN.</summary>
    public List<ColumnDef> Columns { get; } = new();

    /// <summary>Whether there is anything to draw: splitting is on, and the template actually found parts.</summary>
    public bool Active => Enabled && Columns.Count > 0 && Compiled.PartCount > 0 && Compiled.IsValid;

    public ColumnSpec Clone()
    {
        var copy = new ColumnSpec { Enabled = Enabled, Layout = Layout, Template = Template };
        foreach (var column in Columns) copy.Columns.Add(column.Clone());
        return copy;
    }

    /// <summary>Copies everything one spec says onto another, in place - the document's spec is handed out
    /// by reference, so it is written over rather than replaced.</summary>
    public void CopyFrom(ColumnSpec other)
    {
        ArgumentNullException.ThrowIfNull(other);
        Enabled = other.Enabled;
        Layout = other.Layout;
        Template = other.Template;
        Columns.Clear();
        foreach (var column in other.Columns) Columns.Add(column.Clone());
    }

    public static string DefaultName(int index) => "Col " + (index + 1);

    /// <summary>Starts the column list over from the template, keeping nothing.</summary>
    public void Reset()
    {
        Columns.Clear();
        for (int i = 0; i < Compiled.PartCount; i++)
            Columns.Add(new ColumnDef { Source = i, Name = DefaultName(i) });
    }

    /// <summary>
    /// Carries the column list across an edit to the template. Where the number of parts is unchanged,
    /// every name, width and tick stays exactly where it was. Where one has been added or taken away it is
    /// added or taken away AT THE CARET - so typing a new part into the middle of a template does not shift
    /// every name along by one and quietly attach them to the wrong data.
    /// </summary>
    /// <param name="caretPart">Which part the caret is in; see <see cref="LineTemplate.PartIndexAtOffset"/>.</param>
    public void Sync(int caretPart)
    {
        int now = Compiled.PartCount, before = Columns.Count;
        if (now == before) return;

        if (now == before + 1)
        {
            int at = Math.Clamp(caretPart, 0, before);
            foreach (var column in Columns) if (column.Source >= at) column.Source++;
            int after = Columns.FindIndex(c => c.Source == at - 1);
            Columns.Insert(after < 0 ? 0 : after + 1, new ColumnDef { Source = at, Name = DefaultName(at) });
            return;
        }

        if (now == before - 1)
        {
            int at = Math.Clamp(caretPart, 0, before - 1);
            int where = Columns.FindIndex(c => c.Source == at);
            if (where >= 0) Columns.RemoveAt(where);
            foreach (var column in Columns) if (column.Source > at) column.Source--;
            return;
        }

        Reset();   // a big edit - a paste, or the whole thing retyped - is a fresh start
    }

    /// <summary>Settles which part each column shows for any that has not been told, and drops any left
    /// pointing past the end of the template - or at a part another column already shows. Columns built in
    /// code, or read from a file written by an older build, show the part at their own place in the list -
    /// which is what they used to do.</summary>
    public void NormalizeSources()
    {
        for (int i = 0; i < Columns.Count; i++)
            if (Columns[i].Source < 0) Columns[i].Source = i;

        int parts = Compiled.PartCount;
        if (parts > 0) Columns.RemoveAll(c => c.Source >= parts);

        // Two columns showing one part would draw that part's text twice, which is text the line never had.
        // Nothing the dialog does can make that happen; a hand-edited file can.
        var seen = new HashSet<int>();
        Columns.RemoveAll(c => !seen.Add(c.Source));
    }

    /// <summary>Everything worth saving, as one string - so "did that change anything?" is one comparison
    /// rather than a field-by-field walk that a new field could fall out of.</summary>
    public string Describe()
        => string.Join('\u0001', new[] { Enabled.ToString(), Layout.ToString(), Template }
            .Concat(Columns.Select(c => $"{c.Name}/{c.Visible}/{c.Width}/{c.WidthChars}/{c.Align}/{c.Source}")));
}
