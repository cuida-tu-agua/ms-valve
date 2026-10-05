namespace SyWater.Valve.Api.Messaging;

/// <summary>Section "RabbitMq" of appsettings. The password goes in user-secrets, never in git.</summary>
public sealed class RabbitMqOptions
{
    public const string Section = "RabbitMq";

    /// <summary>Empty = do not consume events (the API still answers with what is already stored).</summary>
    public string Host { get; init; } = "";
    public int Port { get; init; } = 5672;
    public string Username { get; init; } = "sywater";
    public string Password { get; init; } = "";
    public string VirtualHost { get; init; } = "/";
    public string Exchange { get; init; } = "sywater.events";

    /// <summary>Messages that cannot be processed end here (fanout exchange + queue "sywater.dead-letters").</summary>
    public string DeadLetterExchange { get; init; } = "sywater.dlx";
    public string DeadLetterQueue { get; init; } = "sywater.dead-letters";

    public ushort Prefetch { get; init; } = 20;
    public int ReconnectSeconds { get; init; } = 5;
}
