using System.Text.Json;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using HotChocolate.Subscriptions;
using TeaApp.Contracts;

namespace TeaApp.Api.Services;

public sealed class RabbitToSubscriptions : BackgroundService
{
    private readonly ITopicEventSender _pub;
    private readonly string _host, _user, _pass;

    private const string BrewingExchange = "tea.brewing", BrewedExchange = "tea.brewed";
    private const string ApiBrewingQueue = "api.subs.brewing", ApiBrewedQueue = "api.subs.brewed";
    private const string BrewingTopic = "orders/brewing", BrewedTopic = "orders/brewed";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public RabbitToSubscriptions(ITopicEventSender pub, string host, string user, string pass)
    {
        _pub = pub;
        (_host, _user, _pass) = (host, user, pass);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var factory = new ConnectionFactory
        {
            HostName = _host,
            UserName = _user,
            Password = _pass
        };

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var conn = await factory.CreateConnectionAsync(ct);
                await using var ch = await conn.CreateChannelAsync();

                // ensure exchanges/queues exist
                await ch.ExchangeDeclareAsync(BrewingExchange, ExchangeType.Fanout, durable: true);
                await ch.ExchangeDeclareAsync(BrewedExchange, ExchangeType.Fanout, durable: true);

                await ch.QueueDeclareAsync(ApiBrewingQueue, durable: true, exclusive: false, autoDelete: false);
                await ch.QueueDeclareAsync(ApiBrewedQueue, durable: true, exclusive: false, autoDelete: false);

                await ch.QueueBindAsync(ApiBrewingQueue, BrewingExchange, "");
                await ch.QueueBindAsync(ApiBrewedQueue, BrewedExchange, "");

                var brewing = new AsyncEventingBasicConsumer(ch);
                brewing.ReceivedAsync += async (_, ea) =>
                {
                    var evt = JsonSerializer.Deserialize<TeaOrderBrewing>(ea.Body.Span, JsonOpts);
                    if (evt is not null) await _pub.SendAsync("orders/brewing", evt, ct);
                };

                var brewed = new AsyncEventingBasicConsumer(ch);
                brewed.ReceivedAsync += async (_, ea) =>
                {
                    var evt = JsonSerializer.Deserialize<TeaOrderBrewed>(ea.Body.Span, JsonOpts);
                    if (evt is not null) await _pub.SendAsync("orders/brewed", evt, ct);
                };

                await ch.BasicConsumeAsync(ApiBrewingQueue, autoAck: true, brewing, ct);
                await ch.BasicConsumeAsync(ApiBrewedQueue, autoAck: true, brewed, ct);

                // Stay alive until shutdown or the connection drops
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break; // normal shutdown
            }
            catch
            {
                // Backoff a bit, then retry connecting
                try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch { }
            }
        }
    }
}