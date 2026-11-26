using System.Globalization;

namespace BattCtl.App.Services;

// Pure functions over sorted samples. No UI types, no IO, no clock reads —
// everything derives from the samples and the range passed in.
public static class HistoryAnalysis
{
    private const int BreakGapMinutes = 10; // a run breaks when a gap exceeds this
    private const int IntervalCapMinutes = 10; // each sample interval counts at most this

    public const string UnknownToken = "\u2014"; // em dash, the app's unknown glyph
    public const string MinusSign = "\u2212"; // U+2212 for negative deltas

    public static HistorySessionKind DeriveKind(HistorySample sample)
    {
        switch (sample.State)
        {
            case "Charging":
                return HistorySessionKind.Charging;
            case "Discharging":
                return HistorySessionKind.Discharging;
            case "NotCharging":
                return sample.Ac == true ? HistorySessionKind.AcIdle : HistorySessionKind.NotCharging;
            default:
                return HistorySessionKind.Unknown;
        }
    }

    // Contiguous runs of one kind, broken on a kind change or a gap longer than
    // 10 minutes. Duration sums the actual sample intervals, each capped at 10
    // minutes. A run that ends because a gap follows sets EndsInGap.
    public static List<HistorySession> BuildSessions(IReadOnlyList<HistorySample> samples)
    {
        var sessions = new List<HistorySession>();
        if (samples.Count == 0)
        {
            return sessions;
        }

        var runStart = 0;
        for (var i = 1; i < samples.Count; i++)
        {
            var current = samples[i].Timestamp;
            var previous = samples[i - 1].Timestamp;
            var gap = current is null || previous is null
                ? (TimeSpan?)null
                : current.Value - previous.Value;

            var isBreak = gap is null
                || gap > TimeSpan.FromMinutes(BreakGapMinutes)
                || DeriveKind(samples[i]) != DeriveKind(samples[i - 1]);

            if (!isBreak)
            {
                continue;
            }

            EmitSession(sessions, samples, runStart, i - 1,
                endsInGap: gap is TimeSpan span && span > TimeSpan.FromMinutes(BreakGapMinutes));
            runStart = i;
        }

        // The last run ends at the window edge, not at a gap.
        EmitSession(sessions, samples, runStart, samples.Count - 1, endsInGap: false);
        return sessions;
    }

    private static void EmitSession(
        List<HistorySession> sessions,
        IReadOnlyList<HistorySample> samples,
        int start,
        int end,
        bool endsInGap)
    {
        // A sample without a timestamp cannot anchor a session; the run is
        // dropped rather than mislabeled.
        if (start >= samples.Count || samples[start].Timestamp is null || samples[end].Timestamp is null)
        {
            return;
        }

        sessions.Add(BuildSession(samples, start, end, endsInGap));
    }

    private static HistorySession BuildSession(IReadOnlyList<HistorySample> samples, int start, int end, bool endsInGap)
    {
        // Each interval belongs to the state of the sample it starts from, so a
        // run also owns the interval that leads into the next run (or into the
        // gap). Without that, every state change would drop one interval and
        // the tiles would not add up to the recorded time.
        var duration = TimeSpan.Zero;
        var cap = TimeSpan.FromMinutes(IntervalCapMinutes);
        for (var i = start; i <= end && i + 1 < samples.Count; i++)
        {
            var interval = (samples[i + 1].Timestamp ?? samples[i].Timestamp ?? DateTimeOffset.MinValue)
                - (samples[i].Timestamp ?? DateTimeOffset.MinValue);
            duration += interval > cap ? cap : interval;
        }

        var first = samples[start];
        var last = samples[end];
        var kind = DeriveKind(first);
        var (energyWh, estimated) = ComputeEnergyWh(first, last, kind);

        return new HistorySession
        {
            Kind = kind,
            Start = first.Timestamp!.Value,
            End = last.Timestamp!.Value,
            Duration = duration,
            StartPercent = first.Pct,
            EndPercent = last.Pct,
            EnergyWh = energyWh,
            EnergyEstimated = estimated,
            EndsInGap = endsInGap,
            KindText = KindText(kind),
            TimeText = FormatTimeRange(first.Timestamp.Value, last.Timestamp.Value),
            DurationText = FormatDuration(duration),
            LevelText = FormatLevelText(kind, first.Pct, last.Pct),
            LevelDetailText = FormatLevelDetail(kind, first.Pct, last.Pct),
            EnergyText = FormatEnergyText(kind, energyWh, estimated),
            GapText = endsInGap ? ", then a gap" : string.Empty,
        };
    }

    // remMWh endpoints when both exist, else (deltaPercent/100) * the run's
    // first full capacity. Magnitude of the level change; null when nothing
    // moved or the data is missing. Never fabricates a value.
    private static (double? EnergyWh, bool Estimated) ComputeEnergyWh(HistorySample first, HistorySample last, HistorySessionKind kind)
    {
        if (kind is not (HistorySessionKind.Charging or HistorySessionKind.Discharging))
        {
            return (null, false);
        }

        if (first.RemMWh is int remStart && last.RemMWh is int remEnd && remStart != remEnd)
        {
            return (Math.Abs(remEnd - remStart) / 1000.0, false);
        }

        if (first.Pct is int pctStart && last.Pct is int pctEnd && pctStart != pctEnd
            && first.FullMWh is int fullMWh && fullMWh > 0)
        {
            return (Math.Abs(pctEnd - pctStart) / 100.0 * fullMWh / 1000.0, true);
        }

        return (null, false);
    }

    // Summary over the window [from, to]. The three time buckets are derived
    // from the same session durations, so "On battery" + "On AC" plus the rare
    // not-charging-off-AC run always equals the recorded time in the coverage
    // line. On AC covers charging and AC idle, both of which are AC time.
    public static HistorySummary BuildSummary(IReadOnlyList<HistorySample> samples, DateTimeOffset from, DateTimeOffset to)
    {
        if (samples.Count == 0)
        {
            return new HistorySummary
            {
                OnBatteryText = UnknownToken,
                OnAcText = UnknownToken,
                LowestLevelText = UnknownToken,
                EnergyUsedText = UnknownToken,
                EnergyEstimated = false,
                CoverageText = string.Empty,
                HasGaps = false,
            };
        }

        var onBattery = TimeSpan.Zero;
        var onAc = TimeSpan.Zero;
        int? lowestPercent = null;
        var energyUsed = 0.0;
        var anyEstimated = false;
        var hadDischarging = false;
        var anyDischargingEnergy = false;

        foreach (var session in BuildSessions(samples))
        {
            switch (session.Kind)
            {
                case HistorySessionKind.Discharging:
                    hadDischarging = true;
                    onBattery += session.Duration;
                    if (session.EnergyWh is double wh)
                    {
                        anyDischargingEnergy = true;
                        energyUsed += wh;
                        anyEstimated |= session.EnergyEstimated;
                    }

                    break;
                case HistorySessionKind.Charging:
                case HistorySessionKind.AcIdle:
                    onAc += session.Duration;
                    break;
            }
        }

        foreach (var sample in samples)
        {
            if (sample.Pct is int level && (lowestPercent is null || level < lowestPercent))
            {
                lowestPercent = level;
            }
        }

        var (coverageText, hasGaps) = BuildCoverage(samples, from, to);

        // "0 Wh" is only legitimate when nothing discharged. Discharging
        // sessions without computable energy (no remMWh endpoints, no pct/full
        // fallback) stay unknown rather than reading as a fabricated zero.
        string energyUsedText;
        if (!hadDischarging)
        {
            energyUsedText = "0 Wh";
        }
        else if (anyDischargingEnergy)
        {
            energyUsedText = FormatEnergyValue(energyUsed) + (anyEstimated ? " (est.)" : string.Empty);
        }
        else
        {
            energyUsedText = UnknownToken;
        }

        return new HistorySummary
        {
            OnBatteryText = FormatDuration(onBattery),
            OnAcText = FormatDuration(onAc),
            LowestLevelText = lowestPercent is int pct ? $"{pct}%" : UnknownToken,
            EnergyUsedText = energyUsedText,
            EnergyEstimated = anyEstimated,
            CoverageText = coverageText,
            HasGaps = hasGaps,
        };
    }

    // Recorded time is the sum of the sample intervals the summary itself
    // counts. The 10-minute join tolerance only decides whether a stretch is a
    // gap; it is not added to the recorded time, so CoverageText and the tiles
    // agree. CoverageText is set only when a gap exists (a window edge with no
    // data, or an internal hole over 10 minutes).
    private static (string CoverageText, bool HasGaps) BuildCoverage(IReadOnlyList<HistorySample> samples, DateTimeOffset from, DateTimeOffset to)
    {
        var windowSpan = to - from;
        if (windowSpan <= TimeSpan.Zero)
        {
            return (string.Empty, false);
        }

        var cap = TimeSpan.FromMinutes(BreakGapMinutes);
        var covered = TimeSpan.Zero;
        var hasGaps = false;

        var first = samples[0].Timestamp;
        if (first is not null && first.Value - from > cap)
        {
            hasGaps = true;
        }

        var last = samples[^1].Timestamp;
        if (last is not null && to - last.Value > cap)
        {
            hasGaps = true;
        }

        for (var i = 1; i < samples.Count; i++)
        {
            var previous = samples[i - 1].Timestamp;
            var current = samples[i].Timestamp;
            if (previous is null || current is null)
            {
                continue;
            }

            var gap = current.Value - previous.Value;
            covered += gap > cap ? cap : gap;
            if (gap > cap)
            {
                hasGaps = true;
            }
        }

        if (!hasGaps)
        {
            return (string.Empty, false);
        }

        if (covered > windowSpan)
        {
            covered = windowSpan;
        }

        return ($"Recorded {FormatDuration(covered)} of the last {FormatWindowLabel(windowSpan)}.", true);
    }

    // The coverage sentence names the window the way the range selector does:
    // "the last 24 h" for the day range, days for the longer ranges.
    private static string FormatWindowLabel(TimeSpan span)
    {
        return span.TotalHours is >= 1 and <= 30 && span.Minutes == 0
            ? $"{(int)span.TotalHours} h"
            : FormatDuration(span);
    }
    public static HistoryView BuildView(HistoryRange range, IReadOnlyList<HistorySample> samples, DateTimeOffset from, DateTimeOffset to)
    {
        var sessions = BuildSessions(samples);
        sessions.Sort((a, b) => b.SortKey.CompareTo(a.SortKey)); // newest first
        var summary = BuildSummary(samples, from, to);

        var levels = new List<int>();
        foreach (var sample in samples)
        {
            if (sample.Pct is int pct)
            {
                levels.Add(pct);
            }
        }

        var hasLimitGuide = false;
        foreach (var sample in samples)
        {
            if (sample.LimitSet == true)
            {
                hasLimitGuide = true;
                break;
            }
        }

        var latest = samples.Count > 0 ? samples[^1] : null;

        return new HistoryView
        {
            Samples = samples,
            Sessions = sessions,
            Summary = summary,
            EarliestSample = samples.Count > 0 ? samples[0].Timestamp : null,
            LatestSample = samples.Count > 0 ? samples[^1].Timestamp : null,
            HasLimitGuide = hasLimitGuide,
            LatestReadoutText = FormatLatestReadout(latest),
            RangeAutomationText = FormatRangeAutomation(range, levels),
        };
    }

    // "Latest Tue 14:35, 80%, on AC, not charging"
    private static string FormatLatestReadout(HistorySample? sample)
    {
        if (sample?.Timestamp is not DateTimeOffset timestamp)
        {
            return string.Empty;
        }

        var time = timestamp.ToLocalTime().ToString("ddd HH:mm", CultureInfo.CurrentCulture);
        var pct = sample.Pct is int p ? $"{p}%" : UnknownToken;

        var power = sample.Ac switch
        {
            true => "on AC",
            false => "on battery",
            null => null,
        };

        var state = sample.State switch
        {
            "Charging" => "charging",
            "Discharging" => "discharging",
            "NotCharging" => "not charging",
            _ => null,
        };

        var parts = new List<string> { $"Latest {time}", pct };
        if (power is not null)
        {
            parts.Add(power);
        }

        if (state is not null)
        {
            parts.Add(state);
        }

        return string.Join(", ", parts);
    }

    // "Charge level over the last 24 hours, 42 to 100 percent. Details in the
    // sessions list below."
    private static string FormatRangeAutomation(HistoryRange range, List<int> levels)
    {
        var rangeName = RangeName(range);

        if (levels.Count == 0)
        {
            return $"Charge level over {rangeName}, no samples.";
        }

        return $"Charge level over {rangeName}, {levels.Min()} to {levels.Max()} percent. Details in the sessions list below.";
    }

    private static string RangeName(HistoryRange range) => range switch
    {
        HistoryRange.Day => "the last 24 hours",
        HistoryRange.Week => "the last 7 days",
        HistoryRange.Month => "the last 30 days",
        _ => "the last 90 days",
    };

    // "2 h 25 min", "40 min", "1 d 3 h", "0 min"
    public static string FormatDuration(TimeSpan span)
    {
        if (span.TotalDays >= 1)
        {
            return span.Hours > 0 ? $"{(int)span.TotalDays} d {span.Hours} h" : $"{(int)span.TotalDays} d";
        }

        if (span.TotalHours >= 1)
        {
            return span.Minutes == 0
                ? $"{(int)span.TotalHours} h"
                : $"{(int)span.TotalHours} h {span.Minutes} min";
        }

        return $"{(int)Math.Round(span.TotalMinutes)} min";
    }

    private static string KindText(HistorySessionKind kind) => kind switch
    {
        HistorySessionKind.Charging => "Charging",
        HistorySessionKind.Discharging => "Discharging",
        HistorySessionKind.AcIdle => "On AC, not charging",
        HistorySessionKind.NotCharging => "Not charging",
        _ => "Unknown",
    };

    // Local time, joined with "to"; the weekday repeats only when the session
    // crosses midnight.
    private static string FormatTimeRange(DateTimeOffset start, DateTimeOffset end)
    {
        var s = start.ToLocalTime();
        var e = end.ToLocalTime();

        return s.Date == e.Date
            ? $"{s:ddd HH:mm} to {e:HH:mm}"
            : $"{s:ddd HH:mm} to {e:ddd HH:mm}";
    }

    private static bool IsIdle(HistorySessionKind kind) =>
        kind is HistorySessionKind.AcIdle or HistorySessionKind.NotCharging;

    // Signed delta when both endpoints exist ("+36%", minus U+2212 for a
    // drop), flat runs read "held/steady at NN%", a lone endpoint reads
    // "NN%", and nothing known reads the unknown token.
    private static string FormatLevelText(HistorySessionKind kind, int? startPercent, int? endPercent)
    {
        if (startPercent is int sp && endPercent is int ep)
        {
            if (Math.Abs(sp - ep) <= 1)
            {
                return IsIdle(kind) ? $"held at {sp}%" : $"steady at {sp}%";
            }

            var delta = ep - sp;
            return delta > 0 ? $"+{delta}%" : $"{MinusSign}{Math.Abs(delta)}%";
        }

        if (startPercent is int s)
        {
            return $"{s}%";
        }

        if (endPercent is int e)
        {
            return $"{e}%";
        }

        return UnknownToken;
    }

    // Compact level range for the detail line ("41% to 77%"); flat runs spell
    // it out; anything else stays empty rather than repeating LevelText.
    private static string FormatLevelDetail(HistorySessionKind kind, int? startPercent, int? endPercent)
    {
        if (startPercent is int s && endPercent is int e)
        {
            if (Math.Abs(s - e) <= 1)
            {
                return IsIdle(kind) ? $"held at {s} percent" : $"steady at {s} percent";
            }

            return $"{Math.Min(s, e)}% to {Math.Max(s, e)}%";
        }

        return string.Empty;
    }

    // Signed by kind: charging gained, discharging spent (U+2212). Idle runs
    // have no energy to show.
    private static string FormatEnergyText(HistorySessionKind kind, double? energyWh, bool estimated)
    {
        if (energyWh is not double wh || wh <= 0 || IsIdle(kind))
        {
            return UnknownToken;
        }

        var sign = kind == HistorySessionKind.Charging ? "+" : MinusSign;
        return sign + FormatEnergyValue(wh) + (estimated ? " (est.)" : string.Empty);
    }

    // "17.8 Wh" (one decimal under 100), "312 Wh" (no decimals), "1.8 kWh".
    private static string FormatEnergyValue(double wh)
    {
        if (wh >= 1000)
        {
            return $"{wh / 1000.0:0.0} kWh";
        }

        if (wh < 100)
        {
            return $"{wh:0.0} Wh";
        }

        return $"{wh:0} Wh";
    }
}
