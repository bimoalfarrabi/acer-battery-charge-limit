using System.Text.Json.Serialization;

namespace BattCtl.App.Services;

public enum HistoryRange
{
    Day,
    Week,
    Month,
    Quarter,
}

public enum HistorySessionKind
{
    Charging,
    Discharging,
    AcIdle,
    NotCharging,
    Unknown,
}

// One JSONL line written by the SYSTEM sampler (v 1). Every key is present in
// the file; unknown values arrive as null, never 0.
public sealed class HistorySample
{
    [JsonPropertyName("v")]
    public int? V { get; set; }

    [JsonPropertyName("t")]
    public string? T { get; set; }

    [JsonIgnore]
    public DateTimeOffset? Timestamp { get; set; }

    [JsonPropertyName("pct")]
    public int? Pct { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("ac")]
    public bool? Ac { get; set; }

    [JsonPropertyName("rateMw")]
    public int? RateMw { get; set; }

    [JsonPropertyName("remMWh")]
    public int? RemMWh { get; set; }

    [JsonPropertyName("fullMWh")]
    public int? FullMWh { get; set; }

    [JsonPropertyName("designMWh")]
    public int? DesignMWh { get; set; }

    [JsonPropertyName("cycleCount")]
    public int? CycleCount { get; set; }

    [JsonPropertyName("limitSet")]
    public bool? LimitSet { get; set; }
}

public sealed class HistorySession
{
    public HistorySessionKind Kind { get; init; }
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
    public TimeSpan Duration { get; init; }
    public int? StartPercent { get; init; }
    public int? EndPercent { get; init; }
    public double? EnergyWh { get; init; }
    public bool EnergyEstimated { get; init; }
    public bool EndsInGap { get; init; }

    // Preformatted so the session rows bind without converters.
    public string KindText { get; init; } = string.Empty;
    public string TimeText { get; init; } = string.Empty;
    public string DurationText { get; init; } = string.Empty;
    public string LevelText { get; init; } = string.Empty;
    public string LevelDetailText { get; init; } = string.Empty;
    public string EnergyText { get; init; } = string.Empty;
    public string GapText { get; init; } = string.Empty;

    public DateTimeOffset SortKey => Start;
}

public sealed class HistorySummary
{
    public string OnBatteryText { get; init; } = string.Empty;
    public string OnAcText { get; init; } = string.Empty;
    public string LowestLevelText { get; init; } = string.Empty;
    public string EnergyUsedText { get; init; } = string.Empty;
    public bool EnergyEstimated { get; init; }
    public string CoverageText { get; init; } = string.Empty;
    public bool HasGaps { get; init; }
}

public sealed class HistoryView
{
    public IReadOnlyList<HistorySample> Samples { get; init; } = Array.Empty<HistorySample>();
    public IReadOnlyList<HistorySession> Sessions { get; init; } = Array.Empty<HistorySession>();
    public HistorySummary Summary { get; init; } = new();
    public DateTimeOffset? EarliestSample { get; init; }
    public DateTimeOffset? LatestSample { get; init; }
    public bool HasLimitGuide { get; init; }
    public string LatestReadoutText { get; init; } = string.Empty;
    public string RangeAutomationText { get; init; } = string.Empty;
}

public sealed class HistoryLoadResult
{
    public IReadOnlyList<HistorySample> Samples { get; init; } = Array.Empty<HistorySample>();
    public int SkippedLines { get; init; }
    public bool DirectoryMissing { get; init; }
    public bool RangeEmpty { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset? EarliestAvailable { get; init; }
}
