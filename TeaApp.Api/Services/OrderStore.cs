using System.Collections.Concurrent;
using TeaApp.Api.GraphQL.Types;

namespace TeaApp.Api.Services;

public sealed class InMemoryOrderStore : IOrderStore
{
    private readonly ConcurrentDictionary<string, Order> _orders = new();

    public void Upsert(Order order) => _orders[order.OrderId] = order;

    public Order? Get(string orderId) =>
        _orders.TryGetValue(orderId, out var o) ? o : null;

    public IEnumerable<Order> GetAll() => _orders.Values;
}