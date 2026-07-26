using System.Xml.Linq;
using Cascade.Core.Model;

namespace Cascade.Core.Persistence;

/// <summary>
/// Imports the original tool's <c>.tat</c> (XML) filter files. Filters are brought in flat as
/// top-level roots (the format has no hierarchy). Verified against real 2025-era <c>.tat</c> files.
/// </summary>
public static class TatImporter
{
    public static FilterCollection Import(string path)
    {
        using var stream = File.OpenRead(path);
        return Import(stream);
    }

    public static FilterCollection Import(Stream stream)
    {
        var doc = XDocument.Load(stream);
        var collection = new FilterCollection();

        var root = doc.Root;
        if (root is null) return collection;

        if (bool.TryParse((string?)root.Attribute("showOnlyFilteredLines"), out bool showOnly))
            collection.ShowOnlyFilteredLines = showOnly;

        var filtersEl = root.Name.LocalName == "filters" ? root : root.Element("filters");
        if (filtersEl is null) return collection;

        foreach (var el in filtersEl.Elements("filter"))
            collection.Add(ParseFilter(el));

        return collection;
    }

    private static Filter ParseFilter(XElement el)
    {
        var filter = new Filter
        {
            Description = (string?)el.Attribute("description") ?? "",
            Enabled = YesNo(el.Attribute("enabled")),
            Kind = YesNo(el.Attribute("excluding")) ? FilterKind.Exclude : FilterKind.Include
        };

        string type = (string?)el.Attribute("type") ?? "matches_text";
        if (type.Contains("marker", StringComparison.OrdinalIgnoreCase))
        {
            filter.Match.Type = FilterMatchType.Marker;
            filter.Match.MarkerIndex = ExtractMarkerIndex(type, el);
        }
        else
        {
            filter.Match.Type = FilterMatchType.Text;
            filter.Match.Text = (string?)el.Attribute("text") ?? "";
            filter.Match.CaseSensitive = YesNo(el.Attribute("case_sensitive"));
            filter.Match.Regex = YesNo(el.Attribute("regex"));
        }

        // Colors are optional; absent => unset (inherits/default). 'foreColor'/'backColor' are the
        // modern attributes; legacy 'color' maps to foreground for pre-2014 files.
        if (RgbColor.TryParseHex((string?)el.Attribute("foreColor") ?? (string?)el.Attribute("color"), out var fg))
            filter.Style.Foreground = fg;
        if (RgbColor.TryParseHex((string?)el.Attribute("backColor"), out var bg))
            filter.Style.Background = bg;

        return filter;
    }

    private static int ExtractMarkerIndex(string type, XElement el)
    {
        var attr = (string?)el.Attribute("marker") ?? (string?)el.Attribute("markerIndex");
        if (int.TryParse(attr, out int m)) return Math.Clamp(m, 0, 7);
        var digits = new string(type.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out int d) ? Math.Clamp(d, 0, 7) : 0;
    }

    private static bool YesNo(XAttribute? attr)
    {
        string? v = (string?)attr;
        return v is not null && (v.Equals("y", StringComparison.OrdinalIgnoreCase) ||
                                 v.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                                 v.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                                 v == "1");
    }
}
