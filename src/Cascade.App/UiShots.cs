using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Cascade.Core.Columns;
using Cascade.Core.Model;

namespace Cascade.App;

/// <summary>
/// Headless screenshot harness (<c>Cascade.exe --screens &lt;outDir&gt; [file] [x.tat]</c>). Renders each
/// dialog with <c>DrawToBitmap</c> and captures the main window from the screen, so the UI can be
/// reviewed at the current DPI without manual interaction.
/// </summary>
internal static class UiShots
{
    public static int Run(string[] args)
    {
        string outDir = args.FirstOrDefault(a => Directory.Exists(a) || a.Contains("shots", StringComparison.OrdinalIgnoreCase))
                        ?? Path.Combine(Path.GetTempPath(), "cascade_shots");
        string? file = args.FirstOrDefault(a => File.Exists(a) && !a.EndsWith(".tat", StringComparison.OrdinalIgnoreCase));
        string? tat = args.FirstOrDefault(a => a.EndsWith(".tat", StringComparison.OrdinalIgnoreCase) && File.Exists(a));
        Directory.CreateDirectory(outDir);

        Console.WriteLine($"DPI scaling test. Output: {outDir}");

        var demoFilter = new Filter
        {
            Enabled = true,
            Description = "disk errors",
            Match = { Text = @"\[OrderService\].+Disk", Regex = true },
            Style = { Foreground = new RgbColor(0xFF, 0xFF, 0xFF), Background = new RgbColor(0x9C, 0x27, 0xB0) }
        };
        ShotDialog(new FilterEditDialog(demoFilter, isNew: false), outDir, "filter-edit");
        ShotDialog(new FindDialog((_, _) => { }), outDir, "find");

        var cols = new ColumnSpec();
        ShotDialog(new ColumnsDialog(cols, "[2026-07-16T18:06:48][inventory-svc][3][2FA8][315C][util][Func][INFO][TFLAG] message text"), outDir, "columns");
        ShotDialog(new PreferencesDialog(new AppSettings()), outDir, "preferences");
        ShotDialog(new GoToDialog(8_295_214, 1), outDir, "goto");

        ShotMainForm(outDir, file, tat);

        Console.WriteLine("done");
        return 0;
    }

    private static void ShotDialog(Form form, string dir, string name)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(60, 60);
        form.Show();
        Application.DoEvents();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 250) { Application.DoEvents(); Thread.Sleep(10); }

        using var bmp = new Bitmap(Math.Max(1, form.Width), Math.Max(1, form.Height));
        form.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        bmp.Save(Path.Combine(dir, name + ".png"), ImageFormat.Png);
        Console.WriteLine($"{name}: {form.Width}x{form.Height}");
        form.Close();
        form.Dispose();
    }

    private static void ShotMainForm(string dir, string? file, string? tat)
    {
        var settings = AppSettings.Load();
        var argList = new List<string>();
        if (file is not null) argList.Add(file);
        if (tat is not null) argList.Add("/Filters:" + tat);
        argList.Add("/demo");

        var form = new MainForm(settings, argList.ToArray())
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(0, 0),
            Size = new Size(1500, 950),
            WindowState = FormWindowState.Normal,
            Opacity = 0 // render off-screen; DrawToBitmap reads the control tree, not the screen
        };
        form.Show();
        form.Activate();
        Application.DoEvents();

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 4000) { Application.DoEvents(); Thread.Sleep(15); }

        using (var bmp = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
            bmp.Save(Path.Combine(dir, "main.png"), ImageFormat.Png);
        }
        Console.WriteLine($"main: {form.Width}x{form.Height}");
        form.Close();
        form.Dispose();
    }
}
