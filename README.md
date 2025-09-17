# TeaApp

A minimal microservices demo built with **.NET 9**, **HotChocolate GraphQL**, **RabbitMQ**, **Kong (OSS) API Gateway**, and **oauth2-proxy** with **Microsoft Entra (Azure AD)**.

The app simulates placing and brewing tea orders, demonstrates async messaging, GraphQL subscriptions, and gateway-level JWT validation.

---

## Services & Responsibilities

- **TeaApp.Api**  
  GraphQL API (queries, mutations, subscriptions). Publishes `TeaOrderPlaced` to RabbitMQ and forwards brewing/brewed events into subscriptions.

- **TeaApp.Brewer**  
  Background worker. Consumes orders from RabbitMQ, simulates brewing, and publishes `tea.brewing` (multiple heartbeats) and `tea.brewed` (once).

- **TeaApp.Notifier**  
  Minimal background worker that consumes `tea.brewed` (can be extended to email/webhooks/etc.).

- **TeaApp.Client**  
  Console client using **MSAL (device code flow)** to get an AAD access token. Calls GraphQL via **Kong** and listens for subscriptions over WebSockets.

- **TeaApp.Contracts**  
  Shared contracts and enums (e.g., `TeaOrderPlaced`, `TeaOrderBrewing`, `TeaOrderBrewed`, `ExchangeKind`).

- **gateway (Kong)**  
  API Gateway (db-less). Routes `/graphql` to the API and **auth-guards** it using **oauth2-proxy**.

- **oauth2-proxy**  
  Validates Bearer tokens (JWT) from Microsoft Entra. Only valid requests are forwarded to TeaApp.Api.

- **RabbitMQ**  
  Message broker for async communication:
  - `tea.orders` → **direct** (used by Brewer; routing key `order.placed`)
  - `tea.brewing` → **fanout** (broadcast; API & Notifier can subscribe)
  - `tea.brewed`  → **fanout** (broadcast; API & Notifier can subscribe)

---

## System Diagram
flowchart TD
  %% Top-to-bottom layout
  %% TeaApp end-to-end flow with Kong + oauth2-proxy + RabbitMQ

  subgraph CLIENT["TeaApp.Client (console app)"]
  end

  subgraph KONG["Kong (API Gateway)"]
  end

  subgraph O2P["oauth2-proxy (OIDC/JWT validation)"]
  end

  subgraph API["TeaApp.Api (ASP.NET Core + GraphQL)"]
  end

  subgraph MQ["RabbitMQ"]
    direction TB
    subgraph EX["Exchanges"]
      EXO[tea.orders (direct)]
      EXB[tea.brewing (fanout)]
      EXD[tea.brewed (fanout)]
    end
    subgraph QQ["Queues"]
      QO[brew.orders]
      QB[api.subs.brewing]
      QD[api.subs.brewed]
    end
  end

  subgraph BREWER["TeaApp.Brewer (worker)"]
  end

  subgraph NOTIFIER["TeaApp.Notifier (worker)"]
  end

  %% Edges with protocols/ports
  CLIENT -->|"HTTP/WS :8000 (GraphQL via /graphql)"| KONG
  KONG -->|"HTTP :4180 (internal)"| O2P
  O2P -->|"HTTP :8080 (internal)"| API
  API -->|"AMQP :5672"| MQ

  %% Routing inside RabbitMQ
  EXO -->|route 'order.placed'| QO
  EXB -->|fanout| QB
  EXD -->|fanout| QD

  %% Workers
  BREWER -->|"consume brew.orders"| QO
  BREWER -.->|"publish TeaOrderBrewing → tea.brewing (fanout)"| EXB
  BREWER -.->|"publish TeaOrderBrewed  → tea.brewed  (fanout)"| EXD

  %% GraphQL Subscriptions bridge in API (RabbitToSubscriptions)
  QB -->|"forward to GraphQL topic: orders/brewing"| API
  QD -->|"forward to GraphQL topic: orders/brewed"| API

  %% Styling
  classDef svc fill:#fff,stroke:#222,stroke-width:1px,rx:6,ry:6;
  class CLIENT,KONG,O2P,API,MQ,BREWER,NOTIFIER svc;
  
---

## Authentication Flow

1. **Client** authenticates with **MSAL** (device code) and acquires an **access token** for the API audience:
   - Audience can be either `api://{API_CLIENT_ID}` or `{API_CLIENT_ID}`.
   - Scope must include the API’s required scope (e.g., `TeaApp.Order`).
2. **Kong** proxies all requests to **oauth2-proxy**.
3. **oauth2-proxy** validates the Bearer token against Microsoft Entra (issuer & audience).
4. If valid, the request proceeds to **TeaApp.Api** (which also validates JWT as a defense in depth).

---

## GraphQL Surface

- **Queries**
  - `teas(): [Tea!]!`
  - `orderById(orderId: String!): Order`
  - `orders(): [Order!]!` (in-memory snapshot held by API)
- **Mutations**
  - `placeTeaOrder(teaId: String!): OrderAccepted!`
- **Subscriptions**
  - `brewing: TeaOrderBrewing!` (multiple heartbeats per order)
  - `brewed: TeaOrderBrewed!` (single completion event)

The API forwards RabbitMQ events to subscriptions via a background bridge (`RabbitToSubscriptions`).

---

## Tech & Libraries

- **.NET 9 / C#**, **HotChocolate GraphQL**
- **RabbitMQ** (exchanges: direct + fanout)
- **Kong (OSS)** in **db-less** mode (`gateway/kong.yaml`)
- **oauth2-proxy** for JWT validation at the edge
- **Microsoft Entra (Azure AD)** via **MSAL (device code flow)**
- **Docker / Docker Compose** for local orchestration

---

## Configuration

All sensitive values come from **environment variables** (via `.env`).  
Example `.env` (values shown as placeholders):

```env
# ========== Tenant ==========
AZUREAD__TENANTID=<tenant-guid>

# ========== API App Registration ==========
API_AZUREAD_CLIENTID=<api-app-client-id>
AZUREAD__AUDIENCE=api://<api-app-client-id>
AUTH__REQUIREDSCOPE=TeaApp.Order

# ========== Client App Registration ==========
CLIENT_AZUREAD_CLIENTID=<client-app-client-id>

# ========== RabbitMQ ==========
RABBIT_HOST=rabbitmq
RABBIT_USER=<username>
RABBIT_PASS=<password>

# ========== Endpoints (client) ==========
API_BASE=http://localhost:8000/graphql
WS_URL=ws://localhost:8000/graphql

# ========== oauth2-proxy ==========
OAUTH2_PROXY_CLIENT_ID=<api-app-client-id-or-dedicated-client-id>
OAUTH2_PROXY_CLIENT_SECRET=unused_not_required
OAUTH2_PROXY_REDIRECT_URL=http://localhost:8000/oauth2/callback
OAUTH2_PROXY_COOKIE_SECRET=tQZm8VtUTQfQzEIVX5Q8WYrnhsWq81IqH1z46zzoxZg=
```

---

## Run Locally

1. **Build & start** the stack:
   ```bash
   docker compose up -d --build
   ```

2. **Health checks**:
   - RabbitMQ UI: http://localhost:15672  
     (user/pass from `.env`)
   - Kong proxy root: http://localhost:8000

3. **Run the console client** (from host):
   ```bash
   cd TeaApp.Client
   dotnet run
   ```
   - Follow the MSAL device-code prompt.
   - Use the menu to list teas, place orders, and watch live brewing updates.

---

## Messaging: Exchanges & Routing

- **`tea.orders` (direct)**
  - **Routing key:** `order.placed`
  - **Publisher:** TeaApp.Api (when `placeTeaOrder` runs)
  - **Consumer:** TeaApp.Brewer (`brew.orders` queue)

- **`tea.brewing` (fanout)**
  - **Publisher:** TeaApp.Brewer (heartbeat events during brew)
  - **Consumers:** TeaApp.Api (bridges to GraphQL subscriptions), TeaApp.Notifier (optional)

- **`tea.brewed` (fanout)**
  - **Publisher:** TeaApp.Brewer (final “done” event)
  - **Consumers:** TeaApp.Api (subscription), TeaApp.Notifier (optional)

---

## Gateway & Security

- **Kong (OSS)** routes `/graphql` to **oauth2-proxy**, which validates JWTs and forwards to **TeaApp.Api**.
- **oauth2-proxy** (OIDC against Microsoft Entra):
  - `OAUTH2_PROXY_OIDC_ISSUER_URL=https://login.microsoftonline.com/<tenant>/v2.0`
  - `OAUTH2_PROXY_ALLOWED_AUDIENCES` includes both `api://<api-client-id>` and `<api-client-id>`.
- **TeaApp.Api** also validates tokens with ASP.NET Core JWT bearer (issuer, audiences, scope).

> Edge validation (Kong + oauth2-proxy) **and** service validation (API) give layered defense.