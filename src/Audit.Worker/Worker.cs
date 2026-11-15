using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Shared.Contracts.Data;
using Shared.Contracts.Events;
using Shared.Contracts.Messaging;
using Shared.Contracts.Models;

namespace Audit.Worker;

public class Worker : BackgroundService
{
    private readonly RabbitMqSettings _settings;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<Worker> _logger;
    private IConnection? _connection;
    private IModel? _channel;

    public Worker(
        IOptions<RabbitMqSettings> settings,
        IServiceScopeFactory scopeFactory,
        ILogger<Worker> logger)
    {
        _settings = settings.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[Audit-Worker] Khởi động dịch vụ lưu lịch sử đấu giá (Idempotent Consumer + Retry & DLQ)...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                EnsureRabbitMqConsumer();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Audit-Worker] Đang chờ kết nối RabbitMQ tại {Host}:{Port}...", _settings.HostName, _settings.Port);
            }

            try
            {
                await ProcessPendingOutboxAuditEventsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Audit-Worker] Lỗi nhẹ khi đọc Outbox dự phòng.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    private void EnsureRabbitMqConsumer()
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

        // 1. Khai báo Dead-Letter Exchange (DLX) & Dead-Letter Queue (DLQ)
        _channel.ExchangeDeclare(
            exchange: _settings.DeadLetterExchangeName,
            type: ExchangeType.Fanout,
            durable: true,
            autoDelete: false);

        _channel.QueueDeclare(
            queue: _settings.AuditDeadLetterQueueName,
            durable: true,
            exclusive: false,
            autoDelete: false);

        _channel.QueueBind(
            queue: _settings.AuditDeadLetterQueueName,
            exchange: _settings.DeadLetterExchangeName,
            routingKey: string.Empty);

        // 2. Khai báo Exchange chính & Audit Queue có gắn Dead-Letter Exchange
        _channel.ExchangeDeclare(
            exchange: _settings.ExchangeName,
            type: ExchangeType.Fanout,
            durable: true,
            autoDelete: false);

        var queueArgs = new Dictionary<string, object>
        {
            ["x-dead-letter-exchange"] = _settings.DeadLetterExchangeName
        };

        _channel.QueueDeclare(
            queue: _settings.AuditQueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: queueArgs);

        _channel.QueueBind(
            queue: _settings.AuditQueueName,
            exchange: _settings.ExchangeName,
            routingKey: string.Empty);

        _channel.BasicQos(prefetchSize: 0, prefetchCount: 10, global: false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += async (_, ea) =>
        {
            var rawJson = Encoding.UTF8.GetString(ea.Body.ToArray());
            var retrySuccess = false;

            // Cơ chế Retry tối đa MaxRetryCount lần trước khi đẩy vào Dead-Letter Queue (DLQ)
            for (var attempt = 1; attempt <= _settings.MaxRetryCount; attempt++)
            {
                try
                {
                    var envelope = JsonSerializer.Deserialize<AuctionEventEnvelope>(rawJson);
                    if (envelope != null)
                    {
                        await SaveAuditLogIdempotentAsync(envelope, rawJson, "RabbitMQ");
                        await MarkOutboxConsumedByAuditAsync(envelope.EventId);
                    }

                    retrySuccess = true;
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "[Audit-Worker-Retry] Lần thử {Attempt}/{MaxRetry} thất bại khi xử lý message DeliveryTag={Tag}",
                        attempt,
                        _settings.MaxRetryCount,
                        ea.DeliveryTag);

                    await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt));
                }
            }

            if (retrySuccess)
            {
                _channel.BasicAck(ea.DeliveryTag, multiple: false);
            }
            else
            {
                _logger.LogError(
                    "[Audit-Worker-DLQ] Message vượt quá {MaxRetry} lần thử -> Chuyển sang Dead-Letter Queue '{Dlq}'!",
                    _settings.MaxRetryCount,
                    _settings.AuditDeadLetterQueueName);

                // requeue: false sẽ kích hoạt x-dead-letter-exchange đẩy sang auction.audit.dlq
                _channel.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
            }
        };

        _channel.BasicConsume(
            queue: _settings.AuditQueueName,
            autoAck: false,
            consumer: consumer);

        _logger.LogInformation(
            "[Audit-Worker] Đã kết nối RabbitMQ! Đang lắng nghe Queue '{Queue}' (DLQ: '{Dlq}')",
            _settings.AuditQueueName,
            _settings.AuditDeadLetterQueueName);
    }

    private async Task ProcessPendingOutboxAuditEventsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuctionDbContext>();

        var pending = await db.OutboxEvents
            .Where(e => !e.ConsumedByAudit)
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
            if (envelope != null)
            {
                await SaveAuditLogIdempotentAsync(
                    envelope,
                    item.PayloadJson,
                    item.PublishedToBroker ? "RabbitMQ-Sync" : "Outbox-Fallback");
            }

            item.ConsumedByAudit = true;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkOutboxConsumedByAuditAsync(string eventId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuctionDbContext>();
        var item = await db.OutboxEvents.FirstOrDefaultAsync(e => e.EventId == eventId);
        if (item != null && !item.ConsumedByAudit)
        {
            item.ConsumedByAudit = true;
            await db.SaveChangesAsync();
        }
    }

    private async Task SaveAuditLogIdempotentAsync(AuctionEventEnvelope envelope, string rawJson, string sourceChannel)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuctionDbContext>();

        // Kiểm tra Idempotent Consumer theo EventId
        var alreadyProcessed = await db.AuditLogs.AnyAsync(a => a.EventId == envelope.EventId);
        if (alreadyProcessed)
        {
            _logger.LogInformation(
                "[Audit-Worker-Idempotent] [CorrelationId: {CorrelationId}] Bỏ qua sự kiện trùng lặp EventId={EventId}",
                envelope.CorrelationId,
                envelope.EventId);
            return;
        }

        AuditLogEntry entry;
        if (envelope.EventType == AuctionEventTypes.BidAccepted && envelope.BidAccepted is not null)
        {
            var b = envelope.BidAccepted;
            entry = new AuditLogEntry
            {
                Id = Guid.NewGuid(),
                EventId = envelope.EventId,
                EventType = envelope.EventType,
                AuctionId = b.AuctionId,
                ProductName = b.ProductName,
                BidderName = b.BidderName,
                Amount = b.BidAmount,
                CorrelationId = envelope.CorrelationId,
                Summary = $"[{sourceChannel}] Người dùng '{b.BidderName}' trả giá {b.BidAmount:N0} VNĐ (trước đó: {b.PreviousPrice:N0} VNĐ) cho '{b.ProductName}'.",
                PayloadJson = rawJson,
                OccurredAtUtc = envelope.OccurredAtUtc,
                ProcessedAtUtc = DateTime.UtcNow
            };
        }
        else if (envelope.EventType == AuctionEventTypes.AuctionEnded && envelope.AuctionEnded is not null)
        {
            var e = envelope.AuctionEnded;
            var winnerInfo = string.IsNullOrWhiteSpace(e.WinnerName)
                ? "Không có người trả giá"
                : $"Người thắng: '{e.WinnerName}' với mức giá {e.FinalPrice:N0} VNĐ";

            entry = new AuditLogEntry
            {
                Id = Guid.NewGuid(),
                EventId = envelope.EventId,
                EventType = envelope.EventType,
                AuctionId = e.AuctionId,
                ProductName = e.ProductName,
                BidderName = e.WinnerName,
                Amount = e.FinalPrice,
                CorrelationId = envelope.CorrelationId,
                Summary = $"[{sourceChannel}] Kết thúc phiên '{e.ProductName}'. {winnerInfo} ({e.Reason}).",
                PayloadJson = rawJson,
                OccurredAtUtc = envelope.OccurredAtUtc,
                ProcessedAtUtc = DateTime.UtcNow
            };
        }
        else
        {
            return;
        }

        db.AuditLogs.Add(entry);
        await db.SaveChangesAsync();

        _logger.LogInformation(
            "[Audit-Worker-Saved] [CorrelationId: {CorrelationId}] Đã ghi Audit Log sự kiện '{EventType}' (EventId: {EventId}) -> {Summary}",
            envelope.CorrelationId,
            envelope.EventType,
            envelope.EventId,
            entry.Summary);
    }

    public override void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        base.Dispose();
    }
}
