namespace MachineConnectionApi.Tests;

using System.Net;
using MachineConnectionApi.Controllers;
using MachineConnectionApi.Models;
using MachineConnectionApi.Options;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

internal static partial class InfluxSettingsRegressionTests
{
    private static async Task ConnectionTestChecksSqlWithoutSaving()
    {
        var fileName = $"influx-test-{Guid.NewGuid():N}.json";
        try
        {
            using var provider = CreateProvider(fileName);
            var monitor = provider.GetRequiredService<IOptionsMonitor<InfluxDbOptions>>();
            var requests = new List<Uri>();
            using var factory = new SqlFactory(request =>
            {
                Check(request.Method == HttpMethod.Get, "Connection test attempted a write");
                Check(request.Headers.Authorization?.Parameter == "fixture-token", "Connection test did not use the existing credential");
                requests.Add(request.RequestUri!);
                return JsonResponse(HttpStatusCode.OK, "[{\"table_count\":0}]");
            });
            var tester = new InfluxConnectionTester(factory);
            var controller = new InfluxSettingsController(provider.GetRequiredService<InfluxSettingsStore>(),
                monitor, tester, NullLogger<InfluxSettingsController>.Instance);
            var result = await controller.Test(Settings("https://remote.example.test:8443"), CancellationToken.None);
            Check(result.Result is OkObjectResult { Value: InfluxConnectionTestResult { Success: true } }, "Missing table should allow a database connection");
            Check(requests.Count == 1 && requests[0].Host == "remote.example.test", "Candidate remote URL was not used");
            Check(requests[0].AbsolutePath == "/api/v3/query_sql" &&
                Uri.UnescapeDataString(requests[0].Query).Contains("information_schema.tables"), "Connection test did not query the database schema");
            Check(monitor.CurrentValue.Url == "http://127.0.0.1:8181" && !File.Exists(SettingsPath(fileName)), "Connection test persisted candidate settings");
            Check(controller.Get().Value?.HasToken == true, "Settings read lost existing token presence");
            await ProbeExistingTable(monitor.CurrentValue);
            foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound })
            {
                using var failureFactory = new SqlFactory(_ => JsonResponse(status, "fixture-token must never be echoed"));
                var failure = await new InfluxConnectionTester(failureFactory).TestAsync(monitor.CurrentValue, CancellationToken.None);
                Check(!failure.Success && !failure.Message.Contains("fixture-token"), "Connection failure leaked data or reported success");
            }
            foreach (var body in new[] { "<html>wrong service</html>", "[]", "[1]", "[{\"table_count\":\"unexpected\"}]" })
            {
                using var malformed = new SqlFactory(_ => JsonResponse(HttpStatusCode.OK, body));
                var failure = await new InfluxConnectionTester(malformed).TestAsync(monitor.CurrentValue, CancellationToken.None);
                Check(!failure.Success, "Malformed SQL response was accepted");
            }
            using var refused = new SqlFactory(_ => throw new HttpRequestException("fixture-token"));
            var unavailable = await new InfluxConnectionTester(refused).TestAsync(monitor.CurrentValue, CancellationToken.None);
            Check(!unavailable.Success && !unavailable.Message.Contains("fixture-token"), "Network error was not safely reported");
        }
        finally { File.Delete(SettingsPath(fileName)); }
    }

    private static async Task ProbeExistingTable(InfluxDbOptions options)
    {
        var calls = 0;
        using var factory = new SqlFactory(request =>
        {
            calls++;
            var sql = Uri.UnescapeDataString(request.RequestUri!.Query);
            Check(request.Method == HttpMethod.Get, "Probe issued a database write");
            if (calls == 1) return JsonResponse(HttpStatusCode.OK, "[{\"table_count\":1}]");
            Check(sql.Contains("SELECT * FROM \"datapoint\" LIMIT 1"), "Probe did not verify table reads");
            return JsonResponse(HttpStatusCode.OK, "[{\"value_f\":1.5}]");
        });
        var result = await new InfluxConnectionTester(factory).TestAsync(options, CancellationToken.None);
        Check(result.Success && calls == 2, "Existing table probe failed");
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    };

    private sealed class SqlFactory(Func<HttpRequestMessage, HttpResponseMessage> respond) : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new(new SqlHandler(respond));
        public HttpClient CreateClient(string name) => _client;
        public void Dispose() => _client.Dispose();
    }

    private sealed class SqlHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
