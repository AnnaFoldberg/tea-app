using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using TeaApp.Contracts;

namespace TeaApp.Api.Services;

public interface IEventPublisher
{
    Task PublishAsync<T>(string exchange, string routingKey, T message, ExchangeKind kind = ExchangeKind.Direct, CancellationToken ct = default);
}
