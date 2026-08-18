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
    private readonly Dictionary<Font, Realised> _faces = new();
    private Graphics? _over;
    private IntPtr _hdc;
    private IntPtr _face, _wasFace;
    private int _fore = -1, _back = -1;
    private bool _opaque;

    /// <summary>Takes the context out of the Graphics. Nothing may touch that Graphics again until
    /// <see cref="Release"/> - GDI+ holds it locked meanwhile, and drawing on it throws.</summary>
    public void Borrow(Graphics over)
    {
        _over = over;
        _hdc = over.GetHdc();
        _wasFace = IntPtr.Zero;
        Unknown();
    }

    public void Release()
    {
        if (_over is null) return;
        // Whatever was in the context when it was borrowed goes back into it: a font that is still selected
        // cannot be deleted, and the context is not ours to leave changed.
        if (_wasFace != IntPtr.Zero) SelectObject(_hdc, _wasFace);
        _over.ReleaseHdc(_hdc);
        _over = null;
        _hdc = IntPtr.Zero;
        _wasFace = IntPtr.Zero;
        Unknown();
    }

    /// <summary>Drops what this remembers of the context's state, so the next draw sets everything it needs
    /// rather than trusting a note about a context that has been put back to how it was. Anything that
    /// saves and restores the context wholesale has to say so here: a restore puts back the font, the
    /// colours and the background mode along with the clip, and a note that survived it would have the next
    /// piece of text drawn in the last one's colours.</summary>
    private void Unknown()
    {
        _face = IntPtr.Zero;
        _fore = _back = -1;
        _opaque = false;
    }

    public bool Holding => _over is not null;

    IntPtr IDeviceContext.GetHdc() => _hdc;

    void IDeviceContext.ReleaseHdc() { }

    /// <summary>Only ever called by <see cref="TextRenderer"/>'s own plumbing, which disposes nothing it
    /// did not create. The brushes are let go of in <see cref="Discard"/>.</summary>
    void IDisposable.Dispose() { }

    /// <summary>Gives back the brushes and fonts kept for what has been drawn so far. The colours and faces
    /// of a view change when its settings or its filters do, which is rare, and there are a handful of them
    /// at a time.</summary>
    public void Discard()
    {
        Release();
        foreach (var brush in _brushes.Values) DeleteObject(brush);
        _brushes.Clear();
        foreach (var face in _faces.Values) DeleteObject(face.Handle);
        _faces.Clear();
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
    /// <para>Plain ASCII in a fixed-pitch face is handed straight to GDI, which is about twice as quick as
    /// going through <see cref="TextRenderer"/> for it. Everything else is laid out: a character the face
    /// has no glyph for has to be fetched from a linked font, and in a face whose characters differ in
    /// width the layout call spaces a run fractionally differently from the plain one - not enough to see,
    /// but enough that the same text would not be drawn the same way twice. Both draw with the SAME font
    /// handle, which is what lets them be mixed at all: the handle decides the antialiasing, and when each
    /// call was left to choose a handle for itself they chose differently on machines whose font smoothing
    /// was not this one's - ClearType on one line, grey on the next.</para>
    /// </summary>
    public void Text(ReadOnlySpan<char> text, int x, int y, Rectangle box, Color fore, Color back, Font font)
    {
        if (box.Width <= 0 || box.Height <= 0) return;
        if (text.IsEmpty) { Fill(box, back); return; }
        if (Variable(font) || text.IndexOfAnyExceptInRange(' ', '~') >= 0)
        {
            Fill(box, back);
            // Saved and restored by hand rather than through a scope object: the text is a span, and a span
            // cannot be captured by anything that outlives the call.
            int saved = SaveDC(_hdc);
            try
            {
                IntersectClipRect(_hdc, box.Left, box.Top, box.Right, box.Bottom);
                TextRenderer.DrawText(this, text, font, new Point(x, y), fore, back, Plain);
            }
            finally { RestoreDC(_hdc, saved); Unknown(); }
            return;
        }

        Ready(font, fore, back);
        var rect = new Rect(box);
        ExtTextOut(_hdc, x, y, EtoOpaque | EtoClipped, ref rect,
                   in System.Runtime.InteropServices.MemoryMarshal.GetReference(text), (uint)text.Length,
                   IntPtr.Zero);
    }

    /// <summary>Text over whatever is already there: a found word over its highlight, the selected part of
    /// a line over the selection. Transparent, unlike <see cref="Text"/> - these are drawn on top of pixels
    /// that are already right, and telling GDI to lay a background down with them would square off the
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
        finally { RestoreDC(_hdc, saved); Unknown(); }
    }

    /// <summary>Puts the context into the state a draw wants, saying only what has changed since the last
    /// one. A screenful is hundreds of draws in a handful of colours and one font.</summary>
    private void Ready(Font font, Color fore, Color back)
    {
        IntPtr face = Face(font).Handle;
        if (face != _face)
        {
            IntPtr was = SelectObject(_hdc, face);
            if (_wasFace == IntPtr.Zero) _wasFace = was;
            _face = face;
        }
        int ink = ColorRef(fore);
        if (ink != _fore) { SetTextColor(_hdc, ink); _fore = ink; }
        int behind = ColorRef(back);
        if (behind != _back) { SetBkColor(_hdc, behind); _back = behind; }
        if (!_opaque) { SetBkMode(_hdc, OpaqueBackground); _opaque = true; }
    }

    private const TextFormatFlags Plain = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

    /// <summary>Narrows the clip to a box for as long as the returned scope lives. Used where drawing is
    /// laid out per cell but must not reach past the text area as a whole.</summary>
    public ClipScope Clip(Rectangle box) => new(this, box);

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
            _canvas.Unknown();
        }
    }

    /// <summary>Text laid out inside a box - right-aligned line numbers, aligned column cells - over a
    /// background it is told about, which is what makes it cheap.</summary>
    public void TextIn(ReadOnlySpan<char> text, Rectangle box, Color fore, Color back, Font font,
                       TextFormatFlags flags)
    {
        if (box.Width <= 0 || box.Height <= 0) return;
        TextRenderer.DrawText(this, text, font, box, fore, back, flags);
        Unknown();
    }

    /// <summary>The same, over what is already there.</summary>
    public void TextIn(ReadOnlySpan<char> text, Rectangle box, Color fore, Font font, TextFormatFlags flags)
    {
        if (box.Width <= 0 || box.Height <= 0) return;
        TextRenderer.DrawText(this, text, font, box, fore, flags);
        Unknown();
    }

    /// <summary>
    /// The GDI font handle for a font, and whether its characters differ in width, worked out once and kept.
    ///
    /// <para>The handle is made the way <see cref="TextRenderer"/> makes its own, which matters for one
    /// field: <c>lfQuality</c>. <see cref="Font.ToHfont"/> leaves that at DEFAULT_QUALITY - "system, you
    /// decide" - while WinForms works out what to ask for from the machine's own font-smoothing settings.
    /// Where the two answers differ, text drawn straight comes out with ClearType's colour fringes and text
    /// that was laid out comes out grey, in the same view. Asking those settings the same question WinForms
    /// asks makes the two agree on any machine, rather than only on one whose smoothing happens to match.
    /// It is asked once here and once there, so even a machine whose smoothing is changed while the app is
    /// running has the two still agreeing with each other.</para>
    ///
    /// <para>Whether the face is fixed-pitch is GDI's to say rather than a caller's to pass in and get
    /// wrong: it decides which of the two draws may be used, and the wrong answer means misplaced text.</para>
    /// </summary>
    private Realised Face(Font font)
    {
        if (_faces.TryGetValue(font, out var known)) return known;
        var logFont = new LogFont();
        font.ToLogFont(logFont);
        logFont.lfQuality = Smoothing;
        IntPtr face = CreateFontIndirect(logFont);
        if (face == IntPtr.Zero) face = font.ToHfont();
        IntPtr was = SelectObject(_hdc, face);
        GetTextMetrics(_hdc, out var metrics);
        SelectObject(_hdc, was);
        return _faces[font] = new Realised(face, (metrics.tmPitchAndFamily & VariablePitch) != 0);
    }

    private readonly record struct Realised(IntPtr Handle, bool Variable);

    /// <summary>Whether the face spaces its characters unevenly, which decides how its text is drawn.</summary>
    private bool Variable(Font font) => Face(font).Variable;

    /// <summary>What WinForms asks GDI for when it lays text out on a context of ours, read from the
    /// machine's font-smoothing settings exactly as it reads them.</summary>
    private static readonly byte Smoothing =
        !SystemInformation.IsFontSmoothingEnabled ? ProofQuality
        : SystemInformation.FontSmoothingType == ClearTypeSmoothing ? ClearTypeQuality
        : AntialiasedQuality;

    private const byte ProofQuality = 2, AntialiasedQuality = 4, ClearTypeQuality = 5;
    private const int ClearTypeSmoothing = 2;

    /// <summary>Which quality the text is drawn at, so a check can say what it is looking at.</summary>
    internal static byte SmoothingForTesting => Smoothing;

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

    private const int OpaqueBackground = 2, TransparentBackground = 1;
    private const byte VariablePitch = 0x01;
    private const uint EtoOpaque = 0x0002, EtoClipped = 0x0004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class LogFont
    {
        public int lfHeight, lfWidth, lfEscapement, lfOrientation, lfWeight;
        public byte lfItalic, lfUnderline, lfStrikeOut, lfCharSet, lfOutPrecision, lfClipPrecision,
                    lfQuality, lfPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string lfFaceName = "";
    }

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

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFontIndirectW")]
    private static extern IntPtr CreateFontIndirect(LogFont font);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TextMetric
    {
        public int tmHeight, tmAscent, tmDescent, tmInternalLeading, tmExternalLeading, tmAveCharWidth,
                   tmMaxCharWidth, tmWeight, tmOverhang, tmDigitizedAspectX, tmDigitizedAspectY;
        public char tmFirstChar, tmLastChar, tmDefaultChar, tmBreakChar;
        public byte tmItalic, tmUnderlined, tmStruckOut, tmPitchAndFamily, tmCharSet;
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetTextMetricsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTextMetrics(IntPtr hdc, out TextMetric metrics);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern void SetTextColor(IntPtr hdc, int colour);

    [DllImport("gdi32.dll")]
    private static extern void SetBkColor(IntPtr hdc, int colour);

    [DllImport("gdi32.dll")]
    private static extern void SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtTextOutW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ExtTextOut(IntPtr hdc, int x, int y, uint options, ref Rect rect,
                                          in char text, uint length, IntPtr spacing);

    [DllImport("gdi32.dll")]
    private static extern int SaveDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern void RestoreDC(IntPtr hdc, int saved);

    [DllImport("gdi32.dll")]
    private static extern void IntersectClipRect(IntPtr hdc, int left, int top, int right, int bottom);

    [DllImport("user32.dll")]
    private static extern void FillRect(IntPtr hdc, ref Rect rect, IntPtr brush);
}
