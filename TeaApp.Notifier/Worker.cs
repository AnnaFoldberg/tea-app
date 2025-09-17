using System.Text.Json;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using TeaApp.Contracts;

namespace TeaApp.Notifier.Worker;

public sealed class Worker : BackgroundService
{
    private readonly string _host, _user, _pass;

    private const string BrewedExchange = "tea.brewed"; // fanout
    private const string BrewedQueue = "notifier.brewed"; // this worker's queue

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public Worker(string host, string user, string pass)
        => (_host, _user, _pass) = (host, user, pass);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var factory = new ConnectionFactory { HostName = _host, UserName = _user, Password = _pass };
        await using var connection = await factory.CreateConnectionAsync(ct);
        await using var channel    = await connection.CreateChannelAsync();

        // Declare exchange + queue
        await channel.ExchangeDeclareAsync(BrewedExchange, ExchangeType.Fanout, durable: true);
        await channel.QueueDeclareAsync(BrewedQueue, durable: true, exclusive: false, autoDelete: false);
        await channel.QueueBindAsync(BrewedQueue, BrewedExchange, "");

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, eventArgs) =>
        {
            var brewed = JsonSerializer.Deserialize<TeaOrderBrewed>(eventArgs.Body.Span, JsonOpts);
            if (brewed is not null)
            {
                // minimal: just ack (no real action yet)
                await channel.BasicAckAsync(eventArgs.DeliveryTag, false, ct);
            }
            else
            {
                await channel.BasicNackAsync(eventArgs.DeliveryTag, false, false, ct);
            }
        };

        await channel.BasicConsumeAsync(BrewedQueue, autoAck: false, consumer: consumer, cancellationToken: ct);

        // Keep service alive
        await Task.Delay(Timeout.Infinite, ct);
    }
}