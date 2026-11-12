using System.Text.Json.Serialization;

namespace BattCtl.App.Services;

public sealed class Snapshot
{
    [JsonPropertyName("schema")]
    public int? Schema { get; set; }

    [JsonPropertyName("limitOn")]
    public bool? LimitOn { get; set; }

    [JsonPropertyName("desired")]
    public bool? Desired { get; set; }

    [JsonPropertyName("supported")]
    public bool? Supported { get; set; }

    [JsonPropertyName("battery")]
    public BatteryInfo? Battery { get; set; }

    [JsonPropertyName("gpu")]
    public GpuInfo? Gpu { get; set; }

    [JsonPropertyName("restoreLog")]
    public List<RestoreLogEntry>? RestoreLog { get; set; }

    [JsonPropertyName("installedAt")]
    public string? InstalledAt { get; set; }
}

public sealed class BatteryInfo
{
    [JsonPropertyName("present")]
    public bool? Present { get; set; }

    [JsonPropertyName("percent")]
    public int? Percent { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("powerOnline")]
    public bool? PowerOnline { get; set; }

    [JsonPropertyName("designCapacityMWh")]
    public int? DesignCapacityMWh { get; set; }

    [JsonPropertyName("fullChargeCapacityMWh")]
    public int? FullChargeCapacityMWh { get; set; }

    [JsonPropertyName("wearPercent")]
    public double? WearPercent { get; set; }

    [JsonPropertyName("cycleCount")]
    public int? CycleCount { get; set; }

    [JsonPropertyName("voltageMv")]
    public int? VoltageMv { get; set; }

    [JsonPropertyName("rateMw")]
    public int? RateMw { get; set; }

    [JsonPropertyName("remainingCapacityMWh")]
    public int? RemainingCapacityMWh { get; set; }

    [JsonPropertyName("estimatedMinutes")]
    public int? EstimatedMinutes { get; set; }
}

public sealed class GpuInfo
{
    [JsonPropertyName("present")]
    public bool? Present { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    [JsonPropertyName("instanceId")]
    public string? InstanceId { get; set; }

    [JsonPropertyName("drivingDisplay")]
    public bool? DrivingDisplay { get; set; }

    [JsonPropertyName("problemCode")]
    public int? ProblemCode { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public sealed class RestoreLogEntry
{
    [JsonPropertyName("at")]
    public string? At { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
