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

Source and capture-only adoption completed on 2026-11-01.
[Product PR #439](https://github.com/tov-vl/gold-src-ops/pull/439) merged at
`844a87a` after all six required checks passed in the
[product run](https://github.com/tov-vl/gold-src-ops/actions/runs/41324703053).
The accepted main has its separate
[post-merge run](https://github.com/tov-vl/gold-src-ops/actions/runs/41326005802).

One normal signed update installed at sequence 19/revision 16 without a local
administrator installer. Independent readback verified nine exact runner hashes,
the four unchanged pins, unchanged broker, enabled/unblocked updater, seven
preserved request IDs/hashes and the previous failed result, image and receipt.
Settled readback confirmed an empty inbox and 19 retained packages. A fresh
post-install heartbeat reported a ready worker and zero game processes.
No new rights, tasks, trust-engine, firewall, addon or production changes occurred.

No Steam, game, fixture, focus or input action ran during that delivery. The
owner subsequently reported the account available and one fresh RU trial ran,
as recorded below. Live weapons input stays disabled with zero reviewed weapon
profiles. End-to-end setup and menu acceptance remain unproved; old jobs and
claims must not be replayed.

## First native setup trial - server acknowledgement, missing snapshot

One fresh RU baseline request ran on 2026-11-02 and ended at 10:56:35 MSK.
Connection, first spawn, native bootstrap snapshot and all owned-window guards
passed without activation intervention. The runner queued each of the five
fixed setup phases once. Independent fixture evidence recorded the settings
request, three menu selections and one `setup-ack language=ru` marker. The
fixture emits that acknowledgement only after checking the exact sequence and
the product's effective language, so the server-side RU setup is confirmed.

The runner did not observe the native setup snapshot. Its terminal result was
`uncertain_error` / `spawn_readiness_failed`, with no setup snapshot and no menu
frames. The end-to-end readiness gate therefore stayed closed before `/guns`
capture. A server acknowledgement does not prove receipt of its client callback
or creation of a BMP. The callback/snapshot failure cause remains unproved.

All three setup log records contained `valid=1677721600`, rather than the
required normalized `valid=1`. Offline source review also confirmed that the
then-current substring check matched that malformed value as a prefix. The producer
must normalize the field and the reader must compare the complete value before
a later acceptance. This trial still failed on missing setup snapshot/menu
frames; the permissive substring check did not produce an accepted result.
The malformed field does not erase the separate effective-language
acknowledgement. No AMXX error files were created.

Independent cleanup confirmed the owned game and exact CFG removed, fixture,
network, relay process and UDP endpoint absent, and a fresh ready heartbeat
with zero game processes. All nine runner hashes, sequence 19 and the seven
previous request hashes were preserved. The queue now holds eight terminal
records; the new result and permanent claims remain retained.

No second attempt, EN run, weapon selection, baseline approval or runner update
followed. The next gate is offline review of setup snapshot delivery and the
diagnostic field format, before proposing another bounded native trial.

## Setup callback compatibility correction

Offline review found `_set` inside the old callback name. An upstream
contributor's [filtering notes in an open Valve repository PR](https://github.com/ValveSoftware/halflife/pull/3160)
describe discarding stufftext containing that fragment. This is a concrete
compatibility concern, not proof of the cause on the tested client: no client
filter trace was collected. The correction changes the fixed one-shot callback
to `gso_lab_ack` in both the bootstrap and fixture. It retains the independent
selection/effective-language checks and callback snapshot gate, without changing
client filters, bindings, input methods or permissions. A missing setup BMP
records `setup_snapshot_missing` and still stops before weapon-menu input.

The fixture now assigns an explicit integer 0/1 before logging `valid`. The
reader compares complete ordered step records and the complete language value,
rejecting the observed long number, zero, extra fields, duplicate/missing or
reordered steps, and language suffixes. Fixture metadata and evidence must name
the corrected callback. Readiness mode advances to `native-fixed-v2`; capture
and live profile identities also advance so old campaigns cannot be silently
reinterpreted as corrected evidence.

New offline coverage uses the actual BMP parser and file receiver with synthetic
files for RU/EN, including missing, malformed or ambiguous snapshots, retained
output collisions, focus refusal and permanent claims. It checks the fixture's
fixed callback names against the generated bootstrap. These checks make no
network, native input, focus, Steam or game calls. The helper compiled
reproducibly with the pinned toolchain. Native callback delivery and actual
client menu rendering remain unverified by these offline checks.

Local verification passed: the complete PowerShell 7/5.1 client policy,
74 real-receiver synthetic-BMP checks, 152 campaign checks, both native
compilation paths, 482 existing synthetic native checks, seven Python fixture
checks and the full .NET quality gate (1,294 passing tests, 62 opt-in browser
skips, no vulnerable packages). No C# code changed, so a new InspectCode run
was not required for this slice.

Source delivery and signed capture-only adoption completed on 2026-11-02.
[Product PR #443](https://github.com/tov-vl/gold-src-ops/pull/443) merged at
`d6537d2` after all six required checks passed in the
[product run](https://github.com/tov-vl/gold-src-ops/actions/runs/41415697570).
The accepted source has its separate
[post-merge run](https://github.com/tov-vl/gold-src-ops/actions/runs/41417625340).

One normal signed update installed at sequence 20/revision 16 without a local
administrator installer. Independent readback verified all nine expected hashes,
eight unchanged dependencies, the four existing pins, enabled/unblocked updater,
eight preserved request IDs/hashes, both earlier failed results, the prior image
and sequence-19 receipt. The normal retention cycle settled at an empty inbox
with 20 retained packages. A fresh post-install heartbeat reported a ready
worker and zero game processes. No package was resent.

No Steam/game launch, native input, fixture start or queue archival ran during
this correction. Trust engine, permissions, tasks, addon and production remain
unchanged. The subsequent RU trial below confirmed callback delivery and the
normalized fixture flags. Zero reviewed weapon profiles still keeps live weapons
input disabled. Preserve all old IDs, claims, results and images; never replay
a failed job.

## Terminal archival and corrected RU trial

On 2026-11-02 one signed no-code-change package advanced the updater to sequence
21/revision 16 and archived exactly eight cleanup-verified terminal request
records. Fresh IDs/hashes matched the reviewed selection before staging. The
installed receipt named exactly those eight IDs. All nine code hashes stayed
unchanged, all eight result hashes remained readable through the archive-aware
reader, and a prior image and update receipt were independently preserved. The
active queue became empty, package retention settled at 21 and a fresh ready
heartbeat reported zero game processes. Results, images and permanent used-ID
claims were retained. No task, permission, trust-engine, addon or production
change was made.

One new RU baseline request then ran with a fresh fixture and request ID. It
ended at 12:42:13 MSK. Connection/spawn, the owned-window guards and all five
fixed setup phases passed without activation intervention. The fixture recorded
three ordered `valid=1` selections and one RU acknowledgement. The corrected
callback produced the native setup snapshot; readiness reached `ready` in
`native-fixed-v2` mode. Callback delivery and normalized successful-step output
are therefore confirmed for this trial.

One native 1920x1200 menu BMP was collected. Visual review found the readable RU
primary-weapon menu with MP5 selected, but the world scene remained visible.
The fixture's required opaque black backdrop was absent. Offline analysis of
that exact BMP with its own body hash returned `PanelMatches=false`, confirming
that the fixed-backdrop structure requirement was not met. The terminal result
was `uncertain_error` / `capture_failed`; no weapon-selection marker appeared.
The guard was not relaxed and no edited or synthetic frame was substituted.
The reason the backdrop was absent remains unproved by this evidence.

Independent cleanup confirmed the game and exact CFG removed, fixture/network,
relay process and UDP endpoint absent, zero AMXX error files and a fresh ready
zero-game heartbeat. All nine runner hashes and all eight archived result hashes
were preserved after the trial. The active queue contains one new terminal
record; sequence 21 is unchanged. No retry, EN run, four-frame baseline approval
or live weapons acceptance followed.

That trial left fixture ShowMenu/backdrop delivery and its observability as the
next offline gate, addressed by the correction below without changing the guard.

## Active-menu observer correction

Offline source review found an unreachable observation path: the product uses
AMXX `menu_display`, whose `UTIL_ShowMenu` writes directly to the engine, while
the fixture waited for a `register_message` ShowMenu callback. AMXX documents
that ordinary message writes bypass other plugins, unlike
[`emessage_begin`](https://www.amxmodx.org/api/messages/emessage_begin).
The [menu producer](https://github.com/alliedmodders/amxmodx/blob/master/amxmodx/newmenus.cpp)
and [message producer](https://github.com/alliedmodders/amxmodx/blob/master/amxmodx/util.cpp)
support this explanation. No message-hook trace was collected in the prior
client trial, so this is a source-level diagnosis of its missing backdrop.

Fixture helper 1.6.0 reads the current AMXX resource through `player_menu_info`
after command dispatch. It accepts only page zero, exactly three ordered primary
or pistol labels, and the product's plain/selected label shape without item
access flags, attached commands or callbacks. It never redraws the menu or calls
its handler. Resource transitions set the existing menu kind and send the same
scene-only ScreenFade; closure or an unrelated resource releases the fade.
The cache is invalidated after selection and disconnect. Separate
`FIXTURE_GUNS_MENU` and `FIXTURE_GUNS_BACKDROP` records distinguish recognition
from an attempted reliable message send. They do not assert client rendering.

The actual Pawn observer passed 45 assertions in the pinned, network-isolated
local server harness. Tests used real AMXX menu resources for all 12 RU/EN
selected variants and rejected wrong order/count, label boundaries, item
commands, access flags and callbacks. Substituted client state and message writes
covered inactive/dead slots, repeated observations, primary/pistol changes,
close/reopen, nonzero pages, cache invalidation and disconnect. The test checked
the unchanged ScreenFade wire fields. No client connected to this server.
Two production-helper compiles produced the same pinned binary; all three input
checks now require that helper, while test binaries remain unacceptable inputs.
The complete Windows client policy passed under PowerShell 7 and 5.1, including
both native compilation paths and synthetic frame/input checks. Seven Python
fixture-policy tests, PowerShell parsing and changed-documentation checks passed.
No C# source changed; the full local .NET suite and InspectCode were not repeated
for this fixture-only correction. Required hosted CI remains a separate gate.

No runner file, image guard, native input method, baseline profile, permission,
task, Steam session, second-laptop installation, addon or production runtime was
changed in that source-only operation. It left one fresh bounded RU capture as
the next gate. The following trial records the native result. Prior IDs and
evidence must not be replayed; full baselines and weapons acceptance remain open.

## Reviewed RU menu baselines

One fresh RU baseline capture with helper 1.6.0 completed on 2026-11-02 at
14:14:19 MSK. Source correction
[PR #446](https://github.com/tov-vl/gold-src-ops/pull/446) was already merged at
`c1b862a` with all six required and independent post-merge checks green. The
installed sequence-21/revision-16 runner and its nine code hashes were unchanged;
no second-laptop installation or permission change was needed.

The client reached `native-fixed-v2` readiness with acknowledged RU setup.
All owned-window checks passed without an activation attempt or manual focus
intervention. The single request ended `captured_unreviewed` with complete
collection, no failure code and `baselineStructureVerified=true`.

All four native 1920x1200 BMPs were collected, hash/size-verified and visually
reviewed. Each has the required opaque black backdrop, readable RU title,
ordered items and exit, with the expected selection:

| Baseline | Selected item |
| --- | --- |
| `primary-default` | MP5 |
| `pistol-default` | USP |
| `primary-selected` | AK-47 |
| `pistol-selected` | Desert Eagle |

Independent offline analysis of the unchanged image gate accepted the structure
of every native BMP and produced four distinct menu-body hashes. No image was
edited or replaced. Fixture records observed menu kinds `1,2,0,1,2`, four opaque
backdrop queue events and one release event. All three baseline selections
reported unchanged held inventory. Actual images now confirm backdrop rendering
for this RU trial; producer records alone are not the basis of that claim.

The result's game/config cleanup flags and independent readback passed. The owned
fixture container/network, relay process and UDP endpoint were absent, AMXX
error-file count was zero and a fresh later heartbeat reported a ready worker
with zero game processes. The prior terminal request was preserved. Two terminal
records remain in the active queue; no running request remains. No retry or EN
attempt followed.

The four RU baselines were reviewed and retained privately. At that RU-only
stage the combined manifest had not been created or installed. The following
EN record completes the reviewed set. Worker `acceptancePassed` and
`rendererVerified` remain false; baseline capture does not prove the later
five-selection, movement, death/respawn, arbitrary-scene readability or full
weapons acceptance scenario.

## Reviewed EN and combined RU/EN baselines

One fresh EN capture with helper 1.6.0 completed on 2026-11-02 at 14:34:18 MSK.
The unchanged sequence-21/revision-16 runner reached acknowledged EN readiness
and completed `captured_unreviewed`, with no failure code or manual activation.
All four native 1920x1200 BMPs were collected and visually reviewed: readable
English titles/items/exit, opaque backdrop, MP5/USP defaults and selected
AK-47/Desert Eagle marks. No image was edited or substituted.

The fixture observed menu kinds `1,2,0,1,2`, four opaque backdrop queue events,
one release and all three baseline selections with unchanged held inventory.
Independent verification confirmed game/config cleanup, absence of the owned
container/network/relay/UDP, zero AMXX error files, a later ready zero-game
heartbeat and nine unchanged runner hashes. Both earlier terminal records and
the reviewed RU metadata were preserved. Three terminal records remain in the
active queue. No retry or additional game run followed.

After reviewing the EN frames, `New-GunsBaselines.ps1 -ImagesReviewed` verified
both original result sets and all eight native files, including language, labels,
readiness, cleanup, exact file hashes/sizes and the unchanged image gate. It
created the private `reviewed-not-installed` manifest with eight distinct
menu-body profiles and two language cases. Original results retain their raw
`captured_unreviewed` status; the separate manifest binds the explicit review.
The four earlier RU images were reused without another RU launch.

The reviewed RU/EN baseline set is complete. No signed update, profile
installation, permission, task, addon or production change occurred. Weapons
interaction is still disabled on the target, and `acceptancePassed` and
`rendererVerified` remain false. The next step is normal predecessor-bound
signed adoption of these eight profiles, followed by separate bounded RU/EN
interaction, returned-control and respawn acceptance. The current evidence
proves only the reviewed baseline capture over the controlled fixture backdrop.
