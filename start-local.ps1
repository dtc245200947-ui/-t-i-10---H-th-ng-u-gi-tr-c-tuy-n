# Script khởi chạy toàn bộ Hệ thống Đấu giá Trực tuyến trên môi trường Local (.NET 10)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host "  KHỞI CHẠY HỆ THỐNG ĐẤU GIÁ TRỰC TUYẾN (ĐỀ TÀI 10 - .NET 10)" -ForegroundColor Cyan
Write-Host "=================================================================" -ForegroundColor Cyan

# Tạo thư mục lưu log và database dùng chung
New-Item -ItemType Directory -Force -Path "$root\logs" | Out-Null
New-Item -ItemType Directory -Force -Path "$root\data" | Out-Null

Write-Host "[1/4] Đang khởi động BidValidation.Grpc (Cổng gRPC: 5051, Health: 5151)..." -ForegroundColor Yellow
Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd '$root'; dotnet run --project src/BidValidation.Grpc/BidValidation.Grpc.csproj"

Start-Sleep -Seconds 2

Write-Host "[2/4] Đang khởi động Auction.Api (REST Minimal API - Cổng: 5050)..." -ForegroundColor Yellow
Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd '$root'; dotnet run --project src/Auction.Api/Auction.Api.csproj"

Start-Sleep -Seconds 2

Write-Host "[3/4] Đang khởi động Auction.Hub (SignalR Realtime & Web App - Cổng: 5052)..." -ForegroundColor Yellow
Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd '$root'; dotnet run --project src/Auction.Hub/Auction.Hub.csproj"

Start-Sleep -Seconds 2

Write-Host "[4/4] Đang khởi động Audit.Worker (RabbitMQ Consumer + Idempotent Audit Log)..." -ForegroundColor Yellow
Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd '$root'; dotnet run --project src/Audit.Worker/Audit.Worker.csproj"

Start-Sleep -Seconds 4

Write-Host "`n✅ Toàn bộ 4 dịch vụ phân tán đã được khởi chạy!" -ForegroundColor Green
Write-Host " 🌐 Giao diện Web App (Bidder Portal): http://localhost:5052" -ForegroundColor Cyan
Write-Host " 🔌 Auction REST API                : http://localhost:5050/auctions" -ForegroundColor Cyan
Write-Host " ⚡ BidValidation gRPC Service      : http://localhost:5051 (Health: http://localhost:5151/health)" -ForegroundColor Cyan
Write-Host "`nĐang mở trình duyệt Web App tại http://localhost:5052 ..." -ForegroundColor Green
Start-Process "http://localhost:5052"
