namespace Shared.Contracts.Dtos;

public record CreateAuctionRequest(
    string ProductName,
    string? ProductDescription,
    string? ImageUrl,
    decimal StartingPrice,
    decimal? MinIncrement,
    DateTime? StartTimeUtc,
    DateTime? EndTimeUtc,
    int? DurationMinutes
);

public record PlaceBidRequest(
    string BidderName,
    decimal Amount
);

public record AuctionDto(
    Guid Id,
    string ProductName,
    string ProductDescription,
    string ImageUrl,
    decimal StartingPrice,
    decimal CurrentPrice,
    decimal MinIncrement,
    string? CurrentWinner,
    int TotalBids,
    DateTime StartTimeUtc,
    DateTime EndTimeUtc,
    string Status,
    bool IsActive,
    double RemainingSeconds,
    DateTime CreatedAtUtc,
    DateTime? ClosedAtUtc
);

public record BidDto(
    Guid Id,
    Guid AuctionId,
    string BidderName,
    decimal Amount,
    DateTime PlacedAtUtc,
    string CorrelationId
);

public record PlaceBidResponseDto(
    bool Success,
    string Message,
    string? ErrorCode,
    decimal CurrentPrice,
    decimal MinimumNextBid,
    string? CurrentWinner,
    string CorrelationId,
    BidDto? Bid
);

public record AuctionResultDto(
    Guid AuctionId,
    string ProductName,
    string Status,
    decimal StartingPrice,
    decimal FinalPrice,
    string? Winner,
    int TotalBids,
    DateTime StartTimeUtc,
    DateTime EndTimeUtc,
    DateTime? ClosedAtUtc,
    string ResultSummary
);
