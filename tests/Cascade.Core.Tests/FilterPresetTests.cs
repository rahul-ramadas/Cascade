using Cascade.Core.Model;
using Cascade.Core.Persistence;
using Xunit;

namespace Cascade.Core.Tests;

public class FilterPresetTests
{
    private static FilterCollection Tree(params string[] names)
    {
        var c = new FilterCollection();
        foreach (var n in names) c.Add(new Filter { Match = { Text = n } });
        return c;
    }

    private static Filter By(FilterCollection c, string text)
        => c.EnumerateDepthFirst().First(f => f.Match.Text == text);

    private static FilterPreset Preset(FilterCollection c, string name, params string[] texts)
        => new(name, texts.Select(t => By(c, t).Id));

    [Fact]
    public void A_preset_is_active_when_every_filter_it_names_is_on()
    {
        var c = Tree("a", "b", "c");
        var p = Preset(c, "pair", "a", "b");

        Assert.False(c.IsPresetActive(p));
        By(c, "a").Enabled = true;
        Assert.False(c.IsPresetActive(p));
        By(c, "b").Enabled = true;
        Assert.True(c.IsPresetActive(p));

        // Something outside the preset being on does not stop it being in effect.
        By(c, "c").Enabled = true;
        Assert.True(c.IsPresetActive(p));

        By(c, "a").Enabled = false;
        Assert.False(c.IsPresetActive(p));
    }

    [Fact]
    public void Ticking_a_filter_by_hand_is_enough_to_make_a_preset_active()
    {
        // The list shows what is really in effect, so it must be derived from the filters and never from
        // "the preset the user last clicked".
        var c = Tree("a", "b");
        var p = Preset(c, "just a", "a");

        By(c, "a").Enabled = true;
        Assert.True(c.IsPresetActive(p));
    }

    [Fact]
    public void Applying_presets_switches_on_exactly_their_union()
    {
        var c = Tree("a", "b", "c", "d");
        var one = Preset(c, "one", "a", "b");
        var two = Preset(c, "two", "c");
        By(c, "d").Enabled = true;

        c.ApplyPresets(new[] { one });
        Assert.Equal(new[] { true, true, false, false }, c.Roots.Select(f => f.Enabled));

        c.ApplyPresets(new[] { one, two });
        Assert.Equal(new[] { true, true, true, false }, c.Roots.Select(f => f.Enabled));

        c.ApplyPresets(new[] { two });
        Assert.Equal(new[] { false, false, true, false }, c.Roots.Select(f => f.Enabled));

        c.ApplyPresets(Array.Empty<FilterPreset>());
        Assert.All(c.Roots, f => Assert.False(f.Enabled));
    }

    [Fact]
    public void Capturing_records_exactly_what_is_enabled_and_not_the_ancestors()
    {
        // A parent's pattern constrains its children whether or not the parent is enabled, so "parent off,
        // children on" is a real arrangement that a preset has to be able to reproduce.
        var c = Tree("parent");
        var parent = By(c, "parent");
        c.Add(new Filter { Match = { Text = "child" }, Enabled = true }, parent);
        parent.Enabled = false;

        var p = c.CaptureEnabled("scoped");
        Assert.Equal(new[] { By(c, "child").Id }, p.FilterIds);

        parent.Enabled = true;
        c.ApplyPresets(new[] { p });
        Assert.False(parent.Enabled);
        Assert.True(By(c, "child").Enabled);
    }

    [Fact]
    public void A_deleted_filter_is_ignored_but_still_remembered()
    {
        var c = Tree("a", "b");
        var p = Preset(c, "pair", "a", "b");
        c.Presets.Add(p);

        c.Remove(By(c, "b"));
        Assert.Equal(1, c.MissingCount(p));
        Assert.Equal(2, p.FilterIds.Count);      // kept: deleting is undoable, so pruning would not be

        By(c, "a").Enabled = true;
        Assert.True(c.IsPresetActive(p));        // what is left of it is on, so it is in effect
    }

    [Fact]
    public void A_preset_whose_filters_have_all_gone_is_not_active()
    {
        var c = Tree("a");
        var p = Preset(c, "gone", "a");
        c.Remove(By(c, "a"));

        Assert.False(c.IsPresetActive(p));
    }

    [Fact]
    public void Presets_round_trip_through_the_filter_file()
    {
        var c = Tree("a", "b");
        c.Presets.Add(Preset(c, "one", "a"));
        c.Presets.Add(Preset(c, "both", "a", "b"));
        string path = Path.Combine(Path.GetTempPath(), "cascade_presets_" + Guid.NewGuid().ToString("N") + ".cascade");
        try
        {
            CascadeFile.Save(path, c);
            var (loaded, _) = CascadeFile.Load(path);

            Assert.Equal(new[] { "one", "both" }, loaded.Presets.Select(p => p.Name));
            Assert.Equal(c.Presets[1].FilterIds, loaded.Presets[1].FilterIds);
            // The ids have to still resolve against the filters saved alongside them.
            Assert.Equal(2, loaded.Presets[1].FilterIds.Count(id => loaded.FindById(id) is not null));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_filter_file_with_no_presets_still_loads()
    {
        var c = Tree("a");
        string path = Path.Combine(Path.GetTempPath(), "cascade_presets_" + Guid.NewGuid().ToString("N") + ".cascade");
        try
        {
            CascadeFile.Save(path, c);
            Assert.DoesNotContain("presets", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
            Assert.Empty(CascadeFile.Load(path).Filters.Presets);
        }
        finally { File.Delete(path); }
    }
}
