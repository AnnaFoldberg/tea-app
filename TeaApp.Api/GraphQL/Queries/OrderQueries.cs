using HotChocolate.Authorization;
using TeaApp.Api.GraphQL.Types;
using TeaApp.Api.Services;

namespace TeaApp.Api.GraphQL.Queries;

[ExtendObjectType("Query")]
public class OrderQueries
{
    private readonly IOrderStore _store;

    public OrderQueries(IOrderStore store)
    {
        _store = store;
    }

    [Authorize(Policy = "RequireApiScope")]
    public Order? OrderById(string orderId) => _store.Get(orderId);

    [Authorize(Policy = "RequireApiScope")]
    public IEnumerable<Order> Orders() => _store.GetAll();
}