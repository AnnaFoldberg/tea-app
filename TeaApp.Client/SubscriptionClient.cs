using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

/// <summary>
/// Minimal GraphQL WebSocket client (graphql-transport-ws) that waits for
/// brewing progress events for a specific order, then exits when brewed.
/// </summary>
public static class SubscriptionClient
{
    /// <summary>
    /// Connects to a GraphQL WebSocket endpoint, starts two subscriptions
    /// (<c>brewing</c> &amp; <c>brewed</c>), prints updates for the specified order,
    /// and returns when a <c>brewed</c> event arrives or when the timeout elapses.
    /// </summary>
    /// <param name="url">GraphQL WS endpoint (e.g., wss://host/graphql).</param>
    /// <param name="token">Bearer access token used for WS auth.</param>
    /// <param name="orderId">Order to filter events on.</param>
    /// <param name="timeout">Max time to wait before giving up.</param>
    /// <param name="externalCt">Optional external cancellation token.</param>
    public static async Task WaitForOrderAsync(string url, string token, string orderId,
        TimeSpan timeout, CancellationToken externalCt = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        cts.CancelAfter(timeout);

        using var ws = new ClientWebSocket();

        // Tell server we speak the graphql-transport-ws subprotocol
        ws.Options.AddSubProtocol("graphql-transport-ws");

        // Pass Bearer token for server-side auth
        ws.Options.SetRequestHeader("Authorization", $"Bearer {token}");

        // --- Open WebSocket connection ---
        await ws.ConnectAsync(new Uri(url), cts.Token);

        // --- GraphQL connection init handshake ---
        await SendJsonAsync(ws, new { type = "connection_init" }, cts.Token);

        // Helper to start a subscription given a client-generated id
        static Task StartSubAsync(ClientWebSocket s, string id, string query, CancellationToken ct) =>
            SendJsonAsync(s, new { id, type = "subscribe", payload = new { query } }, ct);

        // Start two independent subscriptions (one root field each)
        await StartSubAsync(ws, "s1", "subscription { brewing { orderId teaId startedAt } }", cts.Token);
        await StartSubAsync(ws, "s2", "subscription { brewed  { orderId success finishedAt } }", cts.Token);

        // Process incoming WS frames until brewed or timeout/cancel
        while (!cts.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var msg = await ReceiveMessageAsync(ws, cts.Token);
            if (msg is null) break; // server closed the socket

            using var doc = JsonDocument.Parse(msg);

            if (!doc.RootElement.TryGetProperty("type", out var typeEl)) continue;
            var type = typeEl.GetString();

            // Server acknowledges the protocol negotiation
            if (type == "connection_ack") continue;

            // Protocol-level error for a specific subscription
            if (type == "error")
            {
                // Surface server-side subscription error frames
                Console.WriteLine($"[WS] error: {msg}");
                continue;
            }

            // Server indicates a given subscription stream is complete
            if (type == "complete") continue;

            // Data frames can be "next" (graphql-transport-ws) or "data" (legacy)
            if (type is not "next" && type is not "data") continue;

            if (!doc.RootElement.TryGetProperty("payload", out var payload)) continue;
            if (!payload.TryGetProperty("data", out var data)) continue;

            // ----- BREWING events: print heartbeats for this order -----
            if (data.TryGetProperty("brewing", out var brewing))
            {
                if (TryGetString(brewing, "orderId", out var bId) && bId == orderId)
                {
                    TryGetString(brewing, "teaId", out var teaId);
                    TryGetString(brewing, "startedAt", out var startedAt);
                    Console.WriteLine($"[Subscription] Brewing heartbeat for {orderId} (teaId={teaId ?? "?"}, startedAt={startedAt ?? "?"})");
                }
            }

            // ----- BREWED event: marks completion for this order -----
            var brewedReceived = false;

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

        /// <summary>
        /// Sends a JSON payload as a single text WS frame.
        /// </summary>
        static async Task SendJsonAsync(ClientWebSocket socket, object obj, CancellationToken ct)
        {
            var json = JsonSerializer.Serialize(obj);
            var bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }

        /// <summary>
        /// Receives a full WS message (accumulating fragments) and returns it as a string.
        /// Returns <c>null</c> if the server sends a Close frame.
        /// </summary>
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

        /// <summary>
        /// Reads a string property from a JSON object safely.
        /// </summary>
        static bool TryGetString(JsonElement obj, string propName, out string? value)
        {
            value = null;
            return obj.TryGetProperty(propName, out var prop)
                && (value = prop.GetString()) is not null;
        }
    }
}