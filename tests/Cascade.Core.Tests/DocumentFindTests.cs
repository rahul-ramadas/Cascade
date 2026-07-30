using System.Text;
using Cascade.Core.Document;
using Cascade.Core.Find;
using Cascade.Core.Model;

namespace Cascade.Core.Tests;

/// <summary>Find over a real file: the document owns one search per term, sweeps it in the background, and
/// answers from what it has gathered.</summary>
public class DocumentFindTests
{
    private static string Body(int count)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            if (i % 7 == 0) sb.Append("ERROR disk full ").Append(i);
            else if (i % 5 == 0) sb.Append("WARN network down ").Append(i);
            else sb.Append("INFO steady ").Append(i);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static List<long> Expected(string body, FindQuery q)
    {
        var hits = new List<long>();
        var lines = body.Split('\n');
        for (int i = 0; i < lines.Length - 1; i++)
        {
            bool hit = q.Regex
                ? System.Text.RegularExpressions.Regex.IsMatch(lines[i], q.Text,
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant |
                    (q.CaseSensitive ? 0 : System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                : lines[i].Contains(q.Text, q.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
            if (hit) hits.Add(i);
        }
        return hits;
    }

    [Theory]
    [InlineData("ERROR", false, true)]
    [InlineData("error", false, false)]
    [InlineData("ERROR.+network", true, true)]
    [InlineData(@"\bdisk\b", true, true)]
    [InlineData("nothing matches this", false, false)]
    public async Task It_walks_exactly_the_lines_a_plain_scan_would(string text, bool regex, bool caseSensitive)
    {
        string body = Body(20_000);
        string path = Harness.TempFile(Encoding.UTF8.GetBytes(body));
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(path);
            doc.WaitForIndex();

            var q = new FindQuery(text, regex, caseSensitive);
            var expected = Expected(body, q);

            var forward = new List<long>();
            for (long at = 0; ;)
            {
                long hit = await doc.FindNextAsync(q, at, true, CancellationToken.None);
                if (hit < 0) break;
                forward.Add(hit);
                at = hit + 1;
            }
            Assert.Equal(expected, forward);

            var back = new List<long>();
            for (long at = doc.CompletedLineCount - 1; ;)
            {
                long hit = await doc.FindNextAsync(q, at, false, CancellationToken.None);
                if (hit < 0) break;
                back.Add(hit);
                at = hit - 1;
            }
            back.Reverse();
            Assert.Equal(expected, back);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task The_term_is_swept_for_once_however_much_it_is_walked()
    {
        string path = Harness.TempFile(Encoding.UTF8.GetBytes(Body(50_000)));
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(path);
            doc.WaitForIndex();

            var q = new FindQuery("ERROR", false, true);
            await doc.FindNextAsync(q, 0, true, CancellationToken.None);
            // Walking the whole term twice must not extend the sweep past the one pass over the file.
            for (int pass = 0; pass < 2; pass++)
                for (long at = 0; ;)
                {
                    long hit = await doc.FindNextAsync(q, at, true, CancellationToken.None);
                    if (hit < 0) break;
                    at = hit + 1;
                }

            Assert.True(doc.FindComplete);
            Assert.Equal(1, doc.FindProgress);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task An_empty_term_and_an_unparseable_regex_find_nothing()
    {
        string path = Harness.TempFile(Encoding.UTF8.GetBytes(Body(2_000)));
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(path);
            doc.WaitForIndex();

            Assert.Equal(-1, await doc.FindNextAsync(new FindQuery("", false, false), 0, true, CancellationToken.None));
            Assert.Equal(-1, await doc.FindNextAsync(new FindQuery("((unclosed", true, false), 0, true, CancellationToken.None));
            Assert.Equal(-1, await doc.FindNextAsync(new FindQuery("", false, false), 0, false, CancellationToken.None));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task It_only_lands_on_lines_the_view_can_show()
    {
        var lines = new List<string>();
        for (int i = 0; i < 3_000; i++) lines.Add(i % 10 == 0 ? $"TARGET line {i}" : $"other line {i}");
        string path = Harness.TempFile(Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n"));
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(path);
            doc.WaitForIndex();

            var filters = new FilterCollection();
            filters.Add(new Filter { Enabled = true, Match = { Text = "line" } });
            filters.Add(new Filter { Enabled = true, Kind = FilterKind.Exclude, Match = { Text = "0 " } });
            doc.SetFilters(filters);
            doc.Filters.ShowOnlyFilteredLines = true;
            doc.ApplyFilters();
            for (int i = 0; i < 2000 && !doc.IsFilterIdle; i++) Thread.Sleep(5);

            var q = new FindQuery("TARGET", false, true);
            int seen = 0;
            for (long at = 0; seen < 50;)
            {
                long hit = await doc.FindNextAsync(q, at, true, CancellationToken.None);
                if (hit < 0) break;
                Assert.True(doc.IsLineVisible(hit), $"find returned line {hit}, which the view is hiding");
                at = hit + 1;
                seen++;
            }
            Assert.True(seen > 0);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Changing_the_filters_does_not_make_it_sweep_again()
    {
        // The sweep covers every line whether the view shows it or not, so what the filters hide only
        // changes which results can be navigated to - never what has to be read.
        var lines = new List<string>();
        for (int i = 0; i < 5_000; i++) lines.Add(i % 10 == 0 ? $"TARGET line {i}" : $"other line {i}");
        string path = Harness.TempFile(Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n"));
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(path);
            doc.WaitForIndex();

            var q = new FindQuery("TARGET", false, true);
            Assert.Equal(0, await doc.FindNextAsync(q, 0, true, CancellationToken.None));
            for (int i = 0; i < 2000 && !doc.FindComplete; i++) Thread.Sleep(5);
            Assert.True(doc.FindComplete);

            var filters = new FilterCollection();
            filters.Add(new Filter { Enabled = true, Match = { Text = "other" } });
            doc.SetFilters(filters);
            doc.Filters.ShowOnlyFilteredLines = true;
            doc.ApplyFilters();
            for (int i = 0; i < 2000 && !doc.IsFilterIdle; i++) Thread.Sleep(5);

            // Every TARGET line is hidden now, so there is nothing to go to - answered from what was already
            // gathered, with the sweep still showing as complete rather than restarted.
            Assert.Equal(-1, await doc.FindNextAsync(q, 0, true, CancellationToken.None));
            Assert.True(doc.FindComplete);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Opening_another_file_works_the_term_out_again()
    {
        string first = Harness.TempFile(Encoding.UTF8.GetBytes("ERROR here\nnothing\nnothing\n"));
        string second = Harness.TempFile(Encoding.UTF8.GetBytes("nothing\nnothing\nERROR there\n"));
        try
        {
            using var doc = new CascadeDocument();
            var q = new FindQuery("ERROR", false, true);

            doc.Open(first);
            doc.WaitForIndex();
            Assert.Equal(0, await doc.FindNextAsync(q, 0, true, CancellationToken.None));

            doc.Open(second);
            doc.WaitForIndex();
            Assert.Equal(2, await doc.FindNextAsync(q, 0, true, CancellationToken.None));
        }
        finally { File.Delete(first); File.Delete(second); }
    }

    [Fact]
    public async Task Closing_the_file_while_a_sweep_is_running_does_not_read_the_freed_mapping()
    {
        // The sweep reads the memory-mapped file, so it has to be stopped before the mapping is released.
        // Getting this wrong is an access violation rather than a failed assertion.
        string big = Harness.TempFile(Encoding.UTF8.GetBytes(Body(400_000)));
        string small = Harness.TempFile(Encoding.UTF8.GetBytes("hello\n"));
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(big);
            doc.WaitForIndex();
            await doc.FindNextAsync(new FindQuery("ERROR", false, true), 0, true, CancellationToken.None);
            doc.Open(small);          // pulls the mapping out from under the sweep
            doc.WaitForIndex();
            Assert.Equal(-1, await doc.FindNextAsync(new FindQuery("ERROR", false, true), 0, true, CancellationToken.None));
        }
        finally { File.Delete(big); File.Delete(small); }
    }

    [Fact]
    public async Task A_search_before_the_file_is_indexed_still_answers()
    {
        string body = Body(5_000);
        string path = Harness.TempFile(Encoding.UTF8.GetBytes(body));
        try
        {
            using var doc = new CascadeDocument();
            doc.Open(path);           // deliberately not waiting

            var q = new FindQuery("ERROR", false, true);
            long hit = await doc.FindNextAsync(q, 0, true, CancellationToken.None);
            Assert.True(hit < 0 || hit == Expected(body, q)[0]);

            doc.WaitForIndex();
            Assert.Equal(Expected(body, q)[0], await doc.FindNextAsync(q, 0, true, CancellationToken.None));
        }
        finally { File.Delete(path); }
    }
}
