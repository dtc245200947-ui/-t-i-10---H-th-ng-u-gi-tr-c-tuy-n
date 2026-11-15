using BidValidation.Grpc.Services;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var builder = WebApplication.CreateBuilder(args);

var grpcPort = builder.Configuration.GetValue<int>("Server:GrpcPort", 5051);
var httpHealthPort = builder.Configuration.GetValue<int>("Server:HealthPort", 5151);

builder.WebHost.ConfigureKestrel(options =>
{
    // Cổng gRPC chuyên dụng (HTTP/2 cleartext - h2c)
    options.ListenAnyIP(grpcPort, listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http2;
    });

    // Cổng HTTP/1.1 phục vụ Health Check & giám sát
    options.ListenAnyIP(httpHealthPort, listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http1;
    });
});

builder.Services.AddGrpc();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapGrpcService<BidValidatorService>();
app.MapHealthChecks("/health");

app.MapGet("/", () => Results.Ok(new
{
    service = "BidValidation.Grpc",
    status = "Running",
    grpcPort,
    httpHealthPort,
    timestampUtc = DateTime.UtcNow
}));

app.Run();
