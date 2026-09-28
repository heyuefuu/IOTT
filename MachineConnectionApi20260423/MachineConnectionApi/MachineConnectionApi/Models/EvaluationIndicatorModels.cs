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
}

public sealed record EvaluationFile
{
    public string? Id { get; init; }
    public string Name { get; init; } = "";
    public string Size { get; init; } = "";
    public long? SizeBytes { get; init; }
}
