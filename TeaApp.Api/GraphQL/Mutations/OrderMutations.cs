using HotChocolate.Authorization;
using TeaApp.Api.GraphQL.Types;
using TeaApp.Api.Services;
using TeaApp.Contracts;

namespace TeaApp.Api.GraphQL.Mutations;

[ExtendObjectType("Mutation")]
public class OrderMutations
{
    private readonly IEventPublisher _publisher;
    private readonly IOrderStore _store;

    public OrderMutations(IEventPublisher publisher, IOrderStore store)
    {
        _publisher = publisher;
        _store = store;
    }

    [Authorize(Policy = "RequireApiScope")]
    public async Task<OrderAccepted> PlaceTeaOrder(string teaId, CancellationToken ct)
    {
        var orderId = Guid.NewGuid().ToString("n");

        // Publish event to RabbitMQ (Brewer will pick it up)
        await _publisher.PublishAsync("tea.orders", "order.placed",
            new TeaOrderPlaced(orderId, teaId), ct: ct);

        _store.Upsert(new Order(orderId, teaId, "true"));

        // Return response to client immediately
        return new OrderAccepted(orderId, true);
    }
}