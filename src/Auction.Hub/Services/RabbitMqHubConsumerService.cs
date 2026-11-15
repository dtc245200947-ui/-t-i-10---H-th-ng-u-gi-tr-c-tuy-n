using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Auction.Hub.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Shared.Contracts.Data;
using Shared.Contracts.Events;
using Shared.Contracts.Messaging;

namespace Auction.Hub.Services;

/// <summary>
/// Dịch vụ lắng nghe sự kiện đấu giá từ RabbitMQ (kèm cơ chế fallback đọc từ Outbox khi chạy local không bật RabbitMQ)
/// và phát thông báo realtime qua SignalR đến các Bidder Clients.
/// </summary>
public class RabbitMqHubConsumerService : BackgroundService
{
    private readonly RabbitMqSettings _settings;
    private readonly IHubContext<AuctionLiveHub> _hubContext;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RabbitMqHubConsumerService> _logger;
    private readonly ConcurrentDictionary<string, DateTime> _processedEvents = new();
    private IConnection? _connection;
    private IModel? _channel;

    public RabbitMqHubConsumerService(
        IOptions<RabbitMqSettings> settings,
        IHubContext<AuctionLiveHub> hubContext,
        IServiceScopeFactory scopeFactory,
        ILogger<RabbitMqHubConsumerService> logger)
    {
        _settings = settings.Value;
        _hubContext = hubContext;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                EnsureRabbitMqSubscription();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Auction.Hub đang chờ RabbitMQ tại {Host}:{Port}...", _settings.HostName, _settings.Port);
            }

            try
            {
                await PollFallbackOutboxEventsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Lỗi nhẹ khi kiểm tra Outbox dự phòng tại Auction.Hub");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    private void EnsureRabbitMqSubscription()
    {
        if (_channel is { IsOpen: true } && _connection is { IsOpen: true })
        {
            return;
        }

        _channel?.Dispose();
        _connection?.Dispose();

        var factory = new ConnectionFactory
        {
            HostName = _settings.HostName,
            Port = _settings.Port,
            UserName = _settings.UserName,
            Password = _settings.Password,
            AutomaticRecoveryEnabled = true,
            DispatchConsumersAsync = true,
            RequestedConnectionTimeout = TimeSpan.FromSeconds(2)
        };

        _connection = factory.CreateConnection();
        _channel = _connection.CreateModel();

        _channel.ExchangeDeclare(
            exchange: _settings.ExchangeName,
            type: ExchangeType.Fanout,
            durable: true,
            autoDelete: false);

        _channel.QueueDeclare(
            queue: _settings.HubQueueName,
            durable: true,
            exclusive: false,
            autoDelete: false);

        _channel.QueueBind(
            queue: _settings.HubQueueName,
            exchange: _settings.ExchangeName,
            routingKey: string.Empty);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += async (_, ea) =>
        {
            try
            {
                var json = Encoding.UTF8.GetString(ea.Body.ToArray());
                var envelope = JsonSerializer.Deserialize<AuctionEventEnvelope>(json);

                if (envelope != null)
                {
                    await BroadcastEnvelopeToClientsAsync(envelope, "RabbitMQ");
                    await MarkOutboxConsumedByHubAsync(envelope.EventId);
                }

                _channel.BasicAck(ea.DeliveryTag, multiple: false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Auction-Hub] Lỗi khi xử lý message từ RabbitMQ.");
                _channel.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
            }
        };

        _channel.BasicConsume(
            queue: _settings.HubQueueName,
            autoAck: false,
            consumer: consumer);

        _logger.LogInformation(
            "[Auction-Hub] Đã kết nối RabbitMQ thành công! Đang lắng nghe Queue '{Queue}' từ Exchange '{Exchange}'",
            _settings.HubQueueName,
            _settings.ExchangeName);
    }

    private async Task PollFallbackOutboxEventsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuctionDbContext>();

        var pendingItems = await db.OutboxEvents
            .Where(e => !e.ConsumedByHub)
            .OrderBy(e => e.CreatedAtUtc)
            .Take(20)
            .ToListAsync(cancellationToken);

        if (pendingItems.Count == 0)
        {
            return;
        }

        foreach (var item in pendingItems)
        {
            var envelope = JsonSerializer.Deserialize<AuctionEventEnvelope>(item.PayloadJson);
            if (envelope != null)
            {
                await BroadcastEnvelopeToClientsAsync(envelope, item.PublishedToBroker ? "RabbitMQ-Sync" : "Outbox-Fallback");
            }
            item.ConsumedByHub = true;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkOutboxConsumedByHubAsync(string eventId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuctionDbContext>();
        var item = await db.OutboxEvents.FirstOrDefaultAsync(e => e.EventId == eventId);
        if (item != null && !item.ConsumedByHub)
        {
            item.ConsumedByHub = true;
            await db.SaveChangesAsync();
        }
    }

    private async Task BroadcastEnvelopeToClientsAsync(AuctionEventEnvelope envelope, string sourceChannel)
    {
        // Idempotent check tránh phát trùng thông báo lên SignalR
        if (!_processedEvents.TryAdd(envelope.EventId, DateTime.UtcNow))
        {
            return;
        }

        if (envelope.EventType == AuctionEventTypes.BidAccepted && envelope.BidAccepted is not null)
        {
            var bid = envelope.BidAccepted;
            _logger.LogInformation(
                "[SignalR-Broadcast] [{Source}] [CorrelationId: {CorrelationId}] Phát mức giá mới {Amount:N0} VNĐ của '{Bidder}' cho phiên '{ProductName}' ({AuctionId})",
                sourceChannel,
                envelope.CorrelationId,
                bid.BidAmount,
                bid.BidderName,
                bid.ProductName,
                bid.AuctionId);

            var payload = new
            {
                eventId = envelope.EventId,
                correlationId = envelope.CorrelationId,
                sourceChannel,
                bid.BidId,
                bid.AuctionId,
                bid.ProductName,
                bid.BidderName,
                bid.BidAmount,
                bid.PreviousPrice,
                bid.MinIncrement,
                minimumNextBid = bid.BidAmount + bid.MinIncrement,
                bid.TotalBids,
                bid.PlacedAtUtc,
                bid.EndTimeUtc
            };

            await _hubContext.Clients.All.SendAsync("BidAccepted", payload);
        }
        else if (envelope.EventType == AuctionEventTypes.AuctionEnded && envelope.AuctionEnded is not null)
        {
            var ended = envelope.AuctionEnded;
            _logger.LogInformation(
                "[SignalR-Broadcast] [{Source}] [CorrelationId: {CorrelationId}] Phát thông báo KẾT THÚC phiên '{ProductName}' ({AuctionId}) | Người thắng: {Winner} ({FinalPrice:N0} VNĐ)",
                sourceChannel,
                envelope.CorrelationId,
                ended.ProductName,
                ended.AuctionId,
                ended.WinnerName ?? "Không có",
                ended.FinalPrice);

            var payload = new
            {
                eventId = envelope.EventId,
                correlationId = envelope.CorrelationId,
                sourceChannel,
                ended.AuctionId,
                ended.ProductName,
                ended.WinnerName,
                ended.FinalPrice,
                ended.StartingPrice,
                ended.TotalBids,
                ended.EndedAtUtc,
                ended.Reason
            };

            await _hubContext.Clients.All.SendAsync("AuctionEnded", payload);
        }
    }

    public override void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        base.Dispose();
    }
}
