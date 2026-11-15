using Microsoft.EntityFrameworkCore;
using Shared.Contracts.Data;
using Shared.Contracts.Events;

namespace Auction.Api.Services;

/// <summary>
/// Dịch vụ chạy nền tự động đóng các phiên đấu giá đã hết hạn, xác định người chiến thắng,
/// phát sự kiện AuctionEnded qua RabbitMQ và gửi bù các sự kiện Outbox khi RabbitMQ phục hồi.
/// </summary>
public class AuctionExpirationService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RabbitMqEventPublisher _publisher;
    private readonly ILogger<AuctionExpirationService> _logger;

    public AuctionExpirationService(
        IServiceScopeFactory scopeFactory,
        RabbitMqEventPublisher publisher,
        ILogger<AuctionExpirationService> logger)
    {
        _scopeFactory = scopeFactory;
        _publisher = publisher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CloseExpiredAuctionsAsync(stoppingToken);
                await _publisher.FlushPendingOutboxEventsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi trong quá trình kiểm tra phiên đấu giá hết hạn.");
            }

            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
    }

    private async Task CloseExpiredAuctionsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuctionDbContext>();
        var now = DateTime.UtcNow;

        var expiredAuctions = await db.Auctions
            .Where(a => a.Status == "Open" && a.EndTimeUtc <= now)
            .ToListAsync(cancellationToken);

        foreach (var auction in expiredAuctions)
        {
            auction.Status = "Closed";
            auction.ClosedAtUtc = now;
            await db.SaveChangesAsync(cancellationToken);

            var correlationId = $"auto-close-{Guid.NewGuid().ToString("N")[..8]}";
            var endedEvent = new AuctionEndedEvent(
                AuctionId: auction.Id,
                ProductName: auction.ProductName,
                WinnerName: auction.CurrentWinner,
                FinalPrice: auction.CurrentPrice,
                StartingPrice: auction.StartingPrice,
                TotalBids: auction.TotalBids,
                EndedAtUtc: now,
                Reason: "Hết thời gian đấu giá tự động");

            var envelope = new AuctionEventEnvelope(
                EventId: Guid.NewGuid().ToString("N"),
                EventType: AuctionEventTypes.AuctionEnded,
                CorrelationId: correlationId,
                OccurredAtUtc: now,
                BidAccepted: null,
                AuctionEnded: endedEvent);

            _logger.LogInformation(
                "[Auction-AutoClose] [CorrelationId: {CorrelationId}] Phiên đấu giá '{ProductName}' ({AuctionId}) đã kết thúc tự động. Người thắng: {Winner} với mức giá {FinalPrice:N0} VNĐ",
                correlationId,
                auction.ProductName,
                auction.Id,
                auction.CurrentWinner ?? "Không có người trả giá",
                auction.CurrentPrice);

            await _publisher.PublishEventAsync(envelope, "auction.ended");
        }
    }
}
