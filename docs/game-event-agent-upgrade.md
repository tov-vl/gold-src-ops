# Agent-only upgrade and rollback

This R3 workflow upgrades the persistent game-event agent without restarting the
game, changing credentials, migrating the queue or installing game addons. The
existing persistent guard remains unchanged. Operator plans, target paths,
queue identity hashes, baseline contents and recovery inputs stay outside Git.

## Package and freeze

Build a clean reviewed revision with `tools/release/build-game-event-agent.ps1`.
The immutable Linux x64 ZIP contains one agent binary and a schema-1 manifest
with version, source revision, queue schema and payload hash/length/mode. Retain
the ZIP and independently transported SHA-256. Never overwrite a package.

Stage the frozen `ops/gameserver/agent-upgrade.py` root-owned, together with the
package (0600) in a protected operator directory. The transition records the
installed scheduled-capture consumer and adds its pending-agent gate before
changing runtime references. An interrupted transition also blocks scheduled
backup capture. Its prior bytes and hash are part of the rollback plan. The
current addon-upgrade consumer also refuses a pending agent claim. No new privilege,
secret or remote API is introduced.

`prepare --package <zip> --sha256 <package-hash> --directory <new-private-directory>`
verifies both persistent guards, the entire old release payload, backup baseline,
settled queue/spool, active services and file metadata. It stages an immutable
derived pilot bundle: only the agent binary changes; launcher, settings and all
game/producer payloads retain their old bytes and manifest entries. The derived
manifest records both the standalone package and previous manifest hashes.
Its release directory uses the actual derived ZIP digest, preserving the
existing bundle/guard contract.

Preparation writes a private plan binding exact old/new marker, activation,
unit and backup-baseline bytes, game invocation, queue identity/sequence,
guard and workflow hashes. Review these transitions and retain the returned
plan hash. Preparation does not stop a service or change live references.

## Apply and accept

`apply --sha256 <plan-hash> --directory <prepared-directory>` takes the existing
schedule, backup and pilot transition locks in order. Pending backup/addon/agent
claims refuse execution. It checks the frozen identity again, claims the
transition durably, stops only the agent and verifies queue continuity. It
installs the new hash-bound references and updated backup baseline atomically
per file, reloads systemd, checks both guards and starts only the agent.

Acceptance requires the unchanged game invocation, exact new references,
unchanged queue source/sequence, settled spool and valid backup baseline. Target
acceptance must additionally run the new passive status and prove recent idle
import/delivery cycle evidence, no unexpected restart, live A2S and the host
backup preflight/postflight. No synthetic event is sent to production.

## Failure and rollback

A completed command failure permits one bounded restoration of the recorded old
references and old agent, followed by independent guard/baseline/continuity
checks. An interruption, timeout, unexpected reference, queue change or failed
rollback retains the durable claim as `reconciliation-required`; it never
automatically retries a service mutation. Inspect the private journal and
reconcile the actual state before a new decision. Queue, spool, credentials,
player vaults and old releases are never restored, cleared or replayed.

For an accepted transition, `rollback --sha256 <plan-hash> --directory <directory>`
requires exact new references and restores the recorded old ones. This is an
agent-only rollback and preserves current durable data. It refuses drift or a
nonaccepted journal. Retain both releases, plan and baseline records for recovery.

## Verification boundaries

`tools/smoke/agent-upgrade.py` uses real disposable files and SQLite, with a
simulated service manager/guard. It exercises wrong hashes, unrelated drift,
queue work refusal, success, explicit rollback, completed start failure and
interrupted start with no retry. Existing persistent-guard smoke remains a
separate check. Client rendering, real event traffic and 24-hour activity are
outside this diagnostic installation slice.
