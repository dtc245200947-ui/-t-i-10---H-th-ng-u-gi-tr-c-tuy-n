# TÀI LIỆU HỌC TẬP & ÔN THI VẤN ĐÁP / C# - .NET 10 DISTRIBUTED SYSTEM
# GIẢI THÍCH CHI TIẾT TỪNG LỆNH MÃ NGUỒN: ĐỀ TÀI 10 - HỆ THỐNG ĐẤU GIÁ TRỰC TUYẾN
**Hướng dẫn đọc hiểu từng dòng lệnh của ASP.NET Core Minimal API (`Auction.Api`), gRPC (`BidValidation.Grpc`), RabbitMQ Event Bus, SignalR (`Auction.Hub`) và Worker (`Audit.Worker`).**

---

## Mục tiêu học tập & Bảo vệ Đồ án
Đọc hiểu cấu trúc chương trình phân tán; nắm vững cú pháp C# hiện đại (`record`, `lambda`, `async/await`, `LINQ`, `null-coalescing ??`, `pattern matching is null`); phân biệt mã trạng thái HTTP `200`/`201`/`400`/`404`/`503`; giải thích được cho giảng viên từng dòng code khi vấn đáp trực tiếp trên máy tính cá nhân.

### Bảng tóm tắt các Endpoint trong `Auction.Api` (`src/Auction.Api/Program.cs`)

| PHƯƠNG THỨC | ĐƯỜNG DẪN | CHỨC NĂNG | MÃ HTTP TRẢ VỀ |
| :--- | :--- | :--- | :--- |
| `GET` | `/health` | Kiểm tra trạng thái sống của dịch vụ. | `200 OK` |
| `GET` | `/auctions` | Lấy danh sách toàn bộ phiên đấu giá (lọc theo `?status=Open\|Closed`). | `200 OK` |
| `GET` | `/auctions/{id:guid}` | Lấy chi tiết 1 phiên đấu giá theo mã định danh `Guid`. | `200 OK` / `404 Not Found` |
| `POST` | `/auctions` | Tạo phiên đấu giá mới từ JSON body. | `201 Created` / `400 Bad Request` |
| `POST` | `/auctions/{id:guid}/bids` | Gửi mức giá mới $\rightarrow$ Gọi gRPC kiểm tra $\rightarrow$ Lưu DB $\rightarrow$ Phát RabbitMQ. | `200 OK` / `400` / `404` / `503` |
| `GET` | `/auctions/{id:guid}/bids` | Lấy lịch sử các lượt trả giá của phiên đấu giá. | `200 OK` / `404 Not Found` |
| `POST` | `/auctions/{id:guid}/end` | Kết thúc phiên đấu giá, chốt người thắng và phát sự kiện `AuctionEnded`. | `200 OK` / `404 Not Found` |
| `GET` | `/auctions/{id:guid}/result` | Tra cứu kết quả phiên đấu giá và người chiến thắng. | `200 OK` / `404 Not Found` |
| `GET` | `/audit-logs` | Xem lịch sử sự kiện đã được `Audit.Worker` ghi nhận. | `200 OK` |

---

## 01. Chuẩn bị và định hướng đọc mã

So với ví dụ `Catalog.Api` cơ bản (chỉ lưu `List<ProductDto>` tạm trong RAM và dùng `Max(p => p.Id) + 1`), dự án **Đề tài 10 - Hệ thống đấu giá trực tuyến** đã khắc phục toàn bộ các giới hạn được nêu ở Mục 11 của tài liệu mẫu:
1. **Thay `List<T>` trong RAM bằng Entity Framework Core (`AuctionDbContext`)**: Dữ liệu lưu bền vững trong PostgreSQL / SQLite, không bị mất khi tắt ứng dụng.
2. **Thay `int Id (Max + 1)` bằng `Guid`**: Tránh hoàn toàn lỗi trùng ID khi nhiều người dùng gửi request `POST` cùng lúc.
3. **Tách kiểm tra nghiệp vụ sang dịch vụ độc lập `BidValidation.Grpc`**: Giao tiếp tốc độ cao qua HTTP/2 và Protocol Buffers.
4. **Bổ sung giao tiếp bất đồng bộ qua `RabbitMQ` và thời gian thực qua `SignalR`**.

### Sơ đồ đường đi của một HTTP Request đặt giá (`POST /auctions/{id}/bids`)

```text
[Trình duyệt / Postman]
       │  1. POST /auctions/{id}/bids (JSON: bidderName, amount)
       ▼
[Auction.Api - Minimal API :5050]
       │  2. Gắn Correlation ID (Middleware)
       │  3. Truy vấn phiên đấu giá từ Database (db.Auctions.FirstOrDefaultAsync)
       │  4. Gọi gRPC ValidateBidAsync(...) ────────► [BidValidation.Grpc :5051]
       │                                              (Kiểm tra phiên mở, thời gian, giá >= CurrentPrice + MinIncrement)
       │  5. Lưu BidRecord & cập nhật CurrentPrice vào Database (db.SaveChangesAsync)
       │  6. Phát sự kiện BidAccepted ──────────────► [RabbitMQ Exchange: auction.events]
       │                                                     ├──► Queue: auction.hub.queue ───► [Auction.Hub :5052] (Đẩy SignalR Realtime)
       │                                                     └──► Queue: auction.audit.queue ─► [Audit.Worker] (Lưu AuditLog Idempotent)
       ▼
[Trả về HTTP 200 OK (PlaceBidResponseDto)]
```

---

## 02. Phân nhóm cấu trúc mã nguồn trong `src/Auction.Api/Program.cs`

Mã nguồn trong file [src/Auction.Api/Program.cs](file:///d:/Đề tài 10 - Hệ thống đấu giá trực tuyến/src/Auction.Api/Program.cs) được chia thành **5 nhóm rõ ràng**:

- **Nhóm A (Dòng 11–59): Khởi tạo ứng dụng, đăng ký Dependency Injection (DI) và Middleware**
  - Tạo `builder`, cấu hình cổng lắng nghe (`5050`), đăng ký Database (`AddAuctionDatabase`), đăng ký RabbitMQ Publisher, đăng ký gRPC Client (`AddGrpcClient`), khởi tạo dữ liệu mẫu (`EnsureAuctionDatabaseSeededAsync`) và gắn `X-Correlation-ID`.
- **Nhóm B (Dòng 64–100): Các API `GET` tra cứu danh sách và chi tiết phiên đấu giá**
  - `app.MapGet("/auctions", ...)` và `app.MapGet("/auctions/{id:guid}", ...)`.
- **Nhóm C (Dòng 105–165): API `POST /auctions` tạo phiên đấu giá mới**
  - Kiểm tra dữ liệu đầu vào (`ProductName`, `StartingPrice`, `EndTime`), tạo đối tượng `AuctionItem` và lưu vào Database, trả về `Results.Created(...)`.
- **Nhóm D (Dòng 170–320): API `POST /auctions/{id:guid}/bids` xử lý đặt giá phân tán**
  - Tìm phiên đấu giá $\rightarrow$ Gọi gRPC `ValidateBidAsync` $\rightarrow$ Xử lý nhánh từ chối (`400 BadRequest`) hoặc chấp nhận (`200 OK` + lưu DB + phát sự kiện RabbitMQ).
- **Nhóm E (Dòng 325–432): Các API lịch sử trả giá, kết thúc phiên, kết quả và hàm bổ trợ**
  - `GET /auctions/{id:guid}/bids`, `POST /auctions/{id:guid}/end`, `GET /auctions/{id:guid}/result`, `GET /audit-logs`, và hàm chuyển đổi `ToAuctionDto`.

---

## 03. Giải thích từng lệnh: Khởi tạo và Dependency Injection (Nhóm A)

### Dòng 11–15: Khởi tạo Builder và cấu hình cổng
```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);

var httpPort = builder.Configuration.GetValue<int>("Server:Port", 5050);
builder.WebHost.UseUrls($"http://0.0.0.0:{httpPort}");
```
- `WebApplication.CreateBuilder(args)`: Khởi tạo đối tượng xây dựng ứng dụng web, tự động nạp cấu hình từ `appsettings.json` và biến môi trường (trong `docker-compose.yml`).
- `builder.Configuration.GetValue<int>("Server:Port", 5050)`: Đọc cổng từ file cấu hình theo khóa `"Server:Port"`, nếu không khai báo thì lấy giá trị mặc định là `5050`. Việc tách cổng ra cấu hình đáp ứng yêu cầu *"Các cấu hình phụ thuộc môi trường phải được tách khỏi mã nguồn"*.

### Dòng 18–29: Đăng ký Database, RabbitMQ và gRPC Client vào DI Container
```csharp
builder.Services.AddAuctionDatabase(builder.Configuration);

builder.Services.Configure<RabbitMqSettings>(builder.Configuration.GetSection("RabbitMQ"));
builder.Services.AddSingleton<RabbitMqEventPublisher>();
builder.Services.AddHostedService<AuctionExpirationService>();

var grpcAddress = builder.Configuration["GrpcServices:BidValidationUrl"] ?? "http://localhost:5051";
builder.Services.AddGrpcClient<BidValidator.BidValidatorClient>(options =>
{
    options.Address = new Uri(grpcAddress);
});
```
- `AddAuctionDatabase(...)`: Phương thức mở rộng (Extension method) định nghĩa trong `Shared.Contracts` để đăng ký `AuctionDbContext`.
- `AddSingleton<RabbitMqEventPublisher>()`: Tạo một đối tượng duy nhất trong suốt vòng đời ứng dụng để giữ kết nối tới RabbitMQ.
- `AddHostedService<AuctionExpirationService>()`: Đăng ký tiến trình chạy ngầm tự động đóng các phiên đấu giá hết thời gian mỗi 3 giây.
- `AddGrpcClient<BidValidator.BidValidatorClient>(...)`: Đăng ký client gRPC được sinh tự động từ file `bid_validation.proto`. Toán tử `??` (null-coalescing) lấy địa chỉ mặc định `"http://localhost:5051"` nếu cấu hình rỗng.

### Dòng 42–57: Build ứng dụng, Seed dữ liệu và Middleware Correlation ID
```csharp
var app = builder.Build();
app.UseCors();
await app.Services.EnsureAuctionDatabaseSeededAsync();

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
```
- `EnsureAuctionDatabaseSeededAsync()`: Tạo bảng CSDL nếu chưa có và chèn 3 phiên đấu giá mẫu (MacBook Pro M4 Max, Đồng hồ Omega, Bàn phím cơ Keycult) để sinh viên có sẵn dữ liệu demo ngay khi bật máy.
- `app.Use(...)`: Khai báo một **Middleware** chạy trước mọi API. Nếu request gửi lên chưa có header `X-Correlation-ID`, hệ thống tự sinh mã dạng `corr-a3746e4d1c` (cắt 10 ký tự đầu bằng cú pháp range `[..10]`) và đính kèm vào cả Request lẫn Response để truy vết log xuyên suốt các service.

---

## 04. Giải thích các API GET tra cứu phiên đấu giá (Nhóm B)

### 4.1. Lấy danh sách phiên đấu giá (`GET /auctions`)
```csharp
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
```
- **Tách từng thành phần cú pháp**:
  - `(string? status, AuctionDbContext db)`: ASP.NET Core tự động lấy tham số `status` từ query string (ví dụ `/auctions?status=Open`) và tiêm (inject) đối tượng cơ sở dữ liệu `AuctionDbContext` từ DI Container. Dấu `?` sau `string` cho biết tham số này không bắt buộc (nullable).
  - `.AsNoTracking()`: Tối ưu hiệu năng cho truy vấn chỉ đọc (Read-only), báo cho Entity Framework không cần theo dõi thay đổi của các bản ghi này.
  - `.OrderByDescending(a => a.Status == "Open").ThenBy(a => a.EndTimeUtc)`: Đưa các phiên đang mở (`Open`) lên đầu danh sách, sau đó sắp xếp phiên nào sắp hết hạn lên trước.
  - `Results.Ok(dtos)`: Trả về mã trạng thái HTTP `200 OK` kèm mảng JSON `[...]`. Giống như trong `Catalog.Api`, nếu danh sách rỗng thì API vẫn trả `200 OK` cùng mảng rỗng `[]`, không phải `404`.

### 4.2. Lấy chi tiết phiên đấu giá theo ID (`GET /auctions/{id:guid}`)
```csharp
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
```
- **Điểm cần nhớ về Route Constraint `{id:guid}`**:
  - Hậu tố `:guid` tương tự như `:int` trong `Catalog.Api`, nhưng kiểm tra chuỗi trên URL phải đúng định dạng UUID/GUID (ví dụ `11111111-1111-1111-1111-111111111111`).
  - Nếu gọi `GET /auctions/abc` $\rightarrow$ Không khớp route `:guid`, framework trả về `404` ngay lập tức mà không chạy vào hàm handler.
  - Nếu gọi `GET /auctions/99999999-9999-9999-9999-999999999999` $\rightarrow$ Khớp route `:guid`, handler chạy `FirstOrDefaultAsync` trả về `null`, đi vào nhánh `if (auction is null)` và trả về `404 Not Found` kèm thông báo JSON tiếng Việt.

---

## 05. Giải thích API POST tạo phiên đấu giá (`POST /auctions` - Nhóm C)

```csharp
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
    // ... khởi tạo AuctionItem và lưu vào db.Auctions
    db.Auctions.Add(auction);
    await db.SaveChangesAsync();

    return Results.Created($"/auctions/{auction.Id}", ToAuctionDto(auction, now));
});
```
- **Giải thích từng bước**:
  - `CreateAuctionRequest request`: Model binding tự động đọc JSON từ phần thân (Body) của HTTP POST và ánh xạ vào record `CreateAuctionRequest`.
  - Kiểm tra dữ liệu đầu vào: Nếu tên sản phẩm rỗng/chỉ chứa khoảng trắng (`string.IsNullOrWhiteSpace`) hoặc giá khởi điểm `<= 0`, lập tức dừng xử lý và trả về `400 Bad Request` (`Results.BadRequest`).
  - `CurrentPrice = request.StartingPrice`: Khi mới tạo phiên, giá hiện tại được gán bằng giá khởi điểm và `CurrentWinner = null`.
  - `await db.SaveChangesAsync()`: Thực thi lệnh `INSERT INTO "Auctions"` xuống cơ sở dữ liệu thật (khắc phục giới hạn chỉ lưu RAM của `products.Add(product)` trong bài mẫu).
  - `Results.Created($"/auctions/{auction.Id}", ...)`: Trả về HTTP `201 Created`, gắn đường dẫn tài nguyên vừa tạo vào header `Location` và trả về đối tượng `AuctionDto` trong body.

---

## 06. Giải thích luồng Đặt giá Phân tán (`POST /auctions/{id:guid}/bids` - Nhóm D)

Đây là **endpoint quan trọng nhất của Đề tài 10**, kết nối cả 4 công nghệ: **REST $\rightarrow$ gRPC $\rightarrow$ Database $\rightarrow$ RabbitMQ**.

### Bước 1: Kiểm tra phiên đấu giá có tồn tại không
```csharp
var auction = await db.Auctions.FirstOrDefaultAsync(a => a.Id == id);
if (auction is null)
{
    return Results.NotFound(new { message = $"Không tìm thấy phiên đấu giá '{id}'." });
}
```
- Lưu ý: Ở đây **không dùng** `.AsNoTracking()` vì nếu lượt trả giá hợp lệ, ta cần cập nhật trực tiếp `auction.CurrentPrice`, `auction.CurrentWinner`, `auction.TotalBids` và gọi `SaveChangesAsync()`.

### Bước 2: Gọi `BidValidation.Grpc` qua giao thức gRPC
```csharp
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
```
- `grpcValidator.ValidateBidAsync(...)`: Gửi lời gọi thủ tục từ xa (Remote Procedure Call) qua HTTP/2 tới container/tiến trình `BidValidation.Grpc` ở cổng `5051`.
- `deadline: DateTime.UtcNow.AddSeconds(5)`: Đặt thời gian chờ tối đa (Timeout) là 5 giây để tránh treo `Auction.Api` nếu dịch vụ gRPC bị nghẽn.
- Khối `catch (RpcException rpcEx)` ngay bên dưới giúp hệ thống không bị sập khi dịch vụ gRPC tạm dừng: nếu bật `EnableLocalFallbackValidation`, `Auction.Api` tự động chuyển sang kiểm tra dự phòng tại chỗ (`PerformFallbackValidation`).

### Bước 3: Xử lý kết quả trả về từ gRPC (`BidValidatorService.cs`)
```csharp
if (!validationResult.IsValid)
{
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
```
- Bên trong `BidValidation.Grpc/Services/BidValidatorService.cs`, mức giá tối thiểu hợp lệ được tính bằng công thức:
  ```csharp
  var hasPreviousBid = !string.IsNullOrWhiteSpace(request.CurrentWinner);
  var minimumRequiredBid = hasPreviousBid
      ? request.CurrentPrice + minIncrement
      : Math.Max(request.StartingPrice, request.CurrentPrice);
  ```
- Nếu `request.BidAmount < minimumRequiredBid`, dịch vụ gRPC trả về `IsValid = false` cùng mã lỗi `"BID_TOO_LOW"`, và `Auction.Api` trả về HTTP `400 Bad Request` cho Client.

### Bước 4: Lưu mức giá mới vào Database và Phát sự kiện `BidAccepted` qua RabbitMQ
```csharp
auction.CurrentPrice = request.Amount;
auction.CurrentWinner = bidRecord.BidderName;
auction.TotalBids += 1;

db.Bids.Add(bidRecord);
await db.SaveChangesAsync();

await eventPublisher.PublishEventAsync(envelope, "auction.bid.accepted");
```
- Sau khi `db.SaveChangesAsync()` lưu thành công vào bảng `Auctions` và `Bids`, `eventPublisher.PublishEventAsync` đóng gói sự kiện `BidAccepted` thành chuỗi JSON và phát lên **RabbitMQ Fanout Exchange** `auction.events`.
- Từ Exchange `auction.events`, bản tin được nhân bản đồng thời tới 2 hàng đợi:
  1. **`auction.hub.queue`**: Dịch vụ `Auction.Hub` đọc bản tin và gọi `_hubContext.Clients.All.SendAsync("BidAccepted", payload)` để mọi trình duyệt Web và Console Client nhảy số giá mới ngay lập tức.
  2. **`auction.audit.queue`**: Dịch vụ `Audit.Worker` đọc bản tin, kiểm tra không trùng `EventId` (`!await db.AuditLogs.AnyAsync(a => a.EventId == envelope.EventId)`) và lưu vào bảng `AuditLogs`.

---

## 07. Giải thích các kiểu `record` DTO và Event trong `Shared.Contracts`

Tương tự như 3 record ở cuối file `Catalog.Api`, trong dự án này các `record` được tách gọn gàng vào thư mục `src/Shared.Contracts/Dtos` và `src/Shared.Contracts/Events` để cả 6 project cùng sử dụng chung:

| Tên `record` | Vai trò trong hệ thống | Vì sao thiết kế như vậy? |
| :--- | :--- | :--- |
| `CreateAuctionRequest` | Nhận JSON khi client gọi `POST /auctions` | Không chứa `Id`, `CurrentPrice`, `CurrentWinner` vì server tự sinh `Guid` và khởi tạo giá ban đầu. |
| `PlaceBidRequest` | Nhận JSON khi client gọi `POST /auctions/{id}/bids` | Chỉ gồm `(string BidderName, decimal Amount)` — ngăn client tự ý sửa thời gian hay trạng thái phiên. |
| `AuctionDto` | Dữ liệu trả về cho Client khi gọi `GET /auctions` | Bổ sung trường tính toán động `IsActive` và `RemainingSeconds` (số giây còn lại) để giao diện đếm ngược. |
| `PlaceBidResponseDto` | Phản hồi chuẩn hóa cho thao tác đặt giá (`200` hoặc `400`) | Chứa cả `Success`, `ErrorCode`, `Message`, `MinimumNextBid` và `CorrelationId`. |
| `AuctionEventEnvelope` | Khung bọc sự kiện truyền qua RabbitMQ | Chứa `EventId` (để chống trùng lặp Idempotent), `CorrelationId` (để truy vết log) và `EventType`. |

---

## 08. Hướng dẫn kiểm thử từng API bằng Postman hoặc Trình duyệt

Hệ thống của chúng ta chạy `Auction.Api` ở cổng **`5050`** và giao diện Web App (`Auction.Hub`) ở cổng **`5052`**.

### Bước 1: Kiểm thử các API `GET`
| Tên Request | Phương thức & URL | Kết quả kỳ vọng |
| :--- | :--- | :--- |
| Kiểm tra sức khỏe API | `GET http://localhost:5050/health` | `200 OK` — Nội dung: `Healthy` |
| Kiểm tra sức khỏe gRPC | `GET http://localhost:5151/health` | `200 OK` — Nội dung: `Healthy` |
| Lấy danh sách phiên đấu giá | `GET http://localhost:5050/auctions` | `200 OK` — Mảng JSON gồm các phiên đấu giá |
| Lấy chi tiết phiên có tồn tại | `GET http://localhost:5050/auctions/11111111-1111-1111-1111-111111111111` | `200 OK` — Thông tin MacBook Pro M4 Max |
| Lấy chi tiết phiên không tồn tại | `GET http://localhost:5050/auctions/99999999-9999-9999-9999-999999999999` | `404 Not Found` — JSON báo không tìm thấy |
| Sai định dạng GUID | `GET http://localhost:5050/auctions/abc` | `404 Not Found` — Không khớp route `{id:guid}` |

### Bước 2: Kiểm thử `POST /auctions` (Tạo phiên đấu giá mới)
- **URL**: `POST http://localhost:5050/auctions`
- **Header**: `Content-Type: application/json`
- **Body (raw JSON)**:
  ```json
  {
    "productName": "iPad Pro M4 OLED 13-inch 1TB",
    "productDescription": "Hàng chính hãng Apple Việt Nam",
    "startingPrice": 35000000,
    "minIncrement": 500000,
    "durationMinutes": 45
  }
  ```
- **Kỳ vọng**: Trả về `201 Created`, có `id` kiểu `Guid` mới sinh, `currentPrice = 35000000`, `status = "Open"`.

### Bước 3: Kiểm thử `POST /auctions/{id}/bids` (Đặt giá hợp lệ & Đặt giá lỗi)
- **Trường hợp 1 — Đặt giá hợp lệ**:
  - **URL**: `POST http://localhost:5050/auctions/11111111-1111-1111-1111-111111111111/bids`
  - **Body**:
    ```json
    {
      "bidderName": "Trần Đức Nhân",
      "amount": 46000000
    }
    ```
  - **Kỳ vọng**: `200 OK`, `success: true`, `currentWinner: "Trần Đức Nhân"`, `currentPrice: 46000000`. Đồng thời giao diện Web tại `http://localhost:5052` tự động nhảy giá lên `46.000.000 ₫` ngay lập tức!
- **Trường hợp 2 — Đặt giá thấp hơn bước giá tối thiểu (Kiểm tra gRPC từ chối)**:
  - Giữ nguyên URL trên, gửi `"amount": 46100000` (trong khi bước giá tối thiểu là `500000` $\rightarrow$ yêu cầu tối thiểu `46500000`).
  - **Kỳ vọng**: `400 Bad Request`, `success: false`, `errorCode: "BID_TOO_LOW"`.

---

## 09. Bộ câu hỏi & Đáp án Vấn đáp thường gặp khi Bảo vệ Đồ án

### Câu hỏi 1: Tại sao `Auction.Api` không tự kiểm tra điều kiện giá bằng một hàm `if` bên trong nó mà phải gọi sang `BidValidation.Grpc`?
- **Trả lời**: Theo kiến trúc Microservices của đề bài, việc tách `BidValidation.Grpc` thành một dịch vụ độc lập giúp:
  1. **Tách biệt trách nhiệm (Separation of Concerns)**: Quy tắc kiểm tra giá (bước giá, hạn mức, khung giờ, danh sách đen...) có thể thay đổi và nâng cấp độc lập mà không cần build/deploy lại `Auction.Api`.
  2. **Hiệu năng giao tiếp nội bộ cao**: Giao tiếp giữa `Auction.Api` và `BidValidation.Grpc` dùng giao thức **gRPC trên nền HTTP/2** và tuần tự hóa nhị phân **Protocol Buffers**, nhanh và nhẹ hơn nhiều so với gửi JSON qua HTTP/1.1.

### Câu hỏi 2: Tại sao khi có lượt trả giá hợp lệ, `Auction.Api` lại gửi sự kiện qua `RabbitMQ` thay vì gọi trực tiếp `Auction.Hub` và `Audit.Worker`?
- **Trả lời**:
  1. **Giảm độ trễ cho người đặt giá (Asynchronous Decoupling)**: Người dùng gửi `POST /bids` nhận được phản hồi ngay sau khi lưu DB và đẩy bản tin vào RabbitMQ, không phải chờ `Audit.Worker` ghi log hay chờ `Auction.Hub` gửi thông báo tới hàng nghìn client.
  2. **Cơ chế Pub/Sub (Fanout Exchange `auction.events`)**: `Auction.Api` chỉ cần phát sự kiện `BidAccepted` **một lần duy nhất** lên Exchange `auction.events`. RabbitMQ sẽ tự động nhân bản bản tin đó tới cả 2 hàng đợi `auction.hub.queue` và `auction.audit.queue`. Nếu sau này muốn thêm `Notification.Worker` (gửi email), ta chỉ cần gắn thêm queue mới vào Exchange mà không phải sửa một dòng code nào của `Auction.Api`.
  3. **Đảm bảo không mất dữ liệu khi Worker tạm dừng**: Nếu `Audit.Worker` tạm thời tắt để bảo trì, các bản tin vẫn nằm an toàn trong hàng đợi `auction.audit.queue` (`durable: true`). Khi `Audit.Worker` bật lại, nó sẽ tiếp tục xử lý hết các bản tin tồn đọng.

### Câu hỏi 3: "Idempotent Consumer" và "Dead-Letter Queue (DLQ)" trong `Audit.Worker` hoạt động ở dòng code nào?
- **Trả lời**: Nằm trong file [src/Audit.Worker/Worker.cs](file:///d:/Đề tài 10 - Hệ thống đấu giá trực tuyến/src/Audit.Worker/Worker.cs):
  - **Idempotent Consumer (Dòng 225)**: Trước khi thêm vào bảng `AuditLogs`, Worker kiểm tra `var alreadyProcessed = await db.AuditLogs.AnyAsync(a => a.EventId == envelope.EventId);`. Nếu `alreadyProcessed == true`, Worker bỏ qua không ghi trùng lần 2. Ngoài ra cột `EventId` còn được đánh `HasIndex(e => e.EventId).IsUnique()` ở tầng Database.
  - **Retry & Dead-Letter Queue (Dòng 104 và 124–166)**: Hàng đợi `auction.audit.queue` được khai báo kèm tham số `["x-dead-letter-exchange"] = "auction.dlx"`. Khi xử lý bản tin gặp lỗi, vòng lặp `for (var attempt = 1; attempt <= 3; attempt++)` thử lại tối đa 3 lần. Nếu cả 3 lần đều thất bại, lệnh `_channel.BasicNack(ea.DeliveryTag, multiple: false, requeue: false)` sẽ kích hoạt RabbitMQ chuyển bản tin lỗi đó sang hàng đợi `auction.audit.dlq`.
