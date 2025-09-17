using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using TeaApp.Contracts;

namespace TeaApp.Brewer.Worker;

public sealed class Worker : BackgroundService
{
    private readonly string _host, _user, _pass;

    private const string OrdersExchange = "tea.orders"; // direct
    private const string OrdersKey = "order.placed"; // exact-match routing
    private const string OrdersQueue = "brew.orders"; // this worker's queue
    private const string BrewingExchange = "tea.brewing"; // fanout
    private const string BrewedExchange = "tea.brewed"; // fanout

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public Worker(string host, string user, string pass)
        => (_host, _user, _pass) = (host, user, pass);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var factory = new ConnectionFactory { HostName = _host, UserName = _user, Password = _pass };

        await using var connection = await factory.CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync();

        // Declare exchanges/queue/bindings (idempotent)
        await channel.ExchangeDeclareAsync(OrdersExchange,  ExchangeType.Direct, durable: true);
        await channel.ExchangeDeclareAsync(BrewingExchange, ExchangeType.Fanout, durable: true);
        await channel.ExchangeDeclareAsync(BrewedExchange,  ExchangeType.Fanout, durable: true);

        await channel.QueueDeclareAsync(OrdersQueue, durable: true, exclusive: false, autoDelete: false);
        await channel.QueueBindAsync(OrdersQueue, OrdersExchange, OrdersKey);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, eventArgs) =>
        {
            var placed = JsonSerializer.Deserialize<TeaOrderPlaced>(eventArgs.Body.Span, JsonOpts);
            if (placed is null) return;

            // Common message props
            var props = new BasicProperties { ContentType = "application/json", DeliveryMode = DeliveryModes.Persistent };

            // 1) Publish “brewing started”
            var started = new TeaOrderBrewing(placed.OrderId, placed.TeaId, DateTimeOffset.UtcNow);
            var startedBody = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(started, JsonOpts));
            await channel.BasicPublishAsync<BasicProperties>(BrewingExchange, routingKey: "", false, props, startedBody, ct);

            // 2) Simulate brew progress heartbeats
            for (var i = 0; i < 5; i++)
            {
                await Task.Delay(1000, ct);
                var beat = new TeaOrderBrewing(placed.OrderId, placed.TeaId, DateTimeOffset.UtcNow);
                var beatBody = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(beat, JsonOpts));
                await channel.BasicPublishAsync<BasicProperties>(BrewingExchange, "", false, props, beatBody, ct);
            }

            // 3) Publish “brewed”
            var brewed = new TeaOrderBrewed(placed.OrderId, true, DateTimeOffset.UtcNow);
            var brewedBody = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(brewed, JsonOpts));
            await channel.BasicPublishAsync<BasicProperties>(BrewedExchange, "", false, props, brewedBody, ct);
        };

        // Ultra-minimal: autoAck (no manual ack/nack)
        await channel.BasicConsumeAsync(OrdersQueue, autoAck: true, consumer, ct);

        // Keep the service alive
        await Task.Delay(Timeout.Infinite, ct);
    }
}