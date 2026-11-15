# Script dừng các tiến trình dịch vụ đang chạy của Hệ thống Đấu giá Trực tuyến
Write-Host "Đang dừng các tiến trình dịch vụ .NET của hệ thống đấu giá..." -ForegroundColor Yellow
$processes = @("BidValidation.Grpc", "Auction.Api", "Auction.Hub", "Audit.Worker", "Bidder.ConsoleClient")
foreach ($p in $processes) {
    Get-Process -Name $p -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
Write-Host "✅ Đã dừng các dịch vụ." -ForegroundColor Green
