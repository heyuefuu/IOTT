using System.Diagnostics;
using System.Text.Json.Serialization;
using MachineConnectionApi.Models;

namespace MachineConnectionApi.Services;

/// <summary>
/// Measurement boundary, not a connectivity simulator. HasEvidence requires an actual operation in
/// this run; Supported/configuration alone is never success. Implementations must propagate caller
/// cancellation. A transfer implementation must use create-only writes, never execute NC programs,
/// and report HasEvidence=false when it cannot safely produce a complete observation.
/// </summary>
public interface IVerifyMeasurementAdapter
{
    Task<VerifyReadObservation> ReadAsync(VerifyMeasurementTarget target, string address, string dataType, int timeoutMs, CancellationToken ct);
    Task<VerifyReadObservation> ReadTransferDirectoryAsync(VerifyMeasurementTarget target, string directory, int timeoutMs, CancellationToken ct);
    Task<VerifyAttachmentValidation> ValidateAttachmentAsync(EvaluationFile file, CancellationToken ct);
    Task<VerifyTransferObservation> TransferAsync(VerifyMeasurementTarget target, EvaluationFile file, string directory,
        string remoteName, Action<double> reportFraction, CancellationToken ct);
}

public sealed record VerifyReadObservation(bool Supported, bool HasEvidence, bool Success, string Detail, string? Protocol = null);
public sealed record VerifyAttachmentValidation(bool Valid, string Detail);
public sealed record VerifyTransferObservation(bool Supported, bool HasEvidence, bool Success, bool IntegrityVerified,
    long UploadedBytes, double UploadSeconds, string Detail);

public sealed class VerifyMeasurementTarget
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Model { get; init; } = "";
    public string Brand { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string Protocol { get; init; } = "";
    public string? Username { get; init; }
    public int ConnectTimeoutMs { get; init; }
    public int ReadTimeoutMs { get; init; }
    public Dictionary<string, string> ExtendedProperties { get; init; } = new(StringComparer.Ordinal);
    public VerifyTransferTarget? Transfer { get; init; }

    // Loaded once per run's target object, not retained in an adapter-wide/history cache. Selecting
    // a rule address bypasses this cache. The tuple contains only an already-configured Host tag.
    [JsonIgnore] internal (string Address, string DataType)? ConfiguredReadPoint { get; set; }
    [JsonIgnore] internal bool ReadPointResolved { get; set; }
    [JsonIgnore] internal string? ResolvedTransferProtocol { get; set; }

    // Host revision is an opaque, keyed digest, not credentials or a serializable run snapshot.
    // Serialize first-use binding per target so concurrent requests cannot bind different revisions.
    [JsonIgnore] internal SemaphoreSlim VerificationGate { get; } = new(1, 1);
    [JsonIgnore] internal string? VerificationReadRevision { get; set; }
    [JsonIgnore] internal string? VerificationDirectoryRevision { get; set; }
    [JsonIgnore] internal bool VerificationConfigurationChanged { get; set; }
}

public sealed class VerifyTransferTarget
{
    public string Protocol { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string? Username { get; init; }
    public int ConnectTimeoutMs { get; init; }
    public int ReadTimeoutMs { get; init; }
    public Dictionary<string, string> ExtendedProperties { get; init; } = new(StringComparer.Ordinal);
}

public interface IVerifyTestClock
{
    TimeSpan Elapsed { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}

public sealed class SystemVerifyTestClock : IVerifyTestClock
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    public TimeSpan Elapsed => _watch.Elapsed;
    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);
}
