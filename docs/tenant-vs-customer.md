# Tenant vs Customer Terminology

This document clarifies how Wasla uses **Tenant** and **Customer** in code, data, and docs.

## Tenant

A **tenant** is the restaurant or business using Wasla as a SaaS product.

Examples:

- The business that signs up and pays for a subscription
- The entity resolved by subdomain/domain and stored in CentralDb
- The owner of a dedicated tenant database (orders, users, branches, platform connections)

In code today, this concept is mostly named `Customer` in CentralDb (e.g. `Wasla.Domain.Entities.Central.Customer`). That name is legacy and is a candidate for a future rename to `Tenant`.

## Customer (end customer)

A **customer** (or **end customer**) is the restaurant's own customer who places a food order through a delivery platform.

Examples:

- The person who ordered food on Yemeksepeti, GetirYemek, or TrendyolYemek
- Fields on an order such as name, phone, and delivery address

These must **stay** Customer terminology. Do not rename them to Tenant.

## What should become Tenant later

These represent the SaaS tenant / restaurant business and may be renamed in a dedicated refactor:

| Area | Current naming | Intended meaning |
|------|----------------|------------------|
| CentralDb entity | `Customer` | SaaS tenant (restaurant business) |
| `CustomerId` on central records | e.g. Print Bridge devices, memberships | Owning tenant |
| `ICurrentCustomerService`, tenant resolution | "Customer" in API names | Current tenant context |
| Database-per-tenant model | "customer database" in ops/docs | Tenant operational database |
| Routes like `/customer-access-required` | "customer" in URL/copy context | Often means tenant login, not food orderer |

Any rename should be planned, scoped, and coordinated—not done ad hoc in feature work.

## What must stay Customer

These refer to the **food orderer** and must keep Customer naming:

| Area | Examples |
|------|----------|
| Order entity (`CustomerDb`) | `CustomerName`, `CustomerPhone`, `CustomerAddress` |
| Order UI / receipts | Recipient name, phone, address on an order |
| Platform payload mapping | External "customer" fields from Yemeksepeti, Getir, Trendyol |
| Order-related DTOs and display | Anything describing who placed or receives the order |

Renaming these to Tenant would be incorrect and confusing.

## Risky areas for a future Tenant rename

If CentralDb `Customer` is renamed to `Tenant`, these areas need careful review:

1. **Auth cookies and claims** — schemes, claim types, and session keys may embed "Customer".
2. **EF Core migrations** — table names (`Customers`), FK columns (`CustomerId`), indexes, and snapshots in CentralDb and CustomerDb.
3. **Existing database names** — physical DB names and connection-string storage are tied to current naming conventions.
4. **Routes and URLs** — MVC areas, middleware (tenant resolution), and public paths that say "customer".
5. **Print Bridge ownership** — devices and tokens are scoped to a central `CustomerId` (tenant).
6. **Platform connections** — stored per tenant database but resolved through tenant context naming.
7. **CLI and ops scripts** — `add-customer`, `migrate-customer`, admin tooling language.
8. **Localization** — UI strings that mix "customer" (tenant login) vs order recipient meaning.

Do not rename these in passing during unrelated tasks. Use a dedicated migration/rename effort with a checklist derived from this document.

## Related code references

- End-customer fields: `src/Wasla.Domain/Entities/Customer/Order.cs`
- SaaS tenant registry: `src/Wasla.Domain/Entities/Central/Customer.cs`
