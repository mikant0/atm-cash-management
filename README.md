# ATM Cash Management Backend

Cash management backend for a bank's ATM network: cassette configuration, cash loading, two-step
withdrawals with optimal note selection, deposits, reconciliation with four-eyes approval, an
immutable movement ledger, reporting and low-cash alerts.

ASP.NET Core 8 Web API · EF Core 8 (code-first, migrations) · PostgreSQL 16 · xUnit · Testcontainers · Docker

**Status:** every item of Part A and Part B (B1–B10) and every technical requirement is implemented and
covered by automated tests. Known limitations are listed [at the end](#unfinished-items-and-known-limitations).

---

## How to run

### The application

Requirement: Docker.

```bash
docker compose up --build
```

This starts PostgreSQL and the API. Migrations are applied automatically on startup.

| What | Where |
|---|---|
| Swagger UI | http://localhost:8080/swagger |
| OpenAPI document | http://localhost:8080/swagger/v1/swagger.json |
| Health (process is up) | http://localhost:8080/health |
| Health (database reachable) | http://localhost:8080/health/ready |

To run the API outside Docker instead (needs the .NET 8 SDK or later):

```bash
docker compose up -d db
dotnet run --project src/AtmCashManagement.Api --urls http://localhost:8080
```

### The tests

Requirement: .NET SDK 8 or later. The integration tests also need a running Docker engine, because
they start a real PostgreSQL with Testcontainers.

```bash
dotnet test
```

| Project | What it covers |
|---|---|
| `tests/AtmCashManagement.UnitTests` | The 8 acceptance tests of B3 (`NoteSelectorAcceptanceTests`), edge cases of the note selection, cassette allocation, the state machine, cassette layout rules. No database. |
| `tests/AtmCashManagement.IntegrationTests` | The real API (in-process) against a real PostgreSQL: parallel quotes (B6), idempotent confirm, quote expiry, daily limit, lifecycle errors, deposit overflow, four-eyes reconciliation, ledger consistency check, outbox, and the full end-to-end scenario. |

Only the unit tests: `dotnet test tests/AtmCashManagement.UnitTests`

### The Postman collection

Files are in [`postman/`](postman/). Import the collection (and optionally the environment, which
only sets `baseUrl`), then run the requests in order or use the Collection Runner. Each run creates
a new ATM and card, so the collection can be run repeatedly.

From the command line:

```bash
npx newman run postman/ATM-Cash-Management.postman_collection.json -e postman/ATM-Cash-Management.local.postman_environment.json
```

Order: create ATM → configure cassettes → load cash → put in service → request quote → confirm
(→ confirm again with the same key) → deposit → switch to maintenance → reconcile (→ approve) →
report → inventory → consistency check → movements.

---

## API overview

| Method and path | Purpose | Requirement |
|---|---|---|
| `POST /api/atms`, `GET /api/atms`, `GET /api/atms/{id}` | Register and view ATMs | B1 |
| `PUT /api/atms/{id}/cassettes` | Configure the five cassettes | A3–A7 |
| `POST /api/atms/{id}/cassettes/load` | Record cash loaded by hand | A7 |
| `POST /api/atms/{id}/cassettes/unload` | Record cassettes emptied by hand | B8 (UNLOAD) |
| `POST /api/atms/{id}/state` | Request `IN_SERVICE` or `MAINTENANCE` | B2 |
| `GET /api/atms/{id}/inventory` | Notes per denomination and total balance | A12 |
| `POST /api/atms/{id}/withdrawals/quotes` | Step 1: quote, reserves notes for 60 s | A9, B3–B6 |
| `POST /api/atms/{id}/withdrawals/quotes/{quoteId}/confirm` | Step 2: dispense. Needs `Idempotency-Key` | A10, B4 |
| `POST /api/atms/{id}/withdrawals/quotes/{quoteId}/cancel` | Cancel a quote | B4 |
| `POST /api/atms/{id}/deposits` | Deposit notes | A11, B7 |
| `POST /api/atms/{id}/reconciliations` | Record a physical count. Needs `X-User` | A8, B9 |
| `POST /api/atms/{id}/reconciliations/{rid}/approve` · `/reject` | Four-eyes decision. Needs `X-User` | B9 |
| `GET /api/atms/{id}/movements` | Movement ledger of an ATM | B8 |
| `GET /api/atms/{id}/consistency-check` · `GET /api/consistency-check` | Stored balances vs. balances rebuilt from movements | B8 |
| `GET /api/reports/operations` | Operations report (optionally `?atmId=`) | B10 |
| `GET /api/exchange-rates` · `PUT /api/exchange-rates/{currency}` | Rates used for TRY valuation | B9 |

### Error model

Every error is an RFC 7807 `application/problem+json` document with two extra members: a stable,
machine-readable `code` and a `traceId`. Some errors add fields that help the caller react.

```json
{
  "type": "urn:atm-cash:error:atm.operation_not_allowed_in_state",
  "title": "Conflict",
  "status": 409,
  "detail": "WITHDRAW is not allowed while the ATM is MAINTENANCE. Allowed states: IN_SERVICE.",
  "instance": "/api/atms/7f0c.../withdrawals/quotes",
  "code": "atm.operation_not_allowed_in_state",
  "currentState": "MAINTENANCE",
  "operation": "WITHDRAW",
  "allowedStates": ["IN_SERVICE"],
  "traceId": "00-4c1d...-01"
}
```

Status codes: `400` malformed or missing input · `403` four-eyes violation · `404` unknown resource ·
`409` the resource is in a state that does not allow the operation · `422` well-formed request that
breaks a business rule (daily limit, capacity, layout, amount cannot be dispensed). All codes are in
[`DomainException.cs`](src/AtmCashManagement.Api/Domain/DomainException.cs).

---

## Architecture

One deployable API project, organised in layers by folder. I did not split it into several
assemblies: for a service of this size that adds ceremony without adding safety.

```
src/AtmCashManagement.Api
├── Domain/              Pure rules, no EF or HTTP: NoteSelector, CassetteAllocator, AtmStateMachine,
│                        CassetteLayoutRules, entities, DomainException + error codes
├── Application/         Use cases (one transaction each): AtmService, WithdrawalService, DepositService,
│                        ReconciliationService, ReportService, CassetteLedger, request/response contracts
├── Infrastructure/      AppDbContext + migrations, error handling, card hashing, options
├── BackgroundServices/  QuoteExpiryService, OutboxDispatcher
└── Controllers/         Thin HTTP layer
```

The rules that are easy to get wrong (note selection, which cassette a note comes from, state
transitions, layout validation) are pure functions in `Domain/` and are unit tested without a database.

### Data model

| Table | Content |
|---|---|
| `atms` | ATM and its lifecycle state. Its row is the lock for everything that happens on that ATM. |
| `cassettes` | Five per ATM: slot, type, configured currency/denomination, capacity. |
| `cassette_stocks` | Stored balance per cassette, currency and denomination: `count` and `reserved`. A recycling cassette has one active line, the deposit cassette one per kind of note. `CHECK (reserved BETWEEN 0 AND count)`. |
| `cassette_movements` | Append-only ledger (B8). |
| `withdrawal_quotes` | Quote, offered options and reservations (jsonb), status, card hash. |
| `idempotency_records` | Stored result per `Idempotency-Key`. |
| `deposit_transactions` | Deposit and its result (audit). |
| `reconciliations` | Count, per-line differences (jsonb), status, who counted, who decided. |
| `exchange_rates` | Rate to TRY per currency (seeded with sample values). |
| `outbox_messages` | Events waiting to be published. |

### Key design decisions

#### 1. Note selection (B3)

The requirement is the fewest notes with a *limited* number of notes per denomination. Greedy
("largest first") fails: with 50×3 and 20×10 it takes three 50s for 160 and is stuck 10 short,
although 50×2 + 20×3 works. This is a bounded knapsack problem, so
[`NoteSelector`](src/AtmCashManagement.Api/Domain/NoteSelector.cs) uses dynamic programming:

- `minNotes[a]` = fewest notes that add up to amount `a`, built one denomination at a time, each
  used at most as often as the cassettes allow.
- Amounts are divided by the GCD of the denominations (10), so a completely full ATM
  (4 × 100 × 500 = 200,000) needs a table of only 20,001 cells.
- The same table answers the alternatives: the **lower** alternative is the nearest reachable cell
  below the request, the **upper** one the nearest above. So the alternatives are min-note
  breakdowns as well (acceptance test 8).
- The table is only built up to `min(cash in the ATM, max(request, remaining daily limit))`, so an
  upper alternative above the customer's limit is never produced.
- Tie-break: if two breakdowns have the same number of notes, the one with more high-value notes
  wins. This only makes the result deterministic.

I considered brute force over note counts and rejected it: it is fine for four denominations but
gets worse with every denomination, and the DP is not longer to write.

#### 2. Concurrency (B6)

**Pessimistic row lock per ATM.** Every operation that changes an ATM's cash starts its
transaction with `SELECT ... FROM atms WHERE id = @id FOR UPDATE`
([`AppDbContext.LockAtmAsync`](src/AtmCashManagement.Api/Infrastructure/AppDbContext.cs)) and reads
the cassette balances *after* it holds the lock. Two quotes for the same ATM therefore run one after
the other; the second one sees the first one's reservation. Different ATMs do not block each other.

Why this and not optimistic concurrency (row versions + retry): a quote reads all cassettes of a
currency and then writes some of them. With optimistic locking, two parallel quotes conflict almost
every time, and each conflict means recomputing the selection and retrying. A physical ATM serves
one customer at a time, so serialising per ATM costs nothing in practice and is easy to reason about.
It also works with several API instances, because the lock is in the database, not in process memory.

As a second line of defence the database itself refuses an oversold cassette:
`CHECK (reserved >= 0 AND reserved <= count)`.

Test: `Parallel_quotes_never_reserve_the_same_note_twice` sends 20 parallel quote requests for 10
available notes and expects exactly 10 accepted, 10 rejected, `reserved = 10`, and a clean ledger.

#### 3. Quote, reservation, expiry (B4)

- A quote stores its options with the exact cassette each note comes from, plus the reservation.
- **When there is no exact match, both alternatives are offered.** The customer will confirm at
  most one of them, so the reservation is the *larger need per cassette*, not the sum. This
  guarantees either option can be paid while keeping as few notes as possible locked away. On
  confirmation the chosen notes are dispensed and the rest of the reservation is released.
- Expiry is enforced in three places, so correctness never depends on timing:
  1. Confirm compares `now` with `expiresAt`; an overdue quote is rejected even if its status is still `ACTIVE`.
  2. Creating a quote first releases overdue reservations of that ATM (under the lock), so an
     expired quote never blocks the next customer.
  3. `QuoteExpiryService` (hosted service) sweeps every few seconds, so balances and reports catch
     up even when no further request reaches that ATM.
- Time comes from `TimeProvider`, which lets the tests move the clock instead of waiting 60 seconds.

#### 4. Idempotency (B4)

The confirm request requires an `Idempotency-Key` header. Inside the confirm transaction, after the
ATM lock is taken:

1. If a record for the key exists → return the stored response (plus `Idempotency-Replayed: true`).
2. Otherwise dispense, and insert the record **in the same transaction** as the cash movement.

Because the record and the debit commit together, there is no window where the cash is gone but the
key is unknown. Two parallel requests with the same key are serialised by the ATM lock; the second
finds the record. The key's primary key constraint is the safety net. Reusing a key for a different
quote or option is rejected (`422 idempotency.key_reused_with_different_request`); confirming an
already confirmed quote with a *different* key is rejected (`409 quote.already_confirmed`).

Only successful confirmations are stored. Failures (expired, cancelled) are deterministic from the
quote's state, so a retry gets the same error anyway.

#### 5. Movement ledger (B8)

[`CassetteLedger`](src/AtmCashManagement.Api/Application/CassetteLedger.cs) is the only code that
changes a balance; it always writes the movement and the balance together. A movement has two
deltas, because a cassette has two numbers:

| Movement | `count_delta` | `reserved_delta` |
|---|---|---|
| `LOAD` | +n | 0 |
| `UNLOAD` | −n | 0 |
| `DEPOSIT` | +n | 0 |
| `RESERVATION` | 0 | +n |
| `RESERVATION_RELEASE` | 0 | −n |
| `WITHDRAWAL` | −n | −n (the notes leave and stop being reserved) |
| `RECONCILIATION_ADJUSTMENT` | ±n | 0 |

So `count = SUM(count_delta)` and `reserved = SUM(reserved_delta)`. The consistency check endpoint
recomputes both sums with a `GROUP BY` and compares them with `cassette_stocks`, inside one
`REPEATABLE READ` transaction so that concurrent traffic cannot produce a false alarm.

Immutability is enforced twice: `SaveChanges` rejects modified or deleted movements, and a database
trigger (`trg_cassette_movements_append_only`, added to the migration) rejects `UPDATE`/`DELETE`
even for direct SQL. Corrections are made with new movements, never by editing history.

The stored balance is kept (instead of always summing movements) because every quote needs it and
the ledger only grows.

#### 6. Daily limit and card numbers (B5)

- The card number is never stored. `CardHasher` computes HMAC-SHA256 with a secret key; the hash and
  the last four digits are stored. A plain hash would not protect anything, because the space of
  card numbers is small enough to enumerate; the key prevents that.
- There is no usage counter that could drift. The usage is derived from the quotes themselves:
  confirmed amounts of the business day + held amounts of quotes that are still valid.
- While a quote is active it counts with the **largest amount it could still be confirmed for**.
- The same card could be used at two ATMs at the same moment, where the ATM lock does not help.
  "Check the limit, then reserve" is made atomic with a transaction-scoped PostgreSQL advisory lock
  on `(card hash, currency)`. Lock order is always ATM → card, and a transaction holds at most one of each.
- A request above the remaining limit is rejected (`422 withdrawal.daily_limit_exceeded`, with
  `remaining` in the body). The upper alternative is only offered if it fits the remaining limit.

#### 7. Reconciliation (B9)

- The count must cover all five cassettes. Differences are valued in TRY as
  `|difference| × denomination × rate` and summed as absolute values, so a shortage in one cassette
  cannot be hidden by a surplus in another.
- The adjustment movements are booked immediately: the physical count is a fact, and the system
  should match reality. Approval does not decide what is in the cassettes; it decides whether the
  ATM may go back into service. `IN_SERVICE` is refused while the latest reconciliation is
  `AWAITING_APPROVAL` or `REJECTED`.
- Approve/reject must come from a different `X-User` than the one who counted (case-insensitive).
- A rejected reconciliation is fixed by a recount, which books a compensating adjustment.
- The rate used is stored on each reconciliation line, so old reconciliations stay explainable when rates change.

#### 8. Low-cash alerts and outbox (B10)

`CassetteLedger` compares the available count before and after each movement. When a recycling
cassette crosses below 10% of capacity it adds a `LowCashAlert` row to `outbox_messages` through the
same `DbContext`, so it commits or rolls back with the operation. `OutboxDispatcher` (hosted
service) reads unsent rows with `FOR UPDATE SKIP LOCKED`, logs them and sets `sent_at`. Delivery is
at-least-once.

#### 9. Smaller decisions

- **PostgreSQL** over SQL Server: lighter container, and advisory locks and `SKIP LOCKED` fit the design.
- **Enums as strings** in the database and in JSON (`IN_SERVICE`, `AWAITING_APPROVAL`): readable in SQL and in logs.
- **jsonb** for quote options and reconciliation lines: written once, always read as a whole, never queried by field.
- **Amounts are integers.** Banknote amounts have no fractions; `decimal` is used only for TRY valuation.

---

## Assumptions

Points the requirements leave open, and what I decided.

**Lifecycle**

1. `CONFIGURED` and `LOADED` are reached by configuring cassettes and loading cash; they cannot be
   requested directly. Requestable transitions: `LOADED → IN_SERVICE`, `LOADED → MAINTENANCE`,
   `IN_SERVICE → MAINTENANCE`, `MAINTENANCE → IN_SERVICE`.
2. I read "allowed only in MAINTENANCE (or in UNCONFIGURED/CONFIGURED during initial setup)"
   literally: in `LOADED`, loading more cash or reconfiguring requires `MAINTENANCE` first. The
   initial load is therefore one request that can cover several cassettes.
3. Moving to `MAINTENANCE` cancels all open quotes of that ATM and releases their reservations.
4. Cassettes can only be reconfigured while all of them are empty; otherwise the notes inside
   would become unaccounted for. Unloading is an explicit, recorded step (`UNLOAD`).
5. An ATM may return to service from `MAINTENANCE` with empty recycling cassettes (it can still take deposits).
   The first time, the `LOADED` state guarantees that cash was loaded.

**Cassettes**

6. "At least one and at most two … foreign currency" is read as a hard rule: exactly 1 or 2 of the 4 recycling cassettes are USD/EUR.
7. The deposit cassette cannot be loaded by hand; it only receives deposits. Its capacity of 100 is the total over all kinds of notes.
8. Two cassettes with the same currency and denomination: "higher fill ratio first" is evaluated
   note by note on the *available* notes (count − reserved), so once the cassettes are level they
   drain evenly. Ties go to the lower slot. For deposits I chose the mirror rule: the emptiest matching cassette is filled first.

**Withdrawal**

9. With no exact match, both alternatives are offered and reserved together (see decision 3). The
   confirm request names the chosen option (`LOWER`/`UPPER`); with a single option the body may be empty.
10. If neither an exact match nor any alternative exists (no cash at all), no quote is created (`422 withdrawal.cannot_dispense`).
11. A requested amount above the remaining daily limit is rejected, rather than silently offering a smaller amount.
12. The "day" of the daily limit is the calendar day in `Europe/Istanbul` (configurable). A quote
    counts towards the day on which it was created, even if it is confirmed just after midnight.
13. Limits are per card and currency across the whole network, not per ATM.
14. Cancelling a quote twice is harmless and returns `200`. Confirming a cancelled or expired quote is `409`.

**Deposit**

15. A deposit is a single step (no quote/confirm) and is not tied to a card or account; crediting the customer is outside the scope.
16. A currency/denomination outside 20/50/100/500 in the `notes` list is a validation error. Notes the machine cannot identify are reported in `unrecognisedCount`.
17. Deposited notes in a recycling cassette are available for withdrawals immediately.
18. Kinds of notes are processed in request order, which matters only when the deposit cassette is nearly full.

**Reconciliation**

19. "Exceeds TRY 1,000" means strictly greater than 1,000.
20. For the deposit cassette, a kind of note that is not listed in the count is taken as counted zero.
21. While a reconciliation awaits approval, a new one cannot be started for the same ATM; it has to be approved or rejected first.
22. A rejection also needs a different user than the one who counted.

**Reporting**

23. "Available" means dispensable: count − reserved for recycling cassettes, always 0 for the deposit cassette. Totals per currency include the deposit cassette, because it is the bank's cash.
24. The low-cash alert fires when the available count *crosses* below 10% (fewer than 10 notes), not on every later withdrawal from a cassette that is already low. Emptying a cassette on purpose (`UNLOAD`) does not raise it.

---

## Unfinished items and known limitations

Nothing from the assignment is left out. These are the limitations I am aware of:

- **No authentication or authorisation.** `X-User` is trusted as-is, as the assignment allows. A real system needs authenticated users and roles (e.g. only supervisors approve).
- **Quote creation is not idempotent.** If the response to a quote request is lost and the client retries, a second quote is created; the first one releases its notes after 60 seconds. Only confirm takes an `Idempotency-Key`, as required.
- **Idempotency records and outbox rows are never purged.** A retention job is needed in production.
- **The outbox publishes to the log.** Delivery is at-least-once, so a real consumer must tolerate duplicates.
- **All operations on one ATM are serialised.** This is intended, but a long-running transaction on an ATM would delay its other requests. There is no lock timeout configured.
- **Exchange rates are maintained by hand** through the API; the seeded values are samples, not market rates.
- **Unload is whole-cassette only**; partial removal of notes is not supported.
- **Low-cash alerts are not repeated** while a cassette stays low, and there is no "back to normal" event.
- **The card hash key has no rotation scheme.** Rotating it would reset daily-limit tracking for the current day.
- **List endpoints have simple limits, no real paging** (`movements` returns the latest 100 by default, at most 500).
- **Secrets in `docker-compose.yml` and `appsettings.json` are development values** so that the project starts with one command. They must come from a secret store in a real environment.
- **Not load tested.** The concurrency tests prove correctness, not throughput.

---

## Use of AI tools

**Tool:** Claude Code (Anthropic's coding agent).

**How it was used:** 
Due to tight project deadlines, Claude Code was utilized as a code generation and drafting assistant. Based on the assignment requirements, it provided the initial architecture draft, domain and service boilerplate, database migrations, and basic test setups.

**My contributions:**
- **Code Review & Quality Control:** Reviewed the AI-generated code line by line across all layers, verified domain logic, and refactored parts that did not align with requirements or clean architecture standards.
- **Bug Fixing & Troubleshooting:** Diagnosed edge-case failures, mathematical inconsistencies, and subtle framework errors produced by the AI (detailed below).
- **Environment & Containerization:** Configured, verified, and debugged the multi-container Docker environment (`docker-compose`), ensuring PostgreSQL, API networking, automatic migrations, and health checks ran smoothly.
- **Testing & Verification:** Ran and monitored the test suites (`dotnet test`), debugged failing integration tests using Testcontainers, and executed end-to-end API scenario runs via Postman to verify full system reliability.

**Corrections made to AI outputs** (spotted and resolved during review and testing):
- **Invalid Test Assertion:** A unit test asserted that 66,990 could be paid from a full ATM. In reality, the total was 67,000 without a 10-denomination note, making that withdrawal mathematically impossible. The faulty test assumption was identified and rewritten.
- **Algorithmic Explanation Error:** Fixed an incorrect code comment explaining why the greedy approach fails; the original example actually worked with greedy. Replaced it with a mathematically sound counter-example (50×3, 20×10, request 160).
- **Response Header/Format Bug:** Controllers were annotated with `[Produces("application/json")]`, which broke RFC 7807 problem details by forcing `application/json` instead of `application/problem+json` on errors. Discovered during integration testing and corrected.