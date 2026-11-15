# Archive Trigger Implementation Progress

Updated on 2026-11-14. Status: offline state core implemented and locally
verified; transport/workflow/observer adapters are also locally integrated.
Service orchestration, protected credential loading and an R3 review package
are prepared. Publication and R3 rollout remain pending. This is not
a deployed scheduler or a completed freshness trial. The owning boundary is
the [implementation plan](archive-trigger-implementation-plan.md).

## Completed Offline Unit

`ops/availability/archive_trigger_state.py` implements a versioned SQLite
ledger, UTC hourly slots at `:53`, exact 24-hour windows ending five minutes
before the slot, one outstanding claim across generations and a unique slot
within each generation. It validates the complete schema including the
exclusive admission index. Existing unknown, missing or corrupt state cannot
be silently reinitialized.

The injected single-attempt coordinator commits Attempting before invoking its
transport callback. Timeout or an invalid dispatch response persists Unknown;
an abrupt process interruption retains Attempting. Neither authorizes another
send. Accepted records only a run ID. Verified additionally requires strict,
matching receipt and normalized job evidence. Wrong origins, reruns, changed
workflow content, stale windows and invalid counters fail closed. Unrelated
head changes with identical reviewed content remain compatible.

The state library's proof input is a trusted adapter boundary. The GitHub
adapter now fetches the actual first-attempt run/job, reviewed content hashes
and exact timestamped stdout receipt. This establishes correlated workflow
evidence, not an independent B2 download. Recovery remains separate. The state
library itself has no network transport or credential reader.

Transactions reject a regressed clock, occupied admission, capacity exhaustion
or database contention. Bounded retirement removes only terminal claims older
than 21 days and outside the protected assessment window, with a daily count
audit. Unknown claims never expire automatically. No reconciliation override
for an uncertain write is exposed in this unit.

## Verification

Run `python -B tools/smoke/archive-trigger-state.py` from the repository root.
All 26 tests passed on Windows and Linux/WSL, including 19 invalid receipt/proof subcases, durable
restart state, two connections, database contention, corrupt/missing schema,
byte/count capacity, retirement protection and fake transport failure/crash.
The existing `archive-schedule.py` suite also passed all 14 tests.

The second integration unit passed 16 tests on Windows and Linux/WSL, including
one POST, timeout/204, blocked arbitrary routes, bounded bodies/GET budget,
token-free CDN redirection, wrong owner/window/generation, probe exclusion,
one PUT and raw-file cleanup, exact receipt mismatch and independent observer
correlation. The PowerShell availability schedule smoke passed; the changed
workflow parsed with the existing Linux YAML parser. CI itself has not run.

## Connected Adapters

`archive_trigger_github.py` only calls the fixed repository's dispatch, exact
first-attempt run/jobs, job logs and three reviewed content paths. Each instance
is one pass: one POST and up to eight GETs. Redirects are allowed only for a log
download to HTTPS Azure Blob storage, once, without Authorization; response
URLs from dispatch are never followed. No general API method is exposed. A
temporary GET timeout or server failure leaves Accepted pending and cannot
authorize another POST. Contradictory completed evidence enters Unknown.

`archive_trigger_workflow.py` validates admission against protected mode,
generation, pin, exact slot/window and actual workflow/helper content before
the secret-bearing step. Missing mode preserves legacy admission. External mode
requires the legacy scheduler flag to be false. Paused admits no writer;
maintenance permits reviewed manual writes. First-attempt/main checks apply.
The probe skips the source/storage-secret step and cannot refresh health.
The writing wrapper records the expected object identity before its sole PUT,
prints the exact success receipt afterward, and removes its temporary raw data.

The observer supports `inspect-external <projection-file> <generation>` and
`publish-external <projection-file> <generation>`. Its protected read-only
projection is produced only from a Verified ledger claim, with no token or
mutable health flag. It independently checks the public run/job and admission/
receipt-validation steps. Missing, stale, wrong-generation or probe evidence
cannot refresh health. The legacy inspect/publish behavior remains the default.
These interfaces are available for packaging; installed service arguments have
not changed.

The exporter accepts objects up to 64 MiB. Receipt validation now uses that
existing bound independently of the ledger's 16 MiB capacity limit.

The new smoke is included in the existing CI availability-schedule step, but
remote CI has not run. The earlier Python-only unit required no C# inspection.
The later Operator compatibility work has its own .NET validation below.
Disposable test databases are created
in temporary directories and cleaned up by the tests.

## Remaining Integration Gates

1. Actual runtime probe handling. Lost POST IDs and uncertain PUTs remain stop/manual-review
   conditions; no automatic search, override or replay is implemented.
2. Protected-main review/CI, exact accepted source/package identity and signed
   immutable candidate publication. A working-tree review ZIP is not a candidate.
3. Current host/credential preflight and the frozen private R3 manifest.
4. Explicit new credential/service approval, install-disabled verification,
   single-owner cutover, exact-object Recovery and two natural hourly slots.
5. Separate prospective seven-day assessment against the three-hour objective.

No production API call, remote write, new credential, installed identity, flag change
or production dispatch was performed for this unit. The current Archive path
continues to own automatic writes.

## Prepared Service And R3 Package

`archive_trigger_service.py` holds an OS lock across each pass, loads the token
from a protected systemd credential mount, reconciles accepted work while paused
and atomically publishes sanitized state. New work is coalesced to the latest
hour and rejected beyond the 24-hour coverage bound. The timer has five-minute
reconciliation wakes plus the exact UTC `:53` slot. An 85-second process alarm
and 90-second unit deadline bound the pass; termination preserves uncertain
admission. A completed job that appears during the reads is evaluated against
the later clock, avoiding a false future-time rejection.

The root-only stdin store helper refuses to overwrite any token. The local
hidden-input SSH helper writes no local secret and checks the existing host key.
Neither helper has been run with a real token. The observer drop-in is a template
that grants read-only projection traversal through the service group and masks
the ledger and token source/mount. Its generation placeholder deliberately fails
validation until the approved private rollout record supplies it.

The Linux service smoke covers locking, actual process termination, durable
Attempting, pause/drain, missing credentials, stale Prepared, catch-up bounds,
file permissions/symlinks, atomic projection, exclusive token installation,
hidden transfer, package verification and native systemd unit validation. See
the [R3 preparation and rollback record](archive-trigger-rollout.md).
The package builder refuses dirty source for release mode, refuses replacement
outputs and verifies its ZIP contents. Review mode is explicitly marked in its
manifest and cannot establish publication or runtime acceptance.

The final service preparation passed 21 Linux tests, native `systemd-analyze
verify` on private 0644 unit copies without warnings, 16 integration tests
(including completion during network reads), 26 state tests, 14 legacy observer
tests and the PowerShell schedule smoke. The first unit-file check against the
Windows mount reported mount permission warnings; the native-copy rehearsal
verified the intended installation modes. The package smoke uses synthetic Git
identity in WSL because its Git cannot resolve a Windows worktree registration;
the actual Windows package build records the real local base revision and dirty
review marker. No claim of Linux-host installation is made.


## Operator Compatibility

The API adds optional `TriggerSource` with fixed values `GitHubSchedule`,
`ExternalTimer` and `Unknown`. The historical `HasScheduledSuccess` wire name
is retained and describes accepted automatic work from the selected owner.
Ordinary manual runs, probes and reruns never qualify. Two optional numeric
origin fields are bound to the exact observation timestamp. Missing or
mismatched origin timestamps mean Unknown source; invalid source values fail
observation closed. The run age still begins at its creation timestamp.

Detail, overview and the bounded UTF-8 report sanitize origin independently and
use automatic-run labels. Stale/Unknown observation cannot claim a current
source. Existing four warning names/thresholds, Operator authorization, BFF
handling and the other seven report sources retain their boundaries. Deploy
API/Web before enabling origin-emitting observation. Previous API query bytes
ignore these additive metrics after rollback. Target acceptance and image
identity remain separate gates.

Focused local verification passed 51 API/Archive tests and 406 Web tests,
including JSON compatibility, page/report labels and independently validated
origin. The later full Web run passed all 1,130 non-browser tests; 70 browser
tests were skipped by their opt-in gate. The observer smoke passed 14 tests
with the two timestamp-bound origin fields.

The local Quality Gate passed audited restore, format verification, build and
the vulnerable-package audit. The full test command passed 2,240 tests, failed
83 container-dependent tests because the Docker endpoint was unavailable, and
skipped the 70 opt-in browser tests. This is not a green complete Quality Gate.
The final two test-only collection-expression edits were checked by another
successful 51-test Archive run. Container and real browser acceptance remain
required in CI.

InspectCode 2026.2.3.1 ran against this checkout's changed C#/Razor files. Its
remaining route-path warnings treat existing Razor routes as physical wwwroot
paths; HTTP/page tests cover those routes. The trailing-comma style suggestions
conflict with the compiler analyzer's MA0007 requirement, so the compiler-valid
commas remain. Two static-method suggestions overlook captured primary-constructor
dependencies. The explicit nullable type in the invalid-input test is retained
to document its null case. These are recorded exceptions, not a clean inspection
claim. SARIF and detailed local results are retained in the private operator record.

## Component Publication Preparation

The [component publisher](archive-trigger-publication.md) is implemented with
separate tag routing after full CI, signed/main-reachable identity checks,
clean candidate packaging, draft download verification and stable asset promotion
without rebuilding. Existing releases and uncertain writes stop forward progress.
Eleven offline publication tests and the existing CI routing smoke passed.
The inspected provider setting is mutable; the receipt records that limit and
the workflow refuses replacement. No provider setting was changed.

Implementation [PR #546](https://github.com/tov-vl/gold-src-ops/pull/546) and
[main CI](https://github.com/tov-vl/gold-src-ops/actions/runs/43608886213) passed.
Candidate rc.1 passed its checks but stopped after uploading a draft, because
REST tag lookup could not read that draft. The ID-based lookup fix passed 13
publication tests and a read-only check against the retained draft. A new rc.2
must use the reviewed fix; rc.1 and its assets remain preserved without retry or
replacement. These were publication-stage results, not target acceptance.

## Systemd Credential Compatibility Recovery

Candidate rc.2 and application candidate v2.132.0-rc.1 subsequently passed full CI
and independent artifact verification. The first approved R3 attempt accepted
API/Web, independent baseline Recovery and a no-write probe, then stopped before
preparing a production claim. The reviewed rollback restored API/Web and legacy
scheduling. No production Archive was dispatched by that attempt.

A separately approved metadata-only unit confirmed systemd's root-owned read-only
credential with a named-service-user POSIX ACL. The original generic private-file
reader rejected its legitimate owner and ACL mask. The fix keeps that generic
reader unchanged and adds a fixed-path systemd reader that requires a read-only
mount and an exact narrow file/directory ACL. Unexpected owners, principals,
permissions, links, file types, bounds and token formats remain fail-closed.
`ExecStartPre` checks the credential before the ledger or network client.

Local Linux verification passed nine credential tests, including a real ACL and
non-root UID regression, 22 service tests and 26 state tests. The real ACL fixture
uses synthetic data; only its mount flag is simulated on the temporary filesystem.
Actual systemd mount acceptance remains a separate paused-unit target gate.
The transport's 16 integration tests and 13 publication tests passed locally.
New candidate rc.3 publication and the revised R3 remain pending.
