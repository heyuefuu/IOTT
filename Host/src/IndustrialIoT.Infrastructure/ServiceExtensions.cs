namespace IndustrialIoT.Infrastructure;

using IndustrialIoT.Domain.Interfaces;
using IndustrialIoT.Infrastructure.BackgroundServices;
using IndustrialIoT.Infrastructure.Messaging;
using IndustrialIoT.Infrastructure.Persistence;
using IndustrialIoT.Protocols.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public static class InfrastructureServiceExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string connectionString, IConfiguration configuration)
    {
        services.AddDbContext<IoTDbContext>(options =>
            options.UseSqlServer(connectionString));

        services.AddScoped<IDeviceRepository, EfDeviceRepository>();
        services.AddScoped<INCProgramRepository, EfNCProgramRepository>();
        services.AddScoped<ICollectionProfileRepository, EfCollectionProfileRepository>();

        services.AddSingleton<CollectionSchedulerService>();
        services.AddSingleton<ICollectionPipeline>(sp => sp.GetRequiredService<CollectionSchedulerService>());
        services.AddHostedService(sp => sp.GetRequiredService<CollectionSchedulerService>());

        services.AddSingleton<ConnectionPoolService>();
        services.AddSingleton<IDeviceConnectionPool>(sp => sp.GetRequiredService<ConnectionPoolService>());
        services.AddHostedService(sp => sp.GetRequiredService<ConnectionPoolService>());

        // Data output — configuration
        services.Configure<MqttOutputOptions>(options =>
            configuration.GetSection(MqttOutputOptions.SectionName).Bind(options));
        services.Configure<KafkaOutputOptions>(options =>
            configuration.GetSection(KafkaOutputOptions.SectionName).Bind(options));
        services.Configure<DatabaseOutputOptions>(options =>
            configuration.GetSection(DatabaseOutputOptions.SectionName).Bind(options));
        services.Configure<HttpWebhookOutputOptions>(options =>
            configuration.GetSection(HttpWebhookOutputOptions.SectionName).Bind(options));

        // Data output — implementations
        services.AddSingleton<MqttDataOutput>();
        services.AddSingleton<IDataOutput>(sp => sp.GetRequiredService<MqttDataOutput>());

        var kafkaOptions = configuration.GetSection(KafkaOutputOptions.SectionName).Get<KafkaOutputOptions>();
        if (kafkaOptions?.Enabled == true)
        {
            services.AddSingleton<KafkaDataOutput>();
            services.AddSingleton<IDataOutput>(sp => sp.GetRequiredService<KafkaDataOutput>());
        }

        var databaseOptions = configuration.GetSection(DatabaseOutputOptions.SectionName).Get<DatabaseOutputOptions>();
        if (databaseOptions?.Enabled == true)
        {
            services.AddSingleton<DatabaseDataOutput>();
            services.AddSingleton<IDataOutput>(sp => sp.GetRequiredService<DatabaseDataOutput>());
        }

        var webhookOptions = configuration.GetSection(HttpWebhookOutputOptions.SectionName).Get<HttpWebhookOutputOptions>();
        if (webhookOptions?.Enabled == true)
        {
            services.AddSingleton<HttpWebhookDataOutput>();
            services.AddSingleton<IDataOutput>(sp => sp.GetRequiredService<HttpWebhookDataOutput>());
        }

        // Data output — dispatcher
        services.AddHostedService<DataOutputDispatcherService>();

        return services;
    }
}
