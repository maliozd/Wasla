# AGENTS.md — Wasla Project Instructions

This file defines the project instructions for coding agents working in the
Wasla repository. It applies throughout the repository.

## 1. Purpose and working principles

Priorities when working on Wasla:

1. Tenant isolation and data security.
2. Correct and consistent order processing.
3. Reliable printing.
4. Preservation of existing behavior.
5. Maintainable, testable code.
6. A user experience suited to restaurant operations.

Code identifiers, routes, classes, and file names must be in English.

Perform the investigation needed to complete the requested task.
Do not add features, refactors, or dependencies outside the task scope.

Discovering an existing problem does not authorize fixing it.
Report unrelated problems as separate findings.

## 2. Instruction sources and precedence

Before starting work, read:

- `.cursor/rules/00-wasla-core.mdc`
- `.cursor/rules/10-architecture-boundaries.mdc`
- `.cursor/rules/20-localization-ui.mdc`
- `.cursor/rules/30-provider-worker.mdc`

These files are the shared source of detailed project rules.
Do not create divergent copies of the same rules in multiple places.

Apply instructions in this order:

1. The user's explicit instructions for the current task.
2. Applicable directory-specific AGENTS.md instructions.
3. This file and the project rules it references.
4. Explanatory documentation, historical reports, and code comments.

This order does not override the working environment's higher-priority security
or tool instructions.

If instructions conflict, first determine whether the user's existing explicit
instructions resolve the conflict. Otherwise, explain the specific conflict
and seek clarification only for the affected work.

Code is evidence of current behavior, not necessarily correct product behavior.
Documentation may describe product intent without reflecting the current implementation.

Report discrepancies between code and the README, comments, or historical reports.
Do not present historical test results as current verification.

## 3. Behavior by task type

### Read-only review

When the user requests an investigation, audit, or report:

- Do not modify source files.
- Do not run formatters, automatic fixes, or code generation.
- Do not commit, push, check out another branch, or apply migrations.
- Do not perform verification that changes normal tenant data.
- You may run builds and isolated tests when needed for the review.
- Distinguish build/test artifacts from source changes.
- Report findings in severity order with file and line references.

### Draft preparation

When the user wants to review a draft before it is added to the project:

- Show the draft in the conversation first.
- Do not create or update project files.
- Clearly distinguish a proposal from an implemented change.

### Implementation

When the user requests code changes:

- Inspect the existing implementation and relevant tests first.
- Prefer the smallest coherent change.
- Reuse existing services, result types, and shared components.
- Do not create a second parallel implementation of the same functionality.
- Consider relevant failure paths and authorization boundaries.
- Perform verification appropriate to the change.

## 4. Product definition and scope

Wasla is a restaurant software ecosystem with two products: Wasla Orders and
Wasla POS. This repository currently implements Wasla Orders. Wasla POS is a
separate product and has no project or module in this solution yet. Read
`docs/product/product-boundaries.md` before product-scope or POS work.

Wasla Orders is a multi-tenant SaaS that lets restaurants manage orders from
different food-delivery platforms in one operational workflow.

Core scope:

- Platform connections.
- Order synchronization and order history.
- Order acceptance/rejection and preparation/delivery lifecycle.
- Live Screen / kitchen operations display.
- Automatic and manual receipt printing.
- Wasla Print Bridge on Windows.
- Tenant users, roles, and settings.
- Tenant signup, payment, provisioning, and central administration.

Out of scope for this repository unless explicitly requested:

- A full POS system.
- Inventory and warehouse management.
- Accounting and payroll.
- Broad CRM functionality.
- Restaurant ERP.
- General-purpose infrastructure redesign unrelated to the current need.

Do not turn the Wasla Orders Live Screen into the POS application. Add shared
infrastructure only when the requested work establishes a real cross-product need.

Remain MVP-focused without weakening tenant isolation, authorization, or data
consistency in the name of an MVP.

## 5. Core business rules

### 5.1 One store per platform per tenant

Each tenant may connect to at most one store on each platform.

Valid example:

- One Trendyol store.
- One Yemeksepeti store.
- One Getir store.

All three connections may coexist for the same tenant.

Invalid example:

- Connecting two different Trendyol stores to the same tenant.

Additional rules:

- A different StoreId does not make a second connection valid.
- StoreId is provider configuration, not part of connection uniqueness.
- Deactivating a connection does not permit creating a second connection.
- Store and credential changes are managed through the existing connection.
- This rule does not prevent connections to different platforms.
- The rule is tenant-scoped; different tenants have their own connections.
- Changing a store must preserve the meaning and operational safety of historical orders.
- Historical orders must not automatically be treated as orders belonging to the new store.

If the existing code or database conflicts with this rule, report the gap.
This document alone does not authorize a migration or data cleanup.

Do not delete or merge duplicate records without a user decision.

### 5.2 Tenant versus order customer

- Tenant: the restaurant/business account using Wasla.
- Order customer: the end customer placing an order with the restaurant.

Do not rename order-recipient fields such as CustomerName, CustomerPhone, and
CustomerAddress to Tenant terminology.

### 5.3 Automation settings

- Order Sync controls whether orders are fetched from platforms.
- Auto Approve controls automatic acceptance of eligible orders.
- Automatic receipt creation is governed by its corresponding printing setting.
- Manual printing is an explicit operator action and does not depend on the
  automatic-printing setting.
- Do not combine these concepts into one setting or business rule.

## 6. Architecture boundaries

### Wasla.Web

Responsibilities:

- HTTP and MVC/Razor flows.
- User sessions and endpoint authorization.
- Web-specific ViewModels.
- Localized user feedback.
- Thin orchestration through Application services.

Rules:

- Do not put direct EF queries in controllers or middleware.
- Do not inject DbContext.
- Do not call provider clients directly.
- Do not use Contracts DTOs as MVC ViewModels.
- Do not use EF entities as Razor models.
- Do not move business rules into Razor or JavaScript.

Program.cs is the composition-root exception for Infrastructure DI registration.

### Wasla.Application

- Use cases, interfaces, commands, queries, and results.
- May depend on Domain.
- Must not depend on Web or Infrastructure implementation details.
- Do not leak Razor-specific presentation state into reusable contracts.
- Do not expose EF Core types to Web.

### Wasla.Infrastructure

- EF Core, persistence, and migrations.
- Platform clients.
- Encryption, secret management, and email.
- Application interface implementations.
- Data access and external-system integrations.

Do not add a dependency on Web.

### Wasla.Domain

- Entities, enums, and core business concepts.
- Do not add dependencies on Web, Infrastructure, API, or CLI.
- Keep framework dependencies outside Domain where possible.

### Other projects

- Wasla.Contracts: API request/response contracts.
- Wasla.Worker: background synchronization.
- Wasla.PrintBridge: Windows device and printing operations.
- Wasla.Cli: provisioning, migrations, and maintenance tools.
- Wasla.Api: JSON and integration endpoints.

When Web and API expose the same authorized operation, their security and
business rules must not contradict each other.

## 7. Tenant isolation and authorization

Wasla uses a database-per-tenant model:

- CentralDb: tenant registry, memberships, provisioning, and central information.
- TenantDb: the restaurant's operational data.

Rules:

- Obtain tenant context from the existing server-side resolution mechanism.
- Do not trust a tenant identifier from a request body, query string, or route alone.
- Preserve consistency between the authenticated user and tenant context.
- Validate resource access within the relevant tenant.
- Knowing a resource GUID does not grant access to that resource.
- Hiding a UI button does not replace server-side authorization.
- Reuse existing policies; do not introduce an unnecessary parallel role-check system.
- Preserve the applicable antiforgery and authentication contracts for writes.
- Avoid unnecessarily revealing the existence of another tenant's resources.
- Consider tenant scope in new cache keys.
- When changing user or device revocation behavior, inspect existing sessions
  and caches as well.

## 8. Secrets, credentials, and logging

Do not expose the following in logs, source code, or user-facing reports:

- Passwords and password hashes.
- Raw device tokens.
- Access and password-reset tokens.
- Provider API secrets.
- Decrypted connection strings.
- Encryption keys.
- Sensitive HTTP headers or request/response contents.

When inspecting configuration, prefer showing only:

- The key name.
- Whether a value is configured.
- Non-secret structural information.

Additional rules:

- Do not use real credentials in tests.
- Do not change encryption or token formats outside the task scope.
- Do not disable authentication, authorization, or certificate validation to
  make verification easier.
- Truncating sensitive data is not equivalent to safely redacting it.
- Consider sensitive data before logging external-service error bodies.
- New logs should support diagnosis while minimizing user data exposure.

## 9. Provider integrations and Worker

### Provider mode

- Canonical key: `Platforms:ProviderMode`.
- Valid values: `Mock` and `Real`.
- Use ProviderModeResolver for parsing.
- Do not silently default missing or invalid values to Mock.
- Do not reintroduce legacy UseMocks keys.
- Do not make real provider calls in Mock mode.
- Do not present unsupported provider behavior in Real mode as a successful operation.
- Do not reorganize existing mode or provider configuration outside the task scope.

### Synchronization

- One tenant's failure must not stop synchronization for other tenants.
- Worker must skip a tenant whose sync setting is disabled.
- Existing orders must remain visible when sync is disabled.
- Do not start or stop Worker OS processes or services from Web.
- Do not delete and recreate the parent Order row.
- Preserve Order Id and CreatedAt.
- Preserve the existing Platform + ExternalOrderId idempotency contract.
- Apply child item/option changes consistently.
- Prevent stale provider statuses from undoing local operational progress.
- When changing providers, assess pagination, recovery after downtime,
  rate limits, and cancellation behavior.

### External side effects

- Do not perform real acceptance/rejection/delivery operations merely for testing.
- Assess whether a retry could execute the same external operation twice.
- Distinguish provider success from a subsequent local persistence failure.
- Do not claim real API compatibility based only on mock tests.

## 10. Order lifecycle and concurrency

Order statuses represent actual restaurant operations.

When changing related code, consider:

- Whether the transition is valid for the current status.
- Whether the user is authorized to perform it.
- Repeated requests.
- Two operators acting at the same time.
- Worker updates racing with user actions.
- A successful provider call followed by a failed local save.
- Request cancellation and network timeouts.
- Accidental regression from terminal statuses.

Disabling a UI button is not concurrency protection.
Checking before writing does not, by itself, guarantee atomicity.

When adding a transaction or lock:

- Account for the actual database provider.
- Avoid unnecessarily long transaction scopes.
- Assess the impact of external HTTP/email calls inside open transactions.
- Check nested-transaction and execution-strategy behavior.
- Inspect commit/rollback behavior on every return and failure path.
- Do not classify all database errors as duplicate/concurrency errors.

## 11. Print Bridge and the receipt pipeline

### Responsibility separation

- Web and Worker create PrintJobs or call existing services.
- Print Bridge claims jobs and submits them to Windows printing infrastructure.
- Do not introduce browser window.print(), direct desktop calls, or a second
  receipt-generation pipeline.

### Job statuses

Existing basic flow:

Pending → Printing → Printed / Failed

Use Cancelled only according to the existing business rules.

- Do not create a second active receipt job while one is Pending or Printing.
- First prints and reprints must use the existing shared job pipeline.
- Do not describe queueing as completed physical printing.
- Do not present Windows spooler acceptance as a physical-paper-output guarantee.
- Consider duplicate-print risk when handling server-notification failures after printing.
- Explicitly report behavior for device shutdowns and interrupted jobs.

### Receipt contents

- Reuse the existing receipt renderer and template settings.
- Preserve template, language, visibility, and copy-count behavior.
- If reprint semantics are unclear, inspect the existing contract rather than
  silently choosing an original snapshot or current settings.
- Do not expose customer information hidden by settings through another printing path.

### Device setup

- Preserve the existing X-PrintBridge-Token contract.
- Preserve ServerUrl and token validation.
- Do not remove manual setup when automatic setup exists.
- Do not require users to know internal API routes.
- Mask tokens in the UI by default.
- Do not change existing polling defaults outside the task scope.
- Do not affect other devices during setup or identity reset.

### Live status updates

Refer to `docs/orders-printjob-status-signalr.md`.

That document describes future acceptance criteria; it is not evidence that
SignalR is already implemented.

Do not add SignalR or alternative polling scope without a user request.

## 12. Signup, payment, and provisioning

- Registration, payment success, and provisioning are separate states.
- Successful payment does not mean the tenant is ready to use.
- Tenant resolution must not create databases or trigger provisioning.
- Provisioning must use the authorized existing service/CLI flow.
- Do not mark a registration Provisioned by merely changing its status field.
- A retry must not accidentally duplicate an existing tenant or database.
- Assess how interrupted provisioning resumes.
- Do not turn a post-provisioning email failure into a provisioning failure
  contrary to the existing best-effort contract.
- Do not present payment simulation as real payment verification.
- Check simulation-endpoint exposure when assessing production readiness.
- Verify in code whether plan, membership, and device limits are enforced.
- A feature shown on the pricing page is not necessarily implemented in the backend.

## 13. User experience and localization

### Technology and components

- Preserve the existing MVC/Razor, Bootstrap 5, and JavaScript approach.
- Do not introduce React/Vue or a heavy frontend toolchain unless requested.
- Prefer existing shared layouts, partials, and helpers.
- Do not implement separate business logic for the same action on different screens.
- Do not change authorization or lifecycle behavior during visual refinement.

### Operational screens

- Orders should remain stable for searching and history management.
- Live Screen is intended for operations and new-order awareness.
- Do not move notification, audio, polling, or modal responsibilities outside scope.
- Account for focus, open modals, and ongoing user actions during polling.
- Prevent event-handler accumulation in dynamic content.
- Assess whether slow or out-of-order responses can regress the displayed state.
- Preserve IDs, classes, and data attributes that JavaScript relies on.

### Languages

Supported cultures:

- tr-TR
- en-US
- ar-SA
- ru-RU

The default/fallback culture is Turkish.

- Do not hardcode new user-facing text.
- Use SharedResource in Web.
- Use existing localization resources in Print Bridge.
- Pass JavaScript messages through localized configuration.
- Update the language resources required for the touched surface.
- Check Arabic RTL behavior.
- Do not translate user input or database values.
- Do not report incomplete translation coverage as full support.
- Do not show technical enum or exception text to users.
- Preserve accessible labels, keyboard operation, and status feedback.

## 14. Data access and performance

- Fetch only the fields required by list queries.
- Use AsNoTracking for read-only queries where appropriate.
- Bound page sizes.
- Do not introduce a separate database query for every card or row.
- Assess sorting and pagination stability.
- Avoid unnecessary deletion/insertion or writes when data has not changed.
- Define tenant scope, lifetime, and invalidation behavior for new caches.
- Do not present unmeasured performance claims as established facts.
- Do not treat a single-Worker-process safeguard as a multi-process guarantee.

## 15. Git and workspace safety

At the start of work, inspect:

- `git branch --show-current`
- `git status --short`

- Preserve the user's existing uncommitted files.
- Distinguish your changes from pre-existing changes.
- Determine branch policy from the Cursor core rules and the user's explicit instructions.
- Do not switch branches for a read-only review.
- Do not create or check out a branch unless requested.
- Do not commit, push, or merge unless explicitly requested.
- Do not reset, restore, clean, stash, delete, or overwrite without user authorization.
- Do not stop unrelated running processes.
- If stopping a process is necessary, verify its identity and that it belongs
  to this project first.

Do not include in commits:

- Real secrets or credentials.
- Local certificates.
- User-specific configuration.
- Build outputs or temporary files.
- Test databases or real customer data.

## 16. Testing and verification standard

Choose tests appropriate to the risk of the change.
Do not replace behavioral tests with tests that merely repeat source text.

When full solution verification is required:

    dotnet build Wasla.sln
    dotnet test tests/Wasla.UnitTests/Wasla.UnitTests.csproj
    dotnet test tests/Wasla.PrintBridge.Tests/Wasla.PrintBridge.Tests.csproj
    git diff --check

Additional principles:

- Use --no-build only when a successful build exists for the current source.
- Do not report tests of old binaries as current verification after a failed build.
- Assess relevant migration status when the schema changes.
- Do not apply migrations without user authorization.
- Run tests with isolated databases and fake credentials.
- Do not leave persistent PrintJobs in a normal tenant.
- Do not initiate real provider actions or physical printing without explicit scope.

Report these verification types separately:

1. Static code review.
2. Build verification.
3. Source-contract tests.
4. Behavioral unit/integration tests.
5. SQL Server concurrency tests.
6. Browser/DOM tests.
7. Real-platform acceptance tests.
8. Physical device/printer tests.

- SQLite tests do not prove SQL Server locking behavior.
- Sequential-call tests are not concurrency tests.
- Mock tests do not prove the real provider contract.
- Razor compilation does not prove browser interactions work.
- Test count alone is not evidence of production readiness.

## 17. Review and defect-reporting format

For each significant finding, include where possible:

- Severity.
- File and relevant line.
- Triggering condition.
- Expected behavior.
- Current behavior.
- User, data, or operational impact.
- The smallest appropriate improvement direction.
- How it was verified and the limits of that verification.

Distinguish:

- A defect confirmed in code.
- A risk under specific conditions.
- A missing product feature.
- A maintenance or performance improvement.
- An unverified assumption.

Do not claim code was executed when it was not.
Do not claim visual verification without inspecting the UI.
Do not declare production readiness without real-integration verification.

## 18. Delivery at task completion

After implementation, report:

- What changed and why.
- Which files or behaviors were affected.
- Verification performed and its results.
- Checks not performed and concrete blockers.
- Remaining risks.
- Whether a commit or push was performed.

After a read-only review:

- State that source files were not modified.
- Present findings in severity order.
- Assess code quality separately from product completion.
- Do not present the user's existing changes as your own work.

## 19. Maintaining this document

- Keep durable business rules and working principles here.
- Do not turn the active branch, temporary HEAD, daily test count, or phase
  progress into permanent instructions.
- Keep temporary work status and roadmaps in separate documents.
- Do not describe a future feature as an existing implementation.
- Do not turn audit findings into instructions to fix them automatically.
- Do not add product decisions the user has not accepted as definitive rules.
- Check related documentation for conflicts when a new durable decision is made.
