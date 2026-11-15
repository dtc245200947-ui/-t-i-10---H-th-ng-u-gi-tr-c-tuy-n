using System.Globalization;
using BidValidation.Grpc.Protos;
using Grpc.Core;

namespace BidValidation.Grpc.Services;

public class BidValidatorService : BidValidator.BidValidatorBase
{
    private readonly ILogger<BidValidatorService> _logger;

    public BidValidatorService(ILogger<BidValidatorService> logger)
    {
        _logger = logger;
    }

    public override Task<ValidateBidResponse> ValidateBid(ValidateBidRequest request, ServerCallContext context)
    {
        var correlationId = string.IsNullOrWhiteSpace(request.CorrelationId)
            ? Guid.NewGuid().ToString("N")[..12]
            : request.CorrelationId;

        var nowUtc = DateTime.UtcNow;

        _logger.LogInformation(
            "[gRPC-Validate] [CorrelationId: {CorrelationId}] Nhận yêu cầu kiểm tra giá cho phiên {AuctionId} ({ProductName}) | Người trả: {Bidder} | Giá đặt: {BidAmount:N0} VNĐ | Giá hiện tại: {CurrentPrice:N0} VNĐ",
            correlationId,
            request.AuctionId,
            request.ProductName,
            request.BidderName,
            request.BidAmount,
            request.CurrentPrice);

        // 1. Kiểm tra thông tin người trả giá
        if (string.IsNullOrWhiteSpace(request.BidderName) || request.BidderName.Trim().Length < 2)
        {
            return Task.FromResult(Reject(
                correlationId,
                "INVALID_BIDDER",
                "Tên người tham gia đấu giá phải có ít nhất 2 ký tự.",
                request.CurrentPrice + request.MinIncrement,
                nowUtc));
        }

        // 2. Kiểm tra trạng thái phiên đấu giá
        if (!string.Equals(request.AuctionStatus, "Open", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(Reject(
                correlationId,
                "AUCTION_CLOSED",
                "Phiên đấu giá đã kết thúc hoặc không còn mở để nhận lượt trả giá.",
                request.CurrentPrice,
                nowUtc));
        }

        // 3. Kiểm tra thời gian bắt đầu / kết thúc
        if (DateTime.TryParse(request.StartTimeUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var startTimeUtc))
        {
            if (nowUtc < startTimeUtc)
            {
                return Task.FromResult(Reject(
                    correlationId,
                    "AUCTION_NOT_STARTED",
                    $"Phiên đấu giá chưa bắt đầu (Mở lúc {startTimeUtc:dd/MM/yyyy HH:mm:ss} UTC).",
                    request.StartingPrice,
                    nowUtc));
            }
        }

        if (DateTime.TryParse(request.EndTimeUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var endTimeUtc))
        {
            if (nowUtc >= endTimeUtc)
            {
                return Task.FromResult(Reject(
                    correlationId,
                    "AUCTION_EXPIRED",
                    "Thời gian của phiên đấu giá đã kết thúc.",
                    request.CurrentPrice,
                    nowUtc));
            }
        }

        // 4. Tính toán mức giá hợp lệ tối thiểu
        var hasPreviousBid = !string.IsNullOrWhiteSpace(request.CurrentWinner);
        var minIncrement = request.MinIncrement > 0 ? request.MinIncrement : 10_000;
        var minimumRequiredBid = hasPreviousBid
            ? request.CurrentPrice + minIncrement
            : Math.Max(request.StartingPrice, request.CurrentPrice);

        if (request.BidAmount < minimumRequiredBid)
        {
            return Task.FromResult(Reject(
                correlationId,
                "BID_TOO_LOW",
                $"Mức giá đặt ({request.BidAmount:N0} VNĐ) thấp hơn mức tối thiểu hợp lệ ({minimumRequiredBid:N0} VNĐ). Bước giá tối thiểu là {minIncrement:N0} VNĐ.",
                minimumRequiredBid,
                nowUtc));
        }

        // 5. Hợp lệ
        _logger.LogInformation(
            "[gRPC-Validate] [CorrelationId: {CorrelationId}] HỢP LỆ -> Chấp nhận lượt trả giá {BidAmount:N0} VNĐ của '{Bidder}' cho phiên {AuctionId}",
            correlationId,
            request.BidAmount,
            request.BidderName,
            request.AuctionId);

        return Task.FromResult(new ValidateBidResponse
        {
            IsValid = true,
            ErrorCode = "OK",
            Message = "Lượt trả giá hợp lệ.",
            MinimumRequiredBid = request.BidAmount + minIncrement,
            ValidatedAtUtc = nowUtc.ToString("O"),
            CorrelationId = correlationId
        });
    }

    public override Task<HealthCheckResponse> CheckHealth(HealthCheckRequest request, ServerCallContext context)
    {
        return Task.FromResult(new HealthCheckResponse
        {
            Healthy = true,
            ServiceName = "BidValidation.Grpc",
            TimestampUtc = DateTime.UtcNow.ToString("O")
        });
    }

    private ValidateBidResponse Reject(
        string correlationId,
        string errorCode,
        string message,
        double minimumRequiredBid,
        DateTime nowUtc)
    {
        _logger.LogWarning(
            "[gRPC-Validate] [CorrelationId: {CorrelationId}] TỪ CHỐI [{ErrorCode}] -> {Message}",
            correlationId,
            errorCode,
            message);

        return new ValidateBidResponse
        {
            IsValid = false,
            ErrorCode = errorCode,
            Message = message,
            MinimumRequiredBid = minimumRequiredBid,
            ValidatedAtUtc = nowUtc.ToString("O"),
            CorrelationId = correlationId
        };
    }
}
