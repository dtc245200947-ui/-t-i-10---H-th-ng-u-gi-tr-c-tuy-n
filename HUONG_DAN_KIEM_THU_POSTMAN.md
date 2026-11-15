# CẨM NANG THỰC HÀNH • ASP.NET CORE MINIMAL API (.NET 10)
# HƯỚNG DẪN CHI TIẾT KIỂM THỬ API BẰNG POSTMAN DESKTOP
## ĐỀ TÀI 10: HỆ THỐNG ĐẤU GIÁ TRỰC TUYẾN (`Auction.Api` + `BidValidation.Grpc` + `RabbitMQ` + `SignalR`)

**Thao tác từng bước – dữ liệu thử nghiệm – kịch bản đúng/sai – tự động kiểm tra bằng JavaScript (`Post-response`) – chạy Collection Runner – xử lý lỗi thường gặp.**

---

### Bảng tóm tắt các API cần kiểm thử trong `Auction.Api` (Cổng mặc định: `http://localhost:5050`)

| API | Mục đích | Kết quả kỳ vọng |
| :--- | :--- | :--- |
| `GET /` | Kiểm tra ứng dụng `Auction.Api` đang hoạt động | `200 OK` (`service = "Auction.Api"`, `status = "running"`) |
| `GET /auctions` | Lấy danh sách toàn bộ các phiên đấu giá | `200 OK` (Mảng JSON `[...]`) |
| `GET /auctions/{id}` | Lấy thông tin chi tiết của một phiên đấu giá | `200 OK` hoặc `404 Not Found` |
| `POST /auctions` | Tạo phiên đấu giá mới từ JSON body | `201 Created` hoặc `400 Bad Request` |
| `POST /auctions/{id}/bids` | Gửi mức giá mới (qua gRPC `BidValidation` & phát `RabbitMQ`) | `200 OK` (hợp lệ) hoặc `400 Bad Request` (`BID_TOO_LOW`) |
| `GET /auctions/{id}/bids` | Xem lịch sử các lượt trả giá của phiên | `200 OK` |
| `POST /auctions/{id}/end` | Kết thúc phiên đấu giá và chốt người chiến thắng | `200 OK` (`status = "Closed"`, `winner`) |

---

## 01 | Chuẩn bị trước khi sử dụng Postman

### 1.1. Phần mềm và điều kiện cần có
| Thành phần | Yêu cầu |
| :--- | :--- |
| **Dự án .NET 10** | Giải pháp `OnlineAuctionSystem.sln` đã được build và khởi chạy (`.\start-local.ps1` hoặc bấm `F5` trong Visual Studio / VS Code). |
| **Postman Desktop** | Cài ứng dụng Postman trên máy tính; hoặc Import trực tiếp file [OnlineAuctionSystem.postman_collection.json](file:///d:/Đề tài 10 - Hệ thống đấu giá trực tuyến/OnlineAuctionSystem.postman_collection.json) có sẵn trong thư mục gốc của dự án. |
| **Kết nối máy chủ** | Các dịch vụ (`Auction.Api` cổng `5050`, `BidValidation.Grpc` cổng `5051`, `Auction.Hub` cổng `5052`) phải đang chạy trước khi nhấn **Send** trên Postman. |

### 1.2. Khởi chạy hệ thống và xác định cổng
Mở Terminal tại thư mục dự án và chạy:
```powershell
.\start-local.ps1
```
Quan sát dòng thông báo:
- **`Auction.Api` (REST Minimal API)**: `Now listening on: http://0.0.0.0:5050` $\rightarrow$ Địa chỉ dùng trong Postman là **`http://localhost:5050`**.
- **`Auction.Hub` (Giao diện Web & SignalR)**: `Now listening on: http://0.0.0.0:5052` $\rightarrow$ Mở trình duyệt tại **`http://localhost:5052`** để quan sát giá nhảy trực tiếp (Realtime) mỗi khi bạn bấm **Send** đặt giá từ Postman!

---

## 02 | Cách Import nhanh Collection có sẵn (Chỉ mất 5 giây)

Để tiết kiệm thời gian tạo từng request thủ công, dự án đã chuẩn bị sẵn file **[OnlineAuctionSystem.postman_collection.json](file:///d:/Đề tài 10 - Hệ thống đấu giá trực tuyến/OnlineAuctionSystem.postman_collection.json)** gồm đủ **10 kịch bản kiểm thử** và **Post-response JavaScript tests**:
1. Mở **Postman Desktop**.
2. Nhấn nút **Import** ở góc trên bên trái.
3. Kéo thả file `OnlineAuctionSystem.postman_collection.json` (nằm ngay tại thư mục gốc `D:\Đề tài 10 - Hệ thống đấu giá trực tuyến`) vào cửa sổ Import.
4. Bộ sưu tập **`Online Auction API - Lab (Đề tài 10)`** sẽ xuất hiện ngay ở thanh bên trái.

---

## 03 | Tự tạo Collection và biến `baseUrl` (Nếu muốn thao tác từng bước)

### 3.1. Tạo biến dùng chung cho toàn bộ Collection
Chọn Collection **`Online Auction API - Lab (Đề tài 10)`** $\rightarrow$ mở thẻ **Variables** và khai báo 2 biến:

| Variable | Initial Value / Current Value | Ý nghĩa |
| :--- | :--- | :--- |
| `baseUrl` | `http://localhost:5050` | Địa chỉ máy chủ `Auction.Api` |
| `auctionId` | `11111111-1111-1111-1111-111111111111` | Mã GUID của phiên đấu giá đang chọn để test |

### 3.2. Danh sách 10 Request chuẩn trong Collection

| Tên lưu trong Collection | Method | URL |
| :--- | :---: | :--- |
| `01 - Health check` | `GET` | `{{baseUrl}}/` |
| `02 - List auctions` | `GET` | `{{baseUrl}}/auctions` |
| `03 - Create auction - valid` | `POST` | `{{baseUrl}}/auctions` |
| `04 - Create auction - blank name (400)` | `POST` | `{{baseUrl}}/auctions` |
| `05 - Create auction - invalid price <= 0 (400)` | `POST` | `{{baseUrl}}/auctions` |
| `06 - Get auction by ID - valid` | `GET` | `{{baseUrl}}/auctions/{{auctionId}}` |
| `07 - Place bid - valid (gRPC + RabbitMQ)` | `POST` | `{{baseUrl}}/auctions/{{auctionId}}/bids` |
| `08 - Place bid - rejected by gRPC (BID_TOO_LOW 400)` | `POST` | `{{baseUrl}}/auctions/{{auctionId}}/bids` |
| `09 - Get bid history` | `GET` | `{{baseUrl}}/auctions/{{auctionId}}/bids` |
| `10 - End auction & determine winner` | `POST` | `{{baseUrl}}/auctions/{{auctionId}}/end` |

---

## 04 | Kiểm thử API `GET /` (Request 01)

### 4.1. Cấu hình và gửi request
- **Method**: `GET`
- **URL**: `{{baseUrl}}/`
- **Body**: Để trống $\rightarrow$ Nhấn **Send**.

### 4.2. Kết quả cần quan sát
```json
{
  "service": "Auction.Api",
  "status": "running"
}
```
- **HTTP Status**: `200 OK`
- **Header đặc biệt**: Quan sát tab **Headers** của phần Response sẽ thấy `X-Correlation-ID: corr-...` do Middleware của `Auction.Api` tự động sinh.

### 4.3. Đoạn mã tự động kiểm tra (`Scripts` $\rightarrow$ `Post-response`)
```javascript
pm.test("GET / trả về 200", function () {
  pm.response.to.have.status(200);
});
pm.test("Tên dịch vụ và trạng thái đúng", function () {
  const data = pm.response.json();
  pm.expect(data.service).to.equal("Auction.Api");
  pm.expect(data.status).to.equal("running");
});
```

---

## 05 | Kiểm thử API `GET /auctions` (Request 02)

### 5.1. Gửi yêu cầu lấy danh sách phiên đấu giá
- **Method**: `GET`
- **URL**: `{{baseUrl}}/auctions`
- **Kỳ vọng**: Trả về `200 OK` kèm mảng JSON `[...]` chứa ít nhất 3 phiên đấu giá mẫu được khởi tạo sẵn trong Database.

### 5.2. Post-response script
```javascript
pm.test("GET /auctions trả về 200", function () {
  pm.response.to.have.status(200);
});
pm.test("Kết quả là mảng JSON các phiên đấu giá", function () {
  const data = pm.response.json();
  pm.expect(Array.isArray(data)).to.equal(true);
  pm.expect(data.length).to.be.greaterThan(0);
});
```

---

## 06 | Kiểm thử `POST /auctions` – Tạo phiên đấu giá hợp lệ (Request 03)

### 6.1. Cấu hình Postman
- **Method**: `POST`
- **URL**: `{{baseUrl}}/auctions`
- **Body** $\rightarrow$ chọn **raw** $\rightarrow$ chọn **JSON**, nhập nội dung:
```json
{
  "productName": "  Laptop ASUS ROG Zephyrus G16 2026  ",
  "productDescription": "Chính hãng bảo hành 24 tháng",
  "startingPrice": 50000000,
  "minIncrement": 1000000,
  "durationMinutes": 60
}
```

### 6.2. Kết quả trả về (`201 Created`)
```json
{
  "id": "a1b2c3d4-...",
  "productName": "Laptop ASUS ROG Zephyrus G16 2026",
  "productDescription": "Chính hãng bảo hành 24 tháng",
  "startingPrice": 50000000,
  "currentPrice": 50000000,
  "minIncrement": 1000000,
  "currentWinner": null,
  "totalBids": 0,
  "status": "Open",
  "isActive": true
}
```
- Lưu ý: Khoảng trắng thừa ở hai đầu `"  Laptop ASUS ROG Zephyrus G16 2026  "` đã được phương thức `.Trim()` trong C# cắt gọn!

### 6.3. Post-response script (Tự động lưu `auctionId` vừa tạo để dùng cho các bước đặt giá tiếp theo)
```javascript
pm.test("Tạo phiên đấu giá trả về 201 Created", function () {
  pm.response.to.have.status(201);
});
pm.test("Location header chứa đường dẫn phiên đấu giá", () => {
  const location = pm.response.headers.get("Location");
  pm.expect(location).to.include("/auctions/");
});
const response = pm.response.json();
pm.collectionVariables.set("auctionId", response.id);
```

---

## 07 | Kiểm thử `POST /auctions/{id}/bids` – Đặt giá hợp lệ & Bị từ chối bởi gRPC (Request 07 & 08)

### 7.1. Trường hợp Đặt giá HỢP LỆ (Request 07)
- **Method**: `POST`
- **URL**: `{{baseUrl}}/auctions/{{auctionId}}/bids`
- **Body (raw JSON)**:
```json
{
  "bidderName": "Trần Đức Nhân",
  "amount": 52000000
}
```
- **Kết quả kỳ vọng (`200 OK`)**:
```json
{
  "success": true,
  "message": "Đặt giá thành công! Mức giá mới đã được phát realtime tới toàn bộ người theo dõi.",
  "errorCode": null,
  "currentPrice": 52000000,
  "minimumNextBid": 53000000,
  "currentWinner": "Trần Đức Nhân",
  "correlationId": "corr-..."
}
```
- **Quan sát đồng thời trên Web App (`http://localhost:5052`)**: Ngay khoảnh khắc bạn nhấn **Send** trên Postman, giao diện Web sẽ nhận sự kiện `BidAccepted` từ **RabbitMQ $\rightarrow$ SignalR** và nhảy giá lên `52.000.000 ₫`!

### 7.2. Trường hợp Đặt giá THẤP HƠN BƯỚC GIÁ – Bị `BidValidation.Grpc` từ chối (Request 08)
- Giữ nguyên URL `{{baseUrl}}/auctions/{{auctionId}}/bids`, sửa Body thành `52100000` (trong khi bước giá tối thiểu là `1000000`, yêu cầu tối thiểu `53000000`):
```json
{
  "bidderName": "Lê Hoàng Nam",
  "amount": 52100000
}
```
- **Kết quả kỳ vọng (`400 Bad Request`)**:
```json
{
  "success": false,
  "message": "Mức giá đặt (52,100,000 VNĐ) thấp hơn mức tối thiểu hợp lệ (53,000,000 VNĐ). Bước giá tối thiểu là 1,000,000 VNĐ.",
  "errorCode": "BID_TOO_LOW",
  "currentPrice": 52000000,
  "minimumNextBid": 53000000,
  "currentWinner": "Trần Đức Nhân"
}
```

---

## 08 | Bảng tổng hợp các kịch bản kiểm thử biên (Boundary & Error Test Cases)

| Mã TC | Endpoint | Dữ liệu gửi lên | Kỳ vọng HTTP Status & Nội dung |
| :---: | :--- | :--- | :--- |
| **TC-01** | `GET /` | *(Không có body)* | `200 OK` + `"service": "Auction.Api"` |
| **TC-02** | `GET /auctions` | *(Không có body)* | `200 OK` + Mảng JSON các phiên đấu giá |
| **TC-03** | `POST /auctions` | `productName` hợp lệ, `startingPrice = 50000000` | `201 Created` + Header `Location` |
| **TC-04** | `POST /auctions` | `productName = "   "` (chỉ có khoảng trắng) | `400 Bad Request` + thông báo lỗi tên sản phẩm |
| **TC-05** | `POST /auctions` | `startingPrice = 0` hoặc `-50000` | `400 Bad Request` + thông báo lỗi giá khởi điểm |
| **TC-06** | `GET /auctions/{id}` | `{id}` là GUID có tồn tại | `200 OK` + Chi tiết phiên đấu giá |
| **TC-07** | `GET /auctions/{id}` | `{id} = 99999999-9999-9999-9999-999999999999` | `404 Not Found` + thông báo không tìm thấy |
| **TC-08** | `GET /auctions/abc` | `{id}` không phải định dạng GUID | `404 Not Found` (Không khớp route constraint `:guid`) |
| **TC-09** | `POST /auctions/{id}/bids` | `bidderName = "Trần Đức Nhân"`, `amount = 52000000` | `200 OK` + `success: true` + Phát sự kiện RabbitMQ |
| **TC-10** | `POST /auctions/{id}/bids` | `amount = 52100000` (`< CurrentPrice + MinIncrement`) | `400 Bad Request` + `errorCode: "BID_TOO_LOW"` từ gRPC |
| **TC-11** | `POST /auctions/{id}/bids` | `bidderName = ""` (rỗng) | `400 Bad Request` + `errorCode: "INVALID_BIDDER"` từ gRPC |
| **TC-12** | `POST /auctions/{id}/end` | Kết thúc phiên đấu giá rồi thử đặt giá tiếp | Khi kết thúc trả `200 OK` (`Closed`); nếu đặt giá tiếp sẽ nhận `400` (`AUCTION_CLOSED`) |

---

## 09 | Chạy tự động toàn bộ 10 Request bằng Collection Runner

1. Trong Postman Desktop, nhấp chuột phải vào Collection **`Online Auction API - Lab (Đề tài 10)`** $\rightarrow$ chọn **Run collection**.
2. Giữ nguyên thứ tự từ `01 - Health check` đến `10 - End auction & determine winner` (vì Request `03` sẽ tự động tạo phiên mới và gán biến `{{auctionId}}` cho các Request `06` $\rightarrow$ `10`!).
3. Để `Iterations = 1` $\rightarrow$ Nhấn nút **Run Online Auction API - Lab**.
4. **Kết quả đạt chuẩn**: Cả 10/10 Request đều báo **PASSED (Xanh lá 100%)**!
