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

                if (!await db.Auctions.AnyAsync())
                {
                    var now = DateTime.UtcNow;
                    var sampleAuctions = new List<AuctionItem>
                    {
                        new()
                        {
                            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                            ProductName = "MacBook Pro 16-inch M4 Max (64GB RAM, 2TB SSD)",
                            ProductDescription = "Máy trạm di động hiệu năng cao nguyên seal, bảo hành chính hãng Apple Care+ 3 năm.",
                            ImageUrl = "https://images.unsplash.com/photo-1517336714731-489689fd1ca8?w=800&auto=format&fit=crop&q=80",
                            StartingPrice = 45_000_000m,
                            CurrentPrice = 45_000_000m,
                            MinIncrement = 500_000m,
                            CurrentWinner = null,
                            TotalBids = 0,
                            StartTimeUtc = now.AddMinutes(-15),
                            EndTimeUtc = now.AddHours(4),
                            Status = "Open",
                            CreatedAtUtc = now.AddMinutes(-15)
                        },
                        new()
                        {
                            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                            ProductName = "Đồng hồ cơ Omega Speedmaster Moonwatch Professional",
                            ProductDescription = "Phiên bản kỷ niệm Apollo 11, mặt kính Sapphire nguyên khối, đầy đủ hộp sổ thẻ quốc tế.",
                            ImageUrl = "https://images.unsplash.com/photo-1523275335684-37898b6baf30?w=800&auto=format&fit=crop&q=80",
                            StartingPrice = 120_000_000m,
                            CurrentPrice = 125_000_000m,
                            MinIncrement = 1_000_000m,
                            CurrentWinner = "Nguyễn Minh Quân",
                            TotalBids = 1,
                            StartTimeUtc = now.AddMinutes(-30),
                            EndTimeUtc = now.AddHours(2),
                            Status = "Open",
                            CreatedAtUtc = now.AddMinutes(-30),
                            Bids = new List<BidRecord>
                            {
                                new()
                                {
                                    Id = Guid.NewGuid(),
                                    AuctionId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                                    BidderName = "Nguyễn Minh Quân",
                                    Amount = 125_000_000m,
                                    PlacedAtUtc = now.AddMinutes(-10),
                                    CorrelationId = "seed-corr-001"
                                }
                            }
                        },
                        new()
                        {
                            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                            ProductName = "Bàn phím cơ Custom Keycult No.2 Rev.1 Brass Edition",
                            ProductDescription = "Bàn phím cơ giới hạn 50 chiếc toàn cầu, lắp sẵn switch Nixies vintage đã lube & film.",
                            ImageUrl = "https://images.unsplash.com/photo-1595225476474-87563907a212?w=800&auto=format&fit=crop&q=80",
                            StartingPrice = 25_000_000m,
                            CurrentPrice = 25_000_000m,
                            MinIncrement = 200_000m,
                            CurrentWinner = null,
                            TotalBids = 0,
                            StartTimeUtc = now.AddMinutes(-5),
                            EndTimeUtc = now.AddMinutes(45),
                            Status = "Open",
                            CreatedAtUtc = now.AddMinutes(-5)
                        }
                    };

                    db.Auctions.AddRange(sampleAuctions);
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
