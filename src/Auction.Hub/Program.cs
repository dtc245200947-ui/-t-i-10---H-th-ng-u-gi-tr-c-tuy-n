using Auction.Hub.Hubs;
using Auction.Hub.Services;
using Shared.Contracts.Data;
using Shared.Contracts.Messaging;

var webRootCandidates = new[]
{
    Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"),
    Path.Combine(Directory.GetCurrentDirectory(), "src", "Auction.Hub", "wwwroot"),
    Path.Combine(AppContext.BaseDirectory, "wwwroot")
};
var resolvedWebRoot = webRootCandidates.FirstOrDefault(Directory.Exists);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = resolvedWebRoot
});
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);

var httpPort = builder.Configuration.GetValue<int>("Server:Port", 5052);
builder.WebHost.UseUrls($"http://0.0.0.0:{httpPort}");

builder.Services.AddAuctionDatabase(builder.Configuration);
builder.Services.Configure<RabbitMqSettings>(builder.Configuration.GetSection("RabbitMQ"));

builder.Services.AddSignalR();
builder.Services.AddHostedService<RabbitMqHubConsumerService>();
builder.Services.AddHealthChecks();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.SetIsOriginAllowed(_ => true)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials());
});

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

await app.Services.EnsureAuctionDatabaseSeededAsync();

app.MapHealthChecks("/health");

// Endpoint cung cấp cấu hình động cho giao diện Web App
app.MapGet("/client-config", (IConfiguration config) =>
{
    var publicApiUrl = config["ClientConfig:PublicAuctionApiUrl"] ?? "http://localhost:5050";
    return Results.Ok(new
    {
        auctionApiUrl = publicApiUrl,
        hubPath = "/hubs/auction",
        serviceName = "Auction.Hub & Web Portal"
    });
});

app.MapHub<AuctionLiveHub>("/hubs/auction");

app.Run();
