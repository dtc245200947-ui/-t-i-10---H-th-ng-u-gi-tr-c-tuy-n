using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Shared.Contracts.Dtos;
using Shared.Contracts.Models;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

var apiUrl = Environment.GetEnvironmentVariable("AUCTION_API_URL") ?? "http://localhost:5050";
var hubUrl = Environment.GetEnvironmentVariable("AUCTION_HUB_URL") ?? "http://localhost:5052/hubs/auction";

using var httpClient = new HttpClient { BaseAddress = new Uri(apiUrl) };

await using var hubConnection = new HubConnectionBuilder()
    .WithUrl(hubUrl)
    .WithAutomaticReconnect()
    .Build();

hubConnection.On<JsonElement>("BidAccepted", payload =>
{
    var product = payload.GetProperty("productName").GetString();
    var bidder = payload.GetProperty("bidderName").GetString();
    var amount = payload.GetProperty("bidAmount").GetDecimal();
    var corrId = payload.GetProperty("correlationId").GetString();

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"\n🔔 [SIGNALR REALTIME - BidAccepted] '{bidder}' vừa trả giá {amount:N0} VNĐ cho '{product}' (CorrelationId: {corrId})");
    Console.ResetColor();
});

hubConnection.On<JsonElement>("AuctionEnded", payload =>
{
    var product = payload.GetProperty("productName").GetString();
    var winner = payload.TryGetProperty("winnerName", out var w) && w.ValueKind != JsonValueKind.Null
        ? w.GetString()
        : "Không có người trả giá";
    var finalPrice = payload.GetProperty("finalPrice").GetDecimal();
    var corrId = payload.GetProperty("correlationId").GetString();

    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"\n🏆 [SIGNALR REALTIME - AuctionEnded] Phiên '{product}' đã KẾT THÚC! Người thắng: {winner} ({finalPrice:N0} VNĐ) (CorrelationId: {corrId})");
    Console.ResetColor();
});

Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("=========================================================================");
Console.WriteLine("   HỆ THỐNG ĐẤU GIÁ TRỰC TUYẾN - BIDDER CONSOLE CLIENT (.NET 10)");
Console.WriteLine($"   REST API : {apiUrl}");
Console.WriteLine($"   SignalR  : {hubUrl}");
Console.WriteLine("=========================================================================");
Console.ResetColor();

try
{
    await hubConnection.StartAsync();
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("✅ Đã kết nối thành công tới Auction SignalR Hub!");
    Console.ResetColor();
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"⚠️ Chưa kết nối được SignalR Hub ({ex.Message}). Vẫn có thể gọi REST API.");
    Console.ResetColor();
}

if (args.Contains("--auto-demo", StringComparer.OrdinalIgnoreCase))
{
    await RunAutomatedDemoAsync(httpClient, hubConnection);
    return;
}

Console.Write("\nNhập tên người đấu giá của bạn (Mặc định: Nguyễn Văn An): ");
var currentBidder = Console.ReadLine()?.Trim();
if (string.IsNullOrWhiteSpace(currentBidder))
{
    currentBidder = "Nguyễn Văn An";
}

Guid? watchingAuctionId = null;

while (true)
{
    Console.WriteLine("\n------------------------- MENU CHỨC NĂNG -------------------------");
    Console.WriteLine($"Người dùng hiện tại: {currentBidder} | Phiên đang theo dõi: {(watchingAuctionId?.ToString() ?? "Chưa chọn")}");
    Console.WriteLine("1. Danh sách phiên đấu giá (GET /auctions)");
    Console.WriteLine("2. Chọn & Theo dõi một phiên đấu giá qua SignalR (GET /auctions/{id})");
    Console.WriteLine("3. Gửi mức giá mới cho phiên đang theo dõi (POST /auctions/{id}/bids)");
    Console.WriteLine("4. Xem lịch sử trả giá của phiên (GET /auctions/{id}/bids)");
    Console.WriteLine("5. Tạo phiên đấu giá mới (POST /auctions)");
    Console.WriteLine("6. Kết thúc phiên đấu giá & Xem người thắng (POST /auctions/{id}/end)");
    Console.WriteLine("7. Xem nhật ký Audit Worker (GET /audit-logs)");
    Console.WriteLine("8. Chạy kịch bản Demo tự động toàn trình (End-to-End Flow)");
    Console.WriteLine("0. Thoát");
    Console.Write("Chọn chức năng (0-8): ");

    var choice = Console.ReadLine()?.Trim();
    try
    {
        switch (choice)
        {
            case "1":
                var list = await httpClient.GetFromJsonAsync<List<AuctionDto>>("/auctions") ?? new();
                Console.WriteLine($"\nTìm thấy {list.Count} phiên đấu giá:");
                for (var i = 0; i < list.Count; i++)
                {
                    var a = list[i];
                    Console.WriteLine($"  [{i + 1}] ID: {a.Id}");
                    Console.WriteLine($"      Sản phẩm : {a.ProductName} [{a.Status}]");
                    Console.WriteLine($"      Giá hiện tại: {a.CurrentPrice:N0} VNĐ (Bước giá: {a.MinIncrement:N0} VNĐ)");
                    Console.WriteLine($"      Người dẫn đầu: {a.CurrentWinner ?? "Chưa có"} ({a.TotalBids} lượt trả giá)");
                }
                break;

            case "2":
                var auctions = await httpClient.GetFromJsonAsync<List<AuctionDto>>("/auctions") ?? new();
                if (auctions.Count == 0)
                {
                    Console.WriteLine("Chưa có phiên đấu giá nào.");
                    break;
                }
                for (var i = 0; i < auctions.Count; i++)
                {
                    Console.WriteLine($"  [{i + 1}] {auctions[i].ProductName} - Giá: {auctions[i].CurrentPrice:N0} VNĐ ({auctions[i].Id})");
                }
                Console.Write("Nhập số thứ tự phiên muốn theo dõi: ");
                if (int.TryParse(Console.ReadLine(), out var idx) && idx >= 1 && idx <= auctions.Count)
                {
                    watchingAuctionId = auctions[idx - 1].Id;
                    if (hubConnection.State == HubConnectionState.Connected)
                    {
                        await hubConnection.InvokeAsync("JoinAuction", watchingAuctionId.Value.ToString());
                    }
                    Console.WriteLine($"✅ Đang theo dõi phiên '{auctions[idx - 1].ProductName}' ({watchingAuctionId}).");
                }
                break;

            case "3":
                if (watchingAuctionId is null)
                {
                    Console.WriteLine("⚠️ Vui lòng chọn phiên đấu giá ở mục [2] trước.");
                    break;
                }
                var detail = await httpClient.GetFromJsonAsync<AuctionDto>($"/auctions/{watchingAuctionId}");
                if (detail is not null)
                {
                    var minBid = string.IsNullOrWhiteSpace(detail.CurrentWinner)
                        ? detail.StartingPrice
                        : detail.CurrentPrice + detail.MinIncrement;
                    Console.WriteLine($"Giá hiện tại: {detail.CurrentPrice:N0} VNĐ | Giá tối thiểu hợp lệ tiếp theo: {minBid:N0} VNĐ");
                }
                Console.Write("Nhập mức giá muốn đặt (VNĐ): ");
                if (decimal.TryParse(Console.ReadLine(), out var amount))
                {
                    var response = await httpClient.PostAsJsonAsync($"/auctions/{watchingAuctionId}/bids", new PlaceBidRequest(currentBidder, amount));
                    var result = await response.Content.ReadFromJsonAsync<PlaceBidResponseDto>();
                    if (response.IsSuccessStatusCode && result?.Success == true)
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"✅ Đặt giá thành công: {result.Message} (CorrelationId: {result.CorrelationId})");
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"❌ Bị từ chối bởi gRPC [{result?.ErrorCode}]: {result?.Message}");
                        Console.ResetColor();
                    }
                }
                break;

            case "4":
                if (watchingAuctionId is null)
                {
                    Console.WriteLine("⚠️ Vui lòng chọn phiên đấu giá ở mục [2] trước.");
                    break;
                }
                var bids = await httpClient.GetFromJsonAsync<List<BidDto>>($"/auctions/{watchingAuctionId}/bids") ?? new();
                Console.WriteLine($"\nLịch sử trả giá ({bids.Count} lượt):");
                foreach (var b in bids)
                {
                    Console.WriteLine($"  - [{b.PlacedAtUtc:HH:mm:ss}] {b.BidderName}: {b.Amount:N0} VNĐ (CorrId: {b.CorrelationId})");
                }
                break;

            case "5":
                Console.Write("Tên sản phẩm đấu giá: ");
                var name = Console.ReadLine()?.Trim() ?? "Sản phẩm đấu giá mới";
                Console.Write("Giá khởi điểm (VNĐ, mặc định 10000000): ");
                var startPrice = decimal.TryParse(Console.ReadLine(), out var sp) ? sp : 10_000_000m;
                Console.Write("Bước giá tối thiểu (VNĐ, mặc định 500000): ");
                var stepPrice = decimal.TryParse(Console.ReadLine(), out var st) ? st : 500_000m;

                var createRes = await httpClient.PostAsJsonAsync("/auctions", new CreateAuctionRequest(
                    ProductName: name,
                    ProductDescription: "Tạo từ Bidder Console Client",
                    ImageUrl: null,
                    StartingPrice: startPrice,
                    MinIncrement: stepPrice,
                    StartTimeUtc: null,
                    EndTimeUtc: null,
                    DurationMinutes: 60));

                var created = await createRes.Content.ReadFromJsonAsync<AuctionDto>();
                if (created != null)
                {
                    watchingAuctionId = created.Id;
                    Console.WriteLine($"✅ Đã tạo phiên đấu giá '{created.ProductName}' với ID: {created.Id}");
                }
                break;

            case "6":
                if (watchingAuctionId is null)
                {
                    Console.WriteLine("⚠️ Vui lòng chọn phiên đấu giá ở mục [2] trước.");
                    break;
                }
                var endRes = await httpClient.PostAsync($"/auctions/{watchingAuctionId}/end", null);
                var endDto = await endRes.Content.ReadFromJsonAsync<AuctionResultDto>();
                Console.WriteLine($"\n🏁 {endDto?.ResultSummary}");
                break;

            case "7":
                var logs = await httpClient.GetFromJsonAsync<List<AuditLogEntry>>("/audit-logs") ?? new();
                Console.WriteLine($"\nNhật ký Audit Worker ({logs.Count} bản ghi gần nhất):");
                foreach (var l in logs.Take(10))
                {
                    Console.WriteLine($"  - [{l.EventType}] {l.Summary} | EventId: {l.EventId[..10]}... | CorrId: {l.CorrelationId}");
                }
                break;

            case "8":
                await RunAutomatedDemoAsync(httpClient, hubConnection);
                break;

            case "0":
                return;
        }
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Lỗi: {ex.Message}");
        Console.ResetColor();
    }
}

static async Task RunAutomatedDemoAsync(HttpClient httpClient, HubConnection hubConnection)
{
    Console.WriteLine("\n>>> BẮT ĐẦU KỊCH BẢN KIỂM THỬ & DEMO TỰ ĐỘNG TOÀN TRÌNH <<<");

    // 1. Tạo phiên đấu giá mới
    Console.WriteLine("\n[Bước 1] Tạo phiên đấu giá mới qua POST /auctions...");
    var createReq = new CreateAuctionRequest(
        ProductName: "Sony PlayStation 5 Pro 30th Anniversary Limited Edition",
        ProductDescription: "Bản giới hạn kỷ niệm 30 năm nguyên seal",
        ImageUrl: null,
        StartingPrice: 20_000_000m,
        MinIncrement: 500_000m,
        StartTimeUtc: null,
        EndTimeUtc: null,
        DurationMinutes: 30);

    var createRes = await httpClient.PostAsJsonAsync("/auctions", createReq);
    createRes.EnsureSuccessStatusCode();
    var auction = (await createRes.Content.ReadFromJsonAsync<AuctionDto>())!;
    Console.WriteLine($" -> Đã tạo phiên ID: {auction.Id} | Sản phẩm: {auction.ProductName} | Giá khởi điểm: {auction.StartingPrice:N0} VNĐ");

    // 2. Tham gia phòng SignalR
    if (hubConnection.State == HubConnectionState.Connected)
    {
        await hubConnection.InvokeAsync("JoinAuction", auction.Id.ToString());
        Console.WriteLine($"[Bước 2] Đã đăng ký theo dõi phiên {auction.Id} qua SignalR Hub.");
    }

    // 3. Đặt giá hợp lệ lần 1
    Console.WriteLine("\n[Bước 3] Người dùng 'Trần Đức Nhân' đặt giá hợp lệ 20,500,000 VNĐ...");
    var bid1Res = await httpClient.PostAsJsonAsync($"/auctions/{auction.Id}/bids", new PlaceBidRequest("Trần Đức Nhân", 20_500_000m));
    var bid1Data = await bid1Res.Content.ReadFromJsonAsync<PlaceBidResponseDto>();
    Console.WriteLine($" -> Kết quả: Success={bid1Data?.Success} | Message={bid1Data?.Message} | CorrelationId={bid1Data?.CorrelationId}");

    await Task.Delay(1200);

    // 4. Đặt giá KHÔNG hợp lệ (thấp hơn bước giá tối thiểu) để kiểm tra gRPC BidValidation Service
    Console.WriteLine("\n[Bước 4] Người dùng 'Lê Hoàng Nam' thử đặt giá thấp 20,600,000 VNĐ (bước giá tối thiểu 500,000 VNĐ -> cần >= 21,000,000 VNĐ)...");
    var bidInvalidRes = await httpClient.PostAsJsonAsync($"/auctions/{auction.Id}/bids", new PlaceBidRequest("Lê Hoàng Nam", 20_600_000m));
    var bidInvalidData = await bidInvalidRes.Content.ReadFromJsonAsync<PlaceBidResponseDto>();
    Console.WriteLine($" -> Kết quả kiểm tra từ gRPC BidValidation: Success={bidInvalidData?.Success} | ErrorCode={bidInvalidData?.ErrorCode} | Message={bidInvalidData?.Message}");

    // 5. Đặt giá hợp lệ lần 2 vượt lên
    Console.WriteLine("\n[Bước 5] Người dùng 'Lê Hoàng Nam' đặt giá hợp lệ 21,500,000 VNĐ...");
    var bid2Res = await httpClient.PostAsJsonAsync($"/auctions/{auction.Id}/bids", new PlaceBidRequest("Lê Hoàng Nam", 21_500_000m));
    var bid2Data = await bid2Res.Content.ReadFromJsonAsync<PlaceBidResponseDto>();
    Console.WriteLine($" -> Kết quả: Success={bid2Data?.Success} | Giá hiện tại: {bid2Data?.CurrentPrice:N0} VNĐ | Người dẫn đầu: {bid2Data?.CurrentWinner}");

    await Task.Delay(1500);

    // 6. Kết thúc phiên đấu giá và xác định người chiến thắng
    Console.WriteLine("\n[Bước 6] Kết thúc phiên đấu giá (POST /auctions/{id}/end) và xác định người thắng...");
    var endRes = await httpClient.PostAsync($"/auctions/{auction.Id}/end", null);
    var endData = await endRes.Content.ReadFromJsonAsync<AuctionResultDto>();
    Console.WriteLine($" -> Kết quả chung cuộc: {endData?.ResultSummary}");

    await Task.Delay(1500);

    // 7. Tra cứu lịch sử trả giá và Audit Logs
    var bidHistory = await httpClient.GetFromJsonAsync<List<BidDto>>($"/auctions/{auction.Id}/bids") ?? new();
    Console.WriteLine($"\n[Bước 7] Tra cứu GET /auctions/{auction.Id}/bids -> Có {bidHistory.Count} lượt trả giá hợp lệ:");
    foreach (var b in bidHistory)
    {
        Console.WriteLine($"   * {b.BidderName} trả {b.Amount:N0} VNĐ lúc {b.PlacedAtUtc:HH:mm:ss} (CorrelationId: {b.CorrelationId})");
    }

    var auditLogs = await httpClient.GetFromJsonAsync<List<AuditLogEntry>>($"/audit-logs?auctionId={auction.Id}") ?? new();
    Console.WriteLine($"\n[Bước 8] Kiểm tra Audit Worker (GET /audit-logs) -> Đã ghi nhận {auditLogs.Count} sự kiện:");
    foreach (var log in auditLogs)
    {
        Console.WriteLine($"   * [{log.EventType}] {log.Summary} (EventId: {log.EventId})");
    }

    Console.WriteLine("\n>>> HOÀN TẤT KỊCH BẢN DEMO TỰ ĐỘNG THÀNH CÔNG! <<<");
}
