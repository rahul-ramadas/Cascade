using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Xunit;

namespace Cascade.UiTests;

/// <summary>
/// EXPLORATORY: photographs the match map on the real trace under several filter sets, so its legibility can
/// be judged rather than guessed at. Gated on CASCADE_MAPLOOK=1. Writes E:\Temp\maplook.
/// </summary>
public class MapLook : IDisposable
{
    private const string Big = @"E:\Repos\test-file.txt";
    private const string RealFilters = @"E:\Scripts\Orders.cascade";
    private const string Out = @"E:\Temp\maplook";
    private static readonly string Filters = Path.Combine(Out, "Orders.cascade");

    private readonly List<string> _log = new();
    private CascadeApp _app = null!;

    public void Dispose()
    {
        Directory.CreateDirectory(Out);
        File.WriteAllLines(Path.Combine(Out, "log.txt"), _log);
        try { _app?.Dispose(); } catch { }
    }

    private void Say(string s) => _log.Add(s);

    [Fact]
    public void Look()
    {
        if (Environment.GetEnvironmentVariable("CASCADE_MAPLOOK") != "1") return;
        SetProcessDpiAwarenessContext(-4);
        Directory.CreateDirectory(Out);
        File.Copy(RealFilters, Filters, overwrite: true);

        _app = CascadeApp.LaunchExisting(Big, Filters, CascadeApp.NewSettingsDir(),
                                         ownsFiles: false, ownsSettingsDir: true);
        _app.Window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Maximized);
        Thread.Sleep(1500);
        _app.Activate();
        WaitIndexed();
        AllOff();

        Scene("a-one-huge", "[order-service]");
        Scene("b-two-rare", "[ERROR]", "[WARNING]");
        Scene("c-mixed", "[order-service]", "[ERROR]", "[OrderDispatchLoop]");
        Scene("d-many", "[order-service]", "[ERROR]", "[OrderDispatchLoop]", "[bthusb]", "[db-pool]", "[bthserv]");

        Assert.True(true);
    }

    private void Scene(string name, params string[] filters)
    {
        AllOff();
        foreach (var f in filters) Enable(f);
        WaitFiltered();

        // Both regimes, because they are completely different pictures: over the whole file, where the bar
        // length is the local match density, and over the matching rows alone, where every row matches and
        // only the colours can say anything.
        SetMode(false);
        Say($"{name} dim: {string.Join(" + ", filters)} -> {_app.AllStatusText()}");
        Capture($"{name}-dim");
        SetMode(true);
        Say($"{name} filtered: {_app.AllStatusText()}");
        Capture($"{name}-filtered");
    }

    private void SetMode(bool filtered)
    {
        for (int i = 0; i < 3 && _app.MenuItemChecked("View", "Show Only Filtered Lines") != filtered; i++)
        {
            _app.ClickMenuOrThrow("View", "Show Only Filtered Lines");
            Thread.Sleep(2000);
        }
        Say($"  mode now filtered={_app.MenuItemChecked("View", "Show Only Filtered Lines")}");
        WaitFiltered();
        Thread.Sleep(1500);
    }

    private void AllOff()
    {
        _app.ClickMenuOrThrow("Filters", "Disable All");
        Thread.Sleep(1500);
    }

    private void Enable(string contains)
    {
        var node = _app.FilterNode(contains);
        if (node is null) { Say($"  (no filter {contains})"); return; }
        node.AsTreeItem().Select();
        Thread.Sleep(250);
        _app.ShiftKey(_app.Tree(), VirtualKeyShort.SPACE);
        Thread.Sleep(400);
    }

    /// <summary>Saves the map on its own, blown up sideways so a one-pixel lane can actually be looked at,
    /// and the window beside it for context.</summary>
    private void Capture(string name)
    {
        var map = _app.Grid().FindAllChildren(cf => cf.ByControlType(ControlType.ScrollBar))
                      .FirstOrDefault(s => (s.Name ?? "") == "Match map");
        if (map is null) { Say($"  ({name}: no map)"); return; }
        var r = map.BoundingRectangle;
        using var strip = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(strip)) g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(r.Width, r.Height));
        strip.Save(Path.Combine(Out, $"{name}-map.png"), ImageFormat.Png);

        // Nearest-neighbour, so the enlargement shows the pixels rather than a blur of them.
        using var big = new Bitmap(r.Width * 8, r.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(big))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(strip, new Rectangle(0, 0, big.Width, big.Height));
        }
        big.Save(Path.Combine(Out, $"{name}-wide.png"), ImageFormat.Png);

        var w = _app.Window.BoundingRectangle;
        using var shot = new Bitmap(w.Width, w.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(w.Left, w.Top, 0, 0, new Size(w.Width, w.Height));
        shot.Save(Path.Combine(Out, $"{name}-window.png"), ImageFormat.Png);
    }

    private void WaitIndexed()
    {
        for (int i = 0; i < 600 && !_app.AllStatusText().Contains("Total:"); i++) Thread.Sleep(500);
        Thread.Sleep(1000);
    }

    private void WaitFiltered()
    {
        for (int i = 0; i < 240; i++)
        {
            Thread.Sleep(500);
            string s = _app.AllStatusText();
            if (!s.Contains('\u2026') && !s.Contains("Filtering")) { Thread.Sleep(800); return; }
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(int value);
}
