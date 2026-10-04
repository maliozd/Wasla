# Wasla

Wasla is a restaurant software ecosystem built around two products:

- **Wasla Orders** centralizes online food delivery orders and supports the restaurant's operational workflow.
- **Wasla POS** is the separate, touch-oriented point-of-sale product for in-restaurant operations.

This repository currently contains **Wasla Orders**. Wasla POS has no project or module in this solution yet. The products may share tenant/account infrastructure and appropriate domain concepts in the future, but their operational interfaces remain separate. The Wasla Orders Live Screen is not the POS application.

## Wasla Orders

The current codebase includes:

- Tenant signup, payment state, provisioning, users, roles, and settings.
- A database-per-tenant model with a central tenant registry.
- Trendyol GO and Yemeksepeti real provider integrations, plus mock clients for local development.
- Order synchronization, idempotent updates, lifecycle actions, history, and reporting surfaces.
- A real-time operational Live Screen with board, list, and focus views.
- User-scoped guided demo orders that stay outside production order data.
- Automatic and manual receipt jobs through the Windows Wasla Print Bridge.
- Turkish, English, Arabic, and Russian UI resources.

## Technology

- .NET 8 and ASP.NET Core
- MVC / Razor
- EF Core 8 and SQL Server
- Bootstrap 5 and vanilla JavaScript
- Windows Forms for Wasla Print Bridge
- xUnit v3, plus Node's built-in test runner for selected frontend modules

The frontend intentionally follows the existing MVC/Razor, Bootstrap, and vanilla JavaScript structure. See [frontend architecture](docs/frontend/architecture.md) before changing that direction.

## Solution structure

| Project | Responsibility |
| --- | --- |
| `Wasla.Web` | Public, signup, tenant, central-admin, Orders, and Live Screen MVC/Razor UI |
| `Wasla.Api` | JSON and device-facing endpoints |
| `Wasla.Worker` | Background provider synchronization |
| `Wasla.Cli` | Provisioning, migrations, administration, and maintenance |
| `Wasla.Application` | Use cases, interfaces, commands, queries, and result contracts |
| `Wasla.Infrastructure` | EF Core, persistence, provider clients, security, email, and service implementations |
| `Wasla.Domain` | Entities, enums, and core business concepts |
| `Wasla.Contracts` | API request and response contracts |
| `Wasla.PrintBridge` | Windows desktop client for local receipt printing |
| `Wasla.UnitTests` | Server, application, infrastructure, and source-contract tests |
| `Wasla.PrintBridge.Tests` | Print Bridge behavior and protocol tests |

For the process and data-flow map, read [Architecture overview](docs/architecture/overview.md).

## Local development

### Prerequisites

- .NET 8 SDK
- SQL Server or LocalDB
- Windows hosts-file access for `*.wasla.local`
- Node.js when running the frontend module tests
- Windows for running the Print Bridge desktop UI

Web, Worker, API, and most CLI commands require a valid `ENCRYPTION_MASTER_KEY`. The canonical setup, host mapping, database bootstrap, provider-mode, and run commands are in [Local development](docs/operations/local-development.md).

The usual development processes run in separate terminals:

```powershell
dotnet run --project src\Wasla.Web
dotnet run --project src\Wasla.Worker
dotnet run --project src\Wasla.Api
```

Local provider mode must be configured explicitly as `Mock` or `Real`. Missing or invalid configuration fails fast.

## Build and tests

```powershell
dotnet build Wasla.sln
dotnet test tests/Wasla.UnitTests/Wasla.UnitTests.csproj
dotnet test tests/Wasla.PrintBridge.Tests/Wasla.PrintBridge.Tests.csproj
node --test tests/Wasla.UnitTests/Orders/*.test.js tests/Wasla.UnitTests/Signup/*.test.js
```

Read [Testing](docs/operations/testing.md) before reporting verification. Mock tests do not prove real-provider compatibility, and Razor compilation does not replace browser verification.

## Documentation

[Developer documentation](docs/README.md) is the canonical map for architecture, orders, provider integrations, printing, frontend, operations, and product terminology.

Key starting points:

- [Product boundaries](docs/product/product-boundaries.md)
- [Architecture overview](docs/architecture/overview.md)
- [Tenancy](docs/architecture/tenancy.md)
- [Order lifecycle](docs/orders/lifecycle.md)
- [Live Screen](docs/orders/live-screen.md)
- [Food platform integrations](docs/integrations/food-platforms.md)
- [Print Bridge](docs/integrations/print-bridge.md)
- [Local development](docs/operations/local-development.md)

The [initial OrderHub skeleton](docs/archive/INITIAL_SKELETON.md) is retained only as historical context.

## Coding agents

Read `AGENTS.md` before changing the repository. It points to the shared rules in `.cursor/rules/` and defines tenant isolation, architecture, provider, order, printing, localization, testing, and Git constraints. Claude Code also starts from `CLAUDE.md`.
