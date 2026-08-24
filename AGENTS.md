# AGENTS.md

## Project Overview

This project is a ground-up replacement for two existing FileMaker-based business applications.

The new application uses:

- C#
- .NET / ASP.NET Core
- Blazor
- PostgreSQL
- Entity Framework Core unless there is a compelling reason to use something else

The application will be used by multiple users concurrently from desktop computers and iPads. It will eventually be hosted on a central server and accessed through a web browser, including remotely over the internet.

The existing FileMaker application should be treated as the source of current business behavior, but **not necessarily as the model for the new application's architecture**.

The purpose of this rewrite is not to reproduce FileMaker internally. It is to preserve required business behavior while replacing FileMaker-specific architecture with a maintainable modern application.

---

## Primary Goals

Prioritize the following, roughly in this order:

1. Correctness of business behavior
2. Data integrity
3. Readability and maintainability
4. Simple architecture
5. Good user experience
6. Security
7. Performance
8. Cleverness

Performance matters, but avoid premature optimization.

Prefer boring, understandable code over elaborate architecture unless the added complexity solves a real problem.

---

## Development Philosophy

This project is being developed incrementally.

Do not attempt to design the entire final system before implementing useful functionality.

Prefer:

- small, understandable changes
- explicit behavior
- straightforward control flow
- strongly typed domain models
- database constraints where appropriate
- code that can be debugged without requiring knowledge of an elaborate framework

Avoid unnecessary:

- abstraction layers
- wrapper classes
- interfaces with only one foreseeable implementation
- generic repository patterns over Entity Framework
- mediator frameworks
- reflection
- metaprogramming
- dependency injection solely for the sake of dependency injection
- "enterprise" architecture introduced before the project actually requires it

Do not create architecture merely because it is common in large .NET projects.

Complexity must justify itself.

---

## Working With the Existing FileMaker Application

The FileMaker application contains years of accumulated business rules.

When porting behavior from FileMaker:

1. Determine what the existing feature actually does.
2. Separate business rules from FileMaker implementation details.
3. Preserve the business rules that are still required.
4. Implement those rules naturally in the new architecture.
5. Do not reproduce FileMaker workarounds unless the underlying limitation still exists.

FileMaker layouts should generally be treated as UI references, not application architecture.

FileMaker tables should not automatically become PostgreSQL tables one-for-one.

FileMaker scripts should not automatically become C# methods one-for-one.

FileMaker calculated fields should be evaluated individually to determine whether they belong as:

- calculated values in C#
- database-generated values
- SQL queries/views
- persisted columns
- UI-only derived values

If existing behavior appears strange, redundant, or incorrect, point it out rather than silently preserving or changing it.

Do not change business behavior without making the change explicit.

---

## Database Design

PostgreSQL is the authoritative data store.

Design the relational schema deliberately rather than mechanically translating the FileMaker schema.

Prefer:

- normalized relational data where practical
- explicit primary keys
- foreign-key constraints
- appropriate unique constraints
- `NOT NULL` where absence is not meaningful
- database constraints for rules that protect fundamental data integrity
- migrations tracked in source control

Avoid storing structured relational data as JSON merely because doing so is convenient.

JSON/JSONB is acceptable when the data is genuinely document-like or has a flexible structure that does not benefit from relational modeling.

Use transactions when multiple database changes form one logical operation.

Assume multiple users may modify data concurrently. Do not write code that only works correctly in a single-user environment.

Be alert for race conditions, stale updates, duplicate creation, and check-then-insert patterns.

---

## Entity Framework Core

Entity Framework Core is the default data-access mechanism.

Use EF Core directly unless another approach provides a concrete benefit.

Do not introduce a generic repository layer merely to hide EF Core.

Keep queries understandable and be aware of when execution occurs in the database versus in application memory.

Avoid accidental N+1 query patterns.

Do not eagerly load large object graphs without a reason.

Prefer projecting only the data required when querying for lists, reports, dashboards, or other read-heavy views.

Raw SQL is acceptable when it produces a substantially clearer or more efficient solution. If raw SQL is used, parameterize values properly.

---

## Inspection Criteria and Historical Integrity

Inspection criteria for a Part are versioned business records.

A Part may have multiple revisions of its inspection criteria over time. New inspections must use the inspection-criteria revision that is current and applicable when the inspection is started.

Once an inspection has been created, its applicable inspection criteria must remain historically stable.

Specifically:

- Changes to a Part's inspection criteria must apply only to future inspections unless a deliberate business operation explicitly says otherwise.
- Existing or completed inspections must continue to reference the same inspection-criteria revision they were created against.
- Do not update historical inspections merely because the current inspection criteria for a Part have changed.
- Do not model inspections as reading tolerances, tools, units, or other requirements directly from a mutable "current criteria" record.
- Inspection-criteria revisions that are referenced by historical inspections must not be destructively edited or deleted in a way that changes the meaning of those inspections.
- When inspection requirements change, create a new revision rather than modifying the historical revision in place.
- Preserve enough revision identity and effective-history information to determine which criteria applied to any given inspection.

For example, if Overall Length is 20-21 mm under one revision and later changes to 21-22 mm, inspections created under the original revision must continue to show and evaluate against 20-21 mm. Only inspections created under the newer applicable revision should use 21-22 mm.

Treat this as a fundamental data-integrity requirement, not merely a UI behavior.

---

## Backend and Business Logic

Business rules should not live primarily inside Blazor components.

Blazor components should mostly handle:

- presentation
- user interaction
- UI state
- validation feedback
- calling application/business services

Important business behavior should live in normal C# code that can be understood and tested independently of the UI.

Do not create service classes simply to move code out of a component. A service should represent a meaningful responsibility.

Keep domain terminology consistent with the terminology used by the business.

Prefer clear names even when they are longer.

---

## Blazor and UI

The application must work well on:

- normal desktop browsers
- tablets, particularly iPads

Design interfaces responsively.

Do not assume mouse-only interaction.

Avoid controls that depend on hover.

Touch targets should be reasonably sized.

Desktop use is important, so responsive design should not mean reducing every screen to a simplistic mobile layout.

Data-heavy business screens may appropriately use tables, grids, dense forms, and desktop-oriented layouts when those are the best tools for the job.

Preserve efficient workflows. Do not replace a fast business interface with a fashionable but slower UI.

When replacing an existing FileMaker layout, identify what users are trying to accomplish rather than blindly reproducing its visual arrangement.

---

## Validation

Validation should exist at the appropriate layers.

UI validation exists to give users useful feedback.

Server-side validation exists to prevent invalid operations.

Database constraints exist to protect fundamental data integrity.

Do not rely solely on browser-side validation.

Error messages shown to users should explain what went wrong in useful business terms where possible.

---

## Security

Treat all client input as untrusted.

Never rely on the Blazor UI to enforce authorization.

Authorization for protected operations must be enforced server-side.

Avoid constructing SQL using string concatenation.

Do not commit:

- passwords
- API keys
- database credentials
- private keys
- connection strings containing secrets

Use configuration and secret-management mechanisms appropriate to the deployment environment.

Be cautious when implementing:

- file uploads
- file downloads
- user-supplied filenames
- path handling
- report generation
- authentication
- authorization
- externally accessible endpoints

The application is intended eventually to be reachable from the public internet, so features should not be designed under the assumption that the network itself is trusted.

---

## Logging and Error Handling

Do not swallow exceptions.

Unexpected failures should be logged with enough context to diagnose the problem.

Do not expose stack traces, SQL details, secrets, or internal implementation information to normal users.

Prefer structured logging over miscellaneous `Console.WriteLine` debugging in production code.

Temporary diagnostic output is acceptable during development but should not accumulate indefinitely.

Avoid catching broad `Exception` unless there is a concrete reason to handle failures at that boundary.

---

## Testing

Tests should concentrate on behavior where regression would matter.

Prioritize tests for:

- business rules
- calculations
- data transformations
- complicated queries
- permissions
- workflows with important side effects
- bugs that have previously occurred

Do not create tests merely to increase test counts or coverage percentages.

Trivial property getters and framework behavior generally do not need tests.

When fixing a reproducible bug, strongly consider adding a regression test.

---

## Code Style

Use idiomatic modern C#.

Prefer readable code over ceremonial patterns.

Use nullable reference types.

Use asynchronous APIs for database, network, and filesystem operations where appropriate.

Do not append `Async` to methods that are not asynchronous.

Avoid `async void` except where required for event handlers.

Use `var` when the type is obvious from the right-hand side. Use the explicit type when it improves readability.

Prefer early returns when they reduce nesting.

Avoid giant methods, but do not mechanically split straightforward logic into tiny methods solely to satisfy arbitrary line-count rules.

Comments should explain **why**, constraints, unusual behavior, or business reasoning.

Do not write comments that merely restate obvious code.

---

## Dependencies

Before adding a NuGet package, consider whether the functionality:

- already exists in .NET
- can reasonably be implemented with a small amount of code
- justifies another long-term dependency

Do not add libraries merely to avoid writing a few straightforward lines of code.

When recommending a dependency, explain what problem it solves and why it is preferable to the built-in alternatives.

---

## Changes and Refactoring

When asked to implement a feature, keep unrelated changes to a minimum.

Do not opportunistically rewrite neighboring code merely because another design would be cleaner.

Refactoring is encouraged when it:

- directly enables the requested change
- removes a demonstrated problem
- reduces meaningful duplication
- substantially improves comprehensibility

Large architectural changes should be identified explicitly before being performed.

If a requested change exposes a deeper architectural problem, explain the problem rather than silently undertaking a broad rewrite.

---

## AI Behavior

When assisting with this project:

- Inspect existing code before proposing substantial changes.
- Understand the surrounding design before modifying it.
- Do not assume a common .NET pattern is automatically appropriate.
- Point out questionable design decisions.
- Challenge requirements that appear contradictory or technically harmful.
- Distinguish required changes from optional improvements.
- Explain important tradeoffs.
- Avoid changing behavior unrelated to the requested task.
- Do not invent requirements.
- Do not fabricate database fields, routes, models, APIs, or business rules.
- If information is missing, make the smallest reasonable assumption and clearly identify it.
- Preserve existing naming and conventions unless there is a good reason to change them.

When reviewing code, be critical rather than agreeable. Identify actual bugs, maintainability problems, concurrency issues, security concerns, and unnecessary complexity.

Do not praise code unless there is something specifically noteworthy about it.

---

## AI Code Generation

Generated code should be production-quality unless explicitly described as experimental or illustrative.

Do not leave important behavior represented by pseudocode such as:

```csharp
// TODO: save this to the database
```

when implementing that behavior is part of the requested task.

Do not silently omit error handling, authorization, validation, or concurrency concerns just to make an example shorter when those concerns materially affect the design.

When generating or modifying code, consider how the change interacts with:

- existing callers
- database migrations
- nullability
- asynchronous execution
- concurrency
- authorization
- validation
- responsive UI behavior

Prefer complete vertical slices of functionality over large piles of disconnected scaffolding.

---

## Migration Strategy

The FileMaker replacement will be built incrementally.

Do not assume the entire old system will be replaced at once.

Features may temporarily exist in both FileMaker and the new application.

During migration, explicitly consider:

- which system is authoritative for a piece of data
- whether data must move in one or both directions
- how identifiers map between systems
- how duplicate or conflicting edits are prevented
- whether historical data needs to be migrated
- whether temporary integration code can later be removed cleanly

Avoid designs that require a single high-risk "big bang" migration unless there is no practical alternative.

---

## Decision Making

When several technically valid solutions exist, prefer the solution that is:

1. easiest to understand
2. hardest to misuse
3. easiest to debug
4. easiest to change later
5. sufficiently performant

Do not optimize for hypothetical requirements unless those requirements are reasonably foreseeable.

A little duplication is preferable to the wrong abstraction.

Explicit code is preferable to mysterious code.

Simple is preferable to fashionable.