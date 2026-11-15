using System.ComponentModel.DataAnnotations;

namespace Shared.Contracts.Models;

public class AuctionItem
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(200)]
    public string ProductName { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string ProductDescription { get; set; } = string.Empty;

    [MaxLength(500)]
    public string ImageUrl { get; set; } = string.Empty;

    public decimal StartingPrice { get; set; }

    public decimal CurrentPrice { get; set; }

    public decimal MinIncrement { get; set; } = 10_000m;

    [MaxLength(100)]
    public string? CurrentWinner { get; set; }

    public int TotalBids { get; set; }

    public DateTime StartTimeUtc { get; set; }

    public DateTime EndTimeUtc { get; set; }

    /// <summary>
    /// Trạng thái phiên: Open, Closed
    /// </summary>
    [MaxLength(30)]
    public string Status { get; set; } = "Open";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? ClosedAtUtc { get; set; }

    public List<BidRecord> Bids { get; set; } = new();
}

public class BidRecord
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AuctionId { get; set; }

    [Required]
    [MaxLength(100)]
    public string BidderName { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public DateTime PlacedAtUtc { get; set; } = DateTime.UtcNow;

    [MaxLength(100)]
    public string CorrelationId { get; set; } = string.Empty;
}

public class AuditLogEntry
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// EventId duy nhất để đảm bảo Idempotent Consumer (không ghi trùng sự kiện khi RabbitMQ gửi lại)
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string EventId { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string EventType { get; set; } = string.Empty;

    public Guid AuctionId { get; set; }

    [MaxLength(200)]
    public string ProductName { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? BidderName { get; set; }

    public decimal? Amount { get; set; }

    [MaxLength(100)]
    public string CorrelationId { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string PayloadJson { get; set; } = string.Empty;

    public DateTime OccurredAtUtc { get; set; }

    public DateTime ProcessedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Bảng Outbox hỗ trợ cơ chế chịu lỗi (Fault Tolerance) khi RabbitMQ tạm thời ngừng hoạt động
/// và đồng bộ sự kiện giữa các service.
/// </summary>
public class OutboxEventMessage
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(100)]
    public string EventId { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string EventType { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string RoutingKey { get; set; } = string.Empty;

    [MaxLength(100)]
    public string CorrelationId { get; set; } = string.Empty;

    public string PayloadJson { get; set; } = string.Empty;

    public bool PublishedToBroker { get; set; }

    public bool ConsumedByHub { get; set; }

    public bool ConsumedByAudit { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
