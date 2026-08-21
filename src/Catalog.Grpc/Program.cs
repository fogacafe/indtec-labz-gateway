using Indtec.Labz.Gateway.Contracts;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("catalog-grpc"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter());

var app = builder.Build();
app.MapGrpcService<CatalogService>();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.Run();

sealed class CatalogService : Catalog.CatalogBase
{
    public override async Task<ProductReply> GetProduct(GetProductRequest request, Grpc.Core.ServerCallContext context)
    {
        await Task.Delay(40, context.CancellationToken);
        return new ProductReply
        {
            Id = request.Id,
            Name = $"Product {request.Id}",
            Category = "LABZ",
            Price = 149.90
        };
    }
}
