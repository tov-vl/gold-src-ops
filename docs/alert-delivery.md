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
- `goldsrcops.alerts.replay_requests` by `accepted`, `idempotent`, `conflict`,
  and `invalid` result;
- `goldsrcops.alerts.processed_deleted`.

Alert on a non-zero dead-letter count, sustained backlog growth, oldest pending
age beyond the delivery objective, or repeated claim recovery. Thresholds must
reflect the configured retry horizon and the receiver's service objective.
Sustained replay `conflict` or `invalid` growth indicates stale operator context,
idempotency-key misuse, or an unsafe event state. An `idempotent` result is
expected when an operator repeats an ambiguous request with the original key.
No replay metric contains a request ID, event ID, subject, reason, or failure
detail.

Structured delivery logs include event, event type, server, incident, attempt,
claim, outcome, sanitized status/category, and duration. They exclude payloads,
response bodies, webhook URLs, authorization values, and exception messages.
Replay lifecycle events `AlertReplayStarted`, `AlertReplayCompleted`,
`AlertReplayInterrupted`, and `AlertReplayFaulted` use event IDs 3101 through
3104. They include replay request ID, event ID, nullable replay number, bounded
outcome, and duration where applicable. They exclude the operator subject,
reason, payload, previous delivery error, principal claims, webhook URL,
authorization value, and exception message. The durable replay record remains
the source for who requested the mutation and why.

## Recovery And Rollback

The accepted operator-facing inspection and replay design is documented in
`docs/dead-letter-replay.md`. Reader inspection, transactional Operator replay,
and durable replay-record endpoints are implemented, so normal recovery does
not require direct database mutation.

For one reviewed dead letter:

1. Read its detail record and newer-event warning with a Reader token.
2. Correct and verify the receiver condition that caused the failure.
3. Submit one replay with an Operator token, a fresh UUID
   `Idempotency-Key`, and a concise non-secret reason.
4. If the HTTP result is ambiguous, read
   `/api/alert-delivery/replays/{requestId}` and repeat the exact request with
   the same key when no record is visible.
5. Follow the returned replay-record `Location`, verify the replay number, and
   observe delivery telemetry until the event reaches its next terminal state.

`202 Accepted` means the existing event was safely requeued; it does not mean
the receiver has already accepted it. Do not generate a new key for an
ambiguous retry, create a replacement event ID, edit the payload, or reset
outbox fields through routine SQL. A new replay cycle after another dead-letter
transition requires a new review, reason, and key.

Interpret replay outcomes before taking another action:

- `accepted` means the existing event became eligible for delivery and a durable
  audit record was committed atomically;
- `idempotent` returns the durable result for an earlier identical request and
  must not trigger another replay cycle;
- `conflict` means either the key belongs to different intent or a newer event
  is processing, so inspect the response problem code and current event state;
- `invalid` means the request or target state is not replayable, so correct the
  request or re-read the dead-letter detail instead of generating keys
  repeatedly;
- lifecycle outcome `ambiguous` or `faulted` does not prove rollback, so query
  the durable record and retry only with the original key.

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
  --filter "FullyQualifiedName~HttpWebhookAlertDeliveryChannelTests|FullyQualifiedName~AlertDispatcherTests|FullyQualifiedName~AlertDeliveryReplayServiceTests|FullyQualifiedName~MetricsEndpointIntegrationTests|FullyQualifiedName~PostgreSqlOutboxStoreIntegrationTests|FullyQualifiedName~PostgreSqlDeadLetterReplayEndpointIntegrationTests"
```

The focused replay tests verify low-cardinality outcome mapping, Prometheus
export, HTTP-validation accounting, safe structured fields, cancellation, and
fault redaction. The full quality gate remains authoritative before release.

The first production recovery exercise completed on 2026-09-02. A pending
unavailable event survived an API restart while dispatch remained disabled. A
temporary Caddy route restricted to the API container's private address then
accepted the unavailable and recovered events in one attempt each, with two
valid and distinct `Idempotency-Key` values. No dead letter was created, so no
replay was required. This proves the deployed HTTPS delivery path for the
exercise; it does not establish a durable off-host alert receiver. The route was
removed and `AlertDelivery__Enabled=false` restored after verification.
