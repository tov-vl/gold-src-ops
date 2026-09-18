# GoldSrcOps Portfolio Walkthrough

This guide is a three-to-five-minute, read-only walkthrough of the published
GoldSrcOps MVP. It uses the production Reader surface and does not require a
deployment, a game-server action, an alert-delivery change, or test data.

The older `docs/demo.md` remains the local v1 command-line demo. This guide is
the current portfolio presentation path for the v2.18 product.

## Safety Boundary

- Use an existing Reader session. Do not show credentials, tokens, identity
  administration, browser storage, developer tools, or provider consoles.
- Do not expose server addresses, ports, secret references, command payloads,
  incident reasons, webhook configuration, or owner-only evidence.
- Do not submit lifecycle or RCON actions, enable alert delivery, replay a dead
  letter, or manufacture an incident for the recording.
- Use only naturally retained sanitized data. If a view is empty, explain the
  empty state instead of changing production to populate it.

## Preflight

1. Open `https://goldsrcops.com/` and verify that the public dashboard loads.
2. Confirm that the Reader session can open `/operator/servers`.
3. Select one already recovered incident from the Reader UI. Do not hard-code
   its identifier in the recording notes.
4. Close unrelated tabs and hide browser chrome that could reveal personal or
   provider information.
5. Keep this guide off-screen or in a separate private window.

## Recording Sequence

### 1. Public Status - 30 Seconds

Open `https://goldsrcops.com/`.

Say:

> GoldSrcOps is a control plane for GoldSrc and Counter-Strike 1.6 servers. The
> public surface exposes only sanitized fleet health; operational detail stays
> behind Reader or Operator authorization.

Point out current availability and freshness without calling the dashboard an
SLO report. The first prospective `API-01` seven-day window met its target, but
one short window is operational evidence rather than proof of long-term
reliability.

### 2. Fleet Triage - 40 Seconds

Open `/operator/servers`.

Show the compact fleet summary and one controlled server. Explain that the
Reader view prioritizes offline, unknown, stale, and incident-bearing servers
without exposing addresses or credentials. Open the selected server only
through the visible Reader link.

### 3. Incident Investigation - 55 Seconds

Open one existing recovered incident from the Reader UI.

Show the durable incident record, current server context, and bounded A2S
observations around opening and recovery. Explain that current health and a
historical incident are deliberately separate facts. Do not claim that the
nearby observations prove a root cause.

Connect the view to the controlled recovery exercise summarized in
`docs/postmortem-controlled-recovery.md`: a threshold-qualified incident opened
and closed once, and durable alert work survived an API restart and an image
rollback/roll-forward cycle.

### 4. Recent Activity - 40 Seconds

Open `/operator/activity?range=7d`.

Show the bounded time window, source filters, server scope, and opaque cursor
navigation. Explain that the timeline combines sanitized incident, command,
and completed-round activity while keeping payloads, results, identities, and
raw gameplay data outside the response.

### 5. Delivery Triage - 55 Seconds

Open `/operator/alert-delivery`, then follow the Reader link to
`/operator/alert-delivery/pending`.

Show the aggregate status first and the bounded pending list second. Explain
that the list follows durable claim order and contains only safe event, retry,
server-display, and linked-incident metadata. Delivery remains disabled until a
permanent receiver can ingest historical work idempotently and in order. The UI
cannot enable delivery, delete, replay, or reclassify a pending row.

### 6. Close - 30 Seconds

Return to the public dashboard.

Say:

> The project demonstrates a modular .NET control plane, real A2S and guarded
> RCON boundaries, durable PostgreSQL workflows, bounded Reader and Operator
> experiences, immutable image promotion, recovery evidence, and independent
> availability measurement. It is a single reference deployment, not a
> high-availability or long-term SLO claim.

## Evidence To Link With The Recording

- `docs/architecture.md` for component and runtime flows.
- `docs/postmortem-controlled-recovery.md` for the controlled exercise.
- `docs/v2.18-readiness.md` for the latest release boundary.
- `docs/service-level-objectives.md` for SLI definitions and claim limits.
- `docs/release-process.md` for build-once candidate promotion.

## Presenter Checklist

- The run stays between three and five minutes.
- Every production interaction is read-only.
- No personal, provider, network, or secret data is visible.
- Claims distinguish current state, historical evidence, and inference.
- The recording description links the exact stable release and the evidence
  documents above.
