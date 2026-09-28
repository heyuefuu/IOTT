namespace IndustrialIoT.Host.Services;

using IndustrialIoT.Domain.Entities;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.Interfaces;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Registration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Executes only against a deep, server-validated connection snapshot. The existing ID-only pool
/// exposes neither its creation configuration nor a validated execution lease, so borrowing it here
/// would recreate the TOCTOU defect. Verification uses a privately owned driver instead; ordinary
/// development/collection requests still use the pool. No later ID lookup can redirect this driver.
/// A fresh repository scope after I/O also detects edits during execution; it is not the protection
/// against rerouting (the frozen ConnectAsync input is). Do not reuse an EF tracked entity to check
/// for edits, and do not log the snapshot, credentials, or raw SDK exceptions.
/// </summary>
public sealed class VerificationDriverExecutor(
    IDeviceRepository repository, IProtocolDriverFactory factory, IServiceScopeFactory? scopes = null)
{
    public async Task<VerificationExecution<T>> ExecuteAsync<T>(string deviceId, VerificationExpectation expected,
        bool transfer, string operation, Func<ProtocolType, bool> protocolAllowed,
        Func<IProtocolDriver, bool> supportsOperation, Func<IProtocolDriver, Task<T>> action, CancellationToken ct)
    {
        var evidence = new VerificationEvidence { Operation = operation };
        VerificationExecution<T> Failure(int status, string code, string error) =>
            new(status, error, evidence with { Code = code }, default);
        ct.ThrowIfCancellationRequested();
        if (!VerificationConfiguration.IsDigest(expected.ConfigurationFingerprint)
            || expected.Revision is not null && !VerificationConfiguration.IsDigest(expected.Revision))
            return Failure(400, "invalid-expectation", "Verification requires a valid expected configuration.");

        IProtocolDriver? driver = null;
        try
        {
            var source = await LoadSnapshotAsync(deviceId, ct);
            if (source is null) return Failure(404, "device-not-found", "Verification device no longer exists.");
            if (VerificationConfiguration.Fingerprint(source) != expected.ConfigurationFingerprint)
                return Failure(409, "configuration-changed", "Device configuration changed; restart verification.");
            var target = await ResolveTargetAsync(source, transfer, ct);
            if (target is null) return Failure(409, "configuration-changed", "Transfer target no longer exists.");

            var revision = VerificationConfiguration.Revision(source, target, transfer);
            if (expected.Revision is not null && revision != expected.Revision)
                return Failure(409, "configuration-changed", "Device configuration changed; restart verification.");
            var protocol = transfer ? source.ConnectionConfig.Transfer?.Protocol ?? target.Protocol : source.Protocol;
            evidence = evidence with
            {
                ConfigurationMatched = true, ConfigurationFingerprint = expected.ConfigurationFingerprint,
                Revision = revision, Protocol = protocol.ToString(), Simulated = protocol == ProtocolType.Simulator,
            };
            if (evidence.Simulated) return Failure(422, "simulated-driver", "Simulated data is not verification evidence.");
            if (!protocolAllowed(protocol)) return Failure(422, "unsupported", "Protocol has no verified read operation for this request.");

            driver = factory.Create(protocol, target.Brand, target.Model);
            evidence = evidence with { Protocol = driver.Protocol.ToString(), Simulated = VerificationConfiguration.IsSimulated(driver) };
            if (evidence.Simulated) return Failure(422, "simulated-driver", "Simulated data is not verification evidence.");
            if (driver.Protocol != protocol)
                return Failure(422, "driver-protocol-mismatch", "Actual driver protocol does not match the frozen configuration.");
            if (!supportsOperation(driver)) return Failure(422, "unsupported", "Driver does not support the requested verification read.");

            // Do not give the driver a mutable dictionary belonging to our comparison snapshot.
            var config = transfer && source.ConnectionConfig.Transfer is { } inline
                ? VerificationConfiguration.TransferConfig(inline)
                : VerificationConfiguration.Clone(target.ConnectionConfig);
            var connected = await driver.ConnectAsync(config, ct);
            if (!connected.Success)
                return Failure(502, "initialization-failed", "Verification driver initialization failed; no read was attempted.");

            // Validate changes during initialization before issuing the actual read. The check uses
            // a fresh DB scope, while the already-created connection remains pinned to config above.
            if (!await UnchangedAsync(source, transfer, revision, ct))
            {
                evidence = evidence with { ConfigurationMatched = false };
                return Failure(409, "configuration-changed", "Configuration changed during initialization; no read was attempted.");
            }

            T? value = default;
            string? failureCode = null;
            ct.ThrowIfCancellationRequested();
            evidence = evidence with { OperationAttempted = true };
            try { value = await action(driver); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (NotSupportedException) { failureCode = "unsupported"; }
            catch (Exception) { failureCode = "operation-failed"; }

            if (!await UnchangedAsync(source, transfer, revision, ct))
            {
                evidence = evidence with { ConfigurationMatched = false };
                return Failure(409, "configuration-changed", "Configuration changed during verification; result is unscored.");
            }
            if (failureCode is not null)
                return Failure(failureCode == "unsupported" ? 422 : 502, failureCode,
                    failureCode == "unsupported" ? "Driver does not support this read." : "Verification read operation failed.");
            return new(200, null, evidence with { Code = "completed" }, value);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // A failure while obtaining config/creating a driver/connecting is not a failed read.
            // Even if a post-read config check itself failed, there is no trustworthy attribution.
            evidence = evidence with { ConfigurationMatched = false };
            return Failure(502, "initialization-failed", "Verification could not establish a reliable configuration/read result.");
        }
        finally
        {
            if (driver is not null)
            {
                try { await driver.DisposeAsync(); }
                catch (Exception) { /* Do not mask cancellation/results or expose SDK credentials. */ }
            }
        }
    }

    private async Task<bool> UnchangedAsync(Device source, bool transfer, string revision, CancellationToken ct)
    {
        var current = await LoadSnapshotAsync(source.Id, ct);
        if (current is null) return false;
        var target = await ResolveTargetAsync(current, transfer, ct);
        return target is not null && VerificationConfiguration.Revision(current, target, transfer) == revision;
    }

    private async Task<Device?> ResolveTargetAsync(Device source, bool transfer, CancellationToken ct)
    {
        if (!transfer || source.ConnectionConfig.Transfer is not null
            || !source.ConnectionConfig.ExtendedProperties.TryGetValue("transferDeviceId", out var targetId)
            || string.IsNullOrWhiteSpace(targetId) || string.Equals(targetId, source.Id, StringComparison.OrdinalIgnoreCase))
            return source;
        return await LoadSnapshotAsync(targetId, ct);
    }

    private async Task<Device?> LoadSnapshotAsync(string id, CancellationToken ct)
    {
        // Production DI supplies IServiceScopeFactory without new Program.cs registration. Tests
        // can use an in-memory repository directly. Each production query is a fresh unit of work.
        if (scopes is null)
        {
            var device = await repository.GetByIdAsync(id, ct);
            return device is null ? null : VerificationConfiguration.Snapshot(device);
        }
        using var scope = scopes.CreateScope();
        var fresh = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
        var current = await fresh.GetByIdAsync(id, ct);
        return current is null ? null : VerificationConfiguration.Snapshot(current);
    }
}
