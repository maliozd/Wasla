# Product boundaries

## Wasla ecosystem

Wasla consists of two products with separate operational responsibilities:

| Product | Responsibility | Current state in this repository |
| --- | --- | --- |
| **Wasla Orders** | Online delivery order aggregation and restaurant operations | Implemented here |
| **Wasla POS** | Touch-oriented, in-restaurant point of sale | Product direction only; no project or module exists here yet |

## Wasla Orders

Wasla Orders owns online-order operations, including provider connections, synchronization, order lifecycle, Orders management/history, Live Screen, automation, reporting, guided onboarding/demo flows, and receipt printing through Wasla Print Bridge.

The Live Screen is Wasla Orders' operational display. It must remain suitable for incoming platform orders and kitchen/order handling.

## Wasla POS

Wasla POS is intended for in-restaurant workflows such as tables, dine-in and takeaway orders, touch-based product selection, modifiers, payments, and kitchen workflows.

This description records the product boundary. It does not claim that these features are implemented in the current solution.

## Sharing between products

The products may share capabilities when an actual cross-product requirement exists, including:

- Tenant and account management.
- Authentication and authorization concepts.
- Appropriate product, payment, and kitchen domain concepts.
- Provider or integration infrastructure that both products use.
- Common design and localization foundations.

Shared infrastructure must not force the two products into one operational UI or couple their release paths without a concrete need.

## Decision rules

Before designing a feature, identify whether it belongs to Wasla Orders, Wasla POS, or shared Wasla infrastructure.

- Do not turn the existing Live Screen into Wasla POS.
- Do not place speculative POS concepts in the Orders domain merely because they may be useful later.
- Do not duplicate a mature shared capability when both products actually require it.
- Treat the current repository as the Wasla Orders implementation until a deliberate POS repository or module decision is made.

## Related documents

- [Architecture overview](../architecture/overview.md)
- [Live Screen](../orders/live-screen.md)
- [Frontend architecture](../frontend/architecture.md)
- [Terminology](terminology.md)
