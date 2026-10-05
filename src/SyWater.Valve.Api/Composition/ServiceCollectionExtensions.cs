using Microsoft.EntityFrameworkCore;
using SyWater.Valve.Api.Background;
using SyWater.Valve.Api.Messaging;
using SyWater.Valve.Api.Security;
using SyWater.Valve.Application.Ports.In;
using SyWater.Valve.Application.Ports.Out;
using SyWater.Valve.Application.UseCases;
using SyWater.Valve.Domain.Valves;
using SyWater.Valve.Infrastructure.Http;
using SyWater.Valve.Infrastructure.Persistence;

namespace SyWater.Valve.Api.Composition;

/// <summary>Composition root: the ONLY place that knows which adapter implements each port.</summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddValveApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(ValvePolicy.Default);
        services.AddScoped<IGetValveUseCase, GetValveUseCase>();
        services.AddScoped<IRequestValveCommandUseCase, RequestValveCommandUseCase>();
        services.AddScoped<IGetValveCommandUseCase, GetValveCommandUseCase>();
        services.AddScoped<IListValveCommandsUseCase, ListValveCommandsUseCase>();
        services.AddScoped<IDeviceEventsHandler, DeviceEventsHandler>();
        services.AddScoped<ITimeOutCommandsUseCase, TimeOutCommandsUseCase>();
        return services;
    }

    public static IServiceCollection AddValveInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("Valve");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Missing connection string 'ConnectionStrings:Valve' (user-secrets).");

        services.AddDbContext<ValveDbContext>(options => options.UseSqlServer(connectionString));
        services.AddScoped<IValveRepository, EfValveRepository>();
        services.AddScoped<IValveCommandRepository, EfValveCommandRepository>();

        var placesUrl = Required(config, "Services:PlacesBaseUrl");
        var iamUrl = Required(config, "Services:IamBaseUrl");
        var devicesUrl = Required(config, "Services:DevicesBaseUrl");
        var internalKey = config["Internal:ApiKey"];
        if (string.IsNullOrWhiteSpace(internalKey) || internalKey.Length < 24)
            throw new InvalidOperationException(
                "Missing 'Internal:ApiKey' (user-secrets, at least 24 chars). It must be the SAME value as in ms-devices.");

        services.AddHttpContextAccessor();
        services.AddScoped<IAccessTokenProvider, HttpContextAccessTokenProvider>();

        services.AddHttpClient<IPlaceAccessChecker, HttpPlaceAccessChecker>(c => Configure(c, placesUrl));
        services.AddHttpClient<IConfirmationCodeVerifier, HttpConfirmationCodeVerifier>(c => Configure(c, iamUrl));
        services.AddHttpClient(HttpDeviceGateway.ServiceName, c => Configure(c, devicesUrl))
            .AddTypedClient<IDeviceGateway>((http, sp) => new HttpDeviceGateway(
                http, sp.GetRequiredService<IAccessTokenProvider>(), internalKey, sp.GetRequiredService<TimeProvider>()));
        return services;
    }

    /// <summary>RabbitMQ consumer (empty RabbitMq:Host = not started) + the 30 s timeout sweeper (always).</summary>
    public static IServiceCollection AddValveBackground(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<RabbitMqOptions>(config.GetSection(RabbitMqOptions.Section));
        if (!string.IsNullOrWhiteSpace(config[$"{RabbitMqOptions.Section}:Host"]))
            services.AddHostedService<DeviceEventsConsumer>();
        services.AddHostedService<CommandTimeoutSweeper>();
        return services;
    }

    private static string Required(IConfiguration config, string key) =>
        string.IsNullOrWhiteSpace(config[key]) ? throw new InvalidOperationException($"Missing '{key}'.") : config[key]!;

    private static void Configure(HttpClient client, string baseUrl)
    {
        client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
        client.Timeout = TimeSpan.FromSeconds(5);
    }
}
