using Microsoft.AspNetCore.SignalR;

namespace Auction.Hub.Hubs;

public class AuctionLiveHub : Microsoft.AspNetCore.SignalR.Hub
{
    private readonly ILogger<AuctionLiveHub> _logger;

    public AuctionLiveHub(ILogger<AuctionLiveHub> logger)
    {
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("[SignalR-Hub] Client mới đã kết nối: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("[SignalR-Hub] Client ngắt kết nối: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public async Task JoinAuction(string auctionId)
    {
        var groupName = GetGroupName(auctionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        _logger.LogInformation("[SignalR-Hub] Client {ConnectionId} đã tham gia theo dõi phiên đấu giá {AuctionId}", Context.ConnectionId, auctionId);

        await Clients.Caller.SendAsync("JoinedAuctionRoom", new
        {
            auctionId,
            connectionId = Context.ConnectionId,
            joinedAtUtc = DateTime.UtcNow,
            message = $"Đã tham gia theo dõi phiên đấu giá {auctionId} trực tiếp qua SignalR."
        });
    }

    public async Task LeaveAuction(string auctionId)
    {
        var groupName = GetGroupName(auctionId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
        _logger.LogInformation("[SignalR-Hub] Client {ConnectionId} đã rời phiên đấu giá {AuctionId}", Context.ConnectionId, auctionId);
    }

    public static string GetGroupName(string auctionId) => $"auction:{auctionId.ToLowerInvariant()}";
}
