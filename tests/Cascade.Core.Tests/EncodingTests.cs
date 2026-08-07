using System.Text;
using Cascade.Core.Document;
using Cascade.Core.Find;
using Cascade.Core.Model;
using Cascade.Core.Text;

namespace Cascade.Core.Tests;

/// <summary>
/// Opens real files in every encoding the app offers and checks the whole chain end to end: the BOM is
/// skipped, lines are split on the right code unit, non-ASCII text decodes, and both filtering and find
/// agree with the text that is on screen. A file is a stream of bytes and everything downstream depends on
/// reading it the way it was written, so these go through <see cref="CascadeDocument"/> rather than the
/// detector alone.
/// </summary>
public class EncodingTests
{
    static EncodingTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    // Deliberately not ASCII: every one of these round-trips through Windows-1252 as well as Unicode, so the
    // same expectation can be used for all of the single-byte and Unicode cases.
    private static readonly string[] Latin =
    [
        "INFO café ready",
        "WARN naïve façade",
        "ERROR Grüße für München",
        "INFO plain line",
    ];

    // Beyond any single-byte code page, and the last one is outside the BMP (a surrogate pair in UTF-16).
    private static readonly string[] Wide =
    [
        "INFO 日本語のログ",
        "WARN Ελληνικά",
        "ERROR Кириллица",
        "INFO emoji 🚀 tail",
    ];

    private static byte[] Bytes(Encoding enc, bool bom, string[] lines, string newline = "\n")
    {
        byte[] body = lines.Length == 0 ? [] : enc.GetBytes(string.Join(newline, lines) + newline);
        if (!bom) return body;
        byte[] pre = enc.GetPreamble();
        return pre.Length == 0 ? body : [.. pre, .. body];
    }

    private static List<string> ReadThrough(byte[] bytes, Encoding? forced)
    {
        string path = Harness.TempFile(bytes);
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(path, forced);
            doc.WaitForIndex();
            var lines = new List<string>();
            for (long i = 0; i < doc.CompletedLineCount; i++) lines.Add(doc.GetLineText(i));
            return lines;
        }
        finally { TryDelete(path); }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static UnicodeEncoding Utf16(bool bigEndian, bool bom) => new(bigEndian, bom);
    private static UTF32Encoding Utf32(bool bigEndian, bool bom) => new(bigEndian, bom);
    private static UTF8Encoding Utf8(bool bom) => new(bom);
    private static Encoding Windows1252 => Encoding.GetEncoding(1252);

    public static TheoryData<string, bool> Unicodes => new()
    {
        { "utf-8", false }, { "utf-8", true },
        { "utf-16le", false }, { "utf-16le", true },
        { "utf-16be", false }, { "utf-16be", true },
        { "utf-32le", false }, { "utf-32le", true },
        { "utf-32be", false }, { "utf-32be", true },
    };

    private static Encoding Named(string name, bool bom) => name switch
    {
        "utf-8" => Utf8(bom),
        "utf-16le" => Utf16(false, bom),
        "utf-16be" => Utf16(true, bom),
        "utf-32le" => Utf32(false, bom),
        "utf-32be" => Utf32(true, bom),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    // ---- what the user chooses ----

    [Theory]
    [MemberData(nameof(Unicodes))]
    public void An_encoding_chosen_from_the_menu_reads_the_file_correctly(string name, bool bom)
    {
        var enc = Named(name, bom);
        Assert.Equal(Wide, ReadThrough(Bytes(enc, bom, Wide), enc));
        Assert.Equal(Latin, ReadThrough(Bytes(enc, bom, Latin), enc));
    }

    [Fact]
    public void Windows_1252_is_only_readable_when_it_is_chosen()
    {
        byte[] bytes = Bytes(Windows1252, bom: false, Latin);

        // Nothing in the bytes says which code page they are, so auto-detection has to guess UTF-8 - and
        // 0xE9 ("é") is not valid UTF-8, so it comes back as replacement characters. That is exactly the
        // situation the menu exists for.
        var guessed = ReadThrough(bytes, null);
        Assert.NotEqual(Latin, guessed);
        Assert.Contains(guessed, l => l.Contains('\uFFFD', StringComparison.Ordinal));

        Assert.Equal(Latin, ReadThrough(bytes, Windows1252));
    }

    [Fact]
    public void Utf16_without_a_byte_order_mark_needs_the_menu_too()
    {
        // The low byte of a UTF-16 LE newline is 0x0A, so a byte-wise scan happens to split in the right
        // places - but every character decodes with a NUL after it. Lines are only right once UTF-16 is
        // chosen, which is the other reason the menu is there.
        byte[] bytes = Bytes(Utf16(false, false), bom: false, Latin);
        Assert.NotEqual(Latin, ReadThrough(bytes, null));
        Assert.Equal(Latin, ReadThrough(bytes, Utf16(false, bom: false)));
    }

    // ---- what the app works out for itself ----

    [Theory]
    [MemberData(nameof(Unicodes))]
    public void A_byte_order_mark_is_detected_and_never_shows_up_in_the_text(string name, bool bom)
    {
        var enc = Named(name, bom);
        var read = ReadThrough(Bytes(enc, bom, Wide), null);
        if (bom)
        {
            Assert.Equal(Wide, read);
            Assert.DoesNotContain('\uFEFF', read[0]);
        }
        else if (name == "utf-8")
        {
            Assert.Equal(Wide, read); // UTF-8 is the fallback, so a BOM is not needed
        }
    }

    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16le")]
    [InlineData("utf-16be")]
    [InlineData("utf-32le")]
    [InlineData("utf-32be")]
    public void Carriage_returns_are_trimmed_whatever_the_encoding(string name)
    {
        var enc = Named(name, true);
        Assert.Equal(Latin, ReadThrough(Bytes(enc, true, Latin, "\r\n"), null));
    }

    [Theory]
    [InlineData("utf-16le")]
    [InlineData("utf-16be")]
    [InlineData("utf-32le")]
    [InlineData("utf-32be")]
    public void A_stray_0x0A_byte_inside_a_character_does_not_split_a_line(string name)
    {
        // U+0A41 (LE) / U+410A both contain a 0x0A byte that is not a newline. A byte-wise scan would cut
        // the line in half; the per-code-unit scan must not.
        var enc = Named(name, true);
        string[] lines = ["one \u0a41\u410a two", "three"];
        Assert.Equal(lines, ReadThrough(Bytes(enc, true, lines), null));
    }

    [Fact]
    public void An_empty_file_opens_in_any_encoding()
    {
        foreach (var (name, bom) in new[] { ("utf-8", false), ("utf-16le", true), ("utf-32be", true) })
            Assert.Empty(ReadThrough(Bytes(Named(name, bom), bom, []), Named(name, bom)));
    }

    [Fact]
    public void A_file_holding_nothing_but_a_byte_order_mark_has_no_lines()
    {
        Encoding[] all = [Utf8(true), Utf16(false, true), Utf16(true, true), Utf32(false, true), Utf32(true, true)];
        foreach (var enc in all) Assert.Empty(ReadThrough(enc.GetPreamble(), null));
    }

    // ---- everything downstream reads the same text ----

    [Theory]
    [InlineData("utf-8", false)]
    [InlineData("utf-16le", true)]
    [InlineData("utf-16be", true)]
    [InlineData("utf-32le", true)]
    public async Task Filtering_and_find_see_the_decoded_text_not_the_bytes(string name, bool bom)
    {
        var enc = Named(name, bom);
        var body = new List<string>();
        for (int i = 0; i < 500; i++) body.Add(Latin[i % Latin.Length] + " " + i);
        string path = Harness.TempFile(Bytes(enc, bom, [.. body]));
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(path, enc);
            doc.WaitForIndex();

            // Find has to be looking at decoded characters, not bytes. (Run it before anything is hidden:
            // in filtered mode find deliberately refuses to land on a line the view is not showing.)
            long hit = await doc.FindNextAsync(new FindQuery("Grüße", false, false), 0, true, CancellationToken.None);
            Assert.Equal(2, hit);

            // And a filter on a non-ASCII word only matches if the bytes were decoded with this encoding.
            doc.Filters.Add(new Filter { Enabled = true, Match = { Text = "café" } });
            doc.Filters.ShowOnlyFilteredLines = true;
            doc.ApplyFilters();
            WaitFilter(doc);
            Assert.Equal(125, doc.MatchedLineCount);
            Assert.Contains("café", doc.GetLineText(doc.RowToLine(0)), StringComparison.Ordinal);
        }
        finally { TryDelete(path); }
    }

    private static void WaitFilter(CascadeDocument doc, int timeoutMs = 10000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (doc.IsIndexComplete && doc.IsFilterIdle) return;
            Thread.Sleep(5);
        }
        throw new TimeoutException("Filtering did not become idle in time.");
    }

    // ---- the menu's contract ----

    [Fact]
    public void The_encoding_in_effect_is_reported_back()
    {
        string path = Harness.TempFile(Bytes(Utf16(true, true), bom: true, Latin));
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(path);
            doc.WaitForIndex();
            Assert.Equal(1201, doc.Encoding.CodePage); // UTF-16 BE

            doc.Open(path, Windows1252);
            doc.WaitForIndex();
            Assert.Equal(1252, doc.Encoding.CodePage);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Choosing_an_encoding_overrides_a_byte_order_mark()
    {
        // The mark says UTF-8. Asking for Windows-1252 has to win: the menu is how a reader says the file is
        // not what it claims to be, and a choice that silently does nothing is what makes it look broken.
        // The mark's own three bytes then read as the characters they stand for in that code page.
        byte[] bytes = Bytes(Utf8(true), bom: true, ["hello"]);
        Assert.Equal(["\u00ef\u00bb\u00bfhello"], ReadThrough(bytes, Windows1252));
    }

    [Fact]
    public void A_matching_byte_order_mark_is_still_skipped_when_the_encoding_is_chosen_by_hand()
    {
        byte[] bytes = Bytes(Utf8(true), bom: true, Latin);
        Assert.Equal(Latin, ReadThrough(bytes, Utf8(false)));

        byte[] u16 = Bytes(Utf16(false, true), bom: true, Latin);
        Assert.Equal(Latin, ReadThrough(u16, Utf16(false, bom: false)));
    }

    [Fact]
    public void Detection_reports_the_code_unit_size_the_scanner_needs()
    {
        Assert.Equal((1, 0), Size(EncodingDetector.Detect(Utf8(false).GetBytes("abc"))));
        Assert.Equal((1, 3), Size(EncodingDetector.Detect(Utf8(true).GetPreamble())));
        Assert.Equal((2, 2), Size(EncodingDetector.Detect(Utf16(false, true).GetPreamble())));
        Assert.Equal((2, 2), Size(EncodingDetector.Detect(Utf16(true, true).GetPreamble())));
        Assert.Equal((4, 4), Size(EncodingDetector.Detect(Utf32(false, true).GetPreamble())));
        Assert.Equal((4, 4), Size(EncodingDetector.Detect(Utf32(true, true).GetPreamble())));

        // The UTF-32 LE mark starts with the UTF-16 LE mark, so order matters.
        Assert.Equal(12000, EncodingDetector.Detect(Utf32(false, true).GetPreamble()).Encoding.CodePage);

        static (int Unit, int Preamble) Size(DetectedEncoding d) => (d.UnitSize, d.PreambleLength);
    }

    [Theory]
    [InlineData("utf-16le", 1200)]
    [InlineData("utf-16be", 1201)]
    [InlineData("utf-32le", 12000)]
    [InlineData("utf-32be", 12001)]
    public void Endianness_survives_a_hand_picked_encoding_with_no_mark(string name, int codePage)
    {
        var enc = Named(name, bom: false);
        var d = EncodingDetector.ForEncoding(enc, ReadOnlySpan<byte>.Empty);
        Assert.Equal(codePage, d.Encoding.CodePage);
        Assert.Equal(codePage is 1201 or 12001, d.BigEndian);
        Assert.Equal(codePage is 1200 or 1201 ? 2 : 4, d.UnitSize);
    }
}
