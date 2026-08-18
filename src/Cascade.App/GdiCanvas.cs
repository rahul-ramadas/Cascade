using System.Drawing;
using System.Runtime.InteropServices;

namespace Cascade.App;

/// <summary>
/// The device context a <see cref="Graphics"/> stands over, borrowed for the length of a paint and drawn
/// on directly.
///
/// <para>Two measurements on a scrollbar drag through a million-line log put it here. The first: GDI+
/// hands its device context out and takes it back on <b>every</b> call that reaches GDI - which is every
/// piece of text - and that round trip is about twenty microseconds each time, against eighty calls in a
/// screenful. The second, and the larger by far: text drawn over a background GDI has not been told about
/// must read every destination pixel back to blend ClearType against it, where text drawn over a
/// background it knows simply writes them. Filling each row as part of drawing its text, on a device
/// context held for the whole frame, took a screenful of an ordinary log from 6.0 ms to 2.1.</para>
///
/// <para>It is <see cref="IDeviceContext"/> as well, so <see cref="TextRenderer"/> calls can be pointed at
/// the same borrowed context instead of fetching one of their own - which is what the parts that need
/// GDI+'s text layout (alignment, ellipsis, anything not plain ASCII) do.</para>
/// </summary>
internal sealed class GdiCanvas : IDeviceContext
{
    private readonly Dictionary<int, IntPtr> _brushes = new();
    private Graphics? _over;
    private IntPtr _hdc;
    private IntPtr _font;
    private int _fore = Unset, _back = Unset, _mode;

    private const int Unset = -1;   // a COLORREF made here is three bytes, so none of them is ever negative

    /// <summary>Takes the context out of the Graphics. Nothing may touch that Graphics again until
    /// <see cref="Release"/> - GDI+ holds it locked meanwhile, and drawing on it throws.</summary>
    public void Borrow(Graphics over)
    {
        _over = over;
        _hdc = over.GetHdc();
        // Whatever else has drawn through this context, this is what the arithmetic below assumes.
        SetTextAlign(_hdc, TaLeft | TaTop | TaNoUpdateCp);
        _font = IntPtr.Zero;
        _fore = _back = Unset;
        _mode = 0;
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
    /// <para><paramref name="plain"/> says the text is printable ASCII, which is the only case that can go
    /// through the shortest call GDI has. Anything else - a script that needs shaping or reordering, a
    /// character that has to come from a linked font - is left to the layout that has always drawn it.</para>
    /// </summary>
    public void Text(ReadOnlySpan<char> text, int x, int y, Rectangle box, Color fore, Color back,
                     Font font, IntPtr hfont, bool plain)
    {
        if (box.Width <= 0 || box.Height <= 0) return;
        if (text.IsEmpty) { Fill(box, back); return; }
        if (plain && hfont != IntPtr.Zero)
        {
            Use(hfont, fore, back, opaque: true);
            var rect = new Rect(box);
            unsafe
            {
                fixed (char* chars = text)
                    ExtTextOut(_hdc, x, y, EtoOpaque | EtoClipped, &rect, chars, (uint)text.Length, null);
            }
            return;
        }
        Fill(box, back);
        Layout(text, x, y, box, fore, font);
    }

    /// <summary>Text over whatever is already there, for the few things drawn on top of a row rather than
    /// as part of it.</summary>
    public void TextOver(ReadOnlySpan<char> text, int x, int y, Rectangle box, Color fore,
                         Font font, IntPtr hfont, bool plain)
    {
        if (text.IsEmpty || box.Width <= 0 || box.Height <= 0) return;
        if (plain && hfont != IntPtr.Zero)
        {
            Use(hfont, fore, fore, opaque: false);
            var rect = new Rect(box);
            unsafe
            {
                fixed (char* chars = text)
                    ExtTextOut(_hdc, x, y, EtoClipped, &rect, chars, (uint)text.Length, null);
            }
            return;
        }
        Layout(text, x, y, box, fore, font);
    }

    /// <summary>Text through the layout GDI+ has always used, clipped to its box because it may be asked to
    /// start left of one. The box is not filled here; whoever called has done that.</summary>
    private void Layout(ReadOnlySpan<char> text, int x, int y, Rectangle box, Color fore, Font font)
    {
        int saved = SaveDC(_hdc);
        try
        {
            IntersectClipRect(_hdc, box.Left, box.Top, box.Right, box.Bottom);
            TextRenderer.DrawText(this, text, font, new Point(x, y), fore,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
        finally { RestoreDC(_hdc, saved); Forget(); }
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

    /// <summary>Narrows the clip to a box for as long as the returned scope lives. Used where drawing is
    /// laid out per cell but must not reach past the text area as a whole.</summary>
    public ClipScope Clip(Rectangle box) => new(this, box);

    /// <summary>
    /// A narrowed clip, put back when it goes out of scope.
    ///
    /// <para>It is narrowed by saving the whole device context and restoring it, which is the cheap way to
    /// do it - and which also puts back the face and the colours that were selected when it was saved. So
    /// the canvas has to forget what it believes is selected on the way out, or the next piece of text
    /// would be told it need not select the face it is already using, and would come out in whatever the
    /// context reverted to. That is a row of cells in the system font, and it happened for real: a log
    /// split into fields, with the line-number margin turned off, drew every row but the first that
    /// way.</para>
    /// </summary>
    internal readonly struct ClipScope : IDisposable
    {
        private readonly GdiCanvas _canvas;
        private readonly int _saved;

        public ClipScope(GdiCanvas canvas, Rectangle box)
        {
            _canvas = canvas;
            _saved = SaveDC(canvas._hdc);
            IntersectClipRect(canvas._hdc, box.Left, box.Top, box.Right, box.Bottom);
        }

        public void Dispose()
        {
            RestoreDC(_canvas._hdc, _saved);
            _canvas.Forget();
        }
    }

    /// <summary>Lets go of what the canvas believes the context has selected, because something has put the
    /// context back to how it was.</summary>
    private void Forget()
    {
        _font = IntPtr.Zero;
        _fore = _back = Unset;
        _mode = 0;
    }

    private void Use(IntPtr font, Color fore, Color back, bool opaque)
    {
        if (font != _font) { SelectObject(_hdc, font); _font = font; }
        int foreRef = ColorRef(fore);
        if (foreRef != _fore) { SetTextColor(_hdc, foreRef); _fore = foreRef; }
        int mode = opaque ? Opaque : Transparent;
        if (mode != _mode) { SetBkMode(_hdc, mode); _mode = mode; }
        if (!opaque) return;
        int backRef = ColorRef(back);
        if (backRef != _back) { SetBkColor(_hdc, backRef); _back = backRef; }
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

    private const int Transparent = 1, Opaque = 2;
    private const uint EtoOpaque = 0x0002, EtoClipped = 0x0004;
    private const uint TaLeft = 0, TaTop = 0, TaNoUpdateCp = 0;

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
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern void SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern void SetBkColor(IntPtr hdc, int colour);

    [DllImport("gdi32.dll")]
    private static extern void SetTextColor(IntPtr hdc, int colour);

    [DllImport("gdi32.dll")]
    private static extern void SetTextAlign(IntPtr hdc, uint mode);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(int colour);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtTextOutW")]
    private static extern unsafe void ExtTextOut(IntPtr hdc, int x, int y, uint options, Rect* rect,
        char* text, uint count, int* spacing);

    [DllImport("gdi32.dll")]
    private static extern int SaveDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern void RestoreDC(IntPtr hdc, int saved);

    [DllImport("gdi32.dll")]
    private static extern void IntersectClipRect(IntPtr hdc, int left, int top, int right, int bottom);

    [DllImport("user32.dll")]
    private static extern void FillRect(IntPtr hdc, ref Rect rect, IntPtr brush);
}
