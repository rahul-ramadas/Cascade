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
    public const int SchemaVersion = 1;
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
        Mode = c.Mode.ToString(),
        Delimiter = c.Delimiter,
        CollapseConsecutive = c.CollapseConsecutive,
        MaxSplits = c.MaxSplits,
        Template = c.Template,
        Columns = c.Columns.Select(col => new ColumnDefDto
        {
            Name = col.Name,
            Visible = col.Visible,
            Width = col.Width,
            Align = col.Align.ToString()
        }).ToList()
    };

    private static ColumnSpec FromDto(ColumnsDto d)
    {
        var c = new ColumnSpec
        {
            Enabled = d.Enabled,
            Mode = Enum.TryParse<ColumnSplitMode>(d.Mode, out var m) ? m : ColumnSplitMode.Delimiter,
            Delimiter = d.Delimiter ?? "\t",
            CollapseConsecutive = d.CollapseConsecutive,
            MaxSplits = d.MaxSplits,
            Template = d.Template ?? ""
        };
        if (d.Columns is not null)
            foreach (var col in d.Columns)
                c.Columns.Add(new ColumnDef
                {
                    Name = col.Name ?? "",
                    Visible = col.Visible,
                    Width = col.Width,
                    Align = Enum.TryParse<ColumnAlign>(col.Align, out var a) ? a : ColumnAlign.Left
                });
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
        public List<FilterDto>? Children { get; set; }
    }

    private sealed class ColumnsDto
    {
        public bool Enabled { get; set; }
        public string? Mode { get; set; }
        public string? Delimiter { get; set; }
        public bool CollapseConsecutive { get; set; }
        public int MaxSplits { get; set; }
        public string? Template { get; set; }
        public List<ColumnDefDto>? Columns { get; set; }
    }

    private sealed class ColumnDefDto
    {
        public string? Name { get; set; }
        public bool Visible { get; set; } = true;
        public int Width { get; set; }
        public string? Align { get; set; }
    }
}
