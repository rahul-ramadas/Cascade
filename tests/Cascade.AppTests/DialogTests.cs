using System.Reflection;
using Cascade.App;
using Cascade.Core.Model;

namespace Cascade.AppTests;

/// <summary>
/// The two dialogs nothing else reached: Preferences, which is the only way to change most of what is in
/// settings.json, and the one-line prompt used to name a preset.
///
/// <para>Both are built and driven in process on the STA thread, never shown. That is what makes them
/// testable at all - a modal dialog cannot be driven through UI Automation, because the message loop it
/// owns blocks the call that would open it.</para>
/// </summary>
public class DialogTests
{
    /// <summary>
    /// Everything the dialog offers has to make the return journey: shown from settings, and written back
    /// when OK is pressed.
    ///
    /// <para>The failure this is for is a preference added to <see cref="AppSettings"/> and to the dialog's
    /// layout, but left out of either <c>LoadSettings</c> or <c>Apply</c> - at which point the dialog
    /// silently shows a default and silently discards what was typed. Both halves are exercised, in both
    /// directions, because forgetting one of them looks exactly like forgetting the other from the outside.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_preference_the_dialog_offers_makes_the_return_journey() => Sta.Run(() =>
    {
        var settings = new AppSettings
        {
            FontFamily = "Courier New",
            FontSize = 12.5f,
            TabSize = 3,
            ExtraLineSpacing = 2,
            AutoLoadLastFilterFile = true,
            AddNewFiltersAtTop = true,
            FilterPrecedence = FilterPrecedence.ListOrder,
            HangWatchdog = false,
            Automation = false,
        };

        using var dlg = new PreferencesDialog(settings);
        Hidden.Show(dlg);

        // Shown from the settings it was handed...
        Assert.Equal(12.5m, Numeric(dlg, 0).Value);
        Assert.Equal(2m, Numeric(dlg, 1).Value);      // line spacing comes before tab size in the layout
        Assert.Equal(3m, Numeric(dlg, 2).Value);
        Assert.True(Box(dlg, "Load the last filter file").Checked);
        Assert.True(Box(dlg, "Add new filters at the top").Checked);
        Assert.Equal(1, Combo(dlg, "earlier filter").SelectedIndex);
        Assert.False(Box(dlg, "Write a memory dump").Checked);
        Assert.False(Box(dlg, "Support screen readers").Checked);

        // ...changed the way a reader would change them...
        Numeric(dlg, 0).Value = 9.5m;
        Numeric(dlg, 1).Value = 4m;
        Numeric(dlg, 2).Value = 8m;
        Box(dlg, "Load the last filter file").Checked = false;
        Box(dlg, "Add new filters at the top").Checked = false;
        Combo(dlg, "earlier filter").SelectedIndex = 0;
        Box(dlg, "Write a memory dump").Checked = true;
        Box(dlg, "Support screen readers").Checked = true;

        // ...and written back by the button, not by a method the dialog happens to expose.
        Button(dlg, "OK").PerformClick();

        Assert.Equal(9.5f, settings.FontSize);
        Assert.Equal(4, settings.ExtraLineSpacing);
        Assert.Equal(8, settings.TabSize);
        Assert.False(settings.AutoLoadLastFilterFile);
        Assert.False(settings.AddNewFiltersAtTop);
        Assert.Equal(FilterPrecedence.ExcludesWin, settings.FilterPrecedence);
        Assert.True(settings.HangWatchdog);
        Assert.True(settings.Automation);
    });

    [Fact]
    public void Cancelling_leaves_every_preference_as_it_was() => Sta.Run(() =>
    {
        var settings = new AppSettings { TabSize = 4, HangWatchdog = false };
        using var dlg = new PreferencesDialog(settings);
        Hidden.Show(dlg);

        Numeric(dlg, 2).Value = 12m;
        Box(dlg, "Write a memory dump").Checked = true;
        Button(dlg, "Cancel").PerformClick();

        Assert.Equal(4, settings.TabSize);
        Assert.False(settings.HangWatchdog);
    });

    [Fact]
    public void A_colour_button_wears_the_colour_it_stands_for() => Sta.Run(() =>
    {
        var settings = new AppSettings
        {
            ForegroundArgb = Color.Firebrick.ToArgb(),
            BackgroundArgb = Color.Ivory.ToArgb(),
        };
        using var dlg = new PreferencesDialog(settings);
        Hidden.Show(dlg);

        var swatches = Descendants(dlg).OfType<Button>()
                                       .Where(b => b.FlatStyle == FlatStyle.Flat)
                                       .Select(b => b.BackColor.ToArgb())
                                       .ToList();

        Assert.Contains(Color.Firebrick.ToArgb(), swatches);
        Assert.Contains(Color.Ivory.ToArgb(), swatches);
    });

    /// <summary>A settings file carried from another machine can name a font this one does not have. The
    /// box must say so rather than come up empty, and OK must not quietly replace the reader's choice with
    /// whatever happened to be first in the list.</summary>
    [Fact]
    public void A_font_this_machine_does_not_have_is_still_shown() => Sta.Run(() =>
    {
        var settings = new AppSettings { FontFamily = "No Such Typeface At All" };
        using var dlg = new PreferencesDialog(settings);
        Hidden.Show(dlg);

        var fonts = Combo(dlg, "No Such Typeface At All");
        Assert.Equal("No Such Typeface At All", fonts.SelectedItem);

        Button(dlg, "OK").PerformClick();
        Assert.Equal("No Such Typeface At All", settings.FontFamily);
    });

    [Fact]
    public void A_name_prompt_refuses_to_accept_nothing() => Sta.Run(() =>
    {
        using var dlg = new TextInputDialog("Rename Preset", "Preset name");
        Hidden.Show(dlg);
        var ok = Button(dlg, "OK");
        var box = Descendants(dlg).OfType<TextBox>().Single();

        Assert.False(ok.Enabled);            // nothing typed yet

        box.Text = "   ";
        Assert.False(ok.Enabled);            // whitespace is nothing

        box.Text = "  Errors only  ";
        Assert.True(ok.Enabled);
        Assert.Equal("Errors only", dlg.Value);   // trimmed, so a stray space cannot make two presets

        box.Text = "";
        Assert.False(ok.Enabled);            // and it goes back off again
    });

    [Fact]
    public void A_name_prompt_starts_from_the_name_it_was_given() => Sta.Run(() =>
    {
        using var dlg = new TextInputDialog("Rename Preset", "Preset name", "Payments");
        Hidden.Show(dlg);

        Assert.Equal("Payments", dlg.Value);
        Assert.True(Button(dlg, "OK").Enabled);
    });

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }

    /// <summary>Buttons are matched on their text with the mnemonic ampersand taken out, so moving an Alt
    /// key does not break a test that is not about Alt keys.</summary>
    private static Button Button(Control root, string text)
        => Descendants(root).OfType<Button>()
                            .First(b => b.Text.Replace("&", "") == text);

    /// <summary>The spinners in layout order: font size, line spacing, tab size.</summary>
    private static NumericUpDown Numeric(Control root, int index)
        => Descendants(root).OfType<NumericUpDown>().ElementAt(index);

    private static CheckBox Box(Control root, string startsWith)
        => Descendants(root).OfType<CheckBox>()
                            .First(c => c.Text.StartsWith(startsWith, StringComparison.Ordinal));

    /// <summary>A drop-down named by something only it offers, so the dialog gaining another one cannot
    /// silently redirect a test to the wrong control.</summary>
    private static ComboBox Combo(Control root, string offers)
        => Descendants(root).OfType<ComboBox>()
                            .Single(c => c.Items.Cast<object>()
                                          .Any(i => i is string s && s.Contains(offers, StringComparison.Ordinal)));

    /// <summary>Guards the assumption the two helpers above rest on. If the dialog gains a spinner or a box,
    /// this fails and says so, rather than the tests silently reading the wrong control.</summary>
    [Fact]
    public void The_dialog_offers_the_controls_these_tests_reach_for() => Sta.Run(() =>
    {
        using var dlg = new PreferencesDialog(new AppSettings());
        Hidden.Show(dlg);

        Assert.Equal(3, Descendants(dlg).OfType<NumericUpDown>().Count());
        Assert.Equal(4, Descendants(dlg).OfType<CheckBox>().Count());
        Assert.Equal(2, Descendants(dlg).OfType<ComboBox>().Count());

        // Every switch the dialog owns has to be written back by Apply. Counting the boxes against the
        // number of settings Apply assigns would be circular, so this counts them against the fields the
        // dialog declares - which is what a newly added preference adds one of.
        int switches = typeof(PreferencesDialog)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Count(f => f.FieldType == typeof(CheckBox));
        Assert.Equal(switches, Descendants(dlg).OfType<CheckBox>().Count());

        // The precedence drop-down is read back by casting the selected index straight to the enum, so the
        // items have to be in the enum's own order - not merely the right number of them.
        var precedence = Combo(dlg, "earlier filter");
        Assert.Equal(Enum.GetValues<FilterPrecedence>().Length, precedence.Items.Count);
        Assert.Contains("Always hide", (string)precedence.Items[(int)FilterPrecedence.ExcludesWin]!,
                        StringComparison.Ordinal);
        Assert.Contains("earlier filter", (string)precedence.Items[(int)FilterPrecedence.ListOrder]!,
                        StringComparison.Ordinal);
    });
}
