namespace MachineConnectionApi.Models;

public sealed record VerifyTaskRunResult(string TaskId, VerifyTaskDto? Task, string? Error = null);

public sealed class VerifyRunRequest
{
    public string? TaskId { get; set; }
    public string? TaskName { get; set; }
    public string? DeviceId { get; set; }
    public string EvaluationCategory { get; set; } = "machine";
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
    public string? DeviceId { get; set; }
    public EvaluationConfig? EvaluationSnapshot { get; set; }
    public EvaluationMachineSnapshot? MachineSnapshot { get; set; }
}

public sealed record EvaluationMachineSnapshot
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string DeviceCode { get; init; } = "";
    public string Model { get; init; } = "";
    public string ControlSystem { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string Protocol { get; init; } = "";
    public int ConnectTimeoutMs { get; init; }
    public int ReadTimeoutMs { get; init; }
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
