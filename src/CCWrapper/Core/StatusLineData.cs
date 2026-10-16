using System.Text.Json;

namespace CCWrapper.Core;

/// <summary>The documented statusLine JSON (code.claude.com/docs/en/statusline). Every field is optional.</summary>
public sealed record StatusLineData
{
    public string? SessionId { get; init; }
    public string? TranscriptPath { get; init; }
    public string? Cwd { get; init; }
    public string? ModelId { get; init; }
    public string? ModelName { get; init; }
    public string? Effort { get; init; }
    public double? CostUsd { get; init; }
    public long? LinesAdded { get; init; }
    public long? LinesRemoved { get; init; }
    public long? ContextWindowSize { get; init; }
    public double? ContextUsedPercent { get; init; }
    public long? TotalInputTokens { get; init; }
    public long? TotalOutputTokens { get; init; }
    /// <summary>rate_limits.* exist only for Pro/Max, after the first API response.</summary>
    public double? FiveHourUsedPercent { get; init; }
    public DateTimeOffset? FiveHourResetsAt { get; init; }
    public double? SevenDayUsedPercent { get; init; }
    public DateTimeOffset? SevenDayResetsAt { get; init; }
    /// <summary>When the app read it: the status line refreshes as Claude replies, so this dates its rate limits.</summary>
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>Null when the text is not a JSON object.</summary>
    public static StatusLineData? Parse(string json)
    {
        if (JsonlReader.TryParse(json) is not { ValueKind: JsonValueKind.Object } r) return null;
        return new()
        {
            SessionId = Str(r, "session_id"),
            TranscriptPath = Str(r, "transcript_path"),
            Cwd = Str(r, "workspace", "current_dir") ?? Str(r, "cwd"),
            ModelId = Str(r, "model", "id"),
            ModelName = Str(r, "model", "display_name"),
            Effort = Str(r, "effort", "level"),
            CostUsd = Num(r, "cost", "total_cost_usd"),
            LinesAdded = Long(r, "cost", "total_lines_added"),
            LinesRemoved = Long(r, "cost", "total_lines_removed"),
            ContextWindowSize = Long(r, "context_window", "context_window_size"),
            ContextUsedPercent = Num(r, "context_window", "used_percentage"),
            TotalInputTokens = Long(r, "context_window", "total_input_tokens"),
            TotalOutputTokens = Long(r, "context_window", "total_output_tokens"),
            FiveHourUsedPercent = Num(r, "rate_limits", "five_hour", "used_percentage"),
            FiveHourResetsAt = Epoch(r, "rate_limits", "five_hour", "resets_at"),
            SevenDayUsedPercent = Num(r, "rate_limits", "seven_day", "used_percentage"),
            SevenDayResetsAt = Epoch(r, "rate_limits", "seven_day", "resets_at"),
        };
    }

    static JsonElement? Get(JsonElement e, string[] path)
    {
        foreach (var p in path)
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null;
        return e;
    }

    static string? Str(JsonElement e, params string[] p) => Get(e, p) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
    static double? Num(JsonElement e, params string[] p) => Get(e, p) is { ValueKind: JsonValueKind.Number } v ? v.GetDouble() : null;
    static long? Long(JsonElement e, params string[] p) => Num(e, p) is { } d ? (long)d : null;

    // resets_at is Unix epoch seconds; out-of-range values are treated as missing.
    static DateTimeOffset? Epoch(JsonElement e, params string[] p) =>
        Num(e, p) is > 0 and < 253402300799 and var s ? DateTimeOffset.FromUnixTimeSeconds((long)s) : null;
}
