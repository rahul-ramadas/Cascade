using Cascade.Core.Columns;

namespace Cascade.Core.Tests;

/// <summary>The template language: what it matches, what it refuses, and where it says a line stopped
/// fitting. The trace format used throughout is a real one - nine bracketed fields and a message - because
/// that is the shape the feature exists for.</summary>
public class TemplateTests
{
    private const string Trace =
        "[2026-08-05T05:00:02.0472099][BthPort][6][0EF8][1590][rundown_cpp142][PerformWppRundown][INFO][TFLAG_RUNDOWN] WDF PnP state: WdfDevStatePnpStarted";

    private const string TraceEmpty =
        "[2026-08-05T05:00:01.9216907][MSNT_SystemTrace][0][1474][0DA4][Header][][0][0x0] {\"BufferSize\":1048576}";

    private const string Nine = "{[*]}{[*]}{[*]}{[*]}{[*]}{[*]}{[*]}{[*]}{[*]} {*}";

    private static string[] Values(string template, string line)
    {
        var t = new LineTemplate(template);
        Assert.True(t.IsValid, string.Join(" / ", t.Issues.Select(i => i.Message)));
        var m = new TemplateMatch();
        Assert.True(t.Match(line, m), $"no match; wanted <{m.FailureExpected}> at {m.FailurePosition}");
        return Enumerable.Range(0, m.ValueCount).Select(i =>
        {
            var (start, length) = m.Value(i);
            return line.Substring(start, length);
        }).ToArray();
    }

    private static string Part(string template, string line, int part)
    {
        var t = new LineTemplate(template);
        var m = new TemplateMatch();
        Assert.True(t.Match(line, m));
        var (start, length) = m.Part(part);
        return line.Substring(start, length);
    }

    // ---- matching ----

    [Fact]
    public void Nine_bracketed_fields_and_a_message()
    {
        Assert.Equal(
            ["2026-08-05T05:00:02.0472099", "BthPort", "6", "0EF8", "1590",
             "rundown_cpp142", "PerformWppRundown", "INFO", "TFLAG_RUNDOWN",
             "WDF PnP state: WdfDevStatePnpStarted"],
            Values(Nine, Trace));
    }

    /// <summary>An empty field captures nothing but the part around it is still there, and braces in the
    /// data are only data - the template language reads the TEMPLATE, never the line.</summary>
    [Fact]
    public void An_empty_field_and_braces_in_the_data()
    {
        var values = Values(Nine, TraceEmpty);
        Assert.Equal("", values[6]);
        Assert.Equal("{\"BufferSize\":1048576}", values[9]);
        Assert.Equal("[]", Part(Nine, TraceEmpty, 6));
    }

    /// <summary>The point of a value stopping at the next LITERAL rather than the next space: a timestamp
    /// with a space in it is one field, and scanf-style parsing gets this wrong.</summary>
    [Fact]
    public void A_value_stops_at_the_next_literal_not_the_next_space()
        => Assert.Equal(["2026-08-05 09:31:02", "handled request"],
            Values("{[*]} {*}", "[2026-08-05 09:31:02] handled request"));

    [Fact]
    public void One_space_in_the_template_matches_a_run_of_them()
        => Assert.Equal(["a", "b"], Values("{[*]} {*}", "[a]      b"));

    /// <summary>A run that starts with a space has no literal to jump to, so the matcher has to try. Trying
    /// EVERY position is quadratic, and a line of a few hundred thousand spaces then takes seconds - on a
    /// line that may be megabytes, that is a hang. Only the first space of each run can differ, so that is
    /// all that is tried.</summary>
    [Fact]
    public void A_line_of_nothing_but_spaces_does_not_take_for_ever()
    {
        var t = new LineTemplate("{* z}{*}");
        var m = new TemplateMatch();

        // Warm, so the first call's JIT is not what is being timed.
        t.Match(new string(' ', 1000) + "nothing", m);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(t.Match(new string(' ', 400_000) + "nothing to find", m));
        watch.Stop();

        // Linear is a fraction of a millisecond; quadratic was twenty seconds at this length.
        Assert.True(watch.ElapsedMilliseconds < 250, $"took {watch.ElapsedMilliseconds} ms");
    }

    /// <summary>The same shape, but matching - so the faster scan is not simply refusing everything.</summary>
    [Fact]
    public void A_run_of_spaces_before_a_literal_still_matches()
    {
        Assert.Equal(["a", "rest"], Values("{* z}{*}", "a" + new string(' ', 5000) + "zrest"));
        Assert.Equal(["a", "b"], Values("{* }{*}", "a   b"));
    }

    /// <summary>Spaces on either side of a part boundary read as one run and have to behave as one. Taken
    /// as two, the first would eat them all and the second could never be satisfied - so the template
    /// matched NOTHING, while still reporting itself perfectly valid.</summary>
    [Theory]
    [InlineData("{* } {*}")]
    [InlineData("{* }{ *}")]
    [InlineData("{*}  {*}")]
    public void Spaces_either_side_of_a_part_boundary_are_one_run(string template)
    {
        var t = new LineTemplate(template);
        Assert.True(t.IsValid);
        var m = new TemplateMatch();
        foreach (string line in new[] { "a b", "a  b", "a      b" })
            Assert.True(t.Match(line, m), $"<{template}> did not fit <{line}>");
        Assert.Equal(["a", "b"], Values(template, "a  b"));
    }

    /// <summary>The contract, checked over a spread of templates and lines rather than one case at a time:
    /// a template may SHORTEN a line, and may put a single joiner in when a field is carried backwards, but
    /// every other character it shows has to come from the line it was given, at a place that maps back.
    ///
    /// <para>Every arrangement is tried, not just one: several fields hidden at once (which is what makes a
    /// run of hidden fields close up to a single separator) and orders that jump backwards and then forwards
    /// again, which reaches a different path than simply reversing them.</para></summary>
    [Fact]
    public void A_projection_never_invents_text_it_was_not_given()
    {
        string[] templates =
        [
            Nine, "{[*]} {*}", "{*} {*} {*}", "{* }{ }{*}", "{a }{ }{b}", "{[*]}{*}", "{ERR }{*}",
            "{[*] } {*}", "{*}", "{[*]}", @"\{{*}\}", "{*\t}{*}", "a { }{*}", "{a }{ }", "{a }{ }{ }{b}"
        ];
        string[] lines =
        [
            Trace, TraceEmpty, "a  b", "a b c", "ERR something", "[x]y", "{z}", "a\tb",
            "", "[", "[]", "plain line with nothing in it", "[a] and a tail", "a  x", "[09:31] [INFO] hi"
        ];

        var match = new TemplateMatch();
        var projection = new LineProjection();
        int matched = 0, arrangements = 0;

        foreach (string templateText in templates)
        {
            var t = new LineTemplate(templateText);
            Assert.True(t.IsValid, $"<{templateText}>: {string.Join(" / ", t.Issues.Select(i => i.Message))}");

            foreach (string line in lines)
            {
                if (!t.Match(line, match)) continue;
                matched++;

                // The parts are in template order whatever the match did or did not touch.
                for (int i = 1; i < match.PartCount; i++)
                    Assert.True(match.Part(i).Start >= match.Part(i - 1).Start,
                        $"<{templateText}> on <{line}>: part {i} starts before part {i - 1}");

                foreach (int[] order in Orders(t.PartCount))
                    for (int hidden = 0; hidden < (1 << Math.Min(t.PartCount, 4)); hidden++)
                    {
                        var spec = new ColumnSpec { Enabled = true, Template = templateText, Layout = FieldLayout.Inline };
                        spec.Reset();
                        for (int i = 0; i < spec.Columns.Count; i++)
                            if ((hidden & (1 << Math.Min(i, 3))) != 0) spec.Columns[i].Visible = false;
                        var shuffled = order.Select(i => spec.Columns[i]).ToList();
                        spec.Columns.Clear();
                        spec.Columns.AddRange(shuffled);

                        projection.Build(line, spec, match);
                        arrangements++;
                        string what = $"<{templateText}> on <{line}> hiding {hidden} as [{string.Join(",", order)}]";

                        int invented = 0;
                        for (int i = 0; i < projection.Text.Length; i++)
                        {
                            int at = projection.ToLine(i);
                            if (at < 0) { invented++; continue; }
                            Assert.True(at < line.Length, $"{what}: character {i} maps past the end");
                            Assert.Equal(line[at], projection.Text[i]);
                            // ...and the map has to work the other way round too, which is what the checks
                            // on a selection crossing hidden text actually run on.
                            Assert.Equal(i, projection.FromLine(at));
                        }

                        // Only a field carried BACKWARDS invents anything, and only one joiner apiece.
                        int backwards = 0;
                        int previous = -1;
                        foreach (var column in spec.Columns)
                        {
                            if (!column.Visible) continue;
                            if (column.Source < previous) backwards++;
                            previous = column.Source;
                        }
                        Assert.True(invented <= backwards * LineProjection.Joiner.Length,
                            $"{what}: invented {invented} characters for {backwards} backwards moves");
                        Assert.True(projection.Text.Length - invented <= line.Length,
                            $"{what}: gave back more of the line than it was given (<{projection.Text}>)");
                        if (projection.IsWholeLine) Assert.Same(line, projection.Text);
                    }
            }
        }

        // Without this the sweep could pass by matching nothing at all, which is how a test this size
        // quietly stops testing anything.
        Assert.True(matched >= 40, $"only {matched} template/line pairs matched");
        Assert.True(arrangements >= 1200, $"only {arrangements} arrangements tried");
    }

    /// <summary>A handful of orders that between them reach every shape the projection cares about: as
    /// written, reversed, one carried to the front (a backwards jump followed by forwards ones), and one
    /// carried to the back.</summary>
    private static IEnumerable<int[]> Orders(int n)
    {
        var straight = Enumerable.Range(0, n).ToArray();
        yield return straight;
        if (n < 2) yield break;
        yield return straight.Reverse().ToArray();
        yield return straight.Skip(n - 1).Concat(straight.Take(n - 1)).ToArray();
        yield return straight.Skip(1).Concat(straight.Take(1)).ToArray();
        if (n >= 3) yield return [1, 0, .. Enumerable.Range(2, n - 2)];
    }

    [Fact]
    public void A_part_that_ends_in_a_space_still_takes_that_space_with_it()
    {
        var spec = new ColumnSpec { Enabled = true, Template = "{[*] }{*}", Layout = FieldLayout.Inline };
        spec.Reset();
        spec.Columns[0].Visible = false;

        var m = new TemplateMatch();
        Assert.True(spec.Compiled.Match("[a] rest", m));
        var projection = new LineProjection();
        projection.Build("[a] rest", spec, m);
        Assert.Equal("rest", projection.Text);
    }

    /// <summary>A part whose whole content is swallowed by a neighbour's run of spaces is never touched by
    /// the match, so it has to be given a place that keeps the parts in order. Settled at the end instead,
    /// the projection worked out the gaps from a part that came after it and copied text twice - the line
    /// came out LONGER than it went in, carrying text that was in no line of the file.</summary>
    [Theory]
    [InlineData("{* }{ }{*}", "a  b")]
    [InlineData("{a }{ }{b}", "a  b")]
    [InlineData("{ }{ }{*}", "  c")]
    public void A_part_swallowed_by_its_neighbour_does_not_duplicate_text(string template, string line)
    {
        var spec = new ColumnSpec { Enabled = true, Template = template, Layout = FieldLayout.Inline };
        spec.Reset();
        var m = new TemplateMatch();
        Assert.True(spec.Compiled.Match(line, m), $"<{template}> did not match <{line}>");

        // The parts have to be in template order, whatever the match did or did not touch.
        for (int i = 1; i < m.PartCount; i++)
            Assert.True(m.Part(i).Start >= m.Part(i - 1).Start,
                $"part {i} starts at {m.Part(i).Start}, before part {i - 1} at {m.Part(i - 1).Start}");

        var projection = new LineProjection();
        projection.Build(line, spec, m);
        Assert.Equal(line, projection.Text);

        // ...and with something hidden it can only ever get shorter.
        spec.Columns[0].Visible = false;
        projection.Build(line, spec, m);
        Assert.True(projection.Text.Length <= line.Length,
            $"<{projection.Text}> is longer than <{line}>");
    }

    /// <summary>Tabs are ordinary literals, so a run of them is a run - only spaces are flexible.</summary>
    [Fact]
    public void A_tab_is_an_ordinary_literal()
        => Assert.Equal(["a", "", "b"], Values("{*\t}{*\t}{*}", "a\t\tb"));

    [Fact]
    public void Padding_inside_a_field_is_kept()
        => Assert.Equal(["INFO ", "rest"], Values("{[*]}{*}", "[INFO ]rest"));

    [Fact]
    public void Escapes_make_braces_stars_and_backslashes_literal()
    {
        Assert.Equal(["abc"], Values(@"\{{*}\}", "{abc}"));
        Assert.Equal(["x"], Values(@"\*{*}", "*x"));
        Assert.Equal(["y"], Values(@"\\{*}", @"\y"));
    }

    // ---- a dot: any one character ----

    /// <summary>A dot stands for one character of any kind, which is what lets one template read a log
    /// whose punctuation is not quite the same from line to line.</summary>
    [Theory]
    [InlineData("[a]-[b] the message")]
    [InlineData("[a]+[b] the message")]
    [InlineData("[a] [b] the message")]     // a space is a character like any other
    public void A_dot_matches_whatever_one_character_is_there(string line)
        => Assert.Equal(["a", "b", "the message"], Values("{[*]}.{[*]} {*}", line));

    /// <summary>It stands for exactly one, though - so a line with nothing there does not match.</summary>
    [Fact]
    public void A_dot_is_one_character_and_not_none()
        => Assert.False(new LineTemplate("{[*]}.{[*]} {*}").Match("[a][b] the message", new TemplateMatch()));

    [Fact]
    public void A_run_of_dots_matches_exactly_that_many_characters()
    {
        Assert.Equal(["hello"], Values("{..:..:..} {*}", "12:34:56 hello"));
        Assert.False(new LineTemplate("{..:..:..} {*}").Match("12-34-56 hello", new TemplateMatch()));
        Assert.Equal(["ab", "rest"], Values("{[*]}..{*}", "[ab]xyrest"));
    }

    /// <summary>Dots are counted, so a line one character short of what the template asks for is not a
    /// match - and the failure names the dots, which are what is written at that point of the template.
    /// </summary>
    [Fact]
    public void A_line_that_runs_out_under_the_dots_does_not_match()
    {
        var t = new LineTemplate("{[*]}...{*}");
        var m = new TemplateMatch();
        Assert.True(t.Match("[a]xyz", m));
        Assert.False(t.Match("[a]xy", m));
        Assert.Equal("...", m.FailureExpected);
        Assert.Equal(3, m.FailurePosition);
    }

    /// <summary>What comes after the dots still has to be there, so a dot never swallows a line whole.</summary>
    [Fact]
    public void The_text_after_the_dots_still_has_to_line_up()
    {
        Assert.Equal(["a", "rest"], Values("{[*]}..X{*}", "[a]12Xrest"));
        Assert.False(new LineTemplate("{[*]}..X{*}").Match("[a]1X2Xrest", new TemplateMatch()));
    }

    [Fact]
    public void An_escaped_dot_is_an_ordinary_full_stop()
    {
        var t = new LineTemplate(@"{[*]}\.{*}");
        Assert.True(t.IsValid);
        Assert.Equal(["a", "b"], Values(@"{[*]}\.{*}", "[a].b"));
        Assert.False(t.Match("[a]-b", new TemplateMatch()));
    }

    /// <summary>...and text written into a template on the reader's behalf keeps its full stops, or a
    /// detected template would quietly mean something wider than the line it was read from.</summary>
    [Fact]
    public void Text_written_into_a_template_has_its_dots_escaped()
    {
        Assert.Equal(@"a\.b", LineTemplate.Escape("a.b"));
        string detected = LineTemplate.Detect("[a]..[b] c");
        Assert.Equal(@"{[*]}\.\.{[*]} {*}", detected);
        Assert.False(new LineTemplate(detected).Match("[a]xx[b] c", new TemplateMatch()));
    }

    /// <summary>A dot belongs to the part it is written in, exactly as a literal does - so hiding that
    /// field takes the character it stood for away with it.</summary>
    [Fact]
    public void A_dot_belongs_to_the_part_it_is_written_in()
    {
        Assert.Equal("[a]-", Part("{[*].}{*}", "[a]-rest", 0));

        var spec = new ColumnSpec { Enabled = true, Template = "{[*].}{*}", Layout = FieldLayout.Inline };
        spec.Reset();
        spec.Columns[0].Visible = false;
        var m = new TemplateMatch();
        Assert.True(spec.Compiled.Match("[a]-rest", m));
        var projection = new LineProjection();
        projection.Build("[a]-rest", spec, m);
        Assert.Equal("rest", projection.Text);
    }

    /// <summary>A run found by stepping back over dots is still found with one search per attempt. Tried
    /// at every position instead, a template opening a run with a dot would be quadratic - and these run
    /// on lines that may be megabytes long.</summary>
    [Fact]
    public void Dots_in_front_of_a_literal_do_not_make_the_scan_quadratic()
    {
        var t = new LineTemplate("{*}..zz{*}");
        var m = new TemplateMatch();
        t.Match("aa..zzb", m);   // warm

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(t.Match(new string('a', 400_000), m));
        Assert.True(t.Match(new string('a', 400_000) + "xxzztail", m));
        watch.Stop();
        Assert.True(watch.ElapsedMilliseconds < 250, $"took {watch.ElapsedMilliseconds} ms");
    }

    /// <summary>The same for dots in front of a run of spaces: only the first space of each run can start
    /// a match, whether that run is at the head of the template's own run or a few dots into it.</summary>
    [Fact]
    public void Dots_in_front_of_a_run_of_spaces_do_not_make_the_scan_quadratic()
    {
        var t = new LineTemplate("{*}.. z{*}");
        var m = new TemplateMatch();
        t.Match("aa   zb", m);   // warm

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(t.Match(new string(' ', 400_000) + "nothing to find", m));
        watch.Stop();
        Assert.True(watch.ElapsedMilliseconds < 250, $"took {watch.ElapsedMilliseconds} ms");
    }

    /// <summary>A value stops at the first place what follows it can match, and anywhere at all is where a
    /// dot can match - so dots alone leave nothing for the value before them. Legal, and worth pinning
    /// down: the dialog shows the split as it is typed, so what matters is that it is not a surprise
    /// twice.</summary>
    [Fact]
    public void Dots_alone_leave_nothing_for_the_value_in_front_of_them()
    {
        Assert.Equal(["", "bc"], Values("{*}.{*}", "abc"));
        Assert.Equal(["rest"], Values("{.*}", "Xrest"));
        Assert.Equal(["rest"], Values("{...*}", "abcrest"));
    }

    [Fact]
    public void A_part_may_capture_nothing_at_all()
    {
        var t = new LineTemplate("{ERR }{[*]}");
        Assert.True(t.IsValid);
        Assert.Equal(2, t.PartCount);
        Assert.Equal(1, t.ValueCount);
        Assert.Equal(-1, t.PartAt(0).Value);
        Assert.Equal(0, t.PartAt(1).Value);
        Assert.Equal(["x"], Values("{ERR }{[*]}", "ERR [x]"));
    }

    [Fact]
    public void A_value_with_nothing_after_it_takes_the_rest_of_the_line()
        => Assert.Equal(["a", "bc de"], Values("{[*]}{*}", "[a]bc de"));

    [Fact]
    public void A_template_that_matches_nothing_of_the_line_still_matches_an_empty_prefix()
    {
        var t = new LineTemplate("");
        var m = new TemplateMatch();
        Assert.True(t.Match("anything", m));
        Assert.Equal(0, m.PartCount);
        Assert.Equal(0, m.TailStart);
    }

    // ---- failure ----

    [Fact]
    public void A_line_of_another_shape_fails_at_the_first_character()
    {
        var t = new LineTemplate(Nine);
        var m = new TemplateMatch();
        Assert.False(t.Match("plain line with no structure at all", m));
        Assert.Equal(0, m.FailurePosition);
        Assert.Equal("[", m.FailureExpected);
    }

    /// <summary>The failure reported is from the attempt that got furthest, which is where the line stopped
    /// looking like the template - not where the search happened to start.</summary>
    [Fact]
    public void A_failure_points_at_where_the_line_stopped_fitting()
    {
        var t = new LineTemplate("{[*]}{[*]}{*}");
        var m = new TemplateMatch();
        Assert.False(t.Match("[a] no second bracket", m));
        Assert.Equal(3, m.FailurePosition);
        Assert.Equal("[", m.FailureExpected);
    }

    [Fact]
    public void A_missing_space_is_named_as_a_space()
    {
        var t = new LineTemplate("{[*]} {*}");
        var m = new TemplateMatch();
        Assert.False(t.Match("[a]b", m));
        Assert.Equal(" ", m.FailureExpected);
    }

    // ---- what the language refuses ----

    [Theory]
    [InlineData("[*]", "inside { }")]
    [InlineData("{}{*}", "empty")]
    [InlineData("{*}{*}", "nothing between")]
    [InlineData("{[*]", "never closed")]
    [InlineData("a}{*}", "never opened")]
    [InlineData("{{*}}", "another part")]
    [InlineData("{**}", "only one *")]
    [InlineData(@"{*}\", "escapes nothing")]
    public void Templates_that_are_refused(string template, string because)
    {
        var t = new LineTemplate(template);
        Assert.False(t.IsValid);
        Assert.Contains(t.Issues, i => i.Message.Contains(because, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(Nine)]
    [InlineData(@"\{{*}\}")]
    [InlineData("{ERR }{*}")]
    [InlineData("")]
    public void Templates_that_are_accepted(string template) => Assert.True(new LineTemplate(template).IsValid);

    /// <summary>Two values in one part is the case that was deliberately dropped: a part that captures is a
    /// column, one for one, and that is what keeps the two lists the same list.</summary>
    [Fact]
    public void A_part_holds_at_most_one_value()
    {
        var t = new LineTemplate("{[*T*]}");
        Assert.False(t.IsValid);
        Assert.Contains(t.Issues, i => i.Message.Contains("only one *", StringComparison.Ordinal));
    }

    // ---- detect ----

    [Fact]
    public void Detect_reads_the_bracket_groups_off_a_line()
        => Assert.Equal(Nine, LineTemplate.Detect(Trace));

    /// <summary>Groups with something between them are detected too, and what separates them is kept -
    /// the old reader stopped at the first space and lumped the rest into the message.</summary>
    [Fact]
    public void Detect_keeps_what_separates_the_groups()
        => Assert.Equal("{[*]} {[*]} {*}", LineTemplate.Detect("[09:31] [INFO] hello there"));

    [Fact]
    public void Detect_finds_nothing_worth_doing_in_a_plain_line()
        => Assert.Equal("", LineTemplate.Detect("plain line"));

    /// <summary>The message is not part of the header, however many brackets it happens to carry. A trace
    /// whose message is JSON put the whole of it into the template until this was pinned down.</summary>
    [Fact]
    public void Detect_stops_where_the_message_starts()
    {
        Assert.Equal(Nine, LineTemplate.Detect(TraceEmpty));
        Assert.Equal("{[*]} {*}", LineTemplate.Detect("[09:31] took [3] retries to settle"));
        Assert.Equal("{[*]}{[*]} {*}", LineTemplate.Detect("[a][b] c [d] e"));
    }

    [Fact]
    public void Detect_handles_a_line_that_is_nothing_but_groups()
        => Assert.Equal("{[*]}{[*]}", LineTemplate.Detect("[a][b]"));

    /// <summary>Round brackets and angle brackets hold a header together as well as square ones do, and a
    /// line is free to mix them.</summary>
    [Theory]
    [InlineData("(09:31) (INFO) hello", "{(*)} {(*)} {*}")]
    [InlineData("<09:31><INFO> hello", "{<*>}{<*>} {*}")]
    [InlineData("[09:31](INFO)<api> hello", "{[*]}{(*)}{<*>} {*}")]
    public void Detect_reads_the_other_bracket_shapes_too(string line, string want)
        => Assert.Equal(want, LineTemplate.Detect(line));

    /// <summary>The timestamp in front of the first bracket is a field like any other. It is taken only when
    /// it reads as a VALUE - one space in it at most, so a date beside a time qualifies and the opening of a
    /// sentence does not - and the spaces after it are a separator, written outside the braces.</summary>
    [Theory]
    [InlineData("2026-08-05 12:00:00.123 [INFO] hello there", "{*} {[*]} {*}")]
    [InlineData("12:00:00 [INFO] hello", "{*} {[*]} {*}")]
    [InlineData("2026-08-05 12:00:00 [a][b] hello", "{*} {[*]}{[*]} {*}")]
    public void Detect_takes_what_stands_before_the_first_bracket_as_a_field(string line, string want)
        => Assert.Equal(want, LineTemplate.Detect(line));

    /// <summary>...and refuses when it would be a guess rather than a reading: prose with a bracket
    /// somewhere in it, a value with a bracket stuck to it, or a header held together by spaces alone.</summary>
    [Theory]
    [InlineData("the quick brown fox [INFO] jumped")]
    [InlineData("foo[bar] baz")]
    [InlineData("2026-08-05 12:00:00 INFO api-gateway hello")]
    [InlineData("plain line")]
    public void Detect_says_nothing_rather_than_guessing(string line)
        => Assert.Equal("", LineTemplate.Detect(line));

    [Fact]
    public void Detect_produces_a_template_that_matches_what_it_was_read_from()
    {
        string[] lines =
        [
            Trace, TraceEmpty, "[09:31] [INFO] hello",
            "(09:31) (INFO) hello", "<09:31><INFO> hello", "[09:31](INFO)<api> hello",
            "2026-08-05 12:00:00.123 [INFO] hello there", "12:00:00 [INFO] hello"
        ];
        foreach (string line in lines)
        {
            var t = new LineTemplate(LineTemplate.Detect(line));
            Assert.True(t.IsValid, line);
            Assert.True(t.Match(line, new TemplateMatch()), line);
        }
    }

    // ---- the caret, which is what keeps a column list attached to its data across an edit ----

    [Fact]
    public void The_part_at_an_offset_is_the_one_that_starts_there()
    {
        var t = new LineTemplate("{[*]}{[*]} {*}");
        Assert.Equal(0, t.PartIndexAtOffset(0));
        Assert.Equal(0, t.PartIndexAtOffset(4));
        Assert.Equal(1, t.PartIndexAtOffset(5));   // the boundary belongs to the part that starts there
        Assert.Equal(2, t.PartIndexAtOffset(10));  // between the parts
        Assert.Equal(2, t.PartIndexAtOffset(13));
        Assert.Equal(3, t.PartIndexAtOffset(14));  // past the end
    }

    // ---- reuse: the paint asks for one of these per row per frame ----

    [Fact]
    public void A_match_can_be_reused_across_lines_without_carrying_anything_over()
    {
        var t = new LineTemplate(Nine);
        var m = new TemplateMatch();
        Assert.True(t.Match(Trace, m));
        Assert.False(t.Match("nothing like it", m));
        Assert.False(m.Success);
        Assert.True(t.Match(TraceEmpty, m));
        Assert.True(m.Success);
        var (start, length) = m.Value(6);
        Assert.Equal("", TraceEmpty.Substring(start, length));
    }
}
