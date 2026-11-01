# Fixed weapon-menu acceptance

## Scope and claim

R2 client-lab extension for one bounded `/guns` trial per language, RU and EN,
using the accepted addon 0.38.0 on disposable LAN fixtures. The monitoring
application, production addon, saved-data formats, access rules, accounts,
keys, tasks and privileged update engine are unchanged. Protocol 2/revision 16
is preserved. Only existing updateable runner files may change; four pinned
dependencies retain their installed predecessor bytes.

The fixed sequence checks primary-menu exit, MP5/USP -> AK-47/Desert Eagle
selection, reopening, repeated primary selection, pistol-menu exit, returned
movement and the selected loadout after a real death/automatic respawn. The
fixture observes the original inventory, active weapon, clips and reserves
before and after every selection. It never grants weapons or simulates movement.
One real death is requested only after the exact five-step sequence and actual
returned movement. The normal addon/engine performs respawn and item delivery.

The real AMXX menus are displayed over a fixture-only opaque scene backdrop.
No menu text or callback is replaced. This makes reviewed menu pixels stable;
the backdrop is removed on exit. This acceptance does not establish readability
over arbitrary production scenery, renderer identity, persistence across real
Steam identities, PvP or activity aggregation.

## Baselines and guarded input

`capture-guns` is a separate four-frame baseline setup. Its finite generated
program selects only the reviewed fixed menu items. It is not an autonomous
interaction pass. Review initial primary, initial pistol, selected primary and
selected pistol frames in both languages, with complete cleanup, before using
`New-GunsBaselines.ps1 -ImagesReviewed`. The tool verifies native bytes, labels,
language, fixture backdrop and distinct menu hashes. Raw screenshots and the
generated profile manifest remain private.

`New-LabBundle.ps1 -GunsBaselinePath ...` embeds exactly eight reviewed hashes
in the signed runner payload. An ordinary bundle has no weapons input capability.
No remote command accepts arbitrary menu text, hashes, paths or key values.
Baseline capture can be adopted first; enabling interaction requires a second
predecessor-bound signed package with the reviewed profiles. Neither package
changes the trust engine or permissions.

The native layer sends only fixed 0/2/3 menu keys and the fixed `/guns` alias to
the owned game window. Required existing bindings are verified, never rewritten.
Process identity, HWND, foreground, desktop, session, dimensions, cancellation,
finite deadlines and one-attempt phase claims remain mandatory. Every input
phase is tied to fresh reviewed pixels. Uncertain input is never repeated.

## Evidence

Each live case retains five main frames and four checkpoint frames. Review all
nine, not just the final scene:

| Main frame | Expected view |
| --- | --- |
| `primary-default` | MP5 selected, primary title/items/exit |
| `pistol-default` | USP selected after choosing AK-47 |
| `primary-selected` | AK-47 selected after choosing Desert Eagle and reopening |
| `pistol-selected` | Desert Eagle selected after repeating AK-47 |
| `after-respawn` | Closed menus and returned movement after respawn |

The checkpoints prove primary exit, unchanged primary reopening, pistol-choice
closure and pistol exit. Independent fixture markers must confirm unchanged
inventory for all five selections, movement after both exits and pistol choice,
one requested death, AK-47/Desert Eagle with the expected ammunition after spawn,
and movement after spawn. Missing or contradictory evidence cannot be accepted.
Require zero AMXX errors and independently verified game/config/fixture/network/
relay cleanup plus a fresh zero-game heartbeat. Raw worker acceptance and
renderer flags remain false; explicit hash-bound review is separate.

Use `Invoke-LabCampaign.ps1 -Operation prepare -Scenario guns` for the two-case
matrix, with the reviewed baseline manifest and exact installed bundle. Run
one language at a time via `-Panel guns`. Existing hold, resume, review and
uncertain-delivery rules apply. Never migrate source pins, replay a completed
case or delete claims to fit a new preparation. Exact completed requests may be
archived through the existing signed maintenance workflow while preserving
results/images and all unrelated requests.

## Delivery and verification state

Local verification passed on 2026-11-01: 130 controller checks, 10 adapter
checks, 46 weapons protocol/workflow checks in both PowerShell runtimes,
46 profile/archive checks and the complete existing client policy suite.
Both native compilations, 459 synthetic native checks and seven Python tests
passed. The helper rebuilt reproducibly with the pinned AMXX/ReAPI toolchain.
The solution Quality Gate passed: zero build warnings/errors, 1,294 passing
tests, 62 opt-in browser skips and no vulnerable packages.

InspectCode 2026.2.3.1 completed at Hint and higher for all three changed C#
files with zero errors/warnings and 62 reviewed notes. Applicable new notes
were corrected and inspection rerun. Intentional notes preserve public types
called from PowerShell (CA1515), C# 5 compatibility instead of `nameof`
(MA0043), the nine-file runtime compilation boundary (MA0048), existing
formatter style (MA0007), bounded focus failure reporting (CA1031) and a
constructor invoked dynamically from PowerShell (UnusedMember.Global).
No inspection was suppressed or downgraded.

Product [PR #432](https://github.com/tov-vl/gold-src-ops/pull/432) merged at
`06a12ab`. All six required checks passed in
[PR CI](https://github.com/tov-vl/gold-src-ops/actions/runs/41294883255) and
[post-merge CI](https://github.com/tov-vl/gold-src-ops/actions/runs/41295943200).
These checks do not establish native menu acceptance.

## First target trial - not accepted

The capture-only signed package installed successfully on 2026-11-01 at updater
sequence 16. Independent reads verified all nine installed hashes, the four
unchanged pinned dependencies and the absence of weapons input capability.
Three exact owned terminal requests were archived through signed maintenance;
their results and representative images remained byte-identical and readable.
All five unrelated active-queue records retained their identities and hashes.

One RU baseline request produced four valid native BMP files, but all four
were byte-identical and showed the connection/loading screen, not weapon menus.
The server log recorded a connection but no completed in-game menu sequence.
Frame-count waits did not establish readiness in this trial. The underlying
timing/launch cause is not yet proved; increasing waits alone is not acceptance.
No reviewed baseline manifest was created, no input-enabled package was signed,
and neither an EN baseline nor a live weapons interaction was dispatched.

Cleanup passed independently: the owned game and generated configuration were
removed, fixture/container/network/relay were removed, a fresh heartbeat showed
zero game processes, and AMXX reported zero error files. Raw acceptance and
renderer flags remain false. The failed request and images are retained.

A separately signed rollback to the exact predecessor bytes was attempted once.
The gateway rejected it at preflight; the package ID remained `not_reported`,
and a fresh state read still showed sequence 16, enabled and unblocked, with
the same nine capture-only hashes. The rollback did not install and was not
retried. The gateway's 16-file inbox capacity guard is the leading explanation;
the fixed read interface does not expose the actual inbox inventory, so its
count is not claimed as directly observed.

The target therefore retains the capture-only runner. Weapons input remains
disabled and the trial is held. The exact predecessor, signed rollback package,
one-attempt receipts and failed baseline remain in the private operator record.
The subsequent [updater recovery](client-updater-recovery.md) directly observed
the full inbox, retained its 16 successful packages and accepted one fresh signed
no-op update at updater sequence 17. The same nine capture-only runner hashes,
all six client requests and prior failed-trial evidence were preserved. No game
was launched by that recovery. The next gate is readiness synchronization and
a newly frozen baseline attempt. Never delete claims or repeat the rejected
update/game request to bypass this gate. No live weapons acceptance is claimed.

## Capture readiness synchronization

The next source slice replaces the initial frame-count wait with an opt-in
fixture handshake for weapons runs. Prepare both baseline and live fixtures
with `New-LabRun.ps1 -Scenario guns`; the campaign forwards that option.
Legacy MOTD fixtures keep the handshake disabled.

The fixed launch CFG installs three one-shot aliases before connecting. The
fixture requests team/class selection after connection and one bootstrap
snapshot after the first actual spawn. The worker then checks the exact game
process, owner, session and owned-window readiness. Only after a durable phase
claim does the existing fixed native `/guns` action request execution of a
second, job-owned CFG with the finite menu setup. This setup action does not
count as interactive acceptance. The first trigger is excluded from the
fixture's five-selection acceptance sequence.

No setup or baseline snapshots are scheduled during connection. A missing or
filtered callback, multiple bootstrap frames, cancellation, changed owner or
failed window guard terminates the request without retry. The spawn deadline
is 90 seconds; live setup and interaction share the existing monotonic budget.
Client command filters and persisted bindings are never weakened. Both CFGs
require exact content and non-linked paths for cleanup; ambiguous files remain
an incomplete cleanup gate.

Baseline collection now rejects duplicate full frames, duplicate menu bodies
and frames without the expected black backdrop/menu structure. Structural
recognition does not establish titles, language or selected items: all eight
native profiles still require review before weapons input can be enabled.
Live acceptance additionally requires one ordered `connected`, `spawn` and
`program` fixture marker, readiness metadata and confirmed second-CFG cleanup.

Offline verification includes 54 readiness checks and 15 worker cleanup cases
in each PowerShell runtime, the existing client policy suite, both native
compilations, synthetic baseline rejection and the unchanged 459 native checks.
The helper compiles reproducibly using the pinned offline toolchain. Seven Python
policy tests and the solution Quality Gate passed: zero build warnings/errors,
1,294 passing tests, 62 opt-in browser skips and no vulnerable packages. No native
client compatibility or menu acceptance follows from these tests.

Product [PR #436](https://github.com/tov-vl/gold-src-ops/pull/436) merged at
`d02844a`. All six required checks passed in
[PR CI](https://github.com/tov-vl/gold-src-ops/actions/runs/41314174405).
The accepted main revision has its separate
[post-merge run](https://github.com/tov-vl/gold-src-ops/actions/runs/41315478828).

One ordinary signed capture-only update installed at updater sequence 18.
Independent readback verified all nine hashes, the four unchanged pinned
files, unchanged native bridge and enabled/unblocked updater state. All six
client request IDs/hashes, the previous failed result/image and the previous
update receipt were preserved. A fresh post-install heartbeat confirmed a
ready worker and zero game processes. No local administrator installer, new
permission, task, firewall, trust-engine, addon or production change was needed.
Neither Steam nor the game was launched; weapons input remains disabled.

Source delivery and capture-only adoption are complete. The owner subsequently
reported Steam available and one fresh RU baseline was attempted, as recorded
below. No automatic game or input retry is allowed.

## First handshake trial - readiness confirmed, baseline not accepted

One RU request ran on 2026-11-01. The fixture recorded one `connected`, `spawn`
and `program` marker in order. Team join and actual spawn completed. The worker
observed one native bootstrap snapshot and a complete ready owned-window guard:
foreground, active session, default desktop and physical 1920x1200 geometry.
No activation intervention was needed. The fixed native `/guns` setup request
reached the fixture.

The four menu frames did not appear. The worker terminated with
`uncertain_capture` / `game_observed_no_frames`; `FIXTURE_GUNS_START` was absent.
The `program` marker is emitted before sending the client command. It proves
the setup request, not execution of the second CFG. Client-side stage execution
is therefore unproved. Command filtering is a hypothesis, not a demonstrated
root cause; no filter or binding was relaxed. The diagnostic bootstrap remains
on the target and is not exposed by the current fixed image-read interface.

Independent cleanup passed: the owned game stopped, both exact CFGs were
removed, fixture/container/network/relay were absent, AMXX error count was zero
and a fresh post-completion heartbeat reported a ready worker with no game.
All six previous request hashes were preserved. The queue now contains seven
terminal records; sequence 18 and all nine runner hashes remain unchanged.
The job, claims and evidence were retained. No second RU attempt, EN capture,
reviewed baseline manifest or live weapons acceptance was performed.

That trial led to the native setup correction below. The completed fixture and
request must not be reused for another trial.

## Fixed native setup correction

The corrective source slice removes the second CFG and server-triggered `exec`.
After the first-spawn snapshot and owned-window checks, the runner uses five
one-attempt setup phases: open `/settings`, open its language menu, select RU or
EN, close the returned player menu, and send the fixed language-specific setup
acknowledgement. Existing bindings are checked, including `1 -> slot1`; no bind
or command filter is rewritten. The native bridge exposes only these fixed
phases and literals, with the existing identity/focus/desktop/session guards.
No console, global input, arbitrary text or key interface is added.

The fixture observes the actual settings request and three menu selections in
order, then reads the product's existing `goldsrcops_player_language` native.
Only a matching language and sequence produce a one-shot setup snapshot and
`setup-ack language=ru|en` marker. Missing or late acknowledgement stops the
job before opening weapon menus. Every phase has a permanent claim; uncertain
delivery is not repeated. Setup and interaction share the monotonic job budget.

Baseline capture then opens `/guns` through the owned window, requests each
native screenshot with the verified F5 binding, and advances the three fixed
menu selections. Menu structure, frame freshness and integrity are checked
before selections. Four distinct frames remain unreviewed until explicit RU/EN
review; eight reviewed menu profiles still gate live weapons acceptance.
Live evidence additionally requires the exact setup phase list, snapshot hashes,
language-specific acknowledgement and independent fixture selection markers.

Protocol 2/revision 16, the four pinned dependencies, trust engine, permissions,
task definitions, production and addon 0.38.0 remain outside this correction.
The capture/live profile identities advance so old held campaigns cannot be
silently reinterpreted as native-setup evidence. Only one bootstrap CFG now needs
cleanup. Previous failed jobs, receipts and screenshots remain retained.

Local verification passed: the full .NET quality gate (1,294 passing tests,
62 opt-in browser skips and no vulnerable packages), PowerShell 7/5.1 policy
checks, 482 synthetic native checks and both native compilation paths. Focused
readiness and baseline workflows cover missing acknowledgements, stale frames,
ownership/focus changes, cancellation and permanent claims. The pinned fixture
helper compiled reproducibly; it was not started.

InspectCode completed at Hint and higher with zero errors/warnings and 55
reviewed notes. Intentional notes retain C# 5 string parameter names, public
PowerShell entry points, the fixed single-file bridge boundary, repository
comma style and the bounded diagnostic catch. The constructor used from
PowerShell remains callable; no inspection severity or rule was suppressed.

Signed capture-only adoption is pending. The owner reported the shared Steam
account unavailable during implementation. No new Steam/game trial is allowed
until fresh availability; native setup and menu acceptance remain unproved.
