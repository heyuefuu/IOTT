using System.Net;
using MachineConnectionApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MachineConnectionApi.Tests;

internal static class AddressSpaceRegressionTests
{
    public static async Task VerifySimulator(string deviceId)
    {
        using var client = new HttpClient { BaseAddress = new Uri("http://localhost:5173/") };
        var controller = new AddressSpaceController(
            new ClientFactory(client), new ConfigurationBuilder().Build(),
            NullLogger<AddressSpaceController>.Instance);
        var result = await controller.Browse(deviceId, "ns=3;s=85/0:Simulation", "OpcUa", CancellationToken.None);
        if (result is not ContentResult { StatusCode: 200 } content)
            throw new Exception("Simulator browse failed.");
        using var nodes = System.Text.Json.JsonDocument.Parse(content.Content!);
        if (nodes.RootElement.GetArrayLength() != 7)
            throw new Exception("Expected seven Prosys simulation variables.");
        var request = new StringContent(
            "{\"tags\":[{\"address\":\"ns=3;i=1001\",\"dataType\":\"Int32\"}]}",
            System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync($"api/data/{Uri.EscapeDataString(deviceId)}/read", request);
        response.EnsureSuccessStatusCode();
        var values = await response.Content.ReadAsStringAsync();
        using var readings = System.Text.Json.JsonDocument.Parse(values);
        if (readings.RootElement.GetProperty("tags")[0].GetProperty("quality").GetString() != "Good")
            throw new Exception($"Simulator read failed: {values}");
        Console.WriteLine($"Simulator: 7 variables; Counter read: {values}");
    }

    public static async Task RunAll()
    {
        foreach (var protocol in new string?[] { "OpcUa", null })
        foreach (var nodeId in new[] { "i=2253", "ns=3;s=85/0:Simulation", "ns=1;s=ID HNC X Sjwz", "ns=1;s=a\\b//c " })
        {
            using var handler = new BrowseHandler(nodeId);
            using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            var controller = new AddressSpaceController(
                new ClientFactory(client), new ConfigurationBuilder().Build(),
                NullLogger<AddressSpaceController>.Instance);
            var result = await controller.Browse("test-device", nodeId, protocol, CancellationToken.None);
            if (result is not ContentResult { StatusCode: 200 } content || content.Content != BrowseHandler.Nodes)
                throw new Exception($"OPC UA browse failed to preserve NodeId: {nodeId}");
            if (!handler.Browsed)
                throw new Exception("OPC UA browse was not forwarded.");
        }

        using var fileHandler = new BrowseHandler("/CNC/Fanuc");
        using var fileClient = new HttpClient(fileHandler) { BaseAddress = new Uri("http://localhost/") };
        var fileController = new AddressSpaceController(
            new ClientFactory(fileClient), new ConfigurationBuilder().Build(),
            NullLogger<AddressSpaceController>.Instance);
        var fileResult = await fileController.Browse("test-device", "CNC\\Fanuc", "FOCAS", CancellationToken.None);
        if (fileResult is not ContentResult { StatusCode: 200 } || !fileHandler.Browsed)
            throw new Exception("Filesystem-style protocol path normalization regressed.");
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class BrowseHandler(string expectedNodeId) : HttpMessageHandler
    {
        public const string Nodes = "[{\"path\":\"ns=3;i=1001\",\"nodeType\":\"Variable\"}]";
        public bool Browsed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.StartsWith("/api/Devices/", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"protocol\":\"OpcUa\"}")
                });

            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            Browsed = true;
            var matches = query["parentPath"].ToString() == expectedNodeId;
            return Task.FromResult(new HttpResponseMessage(matches ? HttpStatusCode.OK : HttpStatusCode.BadGateway)
            {
                Content = new StringContent(matches ? Nodes : "BadNodeIdUnknown")
            });
        }
    }
}
