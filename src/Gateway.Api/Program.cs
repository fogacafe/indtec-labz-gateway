using System.Diagnostics;
using System.Text.Json;
using Indtec.Labz.Gateway.Contracts;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

var catalogAddress = builder.Configuration["Catalog:Address"] ?? "http://localhost:5081";
var redisConnection = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";

builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConnection));
builder.Services.AddGrpcClient<Catalog.CatalogClient>(options => options.Address = new Uri(catalogAddress))
    .AddStandardResilienceHandler();

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("gateway-api"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddGrpcClientInstrumentation()
        .AddSource("indtec.labz.gateway")
        .AddOtlpExporter());

var app = builder.Build();
var activitySource = new ActivitySource("indtec.labz.gateway");

app.MapGet("/products/{id}", async (string id, IConnectionMultiplexer redis, Catalog.CatalogClient catalog, CancellationToken cancellationToken) =>
{
    var cache = redis.GetDatabase();
    var cacheKey = $"product:{id}";

    using (var cacheActivity = activitySource.StartActivity("redis.get"))
    {
        var cached = await cache.StringGetAsync(cacheKey);
        if (cached.HasValue)
        {
            cacheActivity?.SetTag("cache.hit", true);
            return Results.Ok(new { source = "cache", product = JsonSerializer.Deserialize<ProductDto>(cached!) });
        }
        cacheActivity?.SetTag("cache.hit", false);
    }

    var response = await catalog.GetProductAsync(new GetProductRequest { Id = id }, cancellationToken: cancellationToken);
    var product = new ProductDto(response.Id, response.Name, response.Category, response.Price);

    using (activitySource.StartActivity("redis.set"))
        await cache.StringSetAsync(cacheKey, JsonSerializer.Serialize(product), TimeSpan.FromMinutes(2));

    return Results.Ok(new { source = "catalog", product });
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.Run();

internal sealed record ProductDto(string Id, string Name, string Category, double Price);
