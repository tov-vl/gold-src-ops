# Alert Delivery Operations

GoldSrcOps delivers incident-opened and incident-recovered events through a
transactional PostgreSQL outbox and one deployment-configured HTTP webhook.
Delivery is at least once. Receivers must deduplicate requests by the stable
`Idempotency-Key`, which is the outbox event ID.

Alert delivery is disabled by default. A remote webhook outage does not change
`/health/live` or `/health/ready`; operators observe delivery through backlog,
retry, dead-letter, and duration metrics instead.

## Production Configuration

Set `AlertDelivery__Enabled=true` only after the outbox migration is applied.
Production requires an absolute HTTPS URL without embedded user information.
`AlertDelivery__Authorization` is optional and, when present, contains the
complete HTTP `Authorization` header value.

Inject the authorization value through the deployment secret provider. Do not
store it in an image layer, tracked settings, PostgreSQL, an environment file,
or command history. The example below shows the alert-specific additions to the
full container command in `docs/deployment.md`. It assumes the authorization
variable has already been populated by an approved secret mechanism:

```powershell
$env:AlertDelivery__Enabled = "true"
$env:AlertDelivery__WebhookUrl = "https://alerts.example.net/goldsrcops"
# AlertDelivery__Authorization is injected by the deployment secret provider.

docker run --detach `
  --env AlertDelivery__Enabled `
  --env AlertDelivery__WebhookUrl `
  --env AlertDelivery__Authorization `
  <registry>/gold-src-ops@sha256:<digest>
```

The tracked defaults and their environment-variable overrides are:

| Environment variable | Default | Operational rule |
| --- | ---: | --- |
| `AlertDelivery__Enabled` | `false` | Enable only after migration and receiver readiness are verified. |
| `AlertDelivery__WebhookUrl` | none | Required when enabled; HTTPS is mandatory outside Development. |
| `AlertDelivery__Authorization` | none | Optional complete header value; inject as a secret. |
| `AlertDelivery__LoopDelayMilliseconds` | `500` | Empty-queue delay; valid range is 10 to 60000 ms. |
| `AlertDelivery__MaxConcurrency` | `4` | Per-process concurrency; valid range is 1 to 32. |
| `AlertDelivery__ClaimTimeoutSeconds` | `30` | Must exceed the request timeout. |
| `AlertDelivery__RecoveryIntervalSeconds` | `30` | Frequency of expired-claim recovery. |
| `AlertDelivery__RequestTimeoutSeconds` | `10` | Per-request timeout; valid range is 1 to 300 seconds. |
| `AlertDelivery__MaxAttempts` | `8` | Includes the first delivery attempt; valid range is 1 to 100. |
| `AlertDelivery__BaseRetryDelaySeconds` | `5` | Base for exponential equal-jitter retry. |
| `AlertDelivery__MaximumRetryDelaySeconds` | `300` | Retry and accepted `Retry-After` cap; must be at least the base delay. |
| `AlertDelivery__MetricsIntervalSeconds` | `30` | Backlog gauge refresh interval. |
| `AlertDelivery__ProcessedRetentionDays` | `30` | Processed-message retention; dead letters are retained. |
| `AlertDelivery__CleanupIntervalSeconds` | `300` | Frequency of processed-row cleanup. |
| `AlertDelivery__CleanupBatchSize` | `1000` | Maximum rows deleted by one cleanup pass. |

Invalid values fail application startup. Validation messages name configuration
keys but do not echo the webhook URL or authorization value.

## Rollout And Topology

1. Deploy the additive outbox migration while alert delivery is disabled.
2. Deploy the application and verify enqueue metrics while the dispatcher stays
   disabled.
3. Configure the HTTPS receiver and authorization secret.
4. Enable one dispatcher instance with the default concurrency.
5. Verify delivery, retry, oldest-pending-age, and dead-letter telemetry before
   enabling more instances or raising concurrency.

PostgreSQL atomically owns claims and preserves ordering per incident, so alert
dispatch may run on multiple application replicas. `MaxConcurrency` applies to
each process; account for the replica count when sizing receiver capacity.
Polling remains a separate singleton responsibility.

## Delivery Outcomes

- HTTP `2xx` marks the message `Processed`.
- Connection failures, request timeouts, HTTP `408`, HTTP `429`, and HTTP `5xx`
  schedule a bounded retry.
- Other HTTP `4xx` responses move the message directly to `DeadLetter`.
- A retryable result at `MaxAttempts` moves the message to `DeadLetter`.
- An expired claim below the attempt limit returns to `Pending`; an expired
  final attempt moves directly to `DeadLetter` without another HTTP request.

Each application attempt sends one POST. There is no lower-level automatic HTTP
retry. A valid bounded `Retry-After` is honored; otherwise the dispatcher uses
exponential equal-jitter backoff.

## Observability

The authenticated `/metrics` endpoint exports these OpenTelemetry instruments:

- `goldsrcops.alerts.enqueued`;
- `goldsrcops.alerts.delivery_attempts` and
  `goldsrcops.alerts.delivery_duration`;
- `goldsrcops.alerts.delivered` and
  `goldsrcops.alerts.retries_scheduled`;
- `goldsrcops.alerts.dead_letters` and
  `goldsrcops.alerts.dead_letter_count`;
- `goldsrcops.alerts.claims_recovered`;
- `goldsrcops.alerts.pending` and
  `goldsrcops.alerts.oldest_pending_age`;
- `goldsrcops.alerts.processed_deleted`.

Alert on a non-zero dead-letter count, sustained backlog growth, oldest pending
age beyond the delivery objective, or repeated claim recovery. Thresholds must
reflect the configured retry horizon and the receiver's service objective.

Structured delivery logs include event, event type, server, incident, attempt,
claim, outcome, sanitized status/category, and duration. They exclude payloads,
response bodies, webhook URLs, authorization values, and exception messages.

## Recovery And Rollback

Dead-letter replay is intentionally not automated yet. Before any database-
assisted recovery, stop or disable dispatch for the affected messages, preserve
the original event ID and immutable payload, and review whether the receiver
may already have applied the event. Do not create a replacement ID or reset
attempts blindly.

Disabling `AlertDelivery__Enabled` stops new claims without deleting queued or
dead-letter messages. Application rollback to v1.1 leaves the additive outbox
table unused; returning to v2 resumes from persisted state. Do not down-migrate
the outbox while messages may still be required.

## Verification

Run the production container contract from the repository root:

```powershell
pwsh -NoProfile -File .\tools\smoke\container.ps1
```

The smoke flow verifies Production HTTPS validation, enabled-dispatcher startup,
log safety, separate migrations, hardened container options, and health probes.
It does not send an event to an external endpoint. The HTTP boundary and outbox
state machine are covered by synthetic-server and PostgreSQL tests:

```powershell
dotnet test .\tests\GoldSrcOps.UnitTests\GoldSrcOps.UnitTests.csproj `
  --filter "FullyQualifiedName~HttpWebhookAlertDeliveryChannelTests|FullyQualifiedName~AlertDispatcherTests|FullyQualifiedName~PostgreSqlOutboxStoreIntegrationTests"
```
