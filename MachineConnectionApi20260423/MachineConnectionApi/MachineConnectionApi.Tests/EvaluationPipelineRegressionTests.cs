namespace MachineConnectionApi.Tests;

using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

internal static class EvaluationPipelineRegressionTests
{
    private static EvaluationConfig ChangeProtocol(EvaluationConfig config, string name, string? metricId) => config with
    {
        Indicators = config.Indicators.Select(section => section with
        {
            Children = section.Children.Select(child => child with
            {
                Items = child.Items.Select(item => item.Id == "i_18" ? item with { Name = name, MetricId = metricId } : item).ToList(),
            }).ToList(),
        }).ToList(),
    };

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public class RejectCsProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new InvalidOperationException("Unexpected live connectivity operation");
    }

    private sealed class EmptyMetricStore : IMetricStore
    {
        public List<MetricDto> ReadAll() => [];
        public void WriteAll(IEnumerable<MetricDto> items) => throw new InvalidOperationException("Unexpected metric write");
        public TResult Update<TResult>(Func<List<MetricDto>, TResult> update) => throw new InvalidOperationException("Unexpected metric update");
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DeviceHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Assert(request.RequestUri?.AbsolutePath == "/api/Devices", "Unexpected HTTP operation");
            var body = JsonSerializer.Serialize(new[] { new { id = "fixture-machine", name = "测试机床", model = "fixture-model", brand = "fixture-brand", host = "192.0.2.15", port = 502, protocol = "ModbusTCP", connectTimeoutMs = 1234, readTimeoutMs = 2345, extendedProperties = new { DeviceCode = "CNC-001", ControlSystem = "fixture-system" } } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    public static async Task RunAll()
    {
        var fileName = $"evaluation-pipeline-test-{Guid.NewGuid():N}.json";
        var path = Path.Combine(AppContext.BaseDirectory, "App_Data", fileName);
        try
        {
            var store = new EvaluationIndicatorStore(fileName);
            var saved = store.Save(ChangeProtocol(store.Get("machine"), "新工控协议评价名称", "industrial-protocol"));
            using var handler = new DeviceHandler();
            using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://evaluation-fixture.invalid/") };
            var service = new VerifyAutomationService(new ClientFactory(httpClient), DispatchProxy.Create<ICsConnectivityService, RejectCsProxy>(),
                new EmptyMetricStore(), new ConfigurationBuilder().Build(), NullLogger<VerifyAutomationService>.Instance, store);
            var request = new VerifyRunRequest { DeviceId = "fixture-machine", TaskName = "隔离评价测试", EvaluationCategory = "machine", MetricIds = ["industrial-protocol"] };
            var response = await service.RunAsync(request, CancellationToken.None);
            Assert(response.Metrics.Count == 1 && response.Metrics[0].Name == "新工控协议评价名称", "Task ignored the managed indicator name");
            Assert(response.EvaluationSnapshot?.Version == saved.Version && response.EvaluationSnapshot.Category == "machine", "Task config snapshot mismatch");
            Assert(response.MachineSnapshot is { Id: "fixture-machine", Name: "测试机床", DeviceCode: "CNC-001", Model: "fixture-model", ControlSystem: "fixture-system", Host: "192.0.2.15", Port: 502, Protocol: "ModbusTCP", ConnectTimeoutMs: 1234, ReadTimeoutMs: 2345 }, "Task machine metadata snapshot mismatch");
            store.Save(ChangeProtocol(store.Get("machine"), "后来修改名称", "industrial-protocol"));
            Assert(response.EvaluationSnapshot!.Indicators.SelectMany(section => section.Children).SelectMany(child => child.Items).Single(item => item.Id == "i_18").Name == "新工控协议评价名称", "Later config edits changed a completed task snapshot");
            store.Save(ChangeProtocol(store.Get("machine"), "后来修改名称", null));
            var rejected = false;
            try { await service.RunAsync(request, CancellationToken.None); }
            catch (ArgumentException) { rejected = true; }
            Assert(rejected && handler.RequestCount == 1, "Removed automation binding was not rejected before device access");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
