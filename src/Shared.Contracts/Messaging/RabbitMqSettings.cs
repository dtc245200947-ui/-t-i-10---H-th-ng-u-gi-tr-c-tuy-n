namespace Shared.Contracts.Messaging;

public class RabbitMqSettings
{
    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string ExchangeName { get; set; } = "auction.events";
    public string DeadLetterExchangeName { get; set; } = "auction.dlx";
    public string HubQueueName { get; set; } = "auction.hub.queue";
    public string AuditQueueName { get; set; } = "auction.audit.queue";
    public string AuditDeadLetterQueueName { get; set; } = "auction.audit.dlq";
    public int MaxRetryCount { get; set; } = 3;
}

public static class CorrelationConstants
{
    public const string HeaderName = "X-Correlation-ID";
}
