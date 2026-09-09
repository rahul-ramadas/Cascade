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
    // Keyed by the Font object rather than by its value: a view draws from a fixed set of eight, reused
    // frame after frame, so reference identity settles it without hashing a font description per row.
    private readonly Dictionary<Font, IntPtr> _faces = new(ReferenceEqualityComparer.Instance);
    private Graphics? _over;
    private IntPtr _hdc;
    private int _restoreTo;

    // What the context has already been told, so a row that draws in the same colours and the same face as
    // the row above it says nothing at all. Reset whenever something else may have set them - a paint that
    // went through TextRenderer, or a context just borrowed.
    private IntPtr _face;
    private int _fore = -1;
    private int _back = -1;
    private bool _placed;

    /// <summary>Takes the context out of the Graphics. Nothing may touch that Graphics again until
    /// <see cref="Release"/> - GDI+ holds it locked meanwhile, and drawing on it throws.</summary>
    public void Borrow(Graphics over)
    {
        _over = over;
        _hdc = over.GetHdc();
        // One saved state for the whole frame, put back in one call. The font, the colours and the clip are
        // all changed below and all belong to whoever lent the context.
        _restoreTo = SaveDC(_hdc);
        Forget();
    }

    public void Release()
    {
        if (_over is null) return;
        RestoreDC(_hdc, _restoreTo);
        _over.ReleaseHdc(_hdc);
        _over = null;
        _hdc = IntPtr.Zero;
    }

    private void Forget()
    {
        _face = IntPtr.Zero;
        _fore = _back = -1;
        _placed = false;
    }

    public bool Holding => _over is not null;

    IntPtr IDeviceContext.GetHdc() => _hdc;

    void IDeviceContext.ReleaseHdc() { }

    /// <summary>Only ever called by <see cref="TextRenderer"/>'s own plumbing, which disposes nothing it
    /// did not create. The brushes are let go of in <see cref="Discard"/>.</summary>
    void IDisposable.Dispose() { }

    /// <summary>Gives back the brushes and font handles kept for the colours and faces drawn so far. The
    /// colours of a view change when its settings or its filters do, and its faces when the font or the zoom
    /// does - all rare, and there are a handful of each at a time.</summary>
    public void Discard()
    {
        Release();
        foreach (var brush in _brushes.Values) DeleteObject(brush);
        _brushes.Clear();
        foreach (var face in _faces.Values) if (face != IntPtr.Zero) DeleteObject(face);
        _faces.Clear();
        Forget();
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
    /// <para>Plain printable ASCII in a face that needs no laying out - which is nearly every character of
    /// nearly every log - goes straight to <c>ExtTextOut</c>, told to fill the box and clip to it as part of
    /// the same call. That is ONE call into GDI where the general road is five: a fill, a saved state, a
    /// clip, the text, and the state put back. Each of those takes the kernel's lock on the device context,
    /// which MEASURED at 18% of a frame, and the text one also takes a lock inside <see cref="TextRenderer"/>
    /// to find the font handle it has cached - 9% more. MEASURED on a screenful of 1,150-character lines:
    /// 8.49 ms through TextRenderer against 5.95 direct. Where the screen is at the far end of a wire it is
    /// also one drawing order sent down it instead of five.</para>
    ///
    /// <para><paramref name="plainFace"/> is the caller's word that the font places its characters by width
    /// alone. Only it knows: the same fixed-pitch test that lets a caller work a width out by multiplying is
    /// what makes the two roads agree, and a proportional face is laid out by the text call itself - kerned
    /// and shaped - which MEASURED as a different picture, so it still goes the general way.</para>
    ///
    /// <para>So does anything that is not printable ASCII: a script that needs shaping, or a character the
    /// font does not have and Windows must go looking for in another.</para>
    /// </summary>
    public void Text(ReadOnlySpan<char> text, int x, int y, Rectangle box, Color fore, Color back, Font font,
                     bool plainFace)
    {
        if (box.Width <= 0 || box.Height <= 0) return;
        if (text.IsEmpty) { Fill(box, back); return; }

        if (plainFace && Simple(text) && Face(font) is var face && face != IntPtr.Zero)
        {
            Prepare(face, fore, back);
            var opaque = new Rect(box);
            unsafe
            {
                fixed (char* chars = text)
                    ExtTextOut(_hdc, x, y, EtoOpaque | EtoClipped, ref opaque, chars, text.Length, IntPtr.Zero);
            }
            return;
        }

        Fill(box, back);
        // Saved and restored by hand rather than through a scope object: the text is a span, and a span
        // cannot be captured by anything that outlives the call.
        int saved = SaveDC(_hdc);
        try
        {
            IntersectClipRect(_hdc, box.Left, box.Top, box.Right, box.Bottom);
            TextRenderer.DrawText(this, text, font, new Point(x, y), fore, back, Plain);
        }
        finally { RestoreDC(_hdc, saved); Forget(); }
    }

    /// <summary>Whether a stretch of text is the printable ASCII that needs no shaping and no font but the
    /// one asked for, and so can go the short way.</summary>
    private static bool Simple(ReadOnlySpan<char> text) => text.IndexOfAnyExceptInRange(' ', '~') < 0;

    /// <summary>Tells the context only what it does not already know.</summary>
    private void Prepare(IntPtr face, Color fore, Color back)
    {
        if (!_placed)
        {
            // Where ExtTextOut puts what it is given: the top-left corner of the cell at the point asked
            // for, which is where the general call with NoPadding puts it too.
            SetTextAlign(_hdc, TopLeft);
            SetBkMode(_hdc, OpaqueText);
            _placed = true;
        }
        if (face != _face) { SelectObject(_hdc, face); _face = face; }
        int ink = ColorRef(fore);
        if (ink != _fore) { SetTextColor(_hdc, ink); _fore = ink; }
        int behind = ColorRef(back);
        if (behind != _back) { SetBkColor(_hdc, behind); _back = behind; }
    }

    /// <summary>
    /// A font handle that draws at the width the layout measures, or zero when none can be had.
    ///
    /// <para>The two are not the same font by default. <see cref="Font.ToHfont"/> rounds the em size its own
    /// way, and wherever a point size does not land on a whole pixel that rounding differs from the one
    /// <see cref="TextRenderer"/> uses - so the short road would draw the same characters at a different
    /// pitch. MEASURED on a 96 DPI screen, ten characters of 10pt Consolas came out 70 pixels through
    /// ToHfont against 80 through the layout: a tenth narrower, which would put every column, every mark on
    /// a found word and the sideways scrollbar's range in the wrong place. At 144 DPI the same font lands on
    /// exactly 20 pixels, both agree, and none of it shows - which is why this has to be asked rather than
    /// reasoned about.</para>
    ///
    /// <para>So the height is not trusted but chosen: the neighbours of what GDI+ suggests are tried, and
    /// the first that measures what the layout measures is kept. If none does, the answer is zero and the
    /// caller stays on the general road, which is slower and always right.</para>
    /// </summary>
    private IntPtr Face(Font font)
    {
        if (_faces.TryGetValue(font, out var handle)) return handle;

        int want = TextRenderer.MeasureText(this, Probe, font, new Size(int.MaxValue, int.MaxValue), Plain).Width;
        var description = new LogFont();
        font.ToLogFont(description);
        int suggested = description.lfHeight;

        foreach (int height in (int[])[suggested, suggested - 1, suggested + 1, suggested - 2, suggested + 2])
        {
            description.lfHeight = height;
            IntPtr candidate = CreateFontIndirect(description);
            if (candidate == IntPtr.Zero) continue;
            if (Draws(candidate) == want) return _faces[font] = candidate;
            DeleteObject(candidate);
        }
        return _faces[font] = IntPtr.Zero;
    }

    /// <summary>How wide <see cref="Probe"/> is drawn by a candidate handle, asked of the same context the
    /// text will be drawn on.</summary>
    private int Draws(IntPtr face)
    {
        IntPtr was = SelectObject(_hdc, face);
        GetTextExtentPoint32(_hdc, Probe, Probe.Length, out var size);
        SelectObject(_hdc, was);
        _face = IntPtr.Zero;   // the context no longer holds what this canvas thinks it does
        return size.Width;
    }

    /// <summary>Enough characters that a rounding of half a pixel each shows up as a whole one.</summary>
    private const string Probe = "0000000000";

    /// <summary>How many faces have been given a handle that draws what the layout measures, and how many
    /// could not be. A check reads these to prove the short road is really being taken: it fails safe, so
    /// a search that stopped working would leave every picture right and the speed quietly gone.</summary>
    internal (int Working, int Rejected) FacesForTesting
    {
        get
        {
            int working = 0, rejected = 0;
            foreach (var face in _faces.Values) { if (face == IntPtr.Zero) rejected++; else working++; }
            return (working, rejected);
        }
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
        finally { RestoreDC(_hdc, saved); Forget(); }
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

        // Putting the state back puts the caller's font and colours back with it, so what this canvas
        // believes the context has been told is no longer true.
        public void Dispose() { RestoreDC(_canvas._hdc, _saved); _canvas.Forget(); }
    }

    /// <summary>Text laid out inside a box - right-aligned line numbers, aligned column cells - over a
    /// background it is told about, which is what makes it cheap.</summary>
    public void TextIn(ReadOnlySpan<char> text, Rectangle box, Color fore, Color back, Font font,
                       TextFormatFlags flags)
    {
        if (box.Width <= 0 || box.Height <= 0) return;
        TextRenderer.DrawText(this, text, font, box, fore, back, flags);
        Forget();
    }

    /// <summary>The same, over what is already there.</summary>
    public void TextIn(ReadOnlySpan<char> text, Rectangle box, Color fore, Font font, TextFormatFlags flags)
    {
        if (box.Width <= 0 || box.Height <= 0) return;
        TextRenderer.DrawText(this, text, font, box, fore, flags);
        Forget();
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

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern void SetTextColor(IntPtr hdc, int colour);

    [DllImport("gdi32.dll")]
    private static extern void SetBkColor(IntPtr hdc, int colour);

    [DllImport("gdi32.dll")]
    private static extern void SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern void SetTextAlign(IntPtr hdc, uint align);

    [DllImport("gdi32.dll", EntryPoint = "ExtTextOutW")]
    private static extern unsafe void ExtTextOut(IntPtr hdc, int x, int y, uint options, ref Rect rect,
        char* text, int count, IntPtr spacing);

    [DllImport("gdi32.dll", EntryPoint = "CreateFontIndirectW", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontIndirect([In] LogFont description);

    [DllImport("gdi32.dll", EntryPoint = "GetTextExtentPoint32W", CharSet = CharSet.Unicode)]
    private static extern bool GetTextExtentPoint32(IntPtr hdc, string text, int count, out Size size);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class LogFont
    {
        public int lfHeight;
        public int lfWidth;
        public int lfEscapement;
        public int lfOrientation;
        public int lfWeight;
        public byte lfItalic;
        public byte lfUnderline;
        public byte lfStrikeOut;
        public byte lfCharSet;
        public byte lfOutPrecision;
        public byte lfClipPrecision;
        public byte lfQuality;
        public byte lfPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string lfFaceName = "";
    }

    private const int OpaqueText = 2;
    private const uint EtoOpaque = 0x0002;
    private const uint EtoClipped = 0x0004;
    private const uint TopLeft = 0;   // TA_LEFT | TA_TOP | TA_NOUPDATECP
}
