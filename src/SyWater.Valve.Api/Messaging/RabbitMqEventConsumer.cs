using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using SyWater.Valve.Domain.Common;

namespace SyWater.Valve.Api.Messaging;

/// <summary>
/// Inbound adapter for RabbitMQ (like a controller, for events). Declares its durable queue bound to
/// "sywater.events" and hands each message to <see cref="HandleAsync"/>:
///   - handled            → ACK
///   - malformed / domain error (retrying will not help) → NACK without requeue → dead-letter queue
///   - anything else (database down…) → wait and NACK with requeue → it comes back later,
///     at most <see cref="MaxAttempts"/> times; then it goes to the dead-letter queue (no endless loop)
/// Envelope: {"id","type","occurredAt","data"}.
/// </summary>
public abstract class RabbitMqEventConsumer(IOptions<RabbitMqOptions> options, ILogger logger) : BackgroundService
{
    protected readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly RabbitMqOptions _options = options.Value;

    /// <summary>Failures of the same event (by envelope id) before it is dead-lettered.</summary>
    public const int MaxAttempts = 5;
    private readonly ConcurrentDictionary<string, int> _failures = new();

    /// <summary>Durable queue of this service, e.g. "valve.device-events".</summary>
    protected abstract string QueueName { get; }

    /// <summary>Event types (routing keys) this queue receives.</summary>
    protected abstract IReadOnlyList<string> RoutingKeys { get; }

    /// <summary>Process one event. Throw to retry later; throw DomainException/JsonException to discard.</summary>
    protected abstract Task HandleAsync(string type, JsonElement data, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Retry until the first connection works; after that, the client recovers by itself
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await new ConnectionFactory
                {
                    HostName = _options.Host,
                    Port = _options.Port,
                    UserName = _options.Username,
                    Password = _options.Password,
                    VirtualHost = _options.VirtualHost,
                    ClientProvidedName = QueueName,
                    AutomaticRecoveryEnabled = true,   // reconnects and re-subscribes after a broker restart
                    TopologyRecoveryEnabled = true,
                }.CreateConnectionAsync(stoppingToken);

                await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
                await DeclareTopologyAsync(channel, stoppingToken);
                await channel.BasicQosAsync(0, _options.Prefetch, false, stoppingToken);

                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += (_, delivery) => OnMessageAsync(channel, delivery, stoppingToken);
                await channel.BasicConsumeAsync(QueueName, autoAck: false, consumer, stoppingToken);

                logger.LogInformation("Consuming {Queue} ({Keys}) from RabbitMQ {Host}:{Port}",
                    QueueName, string.Join(", ", RoutingKeys), _options.Host, _options.Port);

                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning("RabbitMQ {Host}:{Port} not reachable ({Error}). Retrying in {Seconds}s",
                    _options.Host, _options.Port, ex.Message, _options.ReconnectSeconds);
                try { await Task.Delay(TimeSpan.FromSeconds(_options.ReconnectSeconds), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>Idempotent: every service declares the same exchange with the same settings.</summary>
    private async Task DeclareTopologyAsync(IChannel channel, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(_options.Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(_options.DeadLetterExchange, ExchangeType.Fanout, durable: true, autoDelete: false, cancellationToken: ct);
        await channel.QueueDeclareAsync(_options.DeadLetterQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await channel.QueueBindAsync(_options.DeadLetterQueue, _options.DeadLetterExchange, routingKey: "", cancellationToken: ct);

        await channel.QueueDeclareAsync(QueueName, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-dead-letter-exchange"] = _options.DeadLetterExchange },
            cancellationToken: ct);
        foreach (var key in RoutingKeys)
            await channel.QueueBindAsync(QueueName, _options.Exchange, key, cancellationToken: ct);
    }

    private async Task OnMessageAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        string type;
        string id;
        JsonElement data;
        try
        {
            using var json = JsonDocument.Parse(delivery.Body);
            type = json.RootElement.GetProperty("type").GetString() ?? delivery.RoutingKey;
            id = json.RootElement.TryGetProperty("id", out var idElement) ? idElement.ToString() : "";
            data = json.RootElement.GetProperty("data").Clone();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            logger.LogError("Malformed event on {Queue} sent to the dead-letter queue: {Error}", QueueName, ex.Message);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, ct);
            return;
        }

        try
        {
            await HandleAsync(type, data, ct);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct);
            _failures.TryRemove(id, out _);
        }
        catch (Exception ex) when (ex is DomainException or JsonException)
        {
            logger.LogError("Event {Type} rejected and sent to the dead-letter queue: {Error}", type, ex.Message);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var attempts = _failures.AddOrUpdate(id, 1, (_, n) => n + 1);
            if (attempts >= MaxAttempts)
            {
                _failures.TryRemove(id, out _);
                logger.LogError(ex, "Event {Type} failed {Attempts} times: sent to the dead-letter queue", type, attempts);
                await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, ct);
                return;
            }
            logger.LogWarning(ex, "Event {Type} failed (attempt {Attempts}); it will be retried", type, attempts);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);   // do not spin while the database is down
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, ct);
        }
    }
}
