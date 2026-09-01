namespace IndustrialIoT.Host.Controllers;

using System.Collections.Concurrent;
using System.Diagnostics;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.Interfaces;
using IndustrialIoT.Protocols.Registration;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/connection-verification")]
public class ConnectionVerificationController : ControllerBase
{
    private readonly IProtocolDriverFactory _factory;
    private readonly IDeviceRepository _deviceRepo;
    private readonly ILogger<ConnectionVerificationController> _logger;

    // In-memory store for test results (production: use distributed cache)
    private static readonly ConcurrentDictionary<string, BatchTestResult> _results = new();

    public ConnectionVerificationController(
        IProtocolDriverFactory factory,
        IDeviceRepository deviceRepo,
        ILogger<ConnectionVerificationController> logger)
    {
        _factory = factory;
        _deviceRepo = deviceRepo;
        _logger = logger;
    }

    /// <summary>批量连接验证</summary>
    [HttpPost("start")]
    public async Task<ActionResult<BatchTestResult>> StartBatchTest(
        [FromBody] BatchTestRequest request,
        CancellationToken ct)
    {
        var devices = request.DeviceIds is { Count: > 0 }
            ? (await Task.WhenAll(request.DeviceIds.Select(id => _deviceRepo.GetByIdAsync(id, ct))))
                .Where(d => d is not null).Select(d => d!).ToList()
            : request.DeviceType is not null
                ? (await _deviceRepo.GetByTypeAsync(request.DeviceType.Value, ct)).ToList()
                : [];

        if (devices.Count == 0)
            return BadRequest("No devices found for the given criteria");

        var concurrency = Math.Clamp(request.Concurrency ?? 50, 1, 500);
        var semaphore = new SemaphoreSlim(concurrency);
        var results = new ConcurrentBag<DeviceTestResult>();
        var sw = Stopwatch.StartNew();

        var tasks = devices.Select(async device =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var itemSw = Stopwatch.StartNew();
                try
                {
                    await using var driver = _factory.Create(device!.Protocol, device.Brand, device.Model);
                    var connectResult = await driver.ConnectAsync(device.ConnectionConfig, ct);
                    if (!connectResult.Success)
                    {
                        itemSw.Stop();
                        results.Add(new DeviceTestResult
                        {
                            DeviceId = device.Id,
                            Success = false,
                            LatencyMs = itemSw.Elapsed.TotalMilliseconds,
                            ErrorMessage = $"Connection failed: {connectResult.ErrorMessage}",
                        });
                        return;
                    }

                    var ping = await driver.PingAsync(ct);
                    itemSw.Stop();

                    results.Add(new DeviceTestResult
                    {
                        DeviceId = device.Id,
                        Success = ping,
                        LatencyMs = itemSw.Elapsed.TotalMilliseconds,
                        ErrorMessage = ping ? null : "Ping returned false",
                    });
                }
                catch (Exception ex)
                {
                    itemSw.Stop();
                    results.Add(new DeviceTestResult
                    {
                        DeviceId = device!.Id,
                        Success = false,
                        LatencyMs = itemSw.Elapsed.TotalMilliseconds,
                        ErrorMessage = ex.Message,
                    });
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        sw.Stop();

        var testId = Guid.NewGuid().ToString("N");
        var successCount = results.Count(r => r.Success);
        var batchResult = new BatchTestResult
        {
            TestId = testId,
            TotalDevices = devices.Count,
            SuccessCount = successCount,
            FailureCount = devices.Count - successCount,
            SuccessRate = devices.Count > 0
                ? Math.Round((double)successCount / devices.Count, 4)
                : 0,
            Results = results.ToList(),
            DurationMs = sw.Elapsed.TotalMilliseconds,
        };

        _results[testId] = batchResult;

        _logger.LogInformation(
            "Batch connection test {TestId}: {Success}/{Total} succeeded in {Duration}ms",
            testId, successCount, devices.Count, sw.ElapsedMilliseconds);

        return Ok(batchResult);
    }

    /// <summary>获取连接验证结果</summary>
    [HttpGet("{testId}")]
    public ActionResult<BatchTestResult> GetResult(string testId)
    {
        if (!_results.TryGetValue(testId, out var result))
            return NotFound($"Test {testId} not found");

        return Ok(result);
    }
}

public record BatchTestRequest
{
    public IReadOnlyList<string>? DeviceIds { get; init; }
    public DeviceType? DeviceType { get; init; }
    public int? Concurrency { get; init; }
}

public record BatchTestResult
{
    public required string TestId { get; init; }
    public int TotalDevices { get; init; }
    public int SuccessCount { get; init; }
    public int FailureCount { get; init; }
    public double SuccessRate { get; init; }
    public required IReadOnlyList<DeviceTestResult> Results { get; init; }
    public double DurationMs { get; init; }
}

public record DeviceTestResult
{
    public required string DeviceId { get; init; }
    public bool Success { get; init; }
    public double LatencyMs { get; init; }
    public string? ErrorMessage { get; init; }
}
