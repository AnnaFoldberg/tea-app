using HotChocolate.Authorization;
using TeaApp.Api.GraphQL.Types;

namespace TeaApp.Api.GraphQL.Queries;

[ExtendObjectType("Query")]
public class TeaQueries
{
    [Authorize(Policy = "RequireApiScope")]
    public IEnumerable<Tea> Teas() => new[]
    {
        new Tea("earl-grey",  "Earl Grey",  40),
        new Tea("sencha",     "Sencha",     30),
        new Tea("peppermint", "Peppermint", 0),
    };
}