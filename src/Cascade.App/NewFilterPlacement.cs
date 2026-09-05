using System.ComponentModel;
using System.Windows.Forms;

namespace Cascade.App;

/// <summary>Where a new filter lands. Whichever key opened the dialog, all three are on offer inside it, so
/// this is only what the choice starts out as.</summary>
public enum NewFilterPlacement
{
    /// <summary>Whichever end of the top-level list the preference names - the top of it, or the bottom.</summary>
    Default,

    /// <summary>Directly above the filter the list is on, as its sibling. Above rather than below because a
    /// filter is usually made to be read before the one that prompted it, and because a line takes its
    /// colour from the first filter in the list that matches.</summary>
    Above,

    /// <summary>Under the filter the list is on, as its child - the new filter narrowing that one.</summary>
    Child,
}

/// <summary>The keys that ask for each place, and how each is written. One list, so the menu that advertises
/// them, the dialog that shows them beside its choices, and the dialog's own handling of them while it is
/// open cannot come to disagree.
///
/// <para>Ctrl+N is the one that was always there and still means what it did. The other two are it plus the
/// modifier that already carries the idea elsewhere in this app: Shift reaches out from the selection the way
/// Shift+Up and Shift+Down extend it, and Alt nests the way Alt+Right does.</para></summary>
internal static class NewFilterKeys
{
    public const Keys Add = Keys.Control | Keys.N;
    public const Keys AddAbove = Keys.Control | Keys.Shift | Keys.N;
    public const Keys AddChild = Keys.Control | Keys.Alt | Keys.N;

    /// <summary>The place a key asks for, or null where it asks for none of them.</summary>
    public static NewFilterPlacement? Asked(Keys keys) => keys switch
    {
        Add => NewFilterPlacement.Default,
        AddAbove => NewFilterPlacement.Above,
        AddChild => NewFilterPlacement.Child,
        _ => null
    };

    public static Keys For(NewFilterPlacement placement) => placement switch
    {
        NewFilterPlacement.Above => AddAbove,
        NewFilterPlacement.Child => AddChild,
        _ => Add
    };

    /// <summary>How a key is written wherever it is shown. Asked of the same converter a menu item uses for
    /// its own shortcut column, so the wording beside a choice in the dialog is the wording in the menu
    /// rather than a second spelling of it that could drift.</summary>
    public static string TextFor(NewFilterPlacement placement) =>
        TypeDescriptor.GetConverter(typeof(Keys)).ConvertToString(For(placement)) ?? "";
}
