using FlaUI.Core.AutomationElements;

namespace Cascade.UiTests;

/// <summary>The exploratory rigs stand on a generated fixture, and a fixture nobody checks is a fixture
/// that quietly stops loading. This opens it for real - at the small size, so it costs one launch.</summary>
public class FixtureSmoke
{
    [Fact]
    public void The_generated_fixture_opens_with_its_filters()
    {
        // The harness waits for this line count anyway, so asking for it keeps the launch instant.
        const int lines = TestData.LineCount;
        string dir = Path.Combine(Path.GetTempPath(), "cascade-fixture-smoke");
        Directory.CreateDirectory(dir);
        string filters = Path.Combine(dir, "fixture.cascade");
        BigFixture.WriteFilters(filters);

        var env = new Dictionary<string, string> { ["CASCADE_TEST_OFFSCREEN"] = "1" };
        using var app = CascadeApp.LaunchExisting(BigFixture.Log(lines), filters, CascadeApp.NewSettingsDir(),
                                                  ownsFiles: false, ownsSettingsDir: true, environment: env);

        Assert.Contains(BigFixture.TotalStatusFor(lines), app.AllStatusText(), StringComparison.Ordinal);
        foreach (string name in new[] { BigFixture.HugeFilter, BigFixture.BusyFilter, BigFixture.MidFilter,
                                        BigFixture.RareFilter, BigFixture.WarnFilter, BigFixture.ExtraFilterA,
                                        BigFixture.ExtraFilterB, BigFixture.ExtraFilterC })
            Assert.True(app.FilterNode(name) is not null, $"{name} is not in the list: {string.Join(" | ", app.RootFilterNames())}");

        // Nothing is enabled, so the whole file is there to start from.
        Assert.NotEmpty(app.Rows());
    }
}
