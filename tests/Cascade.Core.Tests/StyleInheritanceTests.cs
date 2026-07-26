using Cascade.Core.Model;

namespace Cascade.Core.Tests;

public class StyleInheritanceTests
{
    private static readonly ResolvedStyle Defaults =
        new(new RgbColor(0, 0, 0), new RgbColor(255, 255, 255), false, false);

    [Fact]
    public void Child_inherits_background_but_overrides_foreground()
    {
        var error = new Filter { Style = { Foreground = new RgbColor(0, 0, 0), Background = new RgbColor(255, 192, 203) } };
        var disk = new Filter { Style = { Foreground = new RgbColor(179, 71, 0) } }; // bg unset
        error.Children.Add(disk);
        disk.Parent = error;

        var resolved = StyleResolver.Resolve(disk, Defaults);
        Assert.Equal(new RgbColor(179, 71, 0), resolved.Foreground);      // own
        Assert.Equal(new RgbColor(255, 192, 203), resolved.Background);   // inherited from parent
    }

    [Fact]
    public void Fully_unset_child_inherits_all_from_ancestor()
    {
        var error = new Filter { Style = { Foreground = new RgbColor(1, 2, 3), Background = new RgbColor(4, 5, 6), Bold = true } };
        var mid = new Filter();
        var leaf = new Filter();
        error.Children.Add(mid); mid.Parent = error;
        mid.Children.Add(leaf); leaf.Parent = mid;

        var r = StyleResolver.Resolve(leaf, Defaults);
        Assert.Equal(new RgbColor(1, 2, 3), r.Foreground);
        Assert.Equal(new RgbColor(4, 5, 6), r.Background);
        Assert.True(r.Bold);
        Assert.False(r.Italic); // nothing set anywhere → default
    }

    [Fact]
    public void Inheritance_ignores_enabled_state()
    {
        // The parent is disabled but its color still provides the inherited value.
        var parent = new Filter { Enabled = false, Style = { Background = new RgbColor(10, 20, 30) } };
        var child = new Filter { Enabled = true };
        parent.Children.Add(child); child.Parent = parent;

        Assert.Equal(new RgbColor(10, 20, 30), StyleResolver.Resolve(child, Defaults).Background);
    }

    [Fact]
    public void Defaults_used_when_nothing_set()
    {
        var f = new Filter();
        var r = StyleResolver.Resolve(f, Defaults);
        Assert.Equal(Defaults, r);
    }
}
