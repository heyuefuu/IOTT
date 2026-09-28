namespace MachineConnectionApi.Models;

public sealed record KnowledgeRecord
{
    public string Id { get; init; } = "";
    public string MachineName { get; init; } = "";
    public string MachineNo { get; init; } = "";
    public string MachineModel { get; init; } = "";
    public string ControlSystem { get; init; } = "";
    public string PartName { get; init; } = "";
    public string PartFeature { get; init; } = "";
    public string TestLocation { get; init; } = "";
    public string TestDate { get; init; } = "";
    public string Tester { get; init; } = "";
    public string DataSource { get; init; } = "manual";
    public string Category { get; init; } = "machine";
    public EvaluationConfig Snapshot { get; init; } = new();
    public int Version { get; init; }
    public Dictionary<string, double> CategoryWeights { get; init; } = [];
    public Dictionary<string, double> SubCategoryWeights { get; init; } = [];
    public Dictionary<string, double?> Scores { get; init; } = [];
    public Dictionary<string, double> Weights { get; init; } = [];
    public Dictionary<string, string> TestResults { get; init; } = [];
    public Dictionary<string, string> Remarks { get; init; } = [];
    public string Conclusion { get; init; } = "";
    public string Suggestion { get; init; } = "";
    public string? SyncTaskId { get; init; }
    public string? SyncRunId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public double? TotalScore { get; init; }
}

public sealed record KnowledgeTask(
    string Id, string Name, string MachineName, string MachineNo,
    string MachineModel, string ControlSystem, string TestDate, string Status, string Result,
    string Category = "machine");

public sealed record KnowledgeImportResult(int Total, int Success, int Failed, IReadOnlyList<string> Errors);
