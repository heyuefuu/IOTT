namespace MachineConnectionApi.Tests;

using System.Text.Json;
using MachineConnectionApi.Models;
using MachineConnectionApi.Options;
using MachineConnectionApi.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

internal static partial class InfluxSettingsRegressionTests
{
    public static async Task RunAll()
    {
        SettingsPersistAndInvalidateCachedOptions();
        InvalidSettingsAreRejected();
        await ConnectionTestChecksSqlWithoutSaving();
        await WriterSwitchesConnectionsWithoutInterruptingInflightWrites();
    }

    private static ServiceProvider CreateProvider(string fileName, string url = "http://127.0.0.1:8181")
    {
        var services = new ServiceCollection();
        services.Configure<InfluxDbOptions>(options =>
        {
            options.Enabled = true;
            options.Url = url;
            options.Org = "fixture-org";
            options.Bucket = "fixture-db";
            options.Measurement = "datapoint";
            options.Token = "fixture-token";
            options.WriteRetryCount = 0;
        });
        services.AddSingleton(provider => new InfluxSettingsStore(
            provider.GetRequiredService<IOptionsMonitorCache<InfluxDbOptions>>(), fileName));
        services.AddSingleton<IPostConfigureOptions<InfluxDbOptions>>(provider =>
            provider.GetRequiredService<InfluxSettingsStore>());
        return services.BuildServiceProvider();
    }

    private static InfluxSettingsRequest Settings(string url = "http://localhost:8181") => new()
    {
        Enabled = true, Url = url, Org = "fixture-org", Bucket = "fixture-db", Measurement = "datapoint",
    };

    private static void SettingsPersistAndInvalidateCachedOptions()
    {
        var fileName = $"influx-settings-{Guid.NewGuid():N}.json";
        try
        {
            using var provider = CreateProvider(fileName);
            var monitor = provider.GetRequiredService<IOptionsMonitor<InfluxDbOptions>>();
            var store = provider.GetRequiredService<InfluxSettingsStore>();
            var before = monitor.CurrentValue;
            store.Save(Settings("https://remote.example.test:8443/influx/"), before);
            var after = monitor.CurrentValue;
            Check(!ReferenceEquals(before, after), "Options cache was not invalidated");
            Check(after.Url == "https://remote.example.test:8443/influx", "Remote URL was not normalized");
            Check(after.Token == before.Token, "Blank token did not preserve the existing credential");
            var response = JsonSerializer.Serialize(InfluxSettingsResponse.From(after), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using var json = JsonDocument.Parse(response);
            Check(!json.RootElement.TryGetProperty("token", out _) && !response.Contains(before.Token), "Settings response exposed a token");
            Check(json.RootElement.GetProperty("hasToken").GetBoolean(), "Saved token presence was lost");
            store.Save(Settings() with { Token = "rotated-fixture" }, after);
            store.Save(Settings("http://[::1]:8181/") with { Token = " " }, before);
            var reloaded = new InfluxDbOptions();
            new InfluxSettingsStore(new OptionsCache<InfluxDbOptions>(), fileName).PostConfigure(null, reloaded);
            Check(reloaded.Url == "http://[::1]:8181" && reloaded.Token == "rotated-fixture", "Reload or concurrent blank-token preservation failed");
            Check(reloaded.Bucket == "fixture-db" && reloaded.Measurement == "datapoint", "Saved database settings were lost");
        }
        finally { File.Delete(SettingsPath(fileName)); }
    }

    private static void InvalidSettingsAreRejected()
    {
        var current = new InfluxDbOptions { Token = "fixture-token" };
        foreach (var url in new[] { "file:///tmp/influx", "ftp://example.test", "http://user:pass@host:8181", "http://host:8181?x=1", "http://host:8181/#part" })
            ExpectRejected(Settings(url), current);
        foreach (var table in new[] { "data.point", "data;DROP TABLE other", "1table", "table\nname" })
            ExpectRejected(Settings() with { Measurement = table }, current);
        ExpectRejected(Settings() with { Org = "" }, current);
        ExpectRejected(Settings() with { Bucket = "" }, current);
        ExpectRejected(Settings(), new InfluxDbOptions());
        ExpectRejected(Settings() with { Token = "line\r\nbreak" }, current);
        Check(InfluxSettingsStore.Resolve(Settings() with { Enabled = false }, new()).Token == "", "Disabled unconfigured settings should be saveable");
    }

    private static void ExpectRejected(InfluxSettingsRequest request, InfluxDbOptions current)
    {
        try { InfluxSettingsStore.Resolve(request, current); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Invalid Influx settings were accepted");
    }

    private static string SettingsPath(string fileName) => Path.Combine(AppContext.BaseDirectory, "App_Data", fileName);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
