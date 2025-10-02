using System.Text.Json;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using HotChocolate.Subscriptions;
using TeaApp.Contracts;

namespace TeaApp.Api.Services;

/// <summary>
/// Background service that bridges RabbitMQ events into GraphQL subscriptions.
/// Consumes tea brewing/brewed events from RabbitMQ exchanges and republishes
/// them as HotChocolate subscription events via ITopicEventSender.
/// </summary>
public sealed class RabbitToSubscriptions : BackgroundService
{
    private readonly ITopicEventSender _pub;
    private readonly string _host, _user, _pass;

    // RabbitMQ exchange names (Brewer publishes here)
    private const string BrewingExchange = "tea.brewing", BrewedExchange = "tea.brewed";

    // API-specific queues bound to the exchanges
    private const string ApiBrewingQueue = "api.subs.brewing", ApiBrewedQueue = "api.subs.brewed";

    // HotChocolate subscription topics (must match [Topic(...)] attributes in resolvers)
    private const string BrewingTopic = "orders/brewing", BrewedTopic = "orders/brewed";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public RabbitToSubscriptions(ITopicEventSender pub, string host, string user, string pass)
    {
        _pub = pub;
        (_host, _user, _pass) = (host, user, pass);
    }

    /// <summary>
    /// Main worker loop: connects to RabbitMQ, consumes from brewing/brewed queues,
    /// and forwards deserialized events into GraphQL subscription topics.
    /// </summary>
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
                // Establish connection & channel to RabbitMQ
                await using var conn = await factory.CreateConnectionAsync(ct);
                await using var ch = await conn.CreateChannelAsync();

                // --- Ensure exchanges and queues exist ---
                await ch.ExchangeDeclareAsync(BrewingExchange, ExchangeType.Fanout, durable: true);
                await ch.ExchangeDeclareAsync(BrewedExchange, ExchangeType.Fanout, durable: true);

                await ch.QueueDeclareAsync(ApiBrewingQueue, durable: true, exclusive: false, autoDelete: false);
                await ch.QueueDeclareAsync(ApiBrewedQueue, durable: true, exclusive: false, autoDelete: false);

                await ch.QueueBindAsync(ApiBrewingQueue, BrewingExchange, "");
                await ch.QueueBindAsync(ApiBrewedQueue, BrewedExchange, "");

                // --- Consumer for brewing events ---
                var brewing = new AsyncEventingBasicConsumer(ch);
                brewing.ReceivedAsync += async (_, ea) =>
                {
                    // Deserialize RabbitMQ message to contract
                    var evt = JsonSerializer.Deserialize<TeaOrderBrewing>(ea.Body.Span, JsonOpts);
                    if (evt is not null)
                    {
                        // Forward into GraphQL subscription pipeline
                        await _pub.SendAsync("orders/brewing", evt, ct);
                    }
                };

                // --- Consumer for brewed events ---
                var brewed = new AsyncEventingBasicConsumer(ch);
                brewed.ReceivedAsync += async (_, ea) =>
                {
                    var evt = JsonSerializer.Deserialize<TeaOrderBrewed>(ea.Body.Span, JsonOpts);
                    if (evt is not null) await _pub.SendAsync("orders/brewed", evt, ct);
                };

                // Start consuming both queues (autoAck enabled)
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
                // On failure: wait a bit and retry
                try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch { }
            }
        }
    }
}