using System.Diagnostics;
using System.Text.Json;
using Grpc.Core;
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
        .AddHttpClientInstrumentation()
        .AddSource("indtec.labz.gateway")
        .AddOtlpExporter());

var app = builder.Build();
var activitySource = new ActivitySource("indtec.labz.gateway");

app.MapGet("/products/{id}", async (string id, bool? fail, IConnectionMultiplexer redis, Catalog.CatalogClient catalog, CancellationToken cancellationToken) =>
{
    var simulateFailure = fail is true;
    var cache = redis.GetDatabase();
    var cacheKey = $"product:{id}";

    using var requestActivity = activitySource.StartActivity("gateway.product");
    requestActivity?.SetTag("product.id", id);
    requestActivity?.SetTag("failure.simulated", simulateFailure);

    if (!simulateFailure)
    {
        using var cacheActivity = activitySource.StartActivity("redis.get");
        var cached = await cache.StringGetAsync(cacheKey);
        var cacheHit = cached.HasValue;
        cacheActivity?.SetTag("cache.hit", cacheHit);
        requestActivity?.SetTag("cache.hit", cacheHit);

        if (cacheHit)
            return Results.Ok(new { source = "cache", product = JsonSerializer.Deserialize<ProductDto>(cached!) });
    }

    try
    {
        using var catalogActivity = activitySource.StartActivity("catalog.grpc");
        var response = await catalog.GetProductAsync(
            new GetProductRequest { Id = id, SimulateFailure = simulateFailure },
            cancellationToken: cancellationToken);

        var product = new ProductDto(response.Id, response.Name, response.Category, response.Price);

        if (!simulateFailure)
        {
            using var cacheActivity = activitySource.StartActivity("redis.set");
            await cache.StringSetAsync(cacheKey, JsonSerializer.Serialize(product), TimeSpan.FromMinutes(2));
            cacheActivity?.SetTag("cache.ttl.seconds", 120);
        }

        return Results.Ok(new { source = "catalog", product });
    }
    catch (RpcException exception) when (exception.StatusCode == StatusCode.Unavailable)
    {
        requestActivity?.SetStatus(ActivityStatusCode.Error, exception.Status.Detail);
        requestActivity?.SetTag("failure.kind", "downstream_unavailable");
        return Results.Json(
            new { error = "catalog_unavailable", message = "Downstream catalog is temporarily unavailable.", traceId = Activity.Current?.TraceId.ToString() },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.Run();

internal sealed record ProductDto(string Id, string Name, string Category, double Price);
