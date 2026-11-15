using Auction.Api.Services;
using BidValidation.Grpc.Protos;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Shared.Contracts.Data;
using Shared.Contracts.Dtos;
using Shared.Contracts.Events;
using Shared.Contracts.Messaging;
using Shared.Contracts.Models;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);

var httpPort = builder.Configuration.GetValue<int>("Server:Port", 5050);
builder.WebHost.UseUrls($"http://0.0.0.0:{httpPort}");

// Cấu hình Database (PostgreSQL trong Docker hoặc SQLite dùng chung khi chạy local)
builder.Services.AddAuctionDatabase(builder.Configuration);

// Cấu hình RabbitMQ
builder.Services.Configure<RabbitMqSettings>(builder.Configuration.GetSection("RabbitMQ"));
builder.Services.AddSingleton<RabbitMqEventPublisher>();
builder.Services.AddHostedService<AuctionExpirationService>();

// Cấu hình gRPC Client gọi sang BidValidation.Grpc
var grpcAddress = builder.Configuration["GrpcServices:BidValidationUrl"] ?? "http://localhost:5051";
builder.Services.AddGrpcClient<BidValidator.BidValidatorClient>(options =>
{
    options.Address = new Uri(grpcAddress);
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseCors();

// Khởi tạo Database & Seed dữ liệu mẫu
await app.Services.EnsureAuctionDatabaseSeededAsync();

// Middleware gắn Correlation ID cho mọi request để truy vết phân tán
app.Use(async (context, next) =>
{
    if (!context.Request.Headers.TryGetValue(CorrelationConstants.HeaderName, out var correlationId) ||
        string.IsNullOrWhiteSpace(correlationId))
    {
        correlationId = $"corr-{Guid.NewGuid().ToString("N")[..10]}";
        context.Request.Headers[CorrelationConstants.HeaderName] = correlationId;
    }

    context.Response.Headers[CorrelationConstants.HeaderName] = correlationId!;
    await next();
});

app.MapHealthChecks("/health");

// ============================================================================
// 0. GET / - Kiểm tra trạng thái hoạt động của dịch vụ (Health Check JSON)
// ============================================================================
app.MapGet("/", () => Results.Ok(new
{
    service = "Auction.Api",
    status = "running"
}));

// ============================================================================
// 1. GET /auctions - Tra cứu danh sách các phiên đấu giá
// ============================================================================
app.MapGet("/auctions", async (string? status, AuctionDbContext db) =>
{
    var query = db.Auctions.AsNoTracking().AsQueryable();

    if (!string.IsNullOrWhiteSpace(status))
    {
        query = query.Where(a => a.Status.ToLower() == status.ToLower());
    }

    var items = await query
        .OrderByDescending(a => a.Status == "Open")
        .ThenBy(a => a.EndTimeUtc)
        .ToListAsync();

    var now = DateTime.UtcNow;
    var dtos = items.Select(a => ToAuctionDto(a, now)).ToList();
    return Results.Ok(dtos);
});

// ============================================================================
// 2. GET /auctions/{id} - Tra cứu chi tiết một phiên đấu giá
// ============================================================================
app.MapGet("/auctions/{id:guid}", async (Guid id, AuctionDbContext db) =>
{
    var auction = await db.Auctions
        .AsNoTracking()
        .FirstOrDefaultAsync(a => a.Id == id);

    if (auction is null)
    {
        return Results.NotFound(new { message = $"Không tìm thấy phiên đấu giá với ID '{id}'." });
    }

    return Results.Ok(ToAuctionDto(auction, DateTime.UtcNow));
});

// ============================================================================
// 3. POST /auctions - Tạo phiên đấu giá mới
// ============================================================================
app.MapPost("/auctions", async (
    CreateAuctionRequest request,
    HttpContext httpContext,
    AuctionDbContext db,
    ILogger<Program> logger) =>
{
    var correlationId = httpContext.Request.Headers[CorrelationConstants.HeaderName].ToString();

    if (string.IsNullOrWhiteSpace(request.ProductName))
    {
        return Results.BadRequest(new { message = "Tên sản phẩm đấu giá không được để trống." });
    }

    if (request.StartingPrice <= 0)
    {
        return Results.BadRequest(new { message = "Giá khởi điểm phải lớn hơn 0." });
    }

    var now = DateTime.UtcNow;
    var startTime = request.StartTimeUtc?.ToUniversalTime() ?? now;
    var durationMinutes = request.DurationMinutes.GetValueOrDefault(60);
    var endTime = request.EndTimeUtc?.ToUniversalTime() ?? startTime.AddMinutes(durationMinutes > 0 ? durationMinutes : 60);

    if (endTime <= startTime)
    {
        return Results.BadRequest(new { message = "Thời gian kết thúc phải lớn hơn thời gian bắt đầu." });
    }

    var auction = new AuctionItem
    {
        Id = Guid.NewGuid(),
        ProductName = request.ProductName.Trim(),
        ProductDescription = request.ProductDescription?.Trim() ?? "Sản phẩm đấu giá trực tuyến chính hãng.",
        ImageUrl = string.IsNullOrWhiteSpace(request.ImageUrl)
            ? "https://images.unsplash.com/photo-1526170375885-4d8ecf77b99f?w=800&auto=format&fit=crop&q=80"
            : request.ImageUrl.Trim(),
        StartingPrice = request.StartingPrice,
        CurrentPrice = request.StartingPrice,
        MinIncrement = request.MinIncrement is > 0 ? request.MinIncrement.Value : Math.Max(10_000m, Math.Round(request.StartingPrice * 0.02m, 0)),
        CurrentWinner = null,
        TotalBids = 0,
        StartTimeUtc = startTime,
        EndTimeUtc = endTime,
        Status = "Open",
        CreatedAtUtc = now
    };

    db.Auctions.Add(auction);
    await db.SaveChangesAsync();

    logger.LogInformation(
        "[Auction-API] [CorrelationId: {CorrelationId}] Đã tạo phiên đấu giá mới '{ProductName}' ({AuctionId}) | Giá khởi điểm: {StartingPrice:N0} VNĐ | Bước giá: {MinIncrement:N0} VNĐ",
        correlationId,
        auction.ProductName,
        auction.Id,
        auction.StartingPrice,
        auction.MinIncrement);

    return Results.Created($"/auctions/{auction.Id}", ToAuctionDto(auction, now));
});

// ============================================================================
// 4. POST /auctions/{id}/bids - Gửi mức giá mới -> Gọi gRPC kiểm tra -> Lưu DB -> Phát RabbitMQ
// ============================================================================
app.MapPost("/auctions/{id:guid}/bids", async (
    Guid id,
    PlaceBidRequest request,
    HttpContext httpContext,
    AuctionDbContext db,
    BidValidator.BidValidatorClient grpcValidator,
    RabbitMqEventPublisher eventPublisher,
    IConfiguration config,
    ILogger<Program> logger) =>
{
    var correlationId = httpContext.Request.Headers[CorrelationConstants.HeaderName].ToString();

    var auction = await db.Auctions.FirstOrDefaultAsync(a => a.Id == id);
    if (auction is null)
    {
        return Results.NotFound(new { message = $"Không tìm thấy phiên đấu giá '{id}'." });
    }

    // Tự động đóng phiên nếu đã quá hạn trước khi kiểm tra
    var now = DateTime.UtcNow;
    if (auction.Status == "Open" && auction.EndTimeUtc <= now)
    {
        auction.Status = "Closed";
        auction.ClosedAtUtc = now;
        await db.SaveChangesAsync();
    }

    logger.LogInformation(
        "[Auction-API] [CorrelationId: {CorrelationId}] Nhận yêu cầu trả giá từ '{Bidder}' với mức {Amount:N0} VNĐ cho phiên '{ProductName}' ({AuctionId}). Đang gọi gRPC BidValidation Service...",
        correlationId,
        request.BidderName,
        request.Amount,
        auction.ProductName,
        auction.Id);

    ValidateBidResponse validationResult;
    try
    {
        validationResult = await grpcValidator.ValidateBidAsync(new ValidateBidRequest
        {
            AuctionId = auction.Id.ToString(),
            ProductName = auction.ProductName,
            AuctionStatus = auction.Status,
            StartTimeUtc = auction.StartTimeUtc.ToString("O"),
            EndTimeUtc = auction.EndTimeUtc.ToString("O"),
            StartingPrice = (double)auction.StartingPrice,
            CurrentPrice = (double)auction.CurrentPrice,
            MinIncrement = (double)auction.MinIncrement,
            CurrentWinner = auction.CurrentWinner ?? string.Empty,
            BidderName = request.BidderName ?? string.Empty,
            BidAmount = (double)request.Amount,
            CorrelationId = correlationId
        }, deadline: DateTime.UtcNow.AddSeconds(5));
    }
    catch (RpcException rpcEx)
    {
        var allowFallback = config.GetValue<bool>("Resilience:EnableLocalFallbackValidation", true);
        if (!allowFallback)
        {
            logger.LogError(rpcEx,
                "[Auction-API] [CorrelationId: {CorrelationId}] Không thể kết nối tới gRPC BidValidation Service!",
                correlationId);
            return Results.Problem(
                detail: "Dịch vụ kiểm tra giá (Bid Validation gRPC Service) tạm thời không phản hồi.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        logger.LogWarning(
            "[Auction-API-Fallback] [CorrelationId: {CorrelationId}] gRPC BidValidation Service tạm dừng ({StatusCode}) -> Kích hoạt cơ chế kiểm tra dự phòng tại chỗ (Graceful Degradation)!",
            correlationId,
            rpcEx.StatusCode);

        validationResult = PerformFallbackValidation(auction, request, correlationId, now);
    }

    if (!validationResult.IsValid)
    {
        logger.LogWarning(
            "[Auction-API] [CorrelationId: {CorrelationId}] Lượt trả giá bị từ chối ({ErrorCode}): {Message}",
            correlationId,
            validationResult.ErrorCode,
            validationResult.Message);

        return Results.BadRequest(new PlaceBidResponseDto(
            Success: false,
            Message: validationResult.Message,
            ErrorCode: validationResult.ErrorCode,
            CurrentPrice: auction.CurrentPrice,
            MinimumNextBid: (decimal)validationResult.MinimumRequiredBid,
            CurrentWinner: auction.CurrentWinner,
            CorrelationId: correlationId,
            Bid: null));
    }

    // Lưu mức giá hợp lệ vào Database
    var previousPrice = auction.CurrentPrice;
    var bidRecord = new BidRecord
    {
        Id = Guid.NewGuid(),
        AuctionId = auction.Id,
        BidderName = (request.BidderName ?? string.Empty).Trim(),
        Amount = request.Amount,
        PlacedAtUtc = now,
        CorrelationId = correlationId
    };

    auction.CurrentPrice = request.Amount;
    auction.CurrentWinner = bidRecord.BidderName;
    auction.TotalBids += 1;

    db.Bids.Add(bidRecord);
    await db.SaveChangesAsync();

    // Phát sự kiện BidAccepted qua RabbitMQ
    var bidAcceptedEvent = new BidAcceptedEvent(
        BidId: bidRecord.Id,
        AuctionId: auction.Id,
        ProductName: auction.ProductName,
        BidderName: bidRecord.BidderName,
        BidAmount: bidRecord.Amount,
        PreviousPrice: previousPrice,
        MinIncrement: auction.MinIncrement,
        TotalBids: auction.TotalBids,
        PlacedAtUtc: bidRecord.PlacedAtUtc,
        EndTimeUtc: auction.EndTimeUtc);

    var envelope = new AuctionEventEnvelope(
        EventId: Guid.NewGuid().ToString("N"),
        EventType: AuctionEventTypes.BidAccepted,
        CorrelationId: correlationId,
        OccurredAtUtc: now,
        BidAccepted: bidAcceptedEvent,
        AuctionEnded: null);

    await eventPublisher.PublishEventAsync(envelope, "auction.bid.accepted");

    var bidDto = new BidDto(
        bidRecord.Id,
        bidRecord.AuctionId,
        bidRecord.BidderName,
        bidRecord.Amount,
        bidRecord.PlacedAtUtc,
        bidRecord.CorrelationId);

    return Results.Ok(new PlaceBidResponseDto(
        Success: true,
        Message: "Đặt giá thành công! Mức giá mới đã được phát realtime tới toàn bộ người theo dõi.",
        ErrorCode: null,
        CurrentPrice: auction.CurrentPrice,
        MinimumNextBid: auction.CurrentPrice + auction.MinIncrement,
        CurrentWinner: auction.CurrentWinner,
        CorrelationId: correlationId,
        Bid: bidDto));
});

// ============================================================================
// 5. GET /auctions/{id}/bids - Tra cứu lịch sử trả giá của phiên đấu giá
// ============================================================================
app.MapGet("/auctions/{id:guid}/bids", async (Guid id, AuctionDbContext db) =>
{
    var exists = await db.Auctions.AsNoTracking().AnyAsync(a => a.Id == id);
    if (!exists)
    {
        return Results.NotFound(new { message = $"Không tìm thấy phiên đấu giá '{id}'." });
    }

    var records = await db.Bids
        .AsNoTracking()
        .Where(b => b.AuctionId == id)
        .OrderByDescending(b => b.PlacedAtUtc)
        .ToListAsync();

    var bids = records
        .OrderByDescending(b => b.PlacedAtUtc)
        .ThenByDescending(b => b.Amount)
        .Select(b => new BidDto(b.Id, b.AuctionId, b.BidderName, b.Amount, b.PlacedAtUtc, b.CorrelationId))
        .ToList();

    return Results.Ok(bids);
});

// ============================================================================
// 6. POST /auctions/{id}/end - Kết thúc phiên đấu giá & xác định người chiến thắng
// ============================================================================
app.MapPost("/auctions/{id:guid}/end", async (
    Guid id,
    HttpContext httpContext,
    AuctionDbContext db,
    RabbitMqEventPublisher eventPublisher,
    ILogger<Program> logger) =>
{
    var correlationId = httpContext.Request.Headers[CorrelationConstants.HeaderName].ToString();
    var auction = await db.Auctions.FirstOrDefaultAsync(a => a.Id == id);

    if (auction is null)
    {
        return Results.NotFound(new { message = $"Không tìm thấy phiên đấu giá '{id}'." });
    }

    var now = DateTime.UtcNow;
    if (auction.Status != "Closed")
    {
        auction.Status = "Closed";
        auction.ClosedAtUtc = now;
        await db.SaveChangesAsync();

        var endedEvent = new AuctionEndedEvent(
            AuctionId: auction.Id,
            ProductName: auction.ProductName,
            WinnerName: auction.CurrentWinner,
            FinalPrice: auction.CurrentPrice,
            StartingPrice: auction.StartingPrice,
            TotalBids: auction.TotalBids,
            EndedAtUtc: now,
            Reason: "Kết thúc phiên đấu giá theo yêu cầu quản trị / người tổ chức");

        var envelope = new AuctionEventEnvelope(
            EventId: Guid.NewGuid().ToString("N"),
            EventType: AuctionEventTypes.AuctionEnded,
            CorrelationId: correlationId,
            OccurredAtUtc: now,
            BidAccepted: null,
            AuctionEnded: endedEvent);

        await eventPublisher.PublishEventAsync(envelope, "auction.ended");

        logger.LogInformation(
            "[Auction-API] [CorrelationId: {CorrelationId}] Đã kết thúc phiên đấu giá '{ProductName}' ({AuctionId}). Người thắng: {Winner} ({FinalPrice:N0} VNĐ)",
            correlationId,
            auction.ProductName,
            auction.Id,
            auction.CurrentWinner ?? "Không có người trả giá",
            auction.CurrentPrice);
    }

    var summary = string.IsNullOrWhiteSpace(auction.CurrentWinner)
        ? "Phiên đấu giá đã kết thúc mà không có người trả giá nào."
        : $"Chúc mừng '{auction.CurrentWinner}' đã chiến thắng phiên đấu giá với mức giá {auction.CurrentPrice:N0} VNĐ!";

    return Results.Ok(new AuctionResultDto(
        auction.Id,
        auction.ProductName,
        auction.Status,
        auction.StartingPrice,
        auction.CurrentPrice,
        auction.CurrentWinner,
        auction.TotalBids,
        auction.StartTimeUtc,
        auction.EndTimeUtc,
        auction.ClosedAtUtc,
        summary));
});

// ============================================================================
// 7. GET /auctions/{id}/result - Tra cứu kết quả phiên đấu giá & người thắng
// ============================================================================
app.MapGet("/auctions/{id:guid}/result", async (Guid id, AuctionDbContext db) =>
{
    var auction = await db.Auctions.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id);
    if (auction is null)
    {
        return Results.NotFound(new { message = $"Không tìm thấy phiên đấu giá '{id}'." });
    }

    var summary = auction.Status == "Open"
        ? $"Phiên đấu giá vẫn đang mở. Người dẫn đầu tạm thời: {auction.CurrentWinner ?? "Chưa có"} ({auction.CurrentPrice:N0} VNĐ)."
        : string.IsNullOrWhiteSpace(auction.CurrentWinner)
            ? "Phiên đấu giá đã kết thúc mà không có người trả giá."
            : $"Người chiến thắng chung cuộc: '{auction.CurrentWinner}' với mức giá {auction.CurrentPrice:N0} VNĐ ({auction.TotalBids} lượt trả giá).";

    return Results.Ok(new AuctionResultDto(
        auction.Id,
        auction.ProductName,
        auction.Status,
        auction.StartingPrice,
        auction.CurrentPrice,
        auction.CurrentWinner,
        auction.TotalBids,
        auction.StartTimeUtc,
        auction.EndTimeUtc,
        auction.ClosedAtUtc,
        summary));
});

// ============================================================================
// 8. GET /audit-logs - Tra cứu lịch sử Audit Log do Audit Worker ghi nhận
// ============================================================================
app.MapGet("/audit-logs", async (Guid? auctionId, AuctionDbContext db) =>
{
    var query = db.AuditLogs.AsNoTracking().AsQueryable();
    if (auctionId.HasValue)
    {
        query = query.Where(a => a.AuctionId == auctionId.Value);
    }

    var logs = await query
        .OrderByDescending(a => a.ProcessedAtUtc)
        .Take(50)
        .ToListAsync();

    return Results.Ok(logs);
});

// ============================================================================
// 9. POST /auctions/{id}/extend - Nhân viên / Admin gia hạn hoặc mở lại phiên
// ============================================================================
app.MapPost("/auctions/{id:guid}/extend", async (Guid id, ExtendAuctionRequest request, AuctionDbContext db) =>
{
    var auction = await db.Auctions.FirstOrDefaultAsync(a => a.Id == id);
    if (auction is null)
    {
        return Results.NotFound(new { message = $"Không tìm thấy phiên đấu giá '{id}'." });
    }

    var now = DateTime.UtcNow;
    var addMinutes = request.AdditionalMinutes > 0 ? request.AdditionalMinutes : 15;
    var baseTime = (auction.Status == "Open" && auction.EndTimeUtc > now) ? auction.EndTimeUtc : now;

    auction.EndTimeUtc = baseTime.AddMinutes(addMinutes);
    auction.Status = "Open";
    auction.ClosedAtUtc = null;

    await db.SaveChangesAsync();
    return Results.Ok(ToAuctionDto(auction, now));
});

// ============================================================================
// 10. PUT /auctions/{id} - Nhân viên / Admin chỉnh sửa thông tin phiên đấu giá
// ============================================================================
app.MapPut("/auctions/{id:guid}", async (Guid id, UpdateAuctionRequest request, AuctionDbContext db) =>
{
    var auction = await db.Auctions.FirstOrDefaultAsync(a => a.Id == id);
    if (auction is null)
    {
        return Results.NotFound(new { message = $"Không tìm thấy phiên đấu giá '{id}'." });
    }

    if (!string.IsNullOrWhiteSpace(request.ProductName))
        auction.ProductName = request.ProductName.Trim();

    if (!string.IsNullOrWhiteSpace(request.ProductDescription))
        auction.ProductDescription = request.ProductDescription.Trim();

    if (!string.IsNullOrWhiteSpace(request.ImageUrl))
        auction.ImageUrl = request.ImageUrl.Trim();

    if (request.MinIncrement is > 0)
        auction.MinIncrement = request.MinIncrement.Value;

    await db.SaveChangesAsync();
    return Results.Ok(ToAuctionDto(auction, DateTime.UtcNow));
});

// ============================================================================
// 11. DELETE /auctions/{id} - Admin xóa phiên đấu giá khỏi sàn
// ============================================================================
app.MapDelete("/auctions/{id:guid}", async (Guid id, AuctionDbContext db) =>
{
    var auction = await db.Auctions.Include(a => a.Bids).FirstOrDefaultAsync(a => a.Id == id);
    if (auction is null)
    {
        return Results.NotFound(new { message = $"Không tìm thấy phiên đấu giá '{id}'." });
    }

    db.Bids.RemoveRange(auction.Bids);
    db.Auctions.Remove(auction);
    await db.SaveChangesAsync();

    return Results.Ok(new { message = $"Đã xóa phiên đấu giá '{auction.ProductName}' thành công." });
});

app.Run();

static AuctionDto ToAuctionDto(AuctionItem auction, DateTime nowUtc)
{
    var isOpen = string.Equals(auction.Status, "Open", StringComparison.OrdinalIgnoreCase) && auction.EndTimeUtc > nowUtc;
    var remaining = isOpen ? Math.Max(0, (auction.EndTimeUtc - nowUtc).TotalSeconds) : 0;

    return new AuctionDto(
        auction.Id,
        auction.ProductName,
        auction.ProductDescription,
        auction.ImageUrl,
        auction.StartingPrice,
        auction.CurrentPrice,
        auction.MinIncrement,
        auction.CurrentWinner,
        auction.TotalBids,
        auction.StartTimeUtc,
        auction.EndTimeUtc,
        isOpen ? "Open" : "Closed",
        isOpen,
        Math.Round(remaining, 0),
        auction.CreatedAtUtc,
        auction.ClosedAtUtc);
}

static ValidateBidResponse PerformFallbackValidation(
    AuctionItem auction,
    PlaceBidRequest request,
    string correlationId,
    DateTime nowUtc)
{
    var hasWinner = !string.IsNullOrWhiteSpace(auction.CurrentWinner);
    var minBid = hasWinner ? auction.CurrentPrice + auction.MinIncrement : auction.StartingPrice;

    if (string.IsNullOrWhiteSpace(request.BidderName) || request.BidderName.Trim().Length < 2)
    {
        return new ValidateBidResponse
        {
            IsValid = false,
            ErrorCode = "INVALID_BIDDER",
            Message = "Tên người trả giá phải có ít nhất 2 ký tự.",
            MinimumRequiredBid = (double)minBid,
            CorrelationId = correlationId
        };
    }

    if (auction.Status != "Open" || nowUtc >= auction.EndTimeUtc)
    {
        return new ValidateBidResponse
        {
            IsValid = false,
            ErrorCode = "AUCTION_CLOSED",
            Message = "Phiên đấu giá đã đóng hoặc hết thời gian.",
            MinimumRequiredBid = (double)auction.CurrentPrice,
            CorrelationId = correlationId
        };
    }

    if (request.Amount < minBid)
    {
        return new ValidateBidResponse
        {
            IsValid = false,
            ErrorCode = "BID_TOO_LOW",
            Message = $"Giá đặt ({request.Amount:N0} VNĐ) thấp hơn mức tối thiểu hợp lệ ({minBid:N0} VNĐ).",
            MinimumRequiredBid = (double)minBid,
            CorrelationId = correlationId
        };
    }

    return new ValidateBidResponse
    {
        IsValid = true,
        ErrorCode = "OK_FALLBACK",
        Message = "Hợp lệ (Kiểm tra dự phòng).",
        MinimumRequiredBid = (double)(request.Amount + auction.MinIncrement),
        CorrelationId = correlationId
    };
}

public record ExtendAuctionRequest(int AdditionalMinutes = 15);
public record UpdateAuctionRequest(string? ProductName, string? ProductDescription, string? ImageUrl, decimal? MinIncrement);
