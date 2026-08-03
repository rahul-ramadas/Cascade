using System.Globalization;
using System.Text;

namespace Cascade.UiTests;

/// <summary>
/// A large synthetic log and a filter set to open it with, generated once into the temp folder and reused.
///
/// The exploratory rigs used to point at one particular multi-gigabyte trace and one particular saved
/// filter file, both of which existed on exactly one machine. Generating the fixture instead keeps them
/// runnable anywhere, and makes what they assume about the data explicit rather than incidental.
///
/// <c>CASCADE_FIXTURE_DIR</c> moves it; <c>CASCADE_FIXTURE_LINES</c> changes its size.
/// </summary>
internal static class BigFixture
{
    // The filters the rigs work with, by the pattern they carry - which is also how they are found in the
    // list. Each is described by roughly how much of the file it matches, because that is the only property
    // the rigs actually depend on.
    public const string HugeFilter = "[api-gateway]";      // ~35% of the file
    public const string BusyFilter = "[order-service]";    // ~20%
    public const string MidFilter = "[payment-svc]";       // ~10%
    public const string ExtraFilterA = "[inventory-svc]";  // ~10%
    public const string ExtraFilterB = "[db-pool]";        // ~10%
    public const string ExtraFilterC = "[cache]";          // ~10%
    public const string WarnFilter = "[WARN]";             // ~4%
    public const string RareFilter = "[ERROR]";            // ~0.1%

    /// <summary>A term on every line, so "typing marks what is on screen" holds wherever the view is.</summary>
    public const string EveryLineTerm = "req-";

    /// <summary>A term with real but sparse hits.</summary>
    public const string SparseTerm = "declined";

    /// <summary>A regular expression that matches <see cref="SparseTerm"/>, and one that cannot match
    /// anything - which is how a rig tells whether the regex option is really being honoured.</summary>
    public const string RegexTerm = "declin[a-z]+";
    public const string ImpossibleRegexTerm = "declin[0-9]{6}";

    /// <summary>The day every line is stamped with, for "a term that spans matched and unmatched lines".</summary>
    public const string EveryLineDate = "2026-07-31";

    public static string Dir { get; } =
        Environment.GetEnvironmentVariable("CASCADE_FIXTURE_DIR")
        ?? Path.Combine(Path.GetTempPath(), "cascade-fixture");

    public static int Lines { get; } =
        int.TryParse(Environment.GetEnvironmentVariable("CASCADE_FIXTURE_LINES"), out int n) && n > 0
            ? n : 4_000_000;

    /// <summary>What the status bar reads once the whole file is indexed.</summary>
    public static string TotalStatus => TotalStatusFor(Lines);

    public static string TotalStatusFor(int lines) => "Total: " + lines.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>The log, generated on first use (a few hundred MB, tens of seconds) and reused after.</summary>
    public static string Log() => Log(Lines);

    public static string Log(int lines)
    {
        Directory.CreateDirectory(Dir);
        string path = Path.Combine(Dir, $"orders-service-{lines}.log");
        if (File.Exists(path)) return path;
        // Build under a different name and rename, so an interrupted run cannot leave a short file behind
        // that every later run then believes.
        string partial = path + ".part";
        using (var writer = new StreamWriter(partial, false, new UTF8Encoding(false), 1 << 20))
            for (int i = 0; i < lines; i++) writer.Write(Line(i));
        File.Move(partial, path, overwrite: true);
        return path;
    }

    /// <summary>Writes the filter set fresh, so a run that saves or edits filters starts from a known state.
    /// Nothing is enabled and only matching lines are shown, so the view opens on the whole file with a
    /// match count of zero - which is what makes "turn something on and watch it narrow" the first thing a
    /// rig does.</summary>
    public static void WriteFilters(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, FilterJson, new UTF8Encoding(false));
    }

    // ---- the data ----

    private static readonly DateTime Start = new(2026, 7, 31, 9, 0, 0, DateTimeKind.Utc);

    private static readonly string[] Services =
        { "api-gateway", "order-service", "payment-svc", "inventory-svc", "db-pool", "cache", "auth-svc" };

    // Twenty slots, so the mix above is exact rather than approximate.
    private static readonly int[] Wheel = { 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6 };

    private static readonly string[][] Messages =
    {
        new[] { "GET /v1/orders -> 200 in {0}ms", "POST /v1/checkout -> 201 in {0}ms",
                "GET /healthz -> 200 in 1ms", "POST /v1/refunds -> 503 in {0}ms" },
        new[] { "order accepted, {0} items, tenant=acme", "order state advanced to PICKING after {0}ms",
                "retrying inventory-svc after 502 (attempt 2/5) backoff={0}ms" },
        new[] { "charge captured amount={0}.45 USD", "charge declined: card_expired amount={0}.89 USD",
                "upstream timeout after {0}ms calling acquirer-gw region=us-east-1" },
        new[] { "reserved {0} units for sku=SKU-4471", "stock check took {0}ms across 3 warehouses" },
        new[] { "slow query {0}ms: SELECT * FROM orders WHERE tenant_id = $1",
                "connection pool exhausted (64/64) waiters={0}" },
        new[] { "eviction storm on shard={0}, hit rate 41%", "warmed {0} keys for tenant=acme" },
        new[] { "token refreshed for tenant=acme in {0}ms", "rate limit {0} requests remaining" },
    };

    // A burst every half million lines. A uniformly random log has nothing to look at: the minimap, the
    // match map and every "park the view somewhere interesting" step want somewhere the errors cluster.
    private static readonly string[] Incident =
    {
        "charge declined: card_expired amount={0}.89 USD idempotency=3c548406",
        "upstream timeout after 30000ms calling acquirer-gw region=us-east-1",
        "connection pool exhausted (64/64) waiters={0} timeout after 5000ms",
    };

    private static string Line(int i)
    {
        bool burst = i % 500_000 < 24;
        int slot = Wheel[i % Wheel.Length];
        string service = burst ? "payment-svc" : Services[slot];
        string level = burst || i % 1009 == 0 ? "ERROR" : i % 23 == 0 ? "WARN" : i % 7 == 0 ? "DEBUG" : "INFO";
        string[] pool = burst ? Incident : Messages[slot];
        string message = string.Format(CultureInfo.InvariantCulture, pool[i % pool.Length], i % 4000 + 7);
        var at = Start.AddMilliseconds(i * 3L);
        return string.Create(CultureInfo.InvariantCulture,
            $"{at:yyyy-MM-dd HH:mm:ss.fff} [{service}] [{level}] [req-{i:x8}] {message}\n");
    }

    private const string FilterJson = """
        {
          "schemaVersion": 1,
          "showOnlyFilteredLines": true,
          "filters": [
            {
              "id": "f-gateway", "description": "edge traffic", "enabled": false,
              "kind": "Include", "matchType": "Text", "text": "[api-gateway]", "fg": "1b5e9e",
              "children": [
                { "id": "f-5xx", "description": "5xx responses", "enabled": false, "kind": "Include",
                  "matchType": "Text", "regex": true, "text": "-> 5\\d\\d in", "fg": "ffffff", "bg": "1b5e9e" },
                { "id": "f-healthz", "description": "health-check noise", "enabled": false,
                  "kind": "Exclude", "matchType": "Text", "text": "GET /healthz" }
              ]
            },
            {
              "id": "f-orders", "description": "orders", "enabled": false,
              "kind": "Include", "matchType": "Text", "text": "[order-service]", "fg": "2e7d32",
              "children": [
                { "id": "f-retries", "description": "retry storms", "enabled": false, "kind": "Include",
                  "matchType": "Text", "text": "retrying inventory-svc", "bold": true }
              ]
            },
            {
              "id": "f-payments", "description": "payments", "enabled": false,
              "kind": "Include", "matchType": "Text", "text": "[payment-svc]", "fg": "b00020", "bold": true,
              "children": [
                { "id": "f-declined", "description": "declined charges", "enabled": false, "kind": "Include",
                  "matchType": "Text", "text": "declined", "bg": "ffe0e4" },
                { "id": "f-acquirer", "description": "acquirer timeouts", "enabled": false, "kind": "Include",
                  "matchType": "Text", "regex": true, "text": "timeout after \\d+ms", "bg": "ffd9b0" }
              ]
            },
            { "id": "f-error", "description": "errors", "enabled": false, "kind": "Include",
              "matchType": "Text", "text": "[ERROR]", "fg": "b00020", "bold": true },
            { "id": "f-warn", "description": "warnings", "enabled": false, "kind": "Include",
              "matchType": "Text", "text": "[WARN]", "fg": "8a6100", "bg": "fff5d6" },
            { "id": "f-inventory", "description": "inventory", "enabled": false, "kind": "Include",
              "matchType": "Text", "text": "[inventory-svc]", "fg": "6a1b9a" },
            { "id": "f-db", "description": "database", "enabled": false, "kind": "Include",
              "matchType": "Text", "text": "[db-pool]", "fg": "00695c" },
            { "id": "f-cache", "description": "cache", "enabled": false, "kind": "Include",
              "matchType": "Text", "text": "[cache]", "fg": "ef6c00" },
            { "id": "f-auth", "description": "auth", "enabled": false, "kind": "Include",
              "matchType": "Text", "text": "[auth-svc]", "fg": "455a64" }
          ]
        }
        """;
}
