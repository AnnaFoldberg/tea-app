using System.Collections.Concurrent;
using TeaApp.Api.GraphQL.Types;

namespace TeaApp.Api.Services;

public interface IOrderStore
{
    void Upsert(Order order);
    Order? Get(string orderId);
    IEnumerable<Order> GetAll();
}