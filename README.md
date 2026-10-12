# URL Shortener: AI-Assisted Engineering Prototype (.NET 8)

> **Author:** Archi Sahu
> **Timeline:** 3 days
> **Stack:** .NET 8, ASP.NET Core Web API, Swagger/OpenAPI, EF Core 8, SQL Server 2022, Redis 7, FluentValidation, Polly, Serilog, prometheus-net, Docker, GitHub Actions, xUnit, Moq, FluentAssertions

---

## Table of Contents

1. [Overview](#1-overview)
   - [Assessment coverage](#11-assessment-coverage)
2. [Quick Start](#2-quick-start)
3. [Architecture](#3-architecture)
4. [API Reference](#4-api-reference)
5. [Database Schema](#5-database-schema)
6. [AI-Assisted Engineering Workflow](#6-ai-assisted-engineering-workflow)
7. [AI Collaboration Log](#7-ai-collaboration-log)
8. [Scenario 1: Greenfield](#8-scenario-1-greenfield)
9. [Scenario 2: Brownfield](#9-scenario-2-brownfield)
10. [Scenario 3: Ambiguous Requirement](#10-scenario-3-ambiguous-requirement)
11. [Testing & Validation](#11-testing--validation)
12. [Design Decisions & Trade-offs](#12-design-decisions--trade-offs)
13. [Limitations](#13-limitations)
14. [Lessons Learned](#14-lessons-learned)
15. [What I Would Build Next](#15-what-i-would-build-next)

---

## 1. Overview

This is a URL shortener service built with .NET 8 and ASP.NET Core. It turns long URLs into short 6-character codes, redirects visitors to the original address, and records anonymous click analytics (clicks, unique visitors, countries, browsers, daily trends). It is intended as a production-style backend: a relational schema with deliberate indexes, optimistic locking, a Redis cache on the redirect path, per-client rate limiting, a cleanup job for expired links, retries on transient database failures, Prometheus metrics, and click recording that never slows a redirect down.

The goal of the project is to demonstrate engineer-led, AI-assisted development rather than blind code generation. AI produced first drafts of the schema, API, persistence, cache, analytics, rate limiting, cleanup, reporting, retry and metrics code; I reviewed each against the requirements, ran and tested it, and recorded the decisions, the bugs I caught and the trade-offs I accepted (see [section 7](#7-ai-collaboration-log)).

**Implemented**

- Create, redirect (HTTP 302), update, and delete short URLs
- Duplicate URL detection (the same active URL returns the existing code)
- Configurable link expiry, enforced on every redirect and update (404 for unknown codes, 410 for expired)
- Redis cache-aside on the redirect path, with invalidation on update and delete, and automatic fallback to SQL if Redis is down
- Asynchronous click analytics: clicks, unique visitors (hashed, no raw IPs), countries and browsers
- Reporting endpoints: clicks per day (zero-filled), country breakdown and browser breakdown, each with an optional date range
- Rate limiting per client IP (stricter on writes), answered with 429 and `Retry-After`
- Background cleanup of links that expired more than a grace period ago
- Optimistic locking (`RowVersion`) on updates
- Global exception handling with RFC 7807 problem responses, input validation (FluentValidation), health check, Swagger
- Structured logging with Serilog (message templates with named properties; levels and output format set in `appsettings.json`)
- GitHub Actions pipeline (`.github/workflows/ci.yml`): restore, build, unit and integration tests against a SQL Server service container, coverage upload, and a Docker image build
- Polly retry policies on idempotent database operations (reads, reports, deletes); inserts and optimistic-lock saves are never retried
- Metrics at `/metrics` (Prometheus format): HTTP request counts and latency, links created, cache hits and misses, click queue depth, clicks stored and dropped, database retries, expired links deleted

**Not built**

- Kafka / Azure Service Bus and Azure deployment (optional in the assessment); see [section 15](#15-what-i-would-build-next)

### 1.1 Assessment Coverage

| Assessment requirement | Status | Where |
|---|---|---|
| POST `/api/v1/shorten`, GET `/{code}` (302), GET `/api/v1/analytics/{code}`, PUT and DELETE `/api/v1/shorten/{code}` | Done | [Section 4](#4-api-reference) |
| Duplicate URL detection, configurable expiration | Done | [Section 5](#5-database-schema), [Section 2 configuration](#configuration) |
| Redis caching | Done | [Section 3](#3-architecture), Entry 6 |
| Rate limiting middleware | Done | [Section 9](#9-scenario-2-brownfield) |
| Global exception handling, FluentValidation, health checks, Swagger | Done | [Section 4](#4-api-reference) |
| Structured logging (Serilog) | Done | Entry 10 |
| Background job: expired link cleanup | Done | [Section 3](#rate-limiting-and-cleanup) |
| Retry policies (Polly) | Done (idempotent database operations only) | Entry 11, `ResilientShortUrlRepository` |
| Metrics and monitoring | Done (`/metrics`, Prometheus format, plus `/health`) | Entry 11, `AppMetrics` |
| Unit, integration, performance and security tests | Done | [Section 11](#11-testing--validation) |
| Docker, docker-compose | Done | [Section 2](#2-quick-start) |
| CI/CD (GitHub Actions) | Done (build, test, image; no deploy) | `.github/workflows/ci.yml` |
| Architecture diagram, ER diagram | Done | `docs/` |
| Scenarios 1, 2 and 3 | Done | Sections 8 to 10 |
| AI collaboration log, trade-offs, limitations, lessons learned | Done | Sections 7, 12, 13, 14 |
| Kafka / Azure Service Bus, Azure deployment (optional) | Not built | Async analytics uses an in-process queue ([Section 12](#12-design-decisions--trade-offs)) |

---

## 2. Quick Start

### Prerequisites

- .NET 8 SDK
- Docker (Docker Desktop on Windows)

### Run everything with Docker

```bash
git clone https://github.com/Archi23Sahu/UrlShortener
cd UrlShortener
docker compose up --build
```

| Service      | URL                            |
|--------------|--------------------------------|
| API          | http://localhost:8080          |
| Swagger UI   | http://localhost:8080/swagger  |
| Health check | http://localhost:8080/health   |
| Metrics      | http://localhost:8080/metrics  |

The compose file starts SQL Server, Redis and the API. The API waits for both to report healthy, and applies the EF Core migrations on startup (`Database__MigrateOnStartup=true`). `docker compose up --build` was run end to end: health check, create, redirect, analytics and browser report all worked.

To see the metrics once the API has served a few requests:

```bash
curl -s http://localhost:8080/metrics | grep urlshortener_
```

If ports 1433 or 6379 are already in use (for example by containers started with `docker run`), stop those first.

### Run the API locally (for development)

Start only the dependencies, then run the API from your IDE or the CLI.

```bash
docker run -d --name urlshortener-sql -p 1433:1433 -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD=Your_strong_Passw0rd! mcr.microsoft.com/mssql/server:2022-latest
docker run -d --name urlshortener-redis -p 6379:6379 redis:7-alpine
dotnet ef database update --project src
dotnet run --project src
```

`dotnet run` prints the local URLs. In Visual Studio the profile uses `https://localhost:7037`.

### Run tests

```bash
dotnet test                              # everything
dotnet test tests/UnitTests              # no external services
docker compose up -d sqlserver           # needed for the integration tests
dotnet test tests/IntegrationTests
```

Integration tests use their own database (`UrlShortener_IntegrationTests`) on the SQL Server at `localhost,1433`. Set `URLSHORTENER_TEST_DB` to point at a different server.

Load test (start the API with rate limiting off first):

```bash
# PowerShell: $env:RateLimit__Enabled="false"; dotnet run --project src --urls https://localhost:7037
dotnet run --project tests/PerformanceTests -c Release -- https://localhost:7037 1000 1000
```

### Configuration

| Setting                        | Description | Default |
|--------------------------------|-------------|---------|
| `ConnectionStrings:Default`    | SQL Server connection string | `Server=localhost,1433;Database=UrlShortener;User Id=sa;...;TrustServerCertificate=True` |
| `ConnectionStrings:Redis`      | Redis connection string. If empty, an in-memory cache is used | `localhost:6379` |
| `Shortener:BaseUrl`            | Base URL used in short links | `http://localhost:8080` |
| `Shortener:DefaultExpiryDays`  | Link lifetime when the caller gives none | `30` |
| `Shortener:MaxExpiryDays`      | Largest `expiryDays` a caller may request | `365` |
| `Cache:TtlMinutes`             | Longest a redirect lookup is cached (also capped by the link's remaining life) | `10` |
| `Cache:FailureCooldownSeconds` | After a Redis error, skip the cache for this long | `30` |
| `Analytics:Salt`               | Secret mixed into the visitor hash. **Override in every real environment** (`Analytics__Salt`) | `dev-only-change-me` |
| `Analytics:QueueCapacity`      |  In-memory click queue size | `10000` |
| `Analytics:BatchSize`          |   max rows per insert | `100` |
| `Analytics:FlushIntervalSeconds` | max wait before a flush | `2` |
| `Analytics:CountryHeader`      | Request header carrying a 2-letter country code (set by a CDN or proxy) | `CF-IPCountry` |
| `RateLimit:Enabled`            | Master switch for rate limiting (turn off for load tests) | `true` |
| `RateLimit:Write:PermitLimit` / `WindowSeconds` | Create, update and delete requests per client IP per window | `20` / `60` |
| `RateLimit:Read:PermitLimit` / `WindowSeconds` | Redirect and analytics requests per client IP per window | `300` / `60` |
| `Cleanup:Enabled`              | Run the expired-link cleanup job | `true` |
| `Cleanup:IntervalMinutes` / `GracePeriodDays` / `BatchSize` | How often it runs, how long after expiry a link is kept, rows deleted per batch | `60` / `7` / `1000` |
| `Resilience:Enabled`           | Retry transient database failures on idempotent operations | `true` |
| `Resilience:MaxRetryAttempts` / `BaseDelayMilliseconds` | Retries after the first try, and the first delay (doubles each retry, with jitter) | `3` / `100` |
| `Database:MigrateOnStartup`    | Apply migrations when the API starts | `false` (`true` in compose) |
| `Serilog:MinimumLevel`         | Log levels, with per-namespace overrides (for example `UrlShortener.Caching.CachedUrlShortenerService` at `Debug` shows cache hits and misses) | `Information` |

---

## 3. Architecture

### Component diagram

![Architecture diagram](docs/Architecture%20diagram.png)

Diagram source: [docs/architecture.mmd](docs/architecture.mmd) (Mermaid).

1. Front door: every request passes the exception-handling middleware (consistent RFC 7807 errors, no internals leaked) and then the rate limiter (per client IP: 20 writes and 300 reads per minute, otherwise 429). /health and Swagger are not limited.
2. Controllers: ShortenController creates, updates and deletes links (input checked first), RedirectController redirects, and AnalyticsController serves statistics.
3. Lookup layer: CachedUrlShortenerService checks Redis first and falls back to UrlShortenerService, which holds the business rules. A Polly retry decorator (ResilientShortUrlRepository) wraps ShortUrlRepository, the only class that talks to SQL Server, and retries transient SQL errors on idempotent operations only.
4. Clicks: a redirect only places a record in a bounded in-memory queue. A background service saves queued clicks to the database in batches, so redirects never wait for the database. Analytics can trail a click by about 2 seconds.
5. Reports: ReportService returns daily, country and browser breakdowns, aggregated in SQL.
6. Cleanup: an hourly job deletes links that expired more than 7 days ago, in batches.
7. Metrics: HTTP request metrics and the application counters in AppMetrics are served at /metrics (Prometheus format, not rate limited).
8. Design idea: keep the redirect path fast, do slow work in the background, and keep all database access in one repository.


### Redirect request flow

1. Client calls `GET /{code}`. The route only accepts 1 to 12 Base62 characters; anything else gets a plain 404 before reaching a controller.
2. `CachedUrlShortenerService` looks the code up in Redis. A hit returns the link id, target URL and expiry; a link past its expiry in the cache falls through to the database.
3. On a miss, `UrlShortenerService` reads SQL. Unknown code returns 404, expired link returns 410 (neither is cached). A valid result is stored in Redis for the shorter of 10 minutes and the link's remaining life.
4. `ClickTracker` builds a click record (link id, UTC time, hashed visitor, country, browser family) and puts it in a bounded in-memory queue. It does no I/O and never throws; if the queue is full the click is dropped and a warning is logged.
5. The API returns `302` with the `Location` header. The redirect never waits for the database write.
6. In the background, `ClickRecorderBackgroundService` writes queued clicks to `ClickEvents` in batches (up to 100 rows, or every 2 seconds), using a fresh DI scope per batch.

### Write path and cache invalidation

`PUT` and `DELETE` go through the same decorator. After the database change succeeds, the cache entry for that code is removed, so the next redirect reads the new data from SQL. Changes made directly in the database bypass this invalidation; the cache TTL (10 minutes at most) bounds how long a stale entry can be served.

### Rate limiting and cleanup

Every request to the shorten, redirect and analytics endpoints passes the built-in ASP.NET Core rate limiter first (`/health` and Swagger are not limited). Counters are fixed-window and per client IP, with one policy for writes (create, update and delete share a counter) and one for reads. A rejected request gets `429`, a `Retry-After` header and an RFC 7807 body. `ExpiredLinkCleanupService` runs hourly (after a 30 second start delay) and deletes links that expired more than 7 days ago in batches of 1000; their click events go with them through the cascade.

### Project structure

```
UrlShortener/
├── src/
│   ├── Controllers/
│   ├── Services/            (URL service, Base62, normalizer)
│   ├── Repositories/        (DbContext, repository, Polly retry decorator, migration helper)
│   ├── Entities/
│   ├── DTOs/
│   ├── Middleware/
│   ├── Configurations/      (EF mappings and options classes)
│   ├── Analytics/           (click queue, tracker, visitor hash, country resolver, browser detector)
│   ├── RateLimiting/
│   ├── Caching/             (Redis cache decorator, circuit breaker)
│   ├── Metrics/             (Prometheus counters and gauges)
│   ├── BackgroundServices/  (click recorder, expired-link cleanup)
│   ├── Validators/
│   └── Exceptions/
├── tests/
│   ├── UnitTests/
│   ├── IntegrationTests/    (WebApplicationFactory + SQL Server, includes security tests)
│   └── PerformanceTests/    (console load tool)
├── .github/workflows/       (CI pipeline)
├── docker/
├── docs/                    (architecture and ER diagrams, SQL schema and migration scripts)
├── docker-compose.yml
└── README.md
```

---

## 4. API Reference

| Method | Endpoint                                       | Description                                                                  | Success   | Errors        |
|--------|------------------------------------------------|------------------------------------------------------------------------------|-----------|---------------|
| POST   | `/api/v1/shorten`                              | Create a short URL (200 with the existing link if the URL is already active) | 201 / 200 | 400, 429      |
| GET    | `/{code}`                                      | Redirect to the original URL                                                 | 302       | 404, 410, 429 |
| GET    | `/api/v1/analytics/{code}`                     | Click analytics (total, unique, countries)                                   | 200       | 404, 429      |
| GET    | `/api/v1/analytics/{code}/daily?from=&to=`     | Clicks and unique visitors per UTC day (default last 30 days, zero-filled)   | 200       | 400, 404, 429 |
| GET    | `/api/v1/analytics/{code}/countries?from=&to=` | Clicks per country with percentage and an unknown count                      | 200       | 400, 404, 429 |
| GET    | `/api/v1/analytics/{code}/browsers?from=&to=`  | Clicks per browser family with percentage                                    | 200       | 400, 404, 429 |
| PUT    | `/api/v1/shorten/{code}`                       | Change the target URL and optionally reset expiry                            | 200       | 400, 404, 409, 410, 429 |
| DELETE | `/api/v1/shorten/{code}`                       | Delete a short URL and its click history                                     | 204       | 404, 429      |
| GET    | `/health`                                      | Health check (includes the database)                                         | 200       | 503           |
| GET    | `/metrics`                                     | Prometheus metrics                                                           | 200       |               |

Rate limits (per client IP, per minute): 20 for create, update and delete together, 300 for redirects and analytics. Over the limit the API returns `429` with a `Retry-After` header. `/health` is not limited.

Swagger cannot follow the `302` redirect (the browser blocks the cross-origin request), so test `GET /{code}` in a browser tab or with `curl -i`.

### Example: create

```http
POST /api/v1/shorten
Content-Type: application/json

{ "url": "https://learn.microsoft.com/en-us/dotnet/", "expiryDays": 30 }
```

```json
{
  "shortUrl": "http://localhost:8080/100001",
  "expiresAt": "2026-11-08"
}
```

`expiryDays` is optional (default 30, maximum 365). `expiresAt` is a UTC calendar date, as in the assessment contract; the exact expiry instant is kept and enforced by the service, so the link stops working at that time of day on the date shown.

### Example: update

```http
PUT /api/v1/shorten/100001
Content-Type: application/json

{ "url": "https://example.com/new-target", "expiryDays": 60 }
```

Returns `200` with the same shape as create. Returns `409` if another request changed the link first (optimistic lock), `410` if the link has already expired.

### Example: analytics

```json
{
  "clicks": 3,
  "uniqueVisitors": 2,
  "countries": { "US": 2, "IN": 1 }
}
```

Counts can lag a redirect by about 2 seconds because clicks are written asynchronously. `countries` is empty unless a CDN or proxy sets the `CF-IPCountry` header.

### Example: reports

`from` and `to` are dates (`yyyy-MM-dd`, UTC days, both ends inclusive); a value that is not a date, `from` after `to`, or a range over 366 days returns 400. The response shapes are shown in [section 10](#step-4-design-proposal-and-api-design). Example: `GET /api/v1/analytics/100001/daily?from=2026-10-01&to=2026-10-10`.

### Error format (RFC 7807)

All errors use `application/problem+json` with a `traceId` for log correlation. Internal details are never returned for 500 errors.

```json
{
  "title": "Short URL not found",
  "status": 404,
  "detail": "No short URL exists for code 'zzz999'.",
  "traceId": "0HN7..."
}
```

Validation failures (400) add an `errors` object keyed by field name.

| Status | Cause |
|-----|----------|
| 400 | Invalid or missing URL, bad `expiryDays`, bad report date range |
| 404 | Unknown short code |
| 409 | Optimistic-lock conflict on update |
| 410 | Link has expired |
| 429 | Rate limit exceeded (see `Retry-After`) |
| 500 | Unexpected failure (details hidden) |

---

## 5. Database Schema

The full script is in [`docs/schema.sql`](docs/schema.sql).

### ER diagram

![ER diagram](docs/ER%20Diagram.png)


### Keys and indexes, and why
| Index / key | Purpose |
|---|---|
| PK `Id` (bigint identity) on both tables | Surrogate key. Also the input to Base62 encoding. |
| Unique on `ShortUrl.Code` (case-sensitive collation `SQL_Latin1_General_CP1_CS_AS`) | Redirect lookup is by code, so this must be an index seek. Base62 uses upper and lower case, so `aB` and `Ab` are different codes and the default case-insensitive collation would merge them. The unique constraint also guards against collisions. |
| Non-unique on `ShortUrl.OriginalUrlHash` | Duplicate detection. A 2048-character URL cannot be indexed directly (index key limit), so the SHA-256 of the normalized URL is indexed. It is non-unique on purpose so an expired, not-yet-cleaned link never blocks re-shortening the same URL. |
| Non-unique on `ShortUrl.ExpiresAt` | Cleanup job scans for expired rows. |
| Composite on `ClickEvent(ShortUrlId, ClickedAt)` | Analytics queries per link and time range. |
| FK `ClickEvent.ShortUrlId` with cascade delete | Deleting a link removes its click history. |

### Migrations

```bash
dotnet ef migrations add InitialCreate --project src
dotnet ef database update --project src
```

---

## 6. AI-Assisted Engineering Workflow

This is the loop followed for every task in this project:

```
Requirement
   ↓
AI generates options
   ↓
Engineer reviews and decides
   ↓
Engineer refines / edits
   ↓
AI generates tests
   ↓
Engineer validates (runs, extends, fixes)
   ↓
AI drafts documentation
   ↓
Engineer reviews for accuracy
   ↓
Commit (one per scenario)
```

**Principles I followed**

- AI accelerates; I make the decisions and own the result.
- Nothing is committed that I have not read, built and run.
- Every AI-generated test is run, and I add the edge cases it missed.
- Where I rejected or changed AI output, or where it was wrong, I record it (see the log).

---

## 7. AI Collaboration Log

### 7.1 Summary by phase

| Phase | How AI Was Used | Engineer's Role |
|---|---|---|
| Requirements Analysis | Broke the assessment into tasks, listed what each scenario requires, flagged ambiguities | Chose to complete Scenario 1 first, confirmed scope and stack (SQL Server in Docker, Redis, .NET 8) |
| Solution Design | Proposed the schema, indexes, Base62 scheme, cache and analytics designs | Reviewed keys, indexes and trade-offs, picked the options (Base62 over GUID, async analytics, fail-open cache) |
| Implementation | Generated controllers, DTOs, validators, EF Core mappings, repository, services, middleware, cache decorator, background worker | Applied the code to my project, built and ran it, fixed integration issues, tested behaviour manually |
| Testing | Generated xUnit tests (encoder, normalizer, validators, service, cache, analytics) | Ran them, fixed failures, reviewed what is and is not covered |
| Delivery | Wired Serilog and the GitHub Actions pipeline (Entry 10) | Checked the log output, fixed a stale logging namespace, matched CI to the compose setup |
| Resilience and monitoring | Proposed a Polly retry decorator, Prometheus metrics and the tests for both (Entry 11) | Limited retries to idempotent operations, kept Redis on its circuit breaker, wrote the scope down, reviewed the tests |
| Documentation | Drafted README, ER diagram, schema script; reviewed the README against the assessment | Checked against the running system, fixed missing files and stale figures |

### 7.2 Detailed prompt log

Every interaction below follows the same chain:

```
Requirement analysis
   ↓
Prompt
   ↓
AI proposal
   ↓
Human review
   ↓
Accepted / Modified / Rejected
   ↓
Validation (build, tests, manual check)
```

Worked example (Entry 6):

| Step | What happened |
|---|---|
| Requirement | Serve repeat redirects from Redis, keep entries correct after update and delete, keep redirects working if Redis is down |
| Prompt | Asked for the steps first, then for the code |
| AI proposal | Cache-aside decorator with a TTL and a circuit breaker |
| Human review | TTL, invalidation, behaviour when Redis fails |
| Decision | Accepted |
| Validation | Cache hit and miss log lines, update and delete a link, stop Redis |

**About the prompts below.** They are summaries of what I asked, not verbatim transcripts; short quoted fragments are exact. I have kept them honest on purpose, including the vague ones, because the second half of the log shows what a weak prompt cost me.

**Prompt structure I moved towards.** The later entries (6, 7, 11) work better than the early ones because each prompt had the same five parts:

1. **Context:** what already exists and where (project layout, namespaces, shell).
2. **Goal:** the outcome and why it matters (for example "redirects must stay fast").
3. **Constraints:** what must not change or break (the redirect path, existing tests, no new infrastructure).
4. **Output asked for:** steps and options with trade-offs first, code second.
5. **Proof required:** which build, test or manual check shows it works.

Entry format used for every interaction:

```
#### Entry N: <short title>
- Phase
- Task
- Prompt (summary or key excerpt)
- What AI produced
- My review
- Decision: Accepted | Modified | Rejected
- What I changed and why
- Validation
```

#### Entry 1: Requirement analysis

- **Phase:** Requirements
- **Task:** Break the assignment into tasks and surface ambiguities
- **Prompt (summary or key excerpt):** Shared the assessment requirements and asked what I need to do. Later asked for a README template with the AI Collaboration Log pre-structured so I could follow it.
- **What AI produced:** A task breakdown by scenario, a README template, and a list of what each scenario requires (for example that Scenario 1 does not strictly require Docker, while the overall deliverable does).
- **My review:** Checked the breakdown against the updated .NET version of the document and decided to finish Scenario 1 (Tasks 1 to 6, validation, deliverable) before Scenarios 2 and 3.
- **Decision:** Modified (scope ordering is mine)
- **What I changed and why:** Prioritised Scenario 1 so there is a complete working service to extend in Scenarios 2 and 3.
- **Validation:** Checked the task list against the assessment document before starting; later used it as the final checklist.

#### Entry 2: Database design

- **Phase:** Design
- **Task:** ER diagram, entity definitions, EF Core Fluent API mapping and SQL script
- **Prompt (summary or key excerpt):** "Generate: ER Diagram, Entity Definitions" with the project files attached, then "build EF Core DbContext mapping (Fluent API) and a SQL migration script for SQL Server".
- **What AI produced:** `ShortUrl` and `ClickEvent` entities, Fluent API configurations, an ER diagram, schema.sql, and the InitialCreate migration steps.
- **My review:** PK/FK: bigint identity keys, one-to-many with cascade delete. Indexes: unique case-sensitive `Code` for redirect lookups; a hash index for duplicate detection because a 2048-character URL cannot be indexed; `ExpiresAt` for cleanup; `(ShortUrlId, ClickedAt)` for analytics. Query performance: the redirect lookup should be an index seek on `UX_ShortUrls_Code`.
- **Decision:** Accepted, with the review points above confirmed
- **What I changed and why:** Fixed project wiring the generated code assumed: added the missing `AddDbContext` registration and connection string so `dotnet ef migrations add` could run. Chose SQL Server in a Docker container rather than LocalDB so the database matches the compose setup.
- **Validation:** Migration applied to the Docker SQL Server. Ran the four verification queries from docs/schema.sql: aB1234 and Ab1234 both inserted (the Code column is case-sensitive); deleting a link removed its click event (1 row before, 0 after); the index list showed all expected indexes; and the lookup by Code used an Index Seek on UX_ShortUrls_Code.


#### Entry 3: API generation

- **Phase:** Implementation
- **Task:** Controllers, DTOs, validators, Swagger, error handling
- **Prompt (summary or key excerpt):** "Build the controllers, DTOs, Base62 logic, EF Core persistence, and docker-compose."
- **What AI produced:** `ShortenController`, `RedirectController`, `AnalyticsController`, request/response records, FluentValidation validators, a global exception middleware, Swagger, a health endpoint.
- **My review:** Naming: versioned `/api/v1/...`, short code on the root for redirects. Standards: 201 for create (200 when the URL already exists), 302 for redirects, 204 for delete. Errors: one RFC 7807 format with a `traceId`, validation failures grouped by field, no internals on 500.
- **Decision:** Modified
- **What I changed and why:** Fixed a route template that crashed the app at startup (see 7.3 #1), and turned off MVC's implicit `[Required]` on non-nullable strings so every 400 comes from FluentValidation in one format. Replaced my own `AnalyticsResponse` class with the shared DTO.
- **Validation:** `POST /api/v1/shorten` returned 201 with code `100001`. Create, redirect, update, delete, analytics and the 400, 404 and 410 cases are now covered by the integration suite against real SQL Server (85 tests when this entry was written, 89 now).


#### Entry 4: Shortening algorithm

- **Phase:** Design / Implementation
- **Task:** Choose and implement the code generation approach
- **Prompt (summary or key excerpt):** Asked for implementation options for generating short codes, with the trade-offs of each (length, collision risk, predictability, cost), so that I could choose rather than receive a single answer. The assessment asks for a choice of Base62 over GUID/UUID to keep URLs shorter.
- **What AI produced:** Four options, compared below, and a Base62 encoder over the database id.
- **My review:** Compared length, collision risk, predictability and cost.
- **Decision:** Base62 over GUID/UUID, to keep URLs short (as required).
- **What I changed and why:** Added an offset of 62^5 (916,132,832) to the id so every code is at least 6 characters (the first code is `100001`) and stays 6 characters up to about 56 billion links.
- **Validation:** Unit tests cover encoding round trips, boundaries and the offset (part of the 181 unit tests). The integration test creates 25 links in parallel and checks that all codes are distinct.


| Option | Code length | Collisions | Predictable? | Verdict |
|---|---|---|---|---|
| GUID/UUID | 22 to 36 characters | Practically none | No | Rejected: too long for a "short" URL |
| Random Base62 string | 6 to 7 characters | Possible; needs check-and-retry against the database | No | Viable, but every create needs a uniqueness check and possible retries |
| Hash (MD5/SHA) truncated | 6 to 8 characters | Likely as data grows; same URL always gives the same code | Partly | Rejected: collision handling is the main cost, and a fixed code per URL conflicts with expiry and re-shortening |
| **Base62 of the identity id (chosen)** | **6 characters (with offset)** | **None by construction** (database guarantees unique ids; unique index as a safety net) | **Yes, codes are sequential** | **Chosen: shortest, simplest, no retry loop** |

The cost I accepted: codes are guessable in sequence, so anyone can enumerate links. This is listed in [Limitations](#13-limitations); the fix is to scramble the id (for example with a reversible permutation) before encoding.

#### Entry 5: Persistence layer

- **Phase:** Implementation
- **Task:** EF Core entities and repository
- **Prompt (summary or key excerpt):** Covered by the same build request as Entry 3 ("EF Core persistence"). My review points came from the assessment's Task 4 (transactions, concurrency handling, optimistic locking), not from the prompt, which is why the review below is longer than the prompt.
- **What AI produced:** `IShortUrlRepository` / `ShortUrlRepository`, an insert that runs in a transaction, optimistic locking with `RowVersion`, a delete using `ExecuteDeleteAsync`, and a migration helper that retries until the database is ready.
- **My review:**
  - Transactions: the code is derived from the identity id, which is only known after the insert. The insert stores a unique placeholder code, saves to get the id, writes the real code, and commits, all in one transaction so a failure leaves no row with a placeholder.
  - Concurrency: `RowVersion` is a concurrency token; a stale update raises `DbUpdateConcurrencyException`, which the repository maps to a 409 problem response.
  - Reads use `AsNoTracking`. Delete is a single set-based statement, so there is no load-then-delete race.
- **Decision:** Accepted
- **What I changed and why:** Kept the placeholder-then-update approach over a separate sequence, because it needs no extra database object. Cost: two writes per create.
- **Validation:** Unit tests for the service with a mocked repository. An integration test loads the same row in two database contexts, saves one, then saves the other against real SQL Server and checks that the conflict exception (mapped to 409) is thrown. The HTTP API does not expose `RowVersion`, so the 409 cannot be provoked from a client with a normal request.


#### Entry 6: Redis cache

- **Phase:** Implementation
- **Task:** Cache service and invalidation
- **Prompt (summary or key excerpt):** Asked for the steps to add a Redis cache to the redirect path, then asked for the code. Goal: serve repeat redirects from cache instead of SQL Server, keep entries correct after update and delete, and keep redirects working if Redis is down.
- **What AI produced:** `CachedUrlShortenerService`, a cache-aside decorator over the real service using `IDistributedCache`; a small circuit breaker (`CacheHealth`); cache options; compose changes; 17 unit tests.
- **My review:**
  - TTL: the entry lives for the shorter of 10 minutes (configurable) and the link's remaining life, so an expired link can never be served from cache. The TTL is also the upper bound on staleness if an invalidation is missed.
  - Invalidation: update and delete remove the entry after the database change succeeds. There is a small window where a reader repopulates the old value just after an update; the TTL bounds it.
  - Cache hit optimisation: the cached value holds the link id, URL and expiry, so a hit needs no SQL and click tracking still works.
  - Failures: any Redis problem is logged and the request is served from SQL; after a failure the cache is skipped for 30 seconds so a dead Redis costs one slow request, not one per redirect. 404 and 410 results are not cached.
- **Decision:** Accepted
- **What I changed and why:** Used the standard `IDistributedCache` abstraction so tests can use an in-memory implementation, and set short Redis timeouts (500 ms).
- **Validation:** Log output showed `Cache miss` then `Cache hit` for the same code. After `PUT`, the next request was a miss and went to the new URL. After `DELETE`, the next request was a miss and returned 404. With the Redis container stopped, redirects still worked, with one warning and then no further warnings during the cooldown; after restarting Redis the cache recovered by itself.


#### Entry 7: Analytics

- **Phase:** Implementation
- **Task:** Click tracking and analytics
- **Prompt (summary or key excerpt):** Asked what Task 6 required, then asked for the code. Goal: record every redirect as a click event and expose total clicks, unique visitors and clicks per country, without slowing the redirect or storing raw IP addresses.
- **What AI produced:** A bounded in-memory queue, `ClickTracker`, `VisitorHasher`, a header-based country resolver, a hosted service that writes clicks in batches, and unit tests.
- **My review:** Redirect latency: the redirect only enqueues a record in memory and returns. Privacy: visitors are a SHA-256 hash of IP, User-Agent and a salt; no raw IP is stored. Failure handling: the queue is bounded, a full queue drops clicks with a warning, and a bad click (for example a link deleted after the click was queued) is retried on its own so it cannot discard its batch. Shutdown drains the queue.
- **Decision:** Asynchronous processing (as required). Alternatives considered: a synchronous insert per redirect (simplest, but adds database latency to every redirect), and an external broker such as Kafka or Azure Service Bus (durable, but far heavier than this service needs).
- **What I changed and why:** Changed `ResolveAsync` to also return the link id so a click can reference it, and updated the existing test. Left country resolution behind an interface and read it from a header, because there is no GeoIP database in this project.
- **Validation:** Opened a short link several times: `GET /api/v1/analytics/{code}` returned `clicks: 3, uniqueVisitors: 2`. `ClickEvents` rows contained 64-character hashes and no IPs; `Country` was null without the header. Re-run with `curl -H "CF-IPCountry: IN"` and `US` during Scenario 3: both were stored and reported by the countries endpoint.


#### Entry 8: Test generation

- **Phase:** Testing
- **Task:** Unit and integration tests
- **Prompt (summary or key excerpt):** Asked for xUnit, Moq and FluentAssertions tests for each feature as it was built. After reviewing the results I asked for a second pass: integration tests against real SQL Server, security tests, and a load tool. The first prompt named no scenarios, so the AI wrote mostly happy-path tests; the second named the gaps and got better output.
- **What AI produced:** Unit tests for the Base62 encoder, short-code generator, URL normalizer, validators, URL service, cache decorator and analytics components.
- **My review:** Ran the suite and fixed failures (see 7.3). Gaps I found: every service test mocked the repository, so nothing exercised SQL Server, EF Core mappings, the middleware or the routes; the optimistic-lock conflict could only be tested against a mock; the pipeline (rate limiting, validation, error format) had no coverage at all.
- **Decision:** Modified. Kept the generated unit tests and asked for integration, security and load tests on top.
- **What I changed and why:** Added an integration suite that starts the real application with `WebApplicationFactory` against a separate SQL Server database (Redis swapped for an in-memory cache), a security suite, and a load tool. Fixed the generated test code as it failed (see 7.3 #7 to #9).
- **Validation:** `dotnet test`: 266 passed, 0 failed (181 unit, 85 integration including security).


#### Entry 9: Documentation

- **Phase:** Docs
- **Task:** README and architecture docs
- **Prompt (summary or key excerpt):** Asked AI to help complete the README from the template. Later gave it the assessment PDF and asked for a review of the README against it, with the instruction to change anything that did not match.
- **What AI produced:** This README, the ER diagram and the SQL script; then, from the review, embedded diagrams, a missing migration script, corrected commit references and an assessment coverage table (section 1.1).
- **My review:** Checked each section against the running system. Corrections: the README still described rate limiting and cleanup as planned; the Docker Quick Start failed on my machine until a missing `docker/Dockerfile` and a `.dockerignore` were added (see 7.3 #10 and #11); the performance section had to report the failed connections of the 1000-concurrent run instead of a clean number.
- **Decision:** Modified
- **What I changed and why:** Updated capabilities, API table, configuration, diagram, scenarios, limitations and test results to match what was actually built and measured.
- **Validation:** Ran `docker compose up --build` end to end: migrations applied, `/health` healthy, create, redirect, analytics and browser report worked.

#### Entry 10: Structured logging and CI pipeline

- **Phase:** Implementation / Delivery
- **Task:** Add Serilog and a CI/CD pipeline (listed in the assessment's reliability features and Day 3 plan)
- **Prompt (summary or key excerpt):** After reviewing the assessment checklist I asked what Polly, Serilog and CI/CD would cost, then asked for Serilog and the pipeline to be added.
- **What AI produced:** `Serilog.AspNetCore` wired in `Program.cs` (`UseSerilog` reading the `Serilog` section of `appsettings.json`), and `.github/workflows/ci.yml` with a build-and-test job (SQL Server 2022 service container, coverage artifact) and a Docker image build job.
- **My review:** The existing log calls already use message templates (`Created short URL {Code}`), so they become structured properties without code changes. The old `Logging` section named the cache class by its previous namespace, so the Debug override for cache hits and misses would have stopped working after the move to `Caching/`; it was corrected to `UrlShortener.Caching.CachedUrlShortenerService`. I chose not to add per-request logging to keep the redirect path unchanged. I chose not to add Polly: the database calls would gain retries without a clear failure mode to protect against, and the startup migration already retries.
- **Decision:** Modified
- **What I changed and why:** Removed the old `Logging` section so there is one source of log configuration. The workflow uses the same SQL Server image and password as `docker-compose.yml` and the integration-test defaults, so no secrets or overrides are needed in CI.
- **Validation:** Ran the API after the change: the console shows the Serilog format (for example `[18:11:06 DBG] UrlShortener.Caching.CachedUrlShortenerService: Cache miss for 10000G` and `[18:11:07 WRN] UrlShortener.Middleware.ExceptionHandlingMiddleware: Request failed with 410: The short URL '10000G' has expired.`). The cache Debug lines work again after the namespace fix (`Cache miss` then `Cache hit` for the same code). `dotnet test`: 266 passed, 0 failed (181 unit, 85 integration).

#### Entry 11: Polly retries and metrics

- **Phase:** Implementation
- **Task:** Close the two reliability items the assessment lists that were still missing (Polly retry policies, metrics and monitoring)
- **Prompt (summary or key excerpt):** After the README review showed two reliability features from the assessment were missing, I asked for the code changes to close them (Polly retry policies, metrics and monitoring). I then asked whether new tests were needed, and had the AI list the gaps before it wrote any (it named five); finally I asked for the architecture diagram to be updated. Asking for the list first let me approve the scope.
- **What AI produced:** `ResilientShortUrlRepository` (a Polly v8 retry pipeline wrapped around the repository), `ResilienceOptions`, `AppMetrics` (prometheus-net counters and a queue-depth gauge), `/metrics` and HTTP metrics middleware, unit tests for the retry rules and the metric counters, and integration tests for `/metrics`.
- **My review:**
  - Polly: retries are only safe on idempotent operations. I limited them to reads, report queries and set-based deletes. `AddAsync` (transaction plus placeholder code) and `SaveChangesAsync` (tracked entity, optimistic lock) are deliberately not retried, because a blind retry could create a duplicate row or hide a 409.
  - Only transient SQL error numbers are retried (timeout, deadlock victim, Azure SQL busy and throttling codes), with exponential backoff and jitter, three retries by default.
  - This replaces my earlier decision in Entry 10 not to add Polly: wrapping only the idempotent calls gives a clear failure mode to protect against, which was my objection before.
  - Metrics: counters are static and incremented with no I/O on the hot path. Queue depth is read when `/metrics` is scraped, not on every click.
  - Redis is not wrapped in Polly: the circuit breaker from Entry 6 already covers it, and a retry there would slow redirects.
- **Decision:** Modified (scope limited as above)
- **What I changed and why:** Registered the concrete repository and the decorator by hand in `Program.cs` so the pipeline is a singleton. `/metrics` is not rate limited (it carries no rate-limit attribute) and exposes no per-link data.
- **Validation:** `dotnet test tests/UnitTests`: 214 passed (181 existing, 30 for retries and metrics, 3 for the date response format). The new tests cover: retry then success, giving up after the maximum, no retry for non-transient errors, no retry for add or save, the disabled switch, argument forwarding and retry for every retried method, the real SQL error-number list using reflection-built `SqlException` objects, and that each metric counter really increments (compared before and after, because the counters are static and shared between parallel tests). Started the API and confirmed `/metrics` returns the `urlshortener_*` series. Five integration tests were added in `MetricsEndpointTests.cs` (series present, cache hit and miss after a redirect, no short code in the output, decorator resolved from DI, resilience disabled, `/metrics` never rate limited). Full `dotnet test` run against SQL Server in Docker: 303 passed, 0 failed (214 unit, 89 integration).

#### Prompt patterns: what worked and what did not

| Pattern | Seen in | Result |
|---|---|---|
| "Build X" with no layout, namespaces or commands | Entries 3 and 5 | Code that had never been compiled: missing usings, a crashing route template, assumed setup (7.3 #1 to #6) |
| Asking for options with trade-offs before code | Entry 4 | The decision was mine and is documented (options table) |
| Asking for steps first, then code | Entries 6 and 7 | Reviewable output; my review points were already in the plan |
| Stating the failure mode in the goal ("keep redirects working if Redis is down") | Entry 6 | The circuit breaker and the failure tests came from the prompt, not from my later review |
| Asking for "tests" without naming scenarios | Entry 8 | Happy-path unit tests; the SQL, middleware and pipeline gaps were mine to find |
| Asking the AI to list the gaps before writing anything | Entry 11 | Scope agreed first, no unwanted code |
| Asking it to check the work against the assessment document | Entry 9 | Found missing files, stale commit references and two unbuilt features |

Next time I would give every prompt the five parts listed at the top of this section, and ask for the verification command with the code.

#### Entry 12: Final validation against the assessment

- **Phase:** Validation
- **Task:** Check every file and the delivered behaviour against the assessment before submitting
- **Prompt (summary or key excerpt):** Asked for a final validation of each file against the assessment requirements and for the changes needed. Then asked for the contract details I had left as judgement calls to follow the assessment document.
- **What AI produced:** A checklist of requirement against file, four findings (listed in 7.3 #14 to #17), and the change to return `expiresAt` as a UTC date.
- **My review:** The findings were real defects, not wording: with the compose setup the rate limiter was off, and the local-run command could not connect to the database. For the response contract, the assessment shows `"expiresAt": "2027-01-01"`, so I accepted the date format and kept the exact expiry instant inside the service. For `countries` the assessment example mixes a code (`US`) with a name (`India`), so no single rule matches it; I kept ISO codes.
- **Decision:** Accepted, with the country keys left as codes
- **What I changed and why:** Rate limiting is on by default in `appsettings.json` (tests set their own value). The README local-run password matches `appsettings.json`. The empty placeholder test was removed. The load-test project is part of the solution. `expiresAt` is serialized by a small converter, so the service and its tests still use the full timestamp.
- **Validation:** Release build with 0 warnings; unit tests 214 passed; links in the README resolve. The integration suite and `docker compose up --build` must be re-run after these changes: the earlier compose run (Entry 9) was made with rate limiting off.

### 7.3 Bugs or risks AI introduced that I caught

| # | Issue | How I found it | Fix |
|---|---|---|---|
| 1 | The redirect route `{code:regex(^[0-9A-Za-z]{1,12}$)}` crashed the app at startup: MVC treats `[0-9A-Za-z]` as a token like `[controller]` | Ran the app and read the `InvalidOperationException` | Doubled the square brackets and braces, and moved the pattern into one shared constant (`ApiRoutes.CodeSegment`) |
| 2 | The test project had no `using Xunit`, giving 192 build errors | Ran `dotnet test` | Added `<Using Include="Xunit" />` to the test project |
| 3 | The generated code assumed `AddDbContext` and a connection string existed; `dotnet ef migrations add` failed | Ran the migration command | Added the registration and the `ConnectionStrings:Default` setting |
| 4 | Missing `using` and a duplicate `AnalyticsResponse` class gave 31 build errors | Built the solution | Added the shared DTO file and removed the duplicate |
| 5 | Changing `ResolveAsync` to return a link object left an old test comparing it to a string | The test failed on `dotnet test` | Updated the test to assert both the URL and the id |
| 6 | All of the AI-written code was written without compiling it in the AI's environment, so none of it was verified until I built it | Noticed from the build errors above | Treated every drop as untested: built, ran the tests and exercised the endpoints before committing |
| 7 | The integration test factory had two public constructors, which xUnit does not allow for a class fixture; 82 of 85 tests failed at once | Ran `dotnet test` | Made the constructor that takes setting overrides `protected` |
| 8 | The factory used `ConfigureTestServices` without the `Microsoft.AspNetCore.TestHost` using | Build error | Added the using |
| 9 | A path-traversal test sent `/%00` through the in-memory test server, which throws on null bytes before the app runs | Test failure | Removed it from the theory, added a test that expects the server's own rejection, and checked the real API separately |
| 10 | The Docker image build copied my Windows `obj` folder; its restore file pointed to a Visual Studio package folder that does not exist in Linux (`dotnet publish` failed) | Read the Docker build error | Added `.dockerignore` (`**/bin`, `**/obj`) and removed `obj`/`bin` before publishing in the Dockerfile |
| 11 | The `docker/Dockerfile` the compose file references was missing from my project folder, and ports 1433/6379 were already used by my earlier containers | `docker compose up` errors | Added the Dockerfile; stopped the old containers before the compose run |
| 12 | My cleanup test showed an expired link still redirecting: the code was fine, but my manual SQL delete bypassed cache invalidation and Redis still held the entry | Compared Redis and SQL | Cleared the key and re-tested; documented as a trade-off (the TTL bounds staleness) |
| 13 | The first 1000-concurrent load run lost 121 requests to connection errors, so the headline latency was meaningless | Console output and Redis counters did not add up (879 lookups for 1000 requests) | Reported it as it was, explained the cause, and added lower-concurrency runs |
| 14 | `appsettings.json` had `RateLimit:Enabled: false` while the code default and the README said `true`; `docker compose` does not override it, so the container never returned 429 | Final validation against the assessment: compared configuration files with the README | Set it to `true`; tests keep their own explicit value |
| 15 | The local-run `docker run` command in the README used a different SQL password from `appsettings.json`, so the API could not connect | Compared the README commands with the configuration | Corrected the README password |
| 16 | An empty template test (`UnitTest1.Test1`) was counted as a passing integration test | Listed the discovered tests | Deleted it; the test totals in the README were reduced by one |
| 17 | The load-test project was not part of the solution, so CI never built it | Read the solution file against the README structure | Added it to `UrlShortener.slnx` |

---

## 8. Scenario 1: Greenfield

**Requirement:** Build a URL shortener from scratch.

### Task decomposition

| # | Task | AI assistance | Engineer review focus | Outcome |
|---|---|---|---|---|
| 1 | Database design | ER diagram, entities, mappings | PK/FK strategy, indexes, query performance | Two tables, bigint identity keys, case-sensitive unique `Code`, hash index for duplicates, composite index for analytics. See [section 5](#5-database-schema) |
| 2 | API generation | Controllers, DTOs, Swagger | Naming, API standards, error handling | Versioned REST API, 201/200/302/204, one RFC 7807 error format with trace ids |
| 3 | Shortening algorithm | Implementation options | **Decision:** Base62 over GUID for shorter URLs | Base62 of the identity id with an offset: 6-character codes, no collisions. Options table in Entry 4 |
| 4 | Persistence layer | EF Core entities, repository | Transactions, concurrency, optimistic locking | Transactional insert, `RowVersion` optimistic locking mapped to 409, set-based delete |
| 5 | Redis cache | Cache service, invalidation | TTL config, cache hit optimization | Cache-aside decorator, TTL capped by link expiry, invalidation on update/delete, fail-open with circuit breaker |
| 6 | Analytics | Analytics service | **Decision:** async processing to protect redirect latency | Bounded queue plus batched background writer; hashed visitors; header-based country |

### Validation

- Unit tests: 214 passed, 0 failed
- Integration tests: 89 passed, 0 failed (includes the security tests and the five metrics and resilience tests)
- Full run: 303 passed, 0 failed
- Performance test: see [section 11](#performance-results)

**Deliverable:** Working URL shortener service, run with `docker compose up --build` (commit `574547f`, "Scenario 1: Greenfield Development").

---

## 9. Scenario 2: Brownfield

**Existing system:** the Scenario 1 service (create, redirect, update, delete, Redis cache, click analytics, health check). Caching and analytics were built as Scenario 1 Tasks 5 and 6, so the enhancements applied to the running system in this scenario are **rate limiting** and **expiry cleanup**. The analysis is below.

**Enhancement request:** add analytics, expiration, Redis caching, and rate limiting.

### AI workflow

```
Existing code
   ↓
Dependency analysis
   ↓
Impact analysis
   ↓
Implementation suggestions
   ↓
Code generation
   ↓
Test generation
   ↓
Engineer review
```

### Dependency and impact analysis

| Area | Impact found | Risk | Mitigation |
|---|---|---|---|
| Database | No schema change for rate limiting or cleanup. Cleanup uses the existing `IX_ShortUrls_ExpiresAt` index and the cascade to `ClickEvents`. New repository method `DeleteExpiredAsync` | Deleting a link also deletes its click history; one large delete would hold locks | Grace period (7 days by default) before deletion; deletes in batches of 1000 chosen by id |
| Redirect path | Rate limiter added before the endpoints; cache entries never outlive a link, so cleanup needs no cache eviction | Extra work on the hot path; many users behind one NAT share a limit | In-memory counter check only (no I/O); generous read limit (300/min), strict write limit (20/min) |
| API contract | New status code 429 with `Retry-After` and an RFC 7807 body on all rate-limited endpoints; `/health` and Swagger are not limited | Clients that never saw 429 must now handle it | Same problem format as other errors; documented in Swagger and in section 4. Expired-but-not-yet-deleted links still return 410, after deletion 404 |
| Existing tests | The unit tests mock the repository, so the new interface member did not break them. The pipeline had no automated coverage | Regression in create, redirect, update, delete or analytics went unnoticed by unit tests | Manual checks first, then the integration suite (85 tests) against real SQL Server |

### Enhancements

| Enhancement | Commit | AI contribution | My review and changes |
|---|---|---|---|
| Expiration (cleanup of long-expired links) | `c804e35` | `ExpiredLinkCleaner`, `ExpiredLinkCleanupService` (timer, own scope per pass), `DeleteExpiredAsync`, options, unit tests | Chose a grace period so analytics survive a while after expiry. Manual test showed the link still redirecting after I deleted the row by hand: Redis still held the entry. Cause was my test (SQL edits bypass cache invalidation), not the code; recorded as a trade-off |
| Redis caching | `574547f` (Scenario 1) | See Entry 6 (built in Scenario 1, Task 5) | See Entry 6 |
| Analytics | `574547f` (Scenario 1) | See Entry 7 (built in Scenario 1, Task 6) | See Entry 7 |
| Rate limiting | `c804e35` | ASP.NET Core built-in rate limiter, fixed window per client IP, separate write and read policies, 429 handler with `Retry-After`, options, unit tests | Confirmed with a low limit (3) that writes beyond the limit returned 429; all writes (POST, PUT, DELETE) share one counter, which matches the design. Limits are read per request so tests can change them |

### Second enhancement: retries and metrics (Entry 11)

Applied afterwards to the same running system, after reviewing the README against the assessment showed two reliability features missing. Same workflow: existing code, dependency analysis, impact analysis, implementation, tests, review.

| Area | Impact found | Risk | Mitigation |
|---|---|---|---|
| Repository | New decorator over `IShortUrlRepository`; the DI registration in `Program.cs` changes. Services, controllers and tests that use the interface are untouched | Retrying a non-idempotent write could create a duplicate row or hide a 409 | Only reads, reports and set-based deletes are retried; `AddAsync` and `SaveChangesAsync` are not |
| Click recorder | Uses its own `DbContext` and bypasses the repository, so it is not covered by the retry layer | A transient failure on a click batch is not retried as a whole | Existing fallback retries clicks one by one; listed under limitations |
| Redirect path | One counter increment per request, no I/O | Extra work on the hot path | Static in-memory counters; queue depth is read at scrape time |
| API contract | New `GET /metrics` endpoint; no change to existing endpoints | Public exposure of operational counts | No per-link data in the output; listed under limitations (restrict at network level) |
| Configuration | New `Resilience:*` settings; retries can be switched off | Different behaviour between environments | Defaults documented in section 2 |
| Tests | The existing 181 unit tests still pass unchanged; 30 unit and 5 integration tests added | Regression in the existing paths | Full suite re-run after the change: 303 passed (214 unit, 89 integration) |

### Deliverables

- Updated architecture: see the diagram in [section 3](#3-architecture) (rate limiter, cleanup job, retry decorator and metrics added)
- Database migration scripts: `InitialCreate` and `AddBrowserToClickEvents` (EF Core), with plain SQL in [`docs/schema.sql`](docs/schema.sql) and [`docs/migrations/AddBrowserToClickEvents.sql`](docs/migrations/AddBrowserToClickEvents.sql). Scenario 2 itself needed no migration
- Regression test results: at the end of the rate limiting and cleanup work, 181 unit and 85 integration tests passed (create, redirect, update, delete, analytics, health, 404, 410, 429 and the validation cases). After the retry and metrics work the full suite passes: 303 tests (214 unit, 89 integration)

---

## 10. Scenario 3: Ambiguous Requirement

**Requirement as received:** "Users want better reporting."

### Step 1: AI-generated clarifying questions

- Click counts?
- Country-wise usage?
- Browser statistics?
- Daily reports?
- Export functionality?
- Which time zone defines a "day"?
- How far back can a report go, and is there a maximum range?
- How should clicks with an unknown country or browser be shown?
- Should bots be counted or filtered?
- What about clicks recorded before browser data existed?

### Step 2: Product owner response

| Question | Answer |
|---|---|
| Daily clicks | Yes |
| Country analytics | Yes |
| Browser analytics | Yes |
| Export | Out of scope for this iteration (can be added later on top of the same queries) |
| Time zone | UTC (keeps reports reproducible) |
| Range | Default last 30 days, maximum 366 days |
| Unknown values | Shown, not hidden |
| Bots | Counted and labelled "Bot" (not filtered) |
| Old data | Clicks without browser data are reported as "Unknown" |

### Step 3: Updated requirements

- Report clicks and unique visitors per day for one short URL, for a date range, with days that had no clicks shown as zero.
- Report clicks per country with each country's share of the total, and how many clicks had no known country.
- Report clicks per browser family with each family's share of the total.
- All three reports accept an optional `from` and `to` date (UTC days, both ends inclusive); invalid or oversized ranges return 400.
- Existing endpoints and the redirect path keep working unchanged. No export.

### Step 4: Design proposal and API design

**Data:** add a nullable `Browser` column (`varchar(30)`) to `ClickEvents` through the migration `AddBrowserToClickEvents`. It is nullable so old rows stay valid and the change is metadata-only on SQL Server. The browser family is detected from the User-Agent when the click is queued (a small heuristic, no library), so the redirect still does no I/O.

**Code:** a separate `IReportService` / `ReportService` and three new actions on `AnalyticsController`, so the redirect path and the cache decorator are untouched. Aggregation happens in SQL (`GROUP BY`) on the existing `(ShortUrlId, ClickedAt)` index.

```http
GET /api/v1/analytics/{code}/daily?from=2026-10-01&to=2026-10-10
GET /api/v1/analytics/{code}/countries?from=&to=
GET /api/v1/analytics/{code}/browsers?from=&to=
```

```json
{
  "code": "10000H",
  "from": "2026-10-01",
  "to": "2026-10-03",
  "totalClicks": 7,
  "days": [
    { "date": "2026-10-01", "clicks": 0, "uniqueVisitors": 0 },
    { "date": "2026-10-02", "clicks": 3, "uniqueVisitors": 2 },
    { "date": "2026-10-03", "clicks": 4, "uniqueVisitors": 3 }
  ]
}
```

```json
{
  "code": "10000H",
  "totalClicks": 4,
  "unknownClicks": 1,
  "countries": [
    { "country": "IN", "clicks": 2, "percentage": 50.0 },
    { "country": "US", "clicks": 1, "percentage": 25.0 }
  ]
}
```

```json
{
  "code": "10000H",
  "totalClicks": 4,
  "browsers": [
    { "browser": "Chrome", "clicks": 2, "percentage": 50.0 },
    { "browser": "Bot", "clicks": 1, "percentage": 25.0 },
    { "browser": "Firefox", "clicks": 1, "percentage": 25.0 }
  ]
}
```

Browser families: Chrome, Edge, Firefox, Safari, Opera, Internet Explorer, Bot, Other, Unknown. The `daily` default range is the last 30 days ending today (UTC). Unknown code returns 404.

### Step 5: Review, implementation, validation

- **AI proposals I accepted:** nullable column plus a migration; a separate report service; a heuristic detector instead of a library; zero-filled days; an explicit `unknownClicks` count instead of dropping unknowns; a 366-day cap so one request cannot scan unbounded history.
- **AI proposals I changed or rejected:** export (out of scope); a third-party User-Agent parsing package (rejected to avoid a dependency for a coarse breakdown, and the limits are documented: no versions, Samsung Internet falls under "Other"); filtering bots (kept them, labelled, so totals match the older `GET /api/v1/analytics/{code}` count). 
- **Implementation commit:** `f02f5cd` ("Scenario 3: Ambiguous Requirement")
- **Tests added:** `BrowserDetectorTests` (about 20 real User-Agent strings), `ReportServiceTests` (zero-filling, default range, range limits, not found, unknown values, percentages, bounds passed to the repository), `ClickTrackerBrowserTests`, and integration tests `ReportEndpointTests` against real SQL Server.
- **Manual validation:** opened one link in Chrome, Edge and Firefox plus `curl`; sent `CF-IPCountry: IN` and `US`; the three endpoints returned the expected counts. `Browser` was stored per click (`Bot` for `curl`) and `Country` was `NULL` without the header. Called a report with a value that is not a date (`from=30`) and got 400.
- **What I learned while testing:** the end of the range is a UTC midnight. Clicks recorded on the evening of one local day can fall on the next UTC day, so reports can look one day ahead of local time. This is a decision, listed in the trade-offs.

---

## 11. Testing & Validation

### Strategy

| Level | Scope | Tools |
|---|---|---|
| Unit | Retry decorator (Polly), Base62 encoder, short-code generator, URL normalizer, validators, URL service, cache decorator, analytics (queue, hasher, tracker, country resolver, browser detector), report service, rate-limit policy, expired-link cleaner | xUnit, Moq, FluentAssertions |
| Integration | Real application in memory (controllers, validation, middleware, EF Core) against real SQL Server in Docker; Redis replaced by an in-memory cache | xUnit, `WebApplicationFactory`, the compose SQL Server service |
| Performance | 1000 requests against the redirect endpoint at 1000, 100 and 250 in flight, with rate limiting off | Small console load tool in `tests/PerformanceTests` (HttpClient, stopwatch percentiles), Redis `INFO stats` for the cache hit ratio |
| Security | SQL injection, unsafe URL schemes, credentials in URLs, XSS strings, header injection, path traversal, oversized and malformed input, expired links, rate limiting | Automated tests (in `tests/IntegrationTests/SecurityTests.cs`) plus manual checks |

Run: `dotnet test` (all), `dotnet test tests/IntegrationTests` (needs SQL Server: `docker compose up -d sqlserver`). The integration tests use a separate database `UrlShortener_IntegrationTests`; set `URLSHORTENER_TEST_DB` to use another server.

### Results

| Suite | Tests | Passed |
|---|---|---|
| Unit | 214 | 214 |
| Integration (includes the security tests) | 89 | 89 |
| **Total** | **303** | **303** |

> The test counts above are current (303 passed). The coverage figure below was re-measured after Entry 11. The performance figures below were measured before the retry decorator and the metrics middleware were added (Entry 11) and have not been re-run.

**Code coverage** (Visual Studio code coverage over the unit and integration tests, measured after the retry decorator, the metrics and the date response format were added): **78.6% of lines** and **92.6% of blocks**.

- The earlier measurement (coverlet with ReportGenerator, before Entry 11) reported 66.4% of lines. The two figures come from different tools with different exclusion rules, so they are not a like-for-like improvement; the new code and its 38 new tests account for part of the rise.
- Block coverage is the Visual Studio metric: a block is a run of statements with no branch. It is not the same as branch coverage, which was not measured in this run.
- The per-class breakdown from the earlier run (for example, generated migrations and the migrate-on-startup helper at 0%, and the cleanup timer loop at 35.7%) was not re-measured, so it is not repeated here. The migrations and the startup helper are verified by applying them to SQL Server through `docker compose up --build`, not by unit tests.

### Performance results

Environment: everything on one laptop (API, SQL Server and Redis in Docker, load tool), rate limiting off (`RateLimit__Enabled=false`), HTTPS with the development certificate. These are local figures, not production capacity.

| Metric | Result |
|---|---|
| Run A: 1000 requests, 1000 in flight | 879 redirects succeeded, 121 connection errors, 0 rate-limited (429). p50 4,364 ms, p95 6,023 ms, p99 6,257 ms, max 6,269 ms, 159 requests/s over 6.3 s |
| Run B: 1000 requests, 100 in flight | 1000 redirects succeeded, 0 connection errors, 0 rate-limited (429), 0 other. p50 342 ms, p95 608 ms, p99 633 ms, max 873 ms, 266 requests/s over 3.76 s |
| Run C: 1000 requests, 250 in flight | 1000 redirects succeeded, 0 connection errors, 0 rate-limited (429), 0 other. p50 986 ms, p95 1,393 ms, p99 1,544 ms, max 1,571 ms, 250 requests/s over 4.00 s |
| Redis cache hit ratio | Run A: 879 hits, 2 misses (about 99.8%). Across all runs since the counters were first read: 3,790 hits, 5 misses (about 99.9%). The misses are the first lookup of each new link. Counters were not read separately around runs B and C |
| Error rate | Run A: 12.1% (all connection errors, no HTTP 5xx). Runs B and C: 0% (1000 of 1000 redirects each, no connection errors, no 429 or 5xx) |

How to read the results:

- **Run A (1000 in flight):** the 121 failed requests never reached the application (Redis counted exactly the 879 that did). The multi-second latencies are most likely dominated by opening 1000 simultaneous TLS connections on one machine, not by the redirect itself, which is served from cache. The cause of the connection errors in the first run was not confirmed; connection queueing on a single host is the likely explanation.
- **Runs B and C (100 and 250 in flight):** no errors. Throughput stayed at about 250–266 requests/s while median latency rose from 342 ms to 986 ms, so the extra concurrent requests mostly queued. The API, SQL Server, Redis and the load tool all share one laptop, so these figures show the limit of this test setup, not the capacity of the service. Run B is the most representative of the redirect path itself.
- **What was not measured:** the runs hit the cached redirect path (about 99.9% Redis hits). No separate database load test was run, so the figures say little about SQL Server throughput.

### Security checks

| Check | How tested | Result |
|---|---|---|
| SQL injection | `DROP TABLE`, `OR '1'='1'` and `UNION SELECT` strings sent as URLs, and in the code segment of redirect and analytics routes | URL strings are stored as plain data (parameterised queries); code-segment strings never match the route (404). The table and `/health` still work afterwards |
| Invalid URL validation | `javascript:`, `vbscript:`, `file:`, `data:`, `ftp:`, `gopher:`, credentials in the URL, relative and empty URLs, 2,100-character URL, 2 MB body, malformed JSON, wrong content type, bad `expiryDays` | All rejected with 400 (415 for the wrong content type); a URL of exactly 2,048 characters is accepted |
| XSS in submitted URLs | `<script>` as the URL, and inside a valid URL; script text in the path of an error response | Rejected, or escaped in the stored and redirected URL; error bodies never echo raw script |
| Header injection | URL containing an encoded line break and `Set-Cookie` | No `Set-Cookie` header in the redirect response |
| Path traversal in the code | `..%2f`, `%2e%2e`, `..%5c` in redirect, analytics and delete routes | 404 or 400, never a file or server error |
| Expired link handling | Link inserted already expired; link with 2 seconds left (visited, then visited again after expiry) | 410 and no `Location` header; the cached copy does not outlive the link |
| Error responses | 404 bodies checked for stack traces, connection strings and type names | Nothing leaked |
| Rate limiting | Host with a limit of 3 writes and 2 reads per minute | 4th write and 3rd read return 429 with `Retry-After` and a problem body; `/health` is never limited |

Built-in protections these tests confirm: only `http`/`https` URLs are accepted, URLs containing credentials are rejected, URLs are limited to 2048 characters, the redirect route only accepts 1 to 12 Base62 characters, all database access goes through EF Core parameterised queries, and only the target URL is returned in the `Location` header (never rendered as HTML).

---

## 12. Design Decisions & Trade-offs

| Decision | Chosen | Alternative | Why | Cost accepted |
|---|---|---|---|---|
| Code generation | Base62 of the identity id (+ offset) | GUID, random string, truncated hash | Shortest codes, no collisions, no retry loop | Codes are sequential, so links can be enumerated |
| Insert flow | Placeholder code, then real code, in one transaction | Separate sequence or a second table | No extra database object | Two writes per create |
| Duplicate detection | Non-unique index on a SHA-256 of the normalized URL | Index on the URL itself | URLs up to 2048 characters cannot be indexed; non-unique so expired links do not block re-shortening | An extra equality check on the URL after the hash match |
| Expiry in the response | UTC date (`2027-01-01`), as in the assessment contract | Full timestamp | Matches the specified contract; the service still stores and enforces the exact instant | A client cannot see the time of day; a link shown as expiring on a date can stop working earlier that day |
| Redirect status | 302 (temporary) | 301 (permanent) | Links can change, expire or be deleted; a 301 would be cached by browsers forever | Browsers do not cache the redirect, so every click reaches the API |
| Caching | Redis cache-aside decorator | No cache or in-process memory cache | Lower redirect latency; shared across instances | Stale-read window of up to the TTL if an invalidation fails; extra moving part |
| Cache failure | Fail open with a 30 s circuit breaker | Fail the request | Redirects must work without Redis | One slow request (500 ms) per cooldown when Redis is down |
| Analytics | Async via bounded in-memory queue and batch writer | Synchronous insert per redirect, or an external broker | Keeps redirects fast with no new infrastructure | Counts lag about 2 s; clicks still in memory are lost on a crash; clicks dropped when the queue is full |
| Visitor identity | SHA-256 of IP + User-Agent + salt | Store the raw IP or set a cookie | Unique-visitor counts without personal data | Visitors behind one NAT with the same browser count as one; behind a proxy the IP is the proxy's unless forwarded headers are configured |
| Country | Read from a CDN/proxy header behind an interface | GeoIP database | No external data needed | Empty unless a proxy sets the header |
| Concurrency | Optimistic locking (`RowVersion`) | Pessimistic locks | Updates are rare and conflicts are rarer; no held locks | A conflicting update returns 409 and the client must retry |
| Delete | Hard delete with cascade | Soft delete | Simple queries, no data kept for deleted links | History is lost |
| Rate limiting | Built-in ASP.NET Core limiter, fixed window per client IP, separate write and read limits | Sliding window or a Redis-backed limiter | No new dependency, no I/O on the hot path | A client can send up to twice the limit across a window boundary; counters are per instance; behind a proxy all clients share the proxy's address unless forwarded headers are configured |
| Expiry cleanup | Hourly batched delete after a 7 day grace period | Delete at expiry, or database-side jobs | Analytics survive for a while and deletes stay small | Click history is deleted with the link; expired rows linger up to a week |
| Cache vs direct database changes | Invalidate on API writes only | Cache invalidation via database triggers or events | Simple and fast | A manual change in the database can be served stale for up to the TTL (10 minutes) |
| Reporting | Separate `ReportService`, aggregation in SQL, UTC days, nullable `Browser` column with a heuristic detector | Third-party User-Agent parser, local-time reports, a precomputed rollup table | Redirect path untouched, no new dependency, old rows stay valid | Coarse browser families only; evening clicks in the Americas land on the next UTC day; each report scans the link's click rows in range |
| Retries | Polly retry decorator on idempotent database operations only, transient SQL errors, exponential backoff with jitter | Retry everything, or use EF Core `EnableRetryOnFailure` | A blind retry of an insert or an optimistic-lock save can duplicate a row or hide a conflict; a custom transaction does not work with EF's execution strategy | Create and update are exposed to transient failures; click batches are not retried as a whole |
| Metrics | Static prometheus-net counters and gauge, scraped from `/metrics` | OpenTelemetry pipeline, or structured logs only | No I/O on the hot path, no new infrastructure, standard format | Counters are per process; the endpoint is unauthenticated; no dashboards or alerts included |
| Database | SQL Server | NoSQL | Relational model, transactions, identity keys | Heavier to run locally than a document store |

The decision with the most weight is the Base62-from-id scheme. It makes creation simple and collision-free, which removes a whole class of retry and consistency problems, and the price is guessable codes. For a public service I would add a reversible id scrambler before encoding; I left it out to keep the first version simple and deterministic to test.

---

## 13. Limitations

- Single-region deployment only. The in-memory click queue and the cache circuit breaker are per instance, so running several instances would need a shared queue
- Clicks queued in memory are lost if the process crashes; the queue drops clicks when full
- Analytics are coarse: browser is a heuristic family (no versions, no devices), bots are counted and labelled but not filtered, and reports are in UTC days
- Short codes are sequential and guessable
- Rate limiting is simple: fixed windows (up to twice the limit across a boundary), per-instance counters, and the client address is the connection address (forwarded headers are not trusted, so behind a proxy all clients share one limit)
- The cleanup job deletes a link's click history together with the link, after a 7 day grace period
- Changes made directly in the database are not seen by the cache until the entry expires (at most 10 minutes)
- Reports query the click table directly; very large click volumes would need a rollup table
- A 1000-connection burst from one machine over HTTPS lost about 12% of requests to connection errors (see section 11); not tuned further
- The automated integration tests use an in-memory cache instead of Redis; the real Redis path was verified manually and through the `docker compose` run
- The load tests exercise the cached redirect path; no separate database load test was run
- Country is empty unless a CDN or proxy sends the country header
- Behind a reverse proxy the visitor hash uses the proxy's address unless forwarded headers are configured
- Authentication and authorization are not implemented: anyone can update or delete any link
- No custom domains or vanity URLs
- No QR code generation
- Retries cover reads, reports and deletes only. Creating and updating a link, and the click batch insert, are not retried as a whole
- `/metrics` is unauthenticated and exposes operational counts: restrict it at the network or ingress level outside local development. No dashboards or alerts are included
- Metric counters are per process, so with several instances they must be summed by the scraper
- The default analytics salt and SQL password in `appsettings.json` are for local development only

---

## 14. Lessons Learned

- **What AI did well:** Fast, consistent first drafts of boilerplate (controllers, DTOs, mappings, tests), laying out options with trade-offs, and keeping naming and error handling uniform across the project.
- **Where AI needed correction:** The code was written without being compiled, so build errors, a route-template bug and missing usings surfaced only when I built and ran it. It also assumed project setup (DbContext registration, connection string) that my project did not have yet.
- **What I would prompt differently next time:** Give the AI the exact project layout, project and namespace names, and shell (Windows cmd) up front, and ask it to list the build and test commands that prove each change.
- **Biggest time saver:** Generating the schema, persistence and test scaffolding in one pass, so my time went on reviewing decisions rather than typing.
- **What testing taught me:** unit tests with a mocked repository passed while three environment problems (Docker build, test fixture rules, a stale cache after a manual database edit) only appeared when the real pieces ran together. The integration suite and the Docker run found more than the first 181 tests did.
- **Check against the assessment early:** reviewing the README against the assessment PDF late found missing referenced files, stale commit references and two unbuilt reliability features. The check is cheap and belongs in Day 1, not Day 3.
- **Reversing a decision:** in Entry 10 I declined Polly because I could not name a failure mode to protect against. Restricting retries to idempotent operations gave me that failure mode, so I changed my mind and recorded why.
- **Biggest risk of relying on AI:** Confident-looking code that has never been run. Every change needed a build, a test run and a manual check before I trusted it.

---

## 15. What I Would Build Next

- Sliding-window or Redis-backed rate limiting so limits hold across instances
- Export of reports (CSV), and a precomputed daily rollup for high click volumes
- Tune the server for connection bursts (listen backlog, HTTP/2) and repeat the load test on separate machines
- Id scrambling so short codes are not enumerable
- Bot filtering for analytics, and GeoIP for country
- Authentication and per-user link ownership
- JSON log output (Serilog compact formatter) shipped to a log store
- Retries on the click batch insert, and a circuit breaker on the database
- Deployment stage in the pipeline (the current workflow builds, tests and builds the image, but does not deploy)
- Dashboards and alerts on the existing `/metrics` series (redirect latency, cache hit ratio, queue depth, retries)

---





