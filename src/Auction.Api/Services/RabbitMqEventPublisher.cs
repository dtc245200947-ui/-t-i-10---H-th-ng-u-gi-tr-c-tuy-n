using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Shared.Contracts.Data;
using Shared.Contracts.Events;
using Shared.Contracts.Messaging;
using Shared.Contracts.Models;

namespace Auction.Api.Services;

public class RabbitMqEventPublisher : IDisposable
{
    private readonly RabbitMqSettings _settings;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RabbitMqEventPublisher> _logger;
    private readonly object _lock = new();
    private IConnection? _connection;
    private IModel? _channel;

    public RabbitMqEventPublisher(
        IOptions<RabbitMqSettings> settings,
        IServiceScopeFactory scopeFactory,
        ILogger<RabbitMqEventPublisher> logger)
    {
        _settings = settings.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task PublishEventAsync(AuctionEventEnvelope envelope, string routingKey)
    {
        var payloadJson = JsonSerializer.Serialize(envelope);
        var publishedToRabbit = TryPublishToRabbitMq(envelope, routingKey, payloadJson);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuctionDbContext>();

        var outboxMessage = new OutboxEventMessage
        {
            Id = Guid.NewGuid(),
            EventId = envelope.EventId,
            EventType = envelope.EventType,
            RoutingKey = routingKey,
            CorrelationId = envelope.CorrelationId,
            PayloadJson = payloadJson,
            PublishedToBroker = publishedToRabbit,
            ConsumedByHub = false,
            ConsumedByAudit = false,
            CreatedAtUtc = DateTime.UtcNow
        };

        db.OutboxEvents.Add(outboxMessage);
        await db.SaveChangesAsync();

        if (!publishedToRabbit)
        {
            _logger.LogWarning(
                "[RabbitMQ-Fallback] [CorrelationId: {CorrelationId}] RabbitMQ chưa sẵn sàng -> Sự kiện {EventType} ({EventId}) đã được lưu an toàn vào Outbox để đồng bộ tự động.",
                envelope.CorrelationId,
                envelope.EventType,
                envelope.EventId);
        }
    }

    public async Task FlushPendingOutboxEventsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuctionDbContext>();

        var pending = await db.OutboxEvents
            .Where(e => !e.PublishedToBroker)
            .OrderBy(e => e.CreatedAtUtc)
            .Take(20)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
        {
            return;
        }

        foreach (var item in pending)
        {
            var envelope = JsonSerializer.Deserialize<AuctionEventEnvelope>(item.PayloadJson);
            if (envelope != null && TryPublishToRabbitMq(envelope, item.RoutingKey, item.PayloadJson))
            {
                item.PublishedToBroker = true;
                _logger.LogInformation(
                    "[RabbitMQ-Recovery] [CorrelationId: {CorrelationId}] Đã phát bù thành công sự kiện {EventType} ({EventId}) từ Outbox lên RabbitMQ.",
                    item.CorrelationId,
                    item.EventType,
                    item.EventId);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private bool TryPublishToRabbitMq(AuctionEventEnvelope envelope, string routingKey, string payloadJson)
    {
        try
        {
            lock (_lock)
            {
                EnsureChannelConnected();
                if (_channel == null || !_channel.IsOpen)
                {
                    return false;
                }

                var properties = _channel.CreateBasicProperties();
                properties.Persistent = true;
                properties.MessageId = envelope.EventId;
                properties.CorrelationId = envelope.CorrelationId;
                properties.Type = envelope.EventType;
                properties.ContentType = "application/json";

                var body = Encoding.UTF8.GetBytes(payloadJson);

                _channel.BasicPublish(
                    exchange: _settings.ExchangeName,
                    routingKey: routingKey,
                    basicProperties: properties,
                    body: body);

                _logger.LogInformation(
                    "[RabbitMQ-Publish] [CorrelationId: {CorrelationId}] Đã phát sự kiện '{EventType}' (EventId: {EventId}) lên Exchange '{Exchange}' với RoutingKey '{RoutingKey}'",
                    envelope.CorrelationId,
                    envelope.EventType,
                    envelope.EventId,
                    _settings.ExchangeName,
                    routingKey);

                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Không thể kết nối trực tiếp tới RabbitMQ tại {Host}:{Port}", _settings.HostName, _settings.Port);
            return false;
        }
    }

    private void EnsureChannelConnected()
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
            NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
            RequestedConnectionTimeout = TimeSpan.FromSeconds(2)
        };

        _connection = factory.CreateConnection();
        _channel = _connection.CreateModel();

        // Khai báo Fanout Exchange phân phối sự kiện đến cả Auction Hub và Audit Worker
        _channel.ExchangeDeclare(
            exchange: _settings.ExchangeName,
            type: ExchangeType.Fanout,
            durable: true,
            autoDelete: false);
    }

    public void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
    }
}
