using System.Globalization;
using System.IO;
using System.Text.Json;
using CCWrapper.Core;
using static CCWrapper.Data.TranscriptReader.Get;

namespace CCWrapper.Data;

/// <summary>Token counts of an API message or a sum of them. <see cref="Work"/> leaves out cache reads (they re-read the
/// same context every turn and would drown everything else); they are shown on their own.</summary>
public readonly record struct TokenTotals(long Input, long Output, long CacheWrite, long CacheRead)
{
    public long Work => Input + Output + CacheWrite;
    public static TokenTotals operator +(TokenTotals a, TokenTotals b) => new(a.Input + b.Input, a.Output + b.Output, a.CacheWrite + b.CacheWrite, a.CacheRead + b.CacheRead);
}

/// <summary>Tokens of one window: the total, by model (biggest first, Model null = not recorded) and by local hour of day.</summary>
public sealed record WindowUsage(DateTimeOffset Start, TokenTotals Total, IReadOnlyList<(string? Model, long Work)> ByModel, long[] ByHour, int Messages);

public sealed record UsageStatsResult(WindowUsage FiveHour, WindowUsage Week, int Files, TimeSpan Took);

/// <summary>Token usage of an account over the 5 h and weekly windows, summed from the assistant records
/// (<c>message.usage</c>, <c>message.model</c>, <c>timestamp</c>) of the transcripts written since the weekly window began:
/// <c>projects\*\*.jsonl</c> and the subagents' <c>projects\*\*\subagents\*.jsonl</c>. Claude Code writes one record per
/// content block of an API message, so records are merged by message id (the largest count of each field wins).
/// Undocumented format: every field optional, bad lines skipped. Does file IO: off the UI thread.</summary>
public static class UsageStats
{
    static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    static readonly Dictionary<string, (DateTime At, UsageStatsResult Result)> Cache = [];

    /// <summary>The window starts: a known reset in the future gives reset minus the window length, else the last 5 h / 7 d.</summary>
    public static (DateTimeOffset Five, DateTimeOffset Week) Starts(DateTimeOffset? fiveReset, DateTimeOffset? weekReset, DateTimeOffset now) =>
        (fiveReset > now ? fiveReset.Value.AddHours(-5) : now.AddHours(-5), weekReset > now ? weekReset.Value.AddDays(-7) : now.AddDays(-7));

    /// <summary>The last result for the account if younger than a minute, else computed. Never throws.</summary>
    public static UsageStatsResult? Get(Account account, DateTimeOffset? fiveReset, DateTimeOffset? weekReset)
    {
        lock (Cache)
            if (Cache.TryGetValue(account.Letter, out var c) && DateTime.UtcNow - c.At < Ttl) return c.Result;
        try
        {
            var (five, week) = Starts(fiveReset, weekReset, DateTimeOffset.Now);
            var result = Compute(account.Paths.Projects, five, week);
            lock (Cache) Cache[account.Letter] = (DateTime.UtcNow, result);
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static UsageStatsResult Compute(string projectsDir, DateTimeOffset fiveStart, DateTimeOffset weekStart)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var seen = new Dictionary<string, Msg>(); // by message id; ones without an id cannot be merged and go in the list
        var anonymous = new List<Msg>();
        var files = 0;
        foreach (var file in Files(projectsDir, weekStart))
        {
            files++;
            try
            {
                foreach (var (line, _) in JsonlReader.ReadLines(file))
                {
                    // cheap pre-filter: most lines are user records, tool results and so on
                    if (!line.Contains("\"usage\"", StringComparison.Ordinal) || JsonlReader.TryParse(line) is not { } rec) continue;
                    if (Read(rec, weekStart) is not { } m) continue;
                    if (m.Id is null) anonymous.Add(m);
                    else seen[m.Id] = seen.TryGetValue(m.Id, out var old) ? old.Merge(m) : m;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // a file being deleted or locked
        }
        var all = seen.Values.Concat(anonymous).ToList();
        return new(Window(all, fiveStart), Window(all, weekStart), files, watch.Elapsed);
    }

    static IEnumerable<string> Files(string projectsDir, DateTimeOffset since)
    {
        if (!Directory.Exists(projectsDir)) yield break;
        var cut = since.UtcDateTime;
        foreach (var f in new DirectoryInfo(projectsDir).EnumerateFiles("*.jsonl", SearchOption.AllDirectories))
            if (f.LastWriteTimeUtc >= cut) yield return f.FullName; // shortcut: any depth, so also subagents (<id>\subagents\) and anything CC adds
    }

    sealed record Msg(string? Id, DateTimeOffset At, string? Model, TokenTotals Tokens)
    {
        public Msg Merge(Msg o) => this with
        {
            At = o.At < At ? o.At : At, Model = Model ?? o.Model,
            Tokens = new(Math.Max(Tokens.Input, o.Tokens.Input), Math.Max(Tokens.Output, o.Tokens.Output),
                Math.Max(Tokens.CacheWrite, o.Tokens.CacheWrite), Math.Max(Tokens.CacheRead, o.Tokens.CacheRead)),
        };
    }

    static Msg? Read(JsonElement rec, DateTimeOffset since)
    {
        var msg = Prop(rec, "message");
        if (Prop(msg, "usage") is not { ValueKind: JsonValueKind.Object } u) return null;
        if (Str(rec, "timestamp") is not { } ts || !DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) || at < since) return null;
        var model = Str(msg, "model");
        if (model == "<synthetic>") return null; // CC's own error stubs
        long N(string k) => (long)(Num(u, k) ?? 0);
        return new(Str(msg, "id"), at, model, new(N("input_tokens"), N("output_tokens"), N("cache_creation_input_tokens"), N("cache_read_input_tokens")));
    }

    static WindowUsage Window(List<Msg> all, DateTimeOffset start)
    {
        var inside = all.Where(m => m.At >= start).ToList();
        var hours = new long[24];
        foreach (var m in inside) hours[m.At.ToLocalTime().Hour] += m.Tokens.Work;
        var byModel = inside.GroupBy(m => m.Model).Select(g => (g.Key, g.Sum(m => m.Tokens.Work))).OrderByDescending(x => x.Item2).ToList();
        return new(start, inside.Aggregate(default(TokenTotals), (a, m) => a + m.Tokens), byModel, hours, inside.Count);
    }
}
