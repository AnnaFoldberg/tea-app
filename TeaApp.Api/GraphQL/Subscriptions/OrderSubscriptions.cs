using HotChocolate.Authorization;
using TeaApp.Contracts;

namespace TeaApp.Api.GraphQL.Subscriptions;

[ExtendObjectType("Subscription")]
public sealed class OrderSubscriptions
{
    // Clients will subscribe to: subscription { brewing { orderId teaId startedAt } }
    [Authorize(Policy = "RequireApiScope")]
    [Subscribe]
    [Topic("orders/brewing")]
    public TeaOrderBrewing Brewing([EventMessage] TeaOrderBrewing evt) => evt;

    // Clients will subscribe to: subscription { brewed { orderId success finishedAt } }
    [Authorize(Policy = "RequireApiScope")]
    [Subscribe]
    [Topic("orders/brewed")]
    public TeaOrderBrewed Brewed([EventMessage] TeaOrderBrewed evt) => evt;
}