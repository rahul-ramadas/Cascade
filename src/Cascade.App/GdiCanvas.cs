using System.Drawing;
using System.Runtime.InteropServices;

namespace Cascade.App;

/// <summary>
/// The device context a <see cref="Graphics"/> stands over, borrowed for the length of a paint and drawn
/// on directly.
///
/// <para>Text still goes through <see cref="TextRenderer"/>, which is what draws text everywhere else in
/// the app and in WinForms generally. What changed is where it draws and what it is told. Two measurements
/// on a scrollbar drag through a million-line log paid for both. The first: GDI+ hands its device context
/// out and takes it back on <b>every</b> call that reaches GDI - which is every piece of text - and that
/// round trip is about twenty microseconds each time, against eighty calls in a screenful. The second, and
/// the larger by far: text drawn over a background GDI has not been told about must read every destination
/// pixel back to blend ClearType against it, where text drawn over a background it knows simply writes
/// them. Filling each row as part of drawing its text, on a context held for the whole frame, took a
/// screenful of an ordinary log from 5.7 ms to 2.7.</para>
///
/// <para>It is <see cref="IDeviceContext"/> itself, which is how the text calls are pointed at the borrowed
/// context instead of fetching one of their own.</para>
/// </summary>
internal sealed class GdiCanvas : IDeviceContext
{
    private readonly Dictionary<int, IntPtr> _brushes = new();
    private Graphics? _over;
    private IntPtr _hdc;

    /// <summary>Takes the context out of the Graphics. Nothing may touch that Graphics again until
    /// <see cref="Release"/> - GDI+ holds it locked meanwhile, and drawing on it throws.</summary>
    public void Borrow(Graphics over)
    {
        _over = over;
        _hdc = over.GetHdc();
    }

    public void Release()
    {
        if (_over is null) return;
        _over.ReleaseHdc(_hdc);
        _over = null;
        _hdc = IntPtr.Zero;
    }

    public bool Holding => _over is not null;

    IntPtr IDeviceContext.GetHdc() => _hdc;

    void IDeviceContext.ReleaseHdc() { }

    /// <summary>Only ever called by <see cref="TextRenderer"/>'s own plumbing, which disposes nothing it
    /// did not create. The brushes are let go of in <see cref="Discard"/>.</summary>
    void IDisposable.Dispose() { }

    /// <summary>Gives back the brushes kept for the colours drawn so far. The colours of a view change when
    /// its settings or its filters do, which is rare, and there are a handful of them at a time.</summary>
    public void Discard()
    {
        Release();
        foreach (var brush in _brushes.Values) DeleteObject(brush);
        _brushes.Clear();
    }

    public void Fill(Rectangle box, Color colour)
    {
        if (box.Width <= 0 || box.Height <= 0) return;
        var rect = new Rect(box);
        FillRect(_hdc, ref rect, Brush(colour));
    }

    /// <summary>
    /// Draws text and the background behind it in one go: <paramref name="box"/> is filled with
    /// <paramref name="back"/> and the text is drawn at <paramref name="x"/>, clipped to that box - so the
    /// text may start left of it, as a line scrolled sideways does.
    ///
    /// <para>Telling the text call what is behind it is what makes it cheap, and it is the same call, with
    /// the same font, that draws every other piece of text in the app.</para>
    /// </summary>
    public void Text(ReadOnlySpan<char> text, int x, int y, Rectangle box, Color fore, Color back, Font font)
    {
        if (box.Width <= 0 || box.Height <= 0) return;
        Fill(box, back);
        if (text.IsEmpty) return;
        // Saved and restored by hand rather than through a scope object: the text is a span, and a span
        // cannot be captured by anything that outlives the call.
        int saved = SaveDC(_hdc);
        try
        {
            IntersectClipRect(_hdc, box.Left, box.Top, box.Right, box.Bottom);
            TextRenderer.DrawText(this, text, font, new Point(x, y), fore, back, Plain);
        }
        finally { RestoreDC(_hdc, saved); }
    }

    /// <summary>Text over whatever is already there: a found word over its highlight, the selected part of
    /// a line over the selection. Not told what is behind it, unlike <see cref="Text"/> - these are drawn
    /// on top of pixels that are already right, and laying a background down with them would square off the
    /// antialiasing where a selected glyph meets the unselected one beside it. They are also rare, so what
    /// that costs is nothing against how a whole row is drawn.</summary>
    public void TextOver(ReadOnlySpan<char> text, int x, int y, Rectangle box, Color fore, Font font)
    {
        if (text.IsEmpty || box.Width <= 0 || box.Height <= 0) return;
        int saved = SaveDC(_hdc);
        try
        {
            IntersectClipRect(_hdc, box.Left, box.Top, box.Right, box.Bottom);
            TextRenderer.DrawText(this, text, font, new Point(x, y), fore, Plain);
        }
        finally { RestoreDC(_hdc, saved); }
    }

    private const TextFormatFlags Plain = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

    /// <summary>Narrows the clip to a box for as long as the returned scope lives. Used where drawing is
    /// laid out per cell but must not reach past the text area as a whole.</summary>
    public ClipScope Clip(Rectangle box) => new(_hdc, box);

    internal readonly struct ClipScope : IDisposable
    {
        private readonly IntPtr _hdc;
        private readonly int _saved;

        public ClipScope(IntPtr hdc, Rectangle box)
        {
            _hdc = hdc;
            _saved = SaveDC(hdc);
            IntersectClipRect(hdc, box.Left, box.Top, box.Right, box.Bottom);
        }

        public void Dispose() => RestoreDC(_hdc, _saved);
    }

    /// <summary>Text laid out inside a box - right-aligned line numbers, aligned column cells - over a
    /// background it is told about, which is what makes it cheap.</summary>
    public void TextIn(ReadOnlySpan<char> text, Rectangle box, Color fore, Color back, Font font,
                       TextFormatFlags flags)
    {
        if (box.Width <= 0 || box.Height <= 0) return;
        TextRenderer.DrawText(this, text, font, box, fore, back, flags);
    }

    /// <summary>The same, over what is already there.</summary>
    public void TextIn(ReadOnlySpan<char> text, Rectangle box, Color fore, Font font, TextFormatFlags flags)
    {
        if (box.Width <= 0 || box.Height <= 0) return;
        TextRenderer.DrawText(this, text, font, box, fore, flags);
    }

    private IntPtr Brush(Color colour)
    {
        int key = colour.ToArgb();
        if (_brushes.TryGetValue(key, out var brush)) return brush;
        // A view has as many colours as it has filters; anything past that is settings being played with,
        // and starting again costs less than remembering for ever.
        if (_brushes.Count > 256) { foreach (var old in _brushes.Values) DeleteObject(old); _brushes.Clear(); }
        return _brushes[key] = CreateSolidBrush(ColorRef(colour));
    }

    private static int ColorRef(Color colour) => colour.R | (colour.G << 8) | (colour.B << 16);

    // ---- GDI ----

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;

        public Rect(Rectangle box)
        {
            Left = box.Left; Top = box.Top; Right = box.Right; Bottom = box.Bottom;
        }
    }

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(int colour);

    [DllImport("gdi32.dll")]
    private static extern int SaveDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern void RestoreDC(IntPtr hdc, int saved);

    [DllImport("gdi32.dll")]
    private static extern void IntersectClipRect(IntPtr hdc, int left, int top, int right, int bottom);

    [DllImport("user32.dll")]
    private static extern void FillRect(IntPtr hdc, ref Rect rect, IntPtr brush);
}
