using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Cascade.App;

internal sealed class FilterGutter : IDisposable
{
    internal const int LogicalWidth = 24;
    internal const int LogicalIconSize = 16;
    private static readonly int[] IconSizes = [16, 20, 24, 28, 32, 40, 48, 64];
    private Bitmap? _atlas;
    private Bitmap? _icon;
    private Color _ink;

    internal static Color Background => SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(232, 235, 238);
    internal static Color Edge => SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(195, 201, 207);
    internal static Color Hover => SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(209, 222, 233);

    internal static void DrawBand(Graphics graphics, Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        using var background = new SolidBrush(Background);
        using var edge = new Pen(Edge);
        graphics.FillRectangle(background, bounds);
        graphics.DrawLine(edge, bounds.Right - 1, bounds.Top, bounds.Right - 1, bounds.Bottom - 1);
    }

    internal void DrawIcon(Graphics graphics, Rectangle cell, int size, bool highlighted = false)
    {
        size = Math.Min(size, Math.Min(cell.Width, cell.Height) - 2);
        if (size <= 0) return;
        Color ink = SystemInformation.HighContrast
            ? (highlighted ? SystemColors.HighlightText : SystemColors.WindowText)
            : Color.FromArgb(78, 88, 97);
        if (_icon is null || _icon.Width != size || _ink != ink)
        {
            _icon?.Dispose();
            _icon = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            _ink = ink;
            if (_atlas is null)
            {
                using var stream = typeof(FilterGutter).Assembly.GetManifestResourceStream("Cascade.App.Assets.eye-off.png")!;
                using var image = new Bitmap(stream);
                _atlas = new Bitmap(image);
            }

            int left = 0, sourceSize = IconSizes[^1];
            foreach (int candidate in IconSizes)
            {
                sourceSize = candidate;
                if (candidate >= size || candidate == IconSizes[^1]) break;
                left += candidate;
            }
            using var target = Graphics.FromImage(_icon);
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(new ColorMatrix
            {
                Matrix00 = 0, Matrix11 = 0, Matrix22 = 0,
                Matrix40 = ink.R / 255f, Matrix41 = ink.G / 255f, Matrix42 = ink.B / 255f
            });
            target.InterpolationMode = InterpolationMode.HighQualityBicubic;
            target.PixelOffsetMode = PixelOffsetMode.HighQuality;
            target.DrawImage(_atlas, new Rectangle(0, 0, size, size), left, 0, sourceSize, sourceSize, GraphicsUnit.Pixel, attributes);
        }
        graphics.DrawImageUnscaled(_icon, cell.Left + (cell.Width - size) / 2, cell.Top + (cell.Height - size) / 2);
    }

    public void Dispose()
    {
        _icon?.Dispose();
        _atlas?.Dispose();
    }
}