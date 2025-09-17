using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

public static class SubscriptionClient
{
    public static async Task WaitForOrderAsync(string url, string token, string orderId,
        TimeSpan timeout, CancellationToken externalCt = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        cts.CancelAfter(timeout);

        using var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("graphql-transport-ws");
        ws.Options.SetRequestHeader("Authorization", $"Bearer {token}");
        await ws.ConnectAsync(new Uri(url), cts.Token);

        // init
        await SendJsonAsync(ws, new { type = "connection_init" }, cts.Token);

        // Helper to start a subscription with a given id + root field
        static Task StartSubAsync(ClientWebSocket s, string id, string query, CancellationToken ct) =>
            SendJsonAsync(s, new { id, type = "subscribe", payload = new { query } }, ct);

        // Two subscriptions (one root field each)
        await StartSubAsync(ws, "s1", "subscription { brewing { orderId teaId startedAt } }", cts.Token);
        await StartSubAsync(ws, "s2", "subscription { brewed  { orderId success finishedAt } }", cts.Token);

        while (!cts.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var msg = await ReceiveMessageAsync(ws, cts.Token);
            if (msg is null) break;

            using var doc = JsonDocument.Parse(msg);

            if (!doc.RootElement.TryGetProperty("type", out var typeEl)) continue;
            var type = typeEl.GetString();

            if (type == "connection_ack") continue;
            if (type == "error")
            {
                // Surface server-side subscription error frames
                Console.WriteLine($"[WS] error: {msg}");
                continue;
            }
            if (type == "complete") continue;

            if (type is not "next" && type is not "data") continue;

            if (!doc.RootElement.TryGetProperty("payload", out var payload)) continue;
            if (!payload.TryGetProperty("data", out var data)) continue;

            // ----- BREWING (print every heartbeat for this order) -----
            if (data.TryGetProperty("brewing", out var brewing))
            {
                if (TryGetString(brewing, "orderId", out var bId) && bId == orderId)
                {
                    TryGetString(brewing, "teaId", out var teaId);
                    TryGetString(brewing, "startedAt", out var startedAt);
                    Console.WriteLine($"[Subscription] Brewing heartbeat for {orderId} (teaId={teaId ?? "?"}, startedAt={startedAt ?? "?"})");
                }
            }

            // ----- BREWED (final) -----
            var brewedReceived  = false;

            if (data.TryGetProperty("brewed", out var brewed))
            {
                if (TryGetString(brewed, "orderId", out var brId) && brId == orderId)
                {
                    brewedReceived = true;
                    var success = brewed.TryGetProperty("success", out var s) && s.GetBoolean();
                    TryGetString(brewed, "finishedAt", out var finishedAt);
                    Console.WriteLine($"[Subscription] Brewing finished for {orderId} (success={success}, finishedAt={finishedAt ?? "?"})");
                }
            }

            if (brewedReceived)
            {
                // End both subs politely
                await SendJsonAsync(ws, new { id = "s1", type = "complete" }, cts.Token);
                await SendJsonAsync(ws, new { id = "s2", type = "complete" }, cts.Token);
                break;
            }
        }

        // ---------- Helpers ----------
        static async Task SendJsonAsync(ClientWebSocket socket, object obj, CancellationToken ct)
        {
            var json  = JsonSerializer.Serialize(obj);
            var bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }

        // Accumulate fragments until EndOfMessage; return null on close
        static async Task<string?> ReceiveMessageAsync(ClientWebSocket socket, CancellationToken ct)
        {
            var buffer = new byte[8192];
            var sb = new StringBuilder();
            while (true)
            {
                var res = await socket.ReceiveAsync(buffer, ct);
                if (res.MessageType == WebSocketMessageType.Close) return null;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, res.Count));
                if (res.EndOfMessage) return sb.ToString();
            }
        }

        static bool TryGetString(JsonElement obj, string propName, out string? value)
        {
            value = null;
            return obj.TryGetProperty(propName, out var prop)
                && (value = prop.GetString()) is not null;
        }
    }
}