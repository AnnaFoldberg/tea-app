using System.Collections.Concurrent;
using TeaApp.Api.GraphQL.Types;

namespace TeaApp.Api.Services;

/// <summary>
/// Simple thread-safe in-memory store for orders.
/// Uses a <see cref="ConcurrentDictionary{TKey,TValue}"/> to allow safe
/// reads and writes across multiple threads.
/// </summary>
public sealed class InMemoryOrderStore : IOrderStore
{
    // Backing dictionary: key = orderId, value = Order
    private readonly ConcurrentDictionary<string, Order> _orders = new();

    /// <summary>
    /// Inserts or updates an order in the store.
    /// </summary>
    /// <param name="order">The order to insert or update.</param>
    public void Upsert(Order order) => _orders[order.OrderId] = order;

    /// <summary>
    /// Retrieves an order by its ID, or null if not found.
    /// </summary>
    /// <param name="orderId">The ID of the order to look up.</param>
    /// <returns>The order if present, otherwise null.</returns>
    public Order? Get(string orderId) =>
        _orders.TryGetValue(orderId, out var o) ? o : null;

    /// <summary>
    /// Retrieves all orders currently stored.
    /// </summary>
    /// <returns>An enumerable of all orders.</returns>
    public IEnumerable<Order> GetAll() => _orders.Values;
}