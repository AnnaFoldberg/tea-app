using Microsoft.Identity.Client;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.IdentityModel.Tokens.Jwt;

// ---------- DTOs ----------
public record Tea(string Id, string Name, int CaffeineMg);
public record TeasData(Tea[] Teas);

public record OrderAccepted(string Id, bool Accepted);
public record PlaceOrderData(OrderAccepted PlaceTeaOrder);

public record Order(string Id);
public record OrderByIdData(Order? OrderById);
public record OrdersData(Order[] Orders);
internal class Program
{
    private static async Task Main()
    {
        // Config from env (fail fast)
        DotNetEnv.Env.Load(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".env"));

        static string RequireEnv(string name) =>
            Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"Missing environment variable: {name}");

        var tenantId = RequireEnv("AZUREAD__TENANTID");
        var clientId = RequireEnv("CLIENT_AZUREAD_CLIENTID");
        var audience = RequireEnv("AZUREAD__AUDIENCE");
        var requiredScope = RequireEnv("AUTH__REQUIREDSCOPE");
        var apiBase = RequireEnv("API_BASE");
        var wsUrl = RequireEnv("WS_URL");

        // Compose the delegated scope expected by the API (audience/scopeName)
        var scopes = new[] { $"{audience}/{requiredScope}" };

        var http = await AuthenticateAsync(tenantId, clientId, scopes, apiBase);
        await RunMenuAsync(http, wsUrl);
    }

    private static async Task<HttpClient> AuthenticateAsync(string tenantId, string clientId, string[] scopes, string apiBase)
    {
        var pca = PublicClientApplicationBuilder
            .Create(clientId)
            .WithAuthority($"https://login.microsoftonline.com/{tenantId}")
            .WithRedirectUri("http://localhost")
            .Build();

        var result = await pca.AcquireTokenWithDeviceCode(scopes, d =>
        {
            Console.WriteLine(d.Message);
            return Task.CompletedTask;
        }).ExecuteAsync();
        
        var http = new HttpClient { BaseAddress = new Uri(apiBase) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", result.AccessToken);
        return http;
    }

    private static async Task RunMenuAsync(HttpClient http, string wsUrl)
    {
        var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        async Task<T?> Gql<T>(string query, object? variables = null)
        {
            var payload = new { query, variables };
            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await http.PostAsync("", content);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode) throw new Exception($"GraphQL HTTP {response.StatusCode}: {body}");

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("errors", out var errs) &&
                errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0)
                throw new Exception($"GraphQL errors: {errs}");

            if (!doc.RootElement.TryGetProperty("data", out var data)) return default;
            return JsonSerializer.Deserialize<T>(data, json);
        }

        while (true)
        {
            Console.WriteLine("\nTeaApp Client");
            Console.WriteLine("1) List teas");
            Console.WriteLine("2) Place order");
            Console.WriteLine("3) Get order by id");
            Console.WriteLine("4) List all orders"); // ← new
            Console.WriteLine("0) Exit");
            Console.Write("> ");

            var choice = Console.ReadLine();
            if (choice == "0" || string.IsNullOrWhiteSpace(choice)) break;

            try
            {
                switch (choice)
                {
                    case "1":
                    {
                        const string q = @"query { teas { id name caffeineMg } }";
                        var data = await Gql<TeasData>(q);
                        if (data?.Teas is { Length: > 0 } teas)
                        {
                            Console.WriteLine("\nAvailable teas:");
                            foreach (var t in teas) Console.WriteLine($"- {t.Id}  {t.Name}  ({t.CaffeineMg} mg)");
                        }
                        else Console.WriteLine("No teas returned.");
                        break;
                    }

                    case "2":
                    {
                        Console.Write("Enter teaId to order: ");
                        var teaId = Console.ReadLine() ?? "";

                        const string m = @"mutation ($id:String!) {
                          placeTeaOrder(teaId:$id) { id: orderId accepted }
                        }";

                        var placed = await Gql<PlaceOrderData>(m, new { id = teaId });
                        if (placed?.PlaceTeaOrder is null) { Console.WriteLine("Order failed."); break; }

                        var orderId = placed.PlaceTeaOrder.Id;
                        Console.WriteLine($"Order placed. Id={orderId}, Accepted={placed.PlaceTeaOrder.Accepted}");
                        if (!placed.PlaceTeaOrder.Accepted) break;

                        var token = http.DefaultRequestHeaders.Authorization?.Parameter;
                        if (string.IsNullOrEmpty(token)) { Console.WriteLine("No token available."); break; }

                        Console.WriteLine("Waiting for brewing updates... (returns after brewed or timeout)");
                        try
                        {
                            await SubscriptionClient.WaitForOrderAsync(
                                url: wsUrl,
                                token: token,
                                orderId: orderId,
                                timeout: TimeSpan.FromSeconds(30));
                        }
                        catch (TaskCanceledException)
                        {
                            Console.WriteLine("No updates received before timeout.");
                        }
                        break;
                    }

                    case "3":
                    {
                        Console.Write("Enter orderId: ");
                        var orderId = Console.ReadLine() ?? "";

                        const string q = @"query ($oid:String!) { orderById(orderId:$oid) { id: orderId } }";
                        var data = await Gql<OrderByIdData>(q, new { oid = orderId });
                        if (data?.OrderById is null) Console.WriteLine("No order found.");
                        else Console.WriteLine($"Order {data.OrderById.Id}");
                        break;
                    }

                    case "4":
                    {
                        // Requires API to expose 'orders(): [Order!]!' and map fields
                        const string q = @"query { orders { id: orderId } }";
                        var data = await Gql<OrdersData>(q);
                        if (data?.Orders is { Length: > 0 } list)
                        {
                            Console.WriteLine("\nOrders:");
                            foreach (var o in list) Console.WriteLine($"- {o.Id}");
                        }
                        else Console.WriteLine("No orders found.");
                        break;
                    }

                    default:
                        Console.WriteLine("Unknown choice.");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nERROR: {ex.Message}\n");
            }
        }
    }
}