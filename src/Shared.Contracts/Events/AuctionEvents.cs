namespace Shared.Contracts.Events;

public static class AuctionEventTypes
{
    public const string BidAccepted = "BidAccepted";
    public const string AuctionEnded = "AuctionEnded";
}

public record BidAcceptedEvent(
    Guid BidId,
    Guid AuctionId,
    string ProductName,
    string BidderName,
    decimal BidAmount,
    decimal PreviousPrice,
    decimal MinIncrement,
    int TotalBids,
    DateTime PlacedAtUtc,
    DateTime EndTimeUtc
);

public record AuctionEndedEvent(
    Guid AuctionId,
    string ProductName,
    string? WinnerName,
    decimal FinalPrice,
    decimal StartingPrice,
    int TotalBids,
    DateTime EndedAtUtc,
    string Reason
);

public record AuctionEventEnvelope(
    string EventId,
    string EventType,
    string CorrelationId,
    DateTime OccurredAtUtc,
    BidAcceptedEvent? BidAccepted = null,
    AuctionEndedEvent? AuctionEnded = null
);
