# Game-host warning triage

Read-only first checks for the 17 existing warning rules shown on the private
Operator game-host page. This guide does not authorize recovery, restarts,
cleanup, replay, backup capture, journal edits or changes to warning thresholds.
Use the existing private access; no new port or account is needed.

Start with the page evaluation time and oldest rule evaluation. Pending means
the condition is observed but the existing hold has not elapsed; Firing means
it has. Neither is an instruction to restart a service. Preserve bounded
evidence privately: warning name/state, times, aggregate observations and the
existing service result. Never publish credentials, raw player/queue records,
private addresses, backup identifiers or runtime journals in Git or a public
ticket. A fresh empty list covers only these rules, not whole-system health
or external notification delivery.

## Unknown snapshot

Refresh once. If data remains unavailable or incomplete, compare the API
snapshot with existing private monitoring and its collection path. If the
whole page has no usable snapshot, first establish whether that read failed;
do not conclude that the game host is offline. Preserve timestamps and the
bounded failure. Do not create missing data or restart services to obtain green
cards. See [source and validity](v2.111-operator-health-warnings.md#source-and-validity)
and [overview configuration](v2.110-operator-game-host-health.md#configuration).

## Stale snapshot

Compare page time with the oldest rule evaluation and inspect the existing
evaluator and collection freshness. Preserve both timestamps. Old inactive
rules do not establish a current absence of warnings; current metric cards
do not make an old rule snapshot current. Check clock/evaluator observations
before planning any recovery. See
[source and validity](v2.111-operator-health-warnings.md#source-and-validity).

## Agent monitor unavailable

`GameEventAgentMonitorUnavailable`: compare the monitor timestamp with its
timer/service and private collection path. Preserve collector failures and
the last observation. An unread queue is not an empty queue. See
[agent diagnosis](v2.97-agent-alerts.md#diagnosis).

## Agent observation invalid

`GameEventAgentObservationInvalid`: inspect the bounded observer failure and
passive agent status for absent, old or invalid snapshot data. Preserve
validation evidence and timestamps; do not initialize a queue or repair its
schema through a status read. See [agent diagnosis](v2.97-agent-alerts.md#diagnosis).

## Agent worker problem

`GameEventAgentWorkerProblem`: compare the agent process, enabled loop flags,
cycle allowance and last completed/successful cycles. Preserve the existing
runtime journal before choosing recovery. A disabled loop is not a stopped
enabled worker; an idle pass does not prove event delivery. See
[agent diagnosis](v2.97-agent-alerts.md#diagnosis).

## Agent queue delayed

`GameEventAgentQueueDelayed`: compare pending/in-flight counts, oldest enqueue
age, backoff and delivery outcomes while delivery is enabled. Preserve the
outcomes and queue evidence privately. An uncertain delivery needs
reconciliation, not automatic replay. See
[agent warning policy](v2.97-agent-alerts.md#scope-and-warning-policy).

## Agent spool problem

`GameEventAgentSpoolProblem`: compare dead-letter counts, spool availability
and rejected-file counts. Preserve rejected files and queue records. Do not
delete evidence or replay deliveries to clear an alert. An unreadable spool
does not validate retained counts. See [agent diagnosis](v2.97-agent-alerts.md#diagnosis).

## Backup monitor unavailable

`PlayerBackupMonitorUnavailable`: compare the backup-monitor timestamp with
its timer/service and fixed read path. Preserve collection failures and the
last observation. A missing observation does not prove that a copy is absent.
See [backup monitoring](game-player-data-monitoring.md).

## Backup observation invalid

`PlayerBackupObservationInvalid`: inspect the bounded validation failure and
consistency of existing attempt records. Preserve original journal bytes and
pending records. Do not edit them to make validation pass. See
[backup journal states](game-player-data-monitoring.md) and
[schedule/reconciliation contract](game-player-data-schedule.md).

## Backup intervention required

`PlayerBackupInterventionRequired`: compare the last attempt, pending state
and backup-service result before choosing recovery. Preserve original attempt
records and copy capture time. Do not retry an uncertain backup, reset a
failure blindly or treat a closed-uncertain outcome as success. A verified
manual copy and an older failed weekly attempt are independent facts. See
[uncertain-attempt recovery](player-backup-uncertain-recovery.md) and
[service-state reconciliation](player-backup-service-reconciliation.md).

## Backup schedule problem

`PlayerBackupScheduleProblem`: compare timer enabled state, next activation
and the missed-slot observation with the documented schedule. Preserve the
schedule/slot evidence. A missed slot is not permission for a catch-up
capture, and a busy server is not permission to stop it. See
[weekly backup policy](game-player-data-schedule.md).

## Backup copy unavailable

`PlayerBackupCopyUnavailable`: compare the verified copy's original capture
time, source and age with the existing eight-day policy. Preserve the last
verified-copy record. A recent verification does not refresh an old capture,
and a new manual copy does not close an older failed weekly attempt. Preparing
a new copy requires a separate reviewed operation. See
[backup monitoring](game-player-data-monitoring.md) and
[manual-copy acceptance](player-backup-manual-refresh.md).

## Storage monitor unavailable

`GameHostStorageMonitorUnavailable`: compare the storage-monitor timestamp
with its timer/service and fixed collection path. Preserve collection failures
and the last observation; unavailable capacity is not zero free space. See
[storage collection](v2.104-game-storage-monitor.md#collection-display-and-warning-policy).

## Storage observation invalid

`GameHostStorageObservationInvalid`: check source freshness and availability
for each fixed queue, spool, players and backup role. Preserve role validity
and validation evidence. Unsupported inode accounting is distinct from a
failed read and from zero free inodes. Valid roles can remain usable when
another role is unavailable. See
[storage contract](v2.104-game-storage-monitor.md#scope-and-contract).

## Storage bytes low

`GameHostStorageBytesLow`: compare available bytes and ratios for each valid
role with the existing thresholds. Record role and observation time privately;
several roles may share a filesystem, so do not sum their capacities. Do not
delete queue, player or backup data to clear an alert. Cleanup needs its own
reviewed scope. See
[storage policy](v2.104-game-storage-monitor.md#collection-display-and-warning-policy).

## Storage inodes low

`GameHostStorageInodesLow`: compare available inode counts and ratios only
where accounting is supported. Preserve role, support and timestamp evidence.
Missing or unsupported accounting is not zero free inodes. Cleanup requires
its own reviewed scope. See
[inode policy](v2.104-game-storage-monitor.md#collection-display-and-warning-policy).

## Resource monitor unavailable

`GameHostResourceMonitorUnavailable`: compare the resource-monitor timestamp
with its timer/service and fixed collection path. Preserve collection failures
and the last observation; an unread host is not a zero-load host. See
[resource collection](v2.105-game-resource-monitor.md#contract-and-collection).

## Resource observation invalid

`GameHostResourceObservationInvalid`: check source freshness and independent
RAM/CPU availability. Preserve validity evidence for both groups. A failed CPU
sample does not invalidate usable RAM; one CPU sample is not a minute average
or game FPS. See
[resource meaning](v2.105-game-resource-monitor.md#scope-and-meaning).

## Memory low

`GameHostMemoryLow`: compare available RAM bytes and ratio with existing
thresholds and observation time. Preserve RAM/swap observations. MemAvailable
is a host estimate, not a process RSS measurement or an OOM diagnosis. Swap
usage alone does not trigger this rule. No game/agent restart is authorized by
the alert. See
[memory policy](v2.105-game-resource-monitor.md#dashboard-and-warning-policy).
