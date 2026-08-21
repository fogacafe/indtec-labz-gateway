# INDTEC LABZ / 004 — Gateway

> Performance and resilience lab exploring .NET, gRPC, Redis, OpenTelemetry, caching strategies and controlled failure handling.

## 30-second view

```text
GET /products/42
       |
       v
+-------------+       HIT        +-------+
| Gateway.Api | ----------------> | Redis |
+------+------+                   +-------+
       |
       | MISS
       v
+-------------+
| Catalog gRPC|
+-------------+
```

The first request misses Redis, calls the Catalog over gRPC and caches the result. The second request returns directly from Redis. A controlled failure path makes the downstream return `Unavailable`, allowing the resilience pipeline and distributed trace to show what happens when the dependency fails.

## What this lab demonstrates

- .NET 9 minimal API as an edge/gateway service
- gRPC communication through a shared protobuf contract
- Redis cache-aside with an explicit TTL
- OpenTelemetry distributed tracing through OTLP
- Jaeger for local trace visualization
- standard .NET HTTP resilience pipeline around the gRPC client
- controlled downstream failure with a stable `503` response
- Docker Compose as the entire local runtime

## Run

```bash
docker compose up --build
```

Services:

| Service | Address |
| --- | --- |
| Gateway | `http://localhost:5080` |
| Catalog | `http://localhost:5081` |
| Jaeger UI | `http://localhost:16686` |
| Redis | `localhost:6379` |

## Demo 1 — cache miss

```bash
curl http://localhost:5080/products/42
```

Expected shape:

```json
{
  "source": "catalog",
  "product": {
    "id": "42",
    "name": "Product 42",
    "category": "LABZ",
    "price": 149.9
  }
}
```

Trace highlights:

```text
gateway.product
  redis.get      cache.hit=false
  catalog.grpc
    HTTP/2 -> catalog-grpc
  redis.set      cache.ttl.seconds=120
```

## Demo 2 — cache hit

Run the same request again:

```bash
curl http://localhost:5080/products/42
```

Now `source` is `cache` and no downstream Catalog call is required.

Trace highlight:

```text
gateway.product
  redis.get      cache.hit=true
```

## Demo 3 — controlled failure

```bash
curl "http://localhost:5080/products/42?fail=true"
```

The failure request intentionally bypasses the cache, asks the Catalog to simulate `StatusCode.Unavailable`, and lets the resilience pipeline observe the failed downstream call. The Gateway converts the dependency failure into a stable public response:

```json
{
  "error": "catalog_unavailable",
  "message": "Downstream catalog is temporarily unavailable.",
  "traceId": "..."
}
```

with HTTP `503 Service Unavailable`.

The returned `traceId` can be searched in Jaeger, making the failure path easy to follow end-to-end.

## Why cache-aside?

The Gateway owns the read optimization while the Catalog remains unaware of Redis. That keeps the downstream service focused on serving product data and makes cache behavior visible at the boundary where latency matters.

The controlled failure path also skips cache on purpose: a cached response would hide the downstream call and make the resilience experiment meaningless.

## Repository scope

This is intentionally a small lab, not a production gateway framework. The goal is to make four engineering concerns obvious in a few minutes of review: **latency, caching, service-to-service communication and behavior under failure**.
