namespace MachineConnectionApi.Models;

public sealed record EvaluationConfig
{
    public string Category { get; init; } = "machine";
    public int Version { get; init; } = 1;
    public List<EvaluationSection> Indicators { get; init; } = [];
    public string UpdatedAt { get; init; } = "";
}

public sealed record EvaluationSection
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public double Weight { get; init; }
    public List<EvaluationSubcategory> Children { get; init; } = [];
}

public sealed record EvaluationSubcategory
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public double Weight { get; init; }
    public List<EvaluationItem> Items { get; init; } = [];
}

public sealed record EvaluationItem
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public double Weight { get; init; }
    public string Desc { get; init; } = "";
    public string Method { get; init; } = "";
    public string EvidenceType { get; init; } = "standard";
    public string Evidence { get; init; } = "";
    public string Standards { get; init; } = "";
    public List<EvaluationFile> Files { get; init; } = [];
    public List<string> Protocols { get; init; } = [];
    public Dictionary<string, string> Scoring { get; init; } = [];
    public string? MetricId { get; init; }
    public EvaluationAutomationRule? Automation { get; init; }
    public EvaluationPassRule? ManualPassRule { get; init; }
}

/// <summary>Executable rules included in snapshots; legacy scoring prose is never parsed.</summary>
public sealed record EvaluationAutomationRule
{
    public string Unit { get; init; } = "";
    public EvaluationPassRule? PassRule { get; init; }
    public string ScoringMode { get; init; } = "bands";
    public List<EvaluationScoreBand> ScoreBands { get; init; } = [];
    public EvaluationTestSettings Test { get; init; } = new();
}

public sealed record EvaluationPassRule
{
    public string Comparison { get; init; } = "gte";
    public double Threshold { get; init; }
    public string Unit { get; init; } = "";
}

public sealed record EvaluationScoreBand
{
    public double Min { get; init; }
    public double Score { get; init; }
}

public sealed record EvaluationTestSettings
{
    public double DurationMinutes { get; init; } = 30;
    public double SampleIntervalSeconds { get; init; } = 5;
    public int MaxConnections { get; init; } = 4;
    public int FailureLimit { get; init; } = 3;
    public string ConcurrencyMode { get; init; } = "devices";
    public string ReadAddress { get; init; } = "";
    public string ReadDataType { get; init; } = "String";
    public string TargetDirectory { get; init; } = "/";
}

public sealed record EvaluationFile
{
    public string? Id { get; init; }
    public string Name { get; init; } = "";
    public string Size { get; init; } = "";
    public long? SizeBytes { get; init; }
}
