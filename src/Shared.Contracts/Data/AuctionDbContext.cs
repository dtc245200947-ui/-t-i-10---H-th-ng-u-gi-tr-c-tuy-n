using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shared.Contracts.Models;

namespace Shared.Contracts.Data;

public class AuctionDbContext : DbContext
{
    public AuctionDbContext(DbContextOptions<AuctionDbContext> options) : base(options)
    {
    }

    public DbSet<AuctionItem> Auctions => Set<AuctionItem>();
    public DbSet<BidRecord> Bids => Set<BidRecord>();
    public DbSet<AuditLogEntry> AuditLogs => Set<AuditLogEntry>();
    public DbSet<OutboxEventMessage> OutboxEvents => Set<OutboxEventMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AuctionItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.StartingPrice).HasPrecision(18, 2);
            entity.Property(e => e.CurrentPrice).HasPrecision(18, 2);
            entity.Property(e => e.MinIncrement).HasPrecision(18, 2);
            entity.HasMany(e => e.Bids)
                .WithOne()
                .HasForeignKey(b => b.AuctionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BidRecord>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.HasIndex(e => e.AuctionId);
        });

        modelBuilder.Entity<AuditLogEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            // Đảm bảo Idempotent Consumer dựa trên EventId
            entity.HasIndex(e => e.EventId).IsUnique();
            entity.HasIndex(e => e.AuctionId);
        });

        modelBuilder.Entity<OutboxEventMessage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.EventId).IsUnique();
        });
    }
}

public static class DatabaseExtensions
{
    public static IServiceCollection AddAuctionDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration["Database:Provider"] ?? "Sqlite";
        var connectionString = configuration.GetConnectionString("AuctionDb");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Mặc định dùng file SQLite dùng chung tại thư mục gốc data/auction_system.db khi chạy local
            var rootDataDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data"));
            Directory.CreateDirectory(rootDataDir);
            var dbPath = Path.Combine(rootDataDir, "auction_system.db");
            connectionString = $"Data Source={dbPath};Cache=Shared";
            provider = "Sqlite";
        }

        services.AddDbContext<AuctionDbContext>(options =>
        {
            if (provider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase) ||
                provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
            {
                options.UseNpgsql(connectionString);
            }
            else
            {
                options.UseSqlite(connectionString);
            }
        });

        return services;
    }

    public static async Task EnsureAuctionDatabaseSeededAsync(this IServiceProvider services)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AuctionDbContext>();
                await db.Database.EnsureCreatedAsync();

                // Bật chế độ WAL cho SQLite để nhiều process (API, Hub, Worker) truy cập đồng thời mượt mà
                if (db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true)
                {
                    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;");
                }

                var now = DateTime.UtcNow;
                var sampleAuctions = new List<AuctionItem>
                {
                    new()
                    {
                        Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                        ProductName = "MacBook Pro 16-inch M4 Max (64GB RAM, 2TB SSD)",
                        ProductDescription = "[Công nghệ] Máy trạm di động hiệu năng đỉnh cao nguyên seal Apple VN/A, màn hình Liquid Retina XDR, kèm gói AppleCare+ 3 năm.",
                        ImageUrl = "https://images.unsplash.com/photo-1517336714731-489689fd1ca8?w=900&auto=format&fit=crop&q=80",
                        StartingPrice = 45_000_000m,
                        CurrentPrice = 48_500_000m,
                        MinIncrement = 500_000m,
                        CurrentWinner = "Trần Đức Nhân",
                        TotalBids = 3,
                        StartTimeUtc = now.AddMinutes(-20),
                        EndTimeUtc = now.AddHours(4),
                        Status = "Open",
                        CreatedAtUtc = now.AddMinutes(-20)
                    },
                    new()
                    {
                        Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                        ProductName = "Đồng hồ cơ Omega Speedmaster Moonwatch Professional",
                        ProductDescription = "[Đồng hồ xa xỉ] Phiên bản kỷ niệm Apollo 11, bộ máy Co-Axial Master Chronometer Calibre 3861, mặt kính Sapphire nguyên khối, đầy đủ hộp sổ thẻ quốc tế.",
                        ImageUrl = "https://images.unsplash.com/photo-1523275335684-37898b6baf30?w=900&auto=format&fit=crop&q=80",
                        StartingPrice = 120_000_000m,
                        CurrentPrice = 125_000_000m,
                        MinIncrement = 1_000_000m,
                        CurrentWinner = "Nguyễn Minh Quân",
                        TotalBids = 2,
                        StartTimeUtc = now.AddMinutes(-30),
                        EndTimeUtc = now.AddHours(2),
                        Status = "Open",
                        CreatedAtUtc = now.AddMinutes(-30)
                    },
                    new()
                    {
                        Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                        ProductName = "Bàn phím cơ Custom Keycult No.2 Rev.1 Brass Edition",
                        ProductDescription = "[Công nghệ] Bàn phím cơ giới hạn 50 chiếc toàn cầu, khung nhôm CNC mạ đồng nguyên khối, lắp sẵn switch Cherry MX Black Vintage.",
                        ImageUrl = "https://images.unsplash.com/photo-1595225476474-87563907a212?w=900&auto=format&fit=crop&q=80",
                        StartingPrice = 25_000_000m,
                        CurrentPrice = 26_200_000m,
                        MinIncrement = 200_000m,
                        CurrentWinner = "Phạm Minh Tuấn",
                        TotalBids = 4,
                        StartTimeUtc = now.AddMinutes(-15),
                        EndTimeUtc = now.AddHours(3),
                        Status = "Open",
                        CreatedAtUtc = now.AddMinutes(-15)
                    },
                    new()
                    {
                        Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                        ProductName = "Rolex Cosmograph Daytona 18K Everose Gold",
                        ProductDescription = "[Đồng hồ xa xỉ] Tuyệt tác đồng hồ bấm giờ thể thao bằng vàng hồng 18K nguyên khối, vành bezel Cerachrom đen, chứng nhận Superlative Chronometer.",
                        ImageUrl = "https://images.unsplash.com/photo-1547996160-81dfa63595aa?w=900&auto=format&fit=crop&q=80",
                        StartingPrice = 680_000_000m,
                        CurrentPrice = 715_000_000m,
                        MinIncrement = 5_000_000m,
                        CurrentWinner = "Vũ Quốc Khánh",
                        TotalBids = 6,
                        StartTimeUtc = now.AddMinutes(-45),
                        EndTimeUtc = now.AddHours(5),
                        Status = "Open",
                        CreatedAtUtc = now.AddMinutes(-45)
                    },
                    new()
                    {
                        Id = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                        ProductName = "Siêu xe Porsche 911 GT3 RS (992) Weissach Package",
                        ProductDescription = "[Siêu xe] Động cơ 4.0L hút khí tự nhiên 518 mã lực, gói khí động học sợi carbon Weissach, ODO 1.200 km, đầy đủ hồ sơ kiểm định chính hãng.",
                        ImageUrl = "https://images.unsplash.com/photo-1503376780353-7e6692767b70?w=900&auto=format&fit=crop&q=80",
                        StartingPrice = 12_500_000_000m,
                        CurrentPrice = 12_850_000_000m,
                        MinIncrement = 50_000_000m,
                        CurrentWinner = "Đỗ Hải Đăng",
                        TotalBids = 5,
                        StartTimeUtc = now.AddHours(-1),
                        EndTimeUtc = now.AddHours(8),
                        Status = "Open",
                        CreatedAtUtc = now.AddHours(-1)
                    },
                    new()
                    {
                        Id = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                        ProductName = "Máy ảnh Leica M11 Monochrom + Lens Summilux-M 50mm f/1.4",
                        ProductDescription = "[Công nghệ] Máy ảnh rangefinder cảm biến BSI CMOS 60MP chuyên chụp ảnh đen trắng nghệ thuật, thân máy nhôm nguyên khối sản xuất tại Đức.",
                        ImageUrl = "https://images.unsplash.com/photo-1516035069371-29a1b244cc32?w=900&auto=format&fit=crop&q=80",
                        StartingPrice = 185_000_000m,
                        CurrentPrice = 192_000_000m,
                        MinIncrement = 1_000_000m,
                        CurrentWinner = "Nguyễn Thảo Vy",
                        TotalBids = 3,
                        StartTimeUtc = now.AddMinutes(-25),
                        EndTimeUtc = now.AddHours(3),
                        Status = "Open",
                        CreatedAtUtc = now.AddMinutes(-25)
                    },
                    new()
                    {
                        Id = Guid.Parse("77777777-7777-7777-7777-777777777777"),
                        ProductName = "Dây chuyền Kim cương Tự nhiên GIA 5.2 Carat VVS1 Platinum",
                        ProductDescription = "[Trang sức] Kim cương tự nhiên nước D độ tinh khiết VVS1 giác cắt Excellent, chế tác trên nền bạch kim PT950 kèm giấy kiểm định quốc tế GIA.",
                        ImageUrl = "https://images.unsplash.com/photo-1515562141207-7a88fb7ce338?w=900&auto=format&fit=crop&q=80",
                        StartingPrice = 320_000_000m,
                        CurrentPrice = 335_000_000m,
                        MinIncrement = 5_000_000m,
                        CurrentWinner = "Lê Minh Châu",
                        TotalBids = 3,
                        StartTimeUtc = now.AddMinutes(-40),
                        EndTimeUtc = now.AddHours(6),
                        Status = "Open",
                        CreatedAtUtc = now.AddMinutes(-40)
                    },
                    new()
                    {
                        Id = Guid.Parse("88888888-8888-8888-8888-888888888888"),
                        ProductName = "Card đồ họa ASUS ROG Astral RTX 5090 32GB GDDR7 OC",
                        ProductDescription = "[Công nghệ] GPU flagship thế hệ mới nhất, thiết kế 4 quạt tản nhiệt buồng hơi, nguyên seal chính hãng bảo hành 36 tháng.",
                        ImageUrl = "https://images.unsplash.com/photo-1591488320449-011701bb6704?w=900&auto=format&fit=crop&q=80",
                        StartingPrice = 62_000_000m,
                        CurrentPrice = 65_000_000m,
                        MinIncrement = 1_000_000m,
                        CurrentWinner = "Trần Đức Nhân",
                        TotalBids = 3,
                        StartTimeUtc = now.AddMinutes(-10),
                        EndTimeUtc = now.AddHours(4),
                        Status = "Open",
                        CreatedAtUtc = now.AddMinutes(-10)
                    }
                };

                var existingIds = await db.Auctions.Select(a => a.Id).ToListAsync();
                var missingAuctions = sampleAuctions.Where(s => !existingIds.Contains(s.Id)).ToList();
                if (missingAuctions.Count > 0)
                {
                    db.Auctions.AddRange(missingAuctions);
                    await db.SaveChangesAsync();
                }

                break;
            }
            catch (Exception) when (attempt < 5)
            {
                await Task.Delay(300 * attempt);
            }
        }
    }
}
