using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using TeaApp.Contracts;

namespace TeaApp.Api.Services;

public sealed class RabbitMqPublisher : IEventPublisher, IAsyncDisposable
{
    private readonly string _host, _user, _pass;
    private IConnection? _connection;

    // Ensures that only one caller at a time can enter the EnsureConnectionAsync
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public RabbitMqPublisher(string host, string user, string pass)
        => (_host, _user, _pass) = (host, user, pass);

    private async Task EnsureConnectionAsync(CancellationToken ct)
    {
        if (_connection is { IsOpen: true }) return;

        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connection is { IsOpen: true }) return;

            var factory = new ConnectionFactory { HostName = _host, UserName = _user, Password = _pass };
            _connection = await factory.CreateConnectionAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task PublishAsync<T>(string exchange, string routingKey, T message,
        ExchangeKind kind = ExchangeKind.Direct, CancellationToken ct = default)
    {
        await EnsureConnectionAsync(ct).ConfigureAwait(false);

        await using var channel = await _connection!.CreateChannelAsync().ConfigureAwait(false);

        // RabbitMQ's ExchangeType class is just constants, so we can just
        // use the enum name lower-cased.
        var type = kind.ToString().ToLowerInvariant();

        // Publish only to tea.orders
        await channel.ExchangeDeclareAsync(exchange, type: type, durable: true, autoDelete: false, arguments: null)
                .ConfigureAwait(false);

        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        var props = new BasicProperties { ContentType = "application/json", DeliveryMode = DeliveryModes.Persistent };

        await channel.BasicPublishAsync<BasicProperties>(exchange, routingKey, mandatory: false, basicProperties: props, body: payload, cancellationToken: ct)
                .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync().ConfigureAwait(false);

        _initLock.Dispose();
    }
}