using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using TeaApp.Contracts;

namespace TeaApp.Api.Services;

/// <summary>
/// RabbitMQ publisher that ensures a single shared connection and
/// publishes events to configured exchanges.
/// Implements <see cref="IEventPublisher"/> so domain services can
/// send integration events without knowing RabbitMQ details.
/// </summary>
public sealed class RabbitMqPublisher : IEventPublisher, IAsyncDisposable
{
    private readonly string _host, _user, _pass;
    private IConnection? _connection;

    // Ensures that only one caller at a time can initialize the connection
    private readonly SemaphoreSlim _initLock = new(1, 1);

    /// <summary>
    /// Creates a new RabbitMQ publisher with credentials and hostname.
    /// </summary>
    public RabbitMqPublisher(string host, string user, string pass)
        => (_host, _user, _pass) = (host, user, pass);

    /// <summary>
    /// Lazily initializes the RabbitMQ connection in a thread-safe manner.
    /// Ensures only one connection attempt happens at a time.
    /// </summary>
    private async Task EnsureConnectionAsync(CancellationToken ct)
    {
        // Already connected and open
        if (_connection is { IsOpen: true }) return;

        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connection is { IsOpen: true }) return;

            var factory = new ConnectionFactory { HostName = _host, UserName = _user, Password = _pass };

            // Establish new connection
            _connection = await factory.CreateConnectionAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Publishes a message to a RabbitMQ exchange with the given routing key.
    /// If the exchange does not exist, it is declared first.
    /// </summary>
    /// <typeparam name="T">Type of the message payload.</typeparam>
    /// <param name="exchange">Name of the exchange.</param>
    /// <param name="routingKey">Routing key for the message.</param>
    /// <param name="message">Message payload (serialized to JSON).</param>
    /// <param name="kind">Exchange type (direct, fanout, topic, etc.).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task PublishAsync<T>(string exchange, string routingKey, T message,
        ExchangeKind kind = ExchangeKind.Direct, CancellationToken ct = default)
    {
        // Ensure we have an active connection
        await EnsureConnectionAsync(ct).ConfigureAwait(false);

        // Open a lightweight channel for this publish operation
        await using var channel = await _connection!.CreateChannelAsync().ConfigureAwait(false);

        // RabbitMQ's ExchangeType class is just constants, so we can just
        // use the enum name lower-cased.
        var type = kind.ToString().ToLowerInvariant();

        // Declare the exchange (idempotent: safe to call repeatedly)
        await channel.ExchangeDeclareAsync(exchange, type: type, durable: true, autoDelete: false, arguments: null)
                .ConfigureAwait(false);

        // Serialize message to JSON payload
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));

        // Set content type and persistence (DeliveryMode = 2)
        var props = new BasicProperties { ContentType = "application/json", DeliveryMode = DeliveryModes.Persistent };

        // --- Publish message to exchange with routing key ---
        await channel.BasicPublishAsync<BasicProperties>(exchange, routingKey, mandatory: false, basicProperties: props, body: payload, cancellationToken: ct)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// Disposes the RabbitMQ connection and associated resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync().ConfigureAwait(false);

        _initLock.Dispose();
    }
}