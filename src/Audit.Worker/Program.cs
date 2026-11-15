using Audit.Worker;
using Shared.Contracts.Data;
using Shared.Contracts.Messaging;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);

builder.Services.AddAuctionDatabase(builder.Configuration);
builder.Services.Configure<RabbitMqSettings>(builder.Configuration.GetSection("RabbitMQ"));
builder.Services.AddHostedService<Worker>();

var host = builder.Build();

await host.Services.EnsureAuctionDatabaseSeededAsync();

host.Run();
