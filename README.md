# OrderHub — Partial Skeleton (Handoff to Cursor)

This folder is **NOT a complete MVP**. It is a deliberate, clean starting
skeleton covering the hardest-to-agree-on pieces: project layout, Domain
entities, DbContexts, and EF configurations (including all indexes).

Everything else — middleware, factories, auth, platform clients, sync
service, worker, API — is intentionally left for Cursor to generate,
because Cursor can iterate with `dotnet build` locally and this chat
cannot.

---

## ✅ What's Already Done

### Solution & Projects
- `OrderHub.sln` — 5-project solution wired up
- `src/OrderHub.Domain/` — no dependencies
- `src/OrderHub.Application/` — references Domain
- `src/OrderHub.Infrastructure/` — EF Core 8, Polly 8, BCrypt
- `src/OrderHub.Api/` — Cookie auth, Swagger, Serilog
- `src/OrderHub.Worker/` — BackgroundService host

### Domain Layer (complete)
- `Common/BaseEntity.cs`
- `Enums/` — FoodPlatform, OrderStatus, PaymentMethod, PaymentStatus, UserRole, SyncStatus
- `Entities/Central/Customer.cs`
- `Entities/Customer/` — Branch, AppUser, PlatformConnection, Order, OrderItem, OrderItemOption, SyncLog, IntegrationError

### Infrastructure Layer (partial — persistence only)
- `Persistence/Central/CentralDbContext.cs`
- `Persistence/Central/Configurations/CustomerConfiguration.cs`
- `Persistence/Customer/CustomerDbContext.cs`
- `Persistence/Customer/Configurations/OrderConfiguration.cs`
- `Persistence/Customer/Configurations/CustomerDbConfigurations.cs` (the other 7 entities)

All required indexes from your spec are already wired into these
configurations. The solution should build once you add the missing
pieces below and run `dotnet restore`.

---

## ❌ What Cursor Needs to Build

Numbered to match sections in your original prompt.

### 1. Application Abstractions (interfaces only)
Put these under `src/OrderHub.Application/Abstractions/`:
- `Security/ISecretManager.cs` — `EncryptAsync`, `DecryptAsync(ct, keyVersion)`
- `Tenant/ICurrentCustomerService.cs` — exposes the customer resolved per-request
- `Persistence/ICustomerDbContextFactory.cs` — `CreateAsync(Guid customerId, CancellationToken)`
- `Platform/IFoodPlatformClient.cs` — `Platform` property + `FetchOrdersAsync`
- `Platform/IOrderStatusMapper.cs` — `MapToInternalStatus(FoodPlatform, string)`
- `Orders/Services/IOrderSyncService.cs` — `SyncCustomerAsync(Guid customerId, CancellationToken)`

### 2. Encryption (Infrastructure/Security)
- `AesSecretManager.cs` — AES-256-GCM
- Master key from `ENCRYPTION_MASTER_KEY` env var
- Throw at startup if the env var is missing
- **Never log the encrypted or decrypted value**, not even at Debug level

### 3. Tenant Resolution
- `Infrastructure/Tenant/CurrentCustomerService.cs` — reads `HttpContext.Items["CurrentCustomer"]`
- `Api/Middleware/CustomerResolutionMiddleware.cs`:
  1. Extract host from `HttpContext.Request.Host`
  2. Check `IMemoryCache` (5-minute TTL)
  3. If cache miss → query CentralDb: `PrimaryDomain == host && IsActive`
  4. Not found → return 404 "Customer not found"
  5. Inactive → return 503 "Service unavailable"
  6. Store resolved Customer in `HttpContext.Items["CurrentCustomer"]` and cache

### 4. CustomerDbContext Factory
- `Infrastructure/Persistence/Customer/CustomerDbContextFactory.cs`
- Cache `DbContextOptions<CustomerDbContext>` in a `ConcurrentDictionary<Guid, ...>`
- Decrypt connection string once per customer, reuse the same `DbContextOptions` instance
- This is critical — creating new options per-request blows up the EF connection pool

### 5. Authentication (cookie-based, not Identity)
- `Application/Auth/Services/IAuthService.cs`
- `Infrastructure/Services/AuthService.cs`:
  - Resolve CustomerDb via factory
  - Find `AppUser` by email
  - Verify password with BCrypt
  - Sign in with claims: `CustomerId`, `UserId`, `Role`, `Email`
- In `Api/Program.cs`: `AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie()`

### 6. Mock Platform Clients
Under `Infrastructure/Platform/Mock/`:
- `YemeksepetiFoodPlatformClient.cs`
- `GetirYemekFoodPlatformClient.cs`
- `TrendyolYemekFoodPlatformClient.cs`

Each mock should:
- Delay 300–800ms (`Task.Delay(Random.Shared.Next(300, 800))`)
- Return 0–3 fake orders
- ~30% of the time, reuse an existing `ExternalOrderId` to exercise the upsert path
- ~10% of the time, throw `HttpRequestException` to exercise retry/circuit-breaker
- Always populate `RawPayloadJson`

### 7. DTOs & Status Mapping
- `Application/Platform/Dtos/ExternalOrderDto.cs` (fields from your prompt)
- `Application/Platform/Dtos/ExternalOrderItemDto.cs`
- `Application/Platform/Dtos/ExternalOrderItemOptionDto.cs`
- `Infrastructure/Platform/Mapping/DefaultOrderStatusMapper.cs` — simple switch per platform

### 8. Order Sync + Idempotent Upsert (the most important service)
- `Infrastructure/Sync/OrderSyncService.cs`
- Idempotency key:
  ```csharp
  var input = $"{platform}:{externalOrderId}";
  var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
  var idempotencyKey = Convert.ToBase64String(hash);
  ```
- Upsert:
  ```csharp
  var existing = await db.Orders
      .Include(o => o.Items).ThenInclude(i => i.Options)
      .FirstOrDefaultAsync(o => o.IdempotencyKey == key, ct);
  if (existing is null) { /* insert */ }
  else { /* update status/totals/timestamps; delete-and-insert items for MVP */ }
  ```
- Write a `SyncLog` row per sync cycle (start → finish)
- On failure, write an `IntegrationError` row (sanitized message — no secrets)

### 9. Background Worker
- `Worker/Jobs/OrderSyncWorker.cs` : `BackgroundService`
- `ExecuteAsync` loop:
  ```csharp
  while (!ct.IsCancellationRequested)
  {
      using var scope = _scopeFactory.CreateScope();
      var central = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
      var customers = await central.Customers
          .Where(c => c.IsActive)
          .Select(c => c.Id)
          .ToListAsync(ct);

      await Parallel.ForEachAsync(
          customers,
          new ParallelOptions { MaxDegreeOfParallelism = 5, CancellationToken = ct },
          async (customerId, innerCt) =>
          {
              using var innerScope = _scopeFactory.CreateScope();
              var syncer = innerScope.ServiceProvider.GetRequiredService<IOrderSyncService>();
              await syncer.SyncCustomerAsync(customerId, innerCt);
          });

      await Task.Delay(TimeSpan.FromSeconds(30), ct);
  }
  ```

### 10. Polly Retry + Circuit Breaker
Wrap every `IFoodPlatformClient.FetchOrdersAsync` call in a `ResiliencePipeline`:
- **Retry:** 3 attempts, exponential backoff (2s, 4s, 8s), only on `HttpRequestException` / `TimeoutException`
- **Circuit breaker:** persisted per `PlatformConnection` (not in-memory Polly) — when `ConsecutiveFailures >= 5`, set `CircuitOpenUntil = UtcNow.AddMinutes(5)`. The worker must skip connections where `CircuitOpenUntil > UtcNow`. Reset on success.

Polly in-memory circuit breaker is fine as a secondary layer, but the DB-persisted one is what survives process restarts.

### 11. API Endpoints
Under `Api/Controllers/`:
- `AuthController` — `POST /api/auth/login`, `POST /api/auth/logout`, `GET /api/auth/me`
- `BranchesController` — GET list, POST create
- `PlatformConnectionsController` — GET list, POST create, PATCH activate/deactivate. **Never return encrypted secrets in responses.**
- `OrdersController` — GET list (filters: `platform`, `status`, `startDate`, `endDate`, `page`, `pageSize`), GET by id
- `DashboardController` — `GET /api/dashboard/summary` (today's order count, revenue, pending count, platform breakdown, recent 10 orders)

### 12. Program.cs Wiring
For both `OrderHub.Api` and `OrderHub.Worker`:
- Serilog bootstrap (Console + File sinks)
- `AddDbContext<CentralDbContext>` with config connection string
- Register `ISecretManager`, `ICustomerDbContextFactory`, `ICurrentCustomerService`, `IAuthService`, `IOrderSyncService`, all three `IFoodPlatformClient`s, `IOrderStatusMapper`
- API adds `UseMiddleware<CustomerResolutionMiddleware>()` **before** `UseAuthentication()`

### 13. Initial Migrations
Generate two separate migration sets:
```bash
dotnet ef migrations add InitialCreate \
  --project src/OrderHub.Infrastructure \
  --startup-project src/OrderHub.Api \
  --context CentralDbContext \
  --output-dir Persistence/Central/Migrations

dotnet ef migrations add InitialCreate \
  --project src/OrderHub.Infrastructure \
  --startup-project src/OrderHub.Api \
  --context CustomerDbContext \
  --output-dir Persistence/Customer/Migrations
```

---

## 🔧 How to Run (Once Cursor Finishes)

```bash
# 1. Set the encryption key (any 32-byte Base64 string)
export ENCRYPTION_MASTER_KEY="$(openssl rand -base64 32)"

# 2. Build
dotnet restore
dotnet build

# 3. Apply migrations
dotnet ef database update --context CentralDbContext \
    --project src/OrderHub.Infrastructure --startup-project src/OrderHub.Api

# 4. Manually create customer DBs, then apply CustomerDbContext migration to each

# 5. Run the API
dotnet run --project src/OrderHub.Api

# 6. In another terminal, run the worker
dotnet run --project src/OrderHub.Worker
```

---

## 🎯 Suggested Prompt for Cursor

Open the solution in Cursor and use this prompt:

> This is a partial skeleton for the Multi-Platform Food Order Management
> System described in our earlier spec. The solution file, all 5 projects,
> Domain layer (entities + enums), and persistence layer (both DbContexts
> plus EF configurations with indexes) are already in place.
>
> Please read the README.md in the solution root and implement every
> section marked "❌ What Cursor Needs to Build", in the numbered order.
>
> Rules:
> - Do not modify existing Domain entities or EF configurations unless
>   absolutely necessary — if you do, explain why.
> - Run `dotnet build` after each numbered section and fix any errors
>   before moving to the next.
> - Never log secrets. Never return encrypted values from API responses.
> - Use `Parallel.ForEachAsync` for the worker, not `foreach`.
> - Use the exact idempotency key formula from the README.
>
> Start with section 1 (Application Abstractions) and work through
> section 13 (Migrations).

---

## 🧪 Manual Test Checklist (After Cursor Finishes)

1. Create two rows in `CentralDb.Customers` (e.g. `restaurant-a.localhost` and `restaurant-b.localhost`) with encrypted connection strings
2. Create two empty customer databases
3. Run `dotnet ef database update` with `CustomerDbContext` against each
4. Add a few `PlatformConnection` rows in each customer DB
5. Start the worker → should see both customers processed in parallel in the logs
6. Query orders via `GET /api/orders` hitting both domains → both return data
7. Run the worker again → log should say "updated" not "inserted" (idempotency works)
8. Force a platform client to always throw → after 5 attempts, `CircuitOpenUntil` should be set and the worker should skip it
