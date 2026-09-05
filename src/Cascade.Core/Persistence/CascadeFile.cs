using System.Text.Json;
using System.Text.Json.Serialization;
using Cascade.Core.Columns;
using Cascade.Core.IO;
using Cascade.Core.Model;

namespace Cascade.Core.Persistence;

/// <summary>Reads/writes the native <c>.cascade</c> collection format (JSON): the hierarchical filter
/// tree with per-property styles, column spec, and view state. Additive-only via <c>schemaVersion</c>.</summary>
public static class CascadeFile
{
    /// <summary>2 replaced the old delimiter/bracket column settings with a template. Files written by
    /// version 1 are still read and their columns migrated; see <see cref="LegacyColumns"/>.</summary>
    public const int SchemaVersion = 2;
    public const string Extension = ".cascade";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static void Save(string path, FilterCollection filters, ColumnSpec? columns = null)
    {
        var dto = new RootDto
        {
            SchemaVersion = SchemaVersion,
            ShowOnlyFilteredLines = filters.ShowOnlyFilteredLines,
            Filters = filters.Roots.Select(ToDto).ToList(),
            Presets = filters.Presets.Count == 0 ? null : filters.Presets
                .Select(p => new PresetDto { Name = p.Name, FilterIds = p.FilterIds.ToList() }).ToList(),
            Columns = columns is null ? null : ToDto(columns)
        };
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(dto, Options));
    }

    public static (FilterCollection Filters, ColumnSpec? Columns) Load(string path)
    {
        var dto = JsonSerializer.Deserialize<RootDto>(File.ReadAllText(path), Options)
                  ?? new RootDto();
        var filters = new FilterCollection { ShowOnlyFilteredLines = dto.ShowOnlyFilteredLines };
        if (dto.Filters is not null)
            foreach (var f in dto.Filters)
                filters.Add(FromDto(f));
        if (dto.Presets is not null)
            foreach (var p in dto.Presets)
                filters.Presets.Add(new FilterPreset(p.Name ?? "", p.FilterIds ?? new List<string>()));
        ColumnSpec? columns = dto.Columns is null ? null : FromDto(dto.Columns);
        return (filters, columns);
    }

    // ---- model <-> DTO ----

    private static FilterDto ToDto(Filter f) => new()
    {
        Id = f.Id,
        Description = string.IsNullOrEmpty(f.Description) ? null : f.Description,
        Enabled = f.Enabled,
        Kind = f.Kind.ToString(),
        MatchType = f.Match.Type.ToString(),
        Text = f.Match.Text,
        CaseSensitive = f.Match.CaseSensitive ? true : null,
        Regex = f.Match.Regex ? true : null,
        MarkerIndex = f.Match.Type == FilterMatchType.Marker ? f.Match.MarkerIndex : null,
        Fg = f.Style.Foreground?.ToHex(),
        Bg = f.Style.Background?.ToHex(),
        Bold = f.Style.Bold,
        Italic = f.Style.Italic,
        Underline = f.Style.Underline,
        Children = f.Children.Count > 0 ? f.Children.Select(ToDto).ToList() : null
    };

    private static Filter FromDto(FilterDto d)
    {
        var f = new Filter
        {
            Id = d.Id ?? Guid.NewGuid().ToString("N"),
            Description = d.Description ?? "",
            Enabled = d.Enabled,
            Kind = Enum.TryParse<FilterKind>(d.Kind, out var k) ? k : FilterKind.Include
        };
        f.Match.Type = Enum.TryParse<FilterMatchType>(d.MatchType, out var mt) ? mt : FilterMatchType.Text;
        f.Match.Text = d.Text ?? "";
        f.Match.CaseSensitive = d.CaseSensitive ?? false;
        f.Match.Regex = d.Regex ?? false;
        f.Match.MarkerIndex = d.MarkerIndex ?? -1;
        if (RgbColor.TryParseHex(d.Fg, out var fg)) f.Style.Foreground = fg;
        if (RgbColor.TryParseHex(d.Bg, out var bg)) f.Style.Background = bg;
        f.Style.Bold = d.Bold;
        f.Style.Italic = d.Italic;
        f.Style.Underline = d.Underline;
        if (d.Children is not null)
            foreach (var c in d.Children)
            {
                var cc = FromDto(c);
                cc.Parent = f;
                f.Children.Add(cc);
            }
        return f;
    }

    private static ColumnsDto ToDto(ColumnSpec c) => new()
    {
        Enabled = c.Enabled,
        Layout = c.Layout.ToString(),
        Template = c.Template,
        TimePart = c.TimePart >= 0 ? c.TimePart : null,
        TimeFormat = c.TimeFormat.Length > 0 ? c.TimeFormat : null,
        Columns = c.Columns.Select(col => new ColumnDefDto
        {
            Name = col.Name,
            Visible = col.Visible,
            Width = col.Width,
            WidthChars = col.WidthChars,
            Source = col.Source,
            Align = col.Align.ToString()
        }).ToList()
    };

    private static ColumnSpec FromDto(ColumnsDto d)
    {
        var c = new ColumnSpec
        {
            Enabled = d.Enabled,
            Layout = Enum.TryParse<FieldLayout>(d.Layout, out var l) ? l : FieldLayout.Columns,
            Template = d.Template ?? "",
            TimePart = d.TimePart ?? -1,
            TimeFormat = d.TimeFormat ?? ""
        };

        if (d.Columns is not null)
            foreach (var col in d.Columns)
                c.Columns.Add(new ColumnDef
                {
                    Name = col.Name ?? "",
                    Visible = col.Visible,
                    Width = col.Width,
                    WidthChars = col.WidthChars,
                    // Absent in files written before a column could be carried away from its own field.
                    Source = col.Source ?? -1,
                    Align = Enum.TryParse<ColumnAlign>(col.Align, out var a) ? a : ColumnAlign.Left
                });

        // A "mode" only exists in files written before the template language, and says which of the two old
        // shapes this is. Both become templates, so the columns the reader set up still stand.
        if (d.Mode is not null)
            c.Template = d.Mode.Equals("Delimiter", StringComparison.OrdinalIgnoreCase)
                ? LegacyColumns.FromDelimiter(d.Delimiter ?? "\t", c.Columns.Count)
                : LegacyColumns.FromBracketTemplate(d.Template ?? "");

        c.NormalizeSources();
        return c;
    }

    // ---- DTOs ----

    private sealed class RootDto
    {
        public int SchemaVersion { get; set; } = CascadeFile.SchemaVersion;
        public bool ShowOnlyFilteredLines { get; set; }
        public List<FilterDto>? Filters { get; set; }
        public List<PresetDto>? Presets { get; set; }
        public ColumnsDto? Columns { get; set; }
    }

    private sealed class PresetDto
    {
        public string? Name { get; set; }
        public List<string>? FilterIds { get; set; }
    }

    private sealed class FilterDto
    {
        public string? Id { get; set; }
        public string? Description { get; set; }
        public bool Enabled { get; set; }
        public string? Kind { get; set; }
        public string? MatchType { get; set; }
        public string? Text { get; set; }
        public bool? CaseSensitive { get; set; }
        public bool? Regex { get; set; }
        public int? MarkerIndex { get; set; }
        public string? Fg { get; set; }
        public string? Bg { get; set; }
        public bool? Bold { get; set; }
        public bool? Italic { get; set; }
        public bool? Underline { get; set; }
        public List<FilterDto>? Children { get; set; }
    }

    private sealed class ColumnsDto
    {
        public bool Enabled { get; set; }
        public string? Layout { get; set; }
        public string? Template { get; set; }
        public List<ColumnDefDto>? Columns { get; set; }

        /// <summary>Which part of the template holds the timestamp, and what reads it. Absent when the
        /// reader has not said, which leaves the log's own clock to be detected instead.</summary>
        public int? TimePart { get; set; }
        public string? TimeFormat { get; set; }

        // Written only by builds before the template language. Read, migrated, never written again.
        public string? Mode { get; set; }
        public string? Delimiter { get; set; }
    }

    private sealed class ColumnDefDto
    {
        public string? Name { get; set; }
        public bool Visible { get; set; } = true;
        public int Width { get; set; }
        public int WidthChars { get; set; }
        public int? Source { get; set; }
        public string? Align { get; set; }
    }
}
