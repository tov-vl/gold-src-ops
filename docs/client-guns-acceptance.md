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
Next: review a bounded updater-inbox recovery outside this stage's unchanged
trust-engine boundary, then repair readiness synchronization and authorize a
fresh baseline attempt. Never delete claims, widen gateway limits blindly or
repeat the rejected update/game request to bypass this gate. No live weapons
acceptance is claimed.
