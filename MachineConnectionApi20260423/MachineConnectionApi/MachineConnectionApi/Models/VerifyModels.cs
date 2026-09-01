namespace MachineConnectionApi.Models;

public sealed class VerifyRunRequest
{
    public string? TaskId { get; set; }
    public string? TaskName { get; set; }
    public IReadOnlyList<string>? MetricIds { get; set; }
    public VerifyRunOptions? Options { get; set; }
}

public sealed class VerifyRunOptions
{
    public int CommunicationRounds { get; set; } = 3;
    public int ProbeTimeoutMs { get; set; } = 3000;
    public int MaxParallelTargets { get; set; } = 50;
    public int RequiredMinConcurrentSuccess { get; set; }
}

public sealed class VerifyRunResponse
{
    public string RunId { get; set; } = "";
    public string? TaskId { get; set; }
    public string TaskName { get; set; } = "";
    public string Status { get; set; } = "pending";
    public string Result { get; set; } = "";
    public string Detail { get; set; } = "";
    public string StartedAt { get; set; } = "";
    public string CompletedAt { get; set; } = "";
    public List<VerifyMetricResult> Metrics { get; set; } = [];
}

public sealed class VerifyMetricResult
{
    public string MetricId { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "pending";
    public string Result { get; set; } = "";
    public string Value { get; set; } = "";
    public string Reference { get; set; } = "";
    public string Detail { get; set; } = "";
    public List<string> Evidence { get; set; } = [];
}
