# Fixed MOTD panel automation

## Scope

Extend the existing client lab to `/ranks`, `/profile` and `/topall` on RU/EN,
using the same five-frame sequence as `/rules`: initial panel, first OK close,
returned movement, alternate team-chat alias, final OK close and movement.
This is R2 test-infrastructure work, without a production/addon/cloud rollout
or an application release tag. The accepted addon remains 0.38.0.

Only the five already updateable runner files and local controller/fixture
tooling change. The four pinned runner dependencies, privileged updater engine,
policy, tasks, keys, accounts and access rules are preserved. Protocol 2/revision
16 remains compatible; an additive `auto-panel` capability selects only the
three fixed panels. Existing `/rules` and capture-only commands remain supported.
New panel jobs require their exact autonomous marker and cannot accept manual
steps, arbitrary commands, panels or acknowledgement text.

## Fixture and visual boundaries

| Panel | Fixed fixture contents |
| --- | --- |
| `/ranks` | The five rank thresholds and progression rules |
| `/profile` | Unavailable saved data for the LAN identity, zero connection/map counters |
| `/topall` | Three synthetic stored rows: Lab Alpha 500/10, Lab Bravo 100/20, Lab Charlie 25/30; unavailable connected-client place |

The source-built helper seeds only disposable fixture vaults before addon
initialization. It does not impersonate a connected Steam identity, create kills,
close MOTDs or simulate movement. It observes the selected panel, alias, cooldown,
input buttons and actual movement distance. No real player or production data is
copied. Saved personal-profile contents, live arbitrary leaderboard rows, weapon
menus, renderer verification, PvP and activity aggregation remain separate gates.

Before enabling input, native baseline frames are captured with the accepted
capture-only runner and independently reviewed. Each panel/language uses an
exact digest of the opaque 1200x600 content region plus the existing OK-button
check. Wrong text, language, panel, dimensions or blank/flat captures cannot
authorize a click. Focus, process identity, cancellation, freshness, permanent
phase claims and finite deadlines retain their existing guards. There is no
global input fallback or automatic redispatch after an uncertain effect.

## Verification and adoption

Local protocol, native-input, workflow and worker-cleanup tests cover the new
allowlist, marker binding, generated program and refusal paths. Fixture helper
compilation remains reproducible and hash-pinned. Full solution, native harness,
scoped static analysis and required CI results are recorded separately from
real-client acceptance.

Local verification on 2026-10-31 passed:

- 132 protocol/broker checks, 33 autonomous workflow cases and ten worker-cleanup
  cases in each of PowerShell 7 and Windows PowerShell 5.1, plus the existing
  workflow, launch, focus, queue and 60-check updater suites. Target and modern
  native compilation passed. The native harness passed 442 checks; seven Python
  fixture/tunnel tests and scoped formatting passed.
- Six native baseline panel frames were reviewed. Sixty additional checks
  exercised their exact identities, all cross-panel/language combinations,
  changed content and an absent OK button without sending input.
- The helper rebuilt twice to the same reviewed binary hash. Each baseline
  fixture had three synthetic leaderboard rows and zero AMXX error files.
- The complete solution quality gate passed with zero build warnings/errors,
  1294 passed tests, 62 opt-in browser skips and no vulnerable package reported.
- InspectCode 2026.2.3.1 at Hint and higher completed for the three changed C#
  files with zero errors, zero warnings and 59 reviewed notes. One new closure
  warning was corrected before the final run. No inspection was suppressed or
  downgraded. Intentional notes retain the existing compatibility boundary:

| Rule | Count | Reason retained |
| --- | --- | --- |
| CA1031 | 1 | Bounded background focus observation returns its failure to the caller |
| CA1515 | 4 | PowerShell calls these public types outside the assembly |
| MA0043 | 16 | `nameof` requires C# 6; the runtime compilation contract remains C# 5 |
| MA0048 | 31 | The pinned native compilation unit and inert harness group related types |
| MA0007 | 6 | Trailing-comma suggestions conflict with the existing formatter style |
| UnusedMember.Global | 1 | PowerShell constructs the native game-window type dynamically |

Adoption requires a reviewed source revision, signed predecessor-bound update,
all nine installed hash checks and one fresh trial per panel/language. Review
all 30 frames together with independent panel/alias/cooldown/movement markers,
zero AMXX errors and confirmed client/config/fixture/network/relay cleanup.
Raw worker/collector acceptance flags stay false; operator review is separate.
Generated profiles, payloads, frames, claims and rollback inputs remain private.

## Installation and deferred interaction gate - 2026-10-31

Product PR [#424](https://github.com/tov-vl/gold-src-ops/pull/424) passed
[required CI](https://github.com/tov-vl/gold-src-ops/actions/runs/41228944932)
and was accepted as `31f57be709edebfd35628783d950bffba259db19`.
The matching [post-merge workflow](https://github.com/tov-vl/gold-src-ops/actions/runs/41230240399)
targets that exact merge revision.
The signed runner update installed at sequence 14/revision 16. Fresh readback
matched all nine payload hashes and confirmed the new fixed capability. The
four pinned dependencies, trust engine, policy, tasks and access rules were
preserved. One common script retained its hash-verified predecessor byte format
after normalized-text equality was checked, as in the earlier adoption.

Before installation, six fresh capture-only fixtures produced the reviewed
panel/language baselines. Their client/configuration/fixture/network/relay
cleanup and zero AMXX errors were verified. Bounded signed queue maintenance
archived only explicitly selected completed requests, preserving their results
and images. No production, addon or cloud resource was changed.

The first autonomous EN request was cancelled after the owner reported the
Steam account in use by another game. It produced zero frames. The terminal
result retained its failure state; target cleanup, temporary configuration
removal, a fresh zero-game heartbeat and local fixture/network/relay removal
were independently verified. No further game request was dispatched and no
automatic retry occurred. A local operator hold prevents new fixture/game
dispatch until Steam availability is explicitly restored.

At that point, all six panel/language interaction acceptances were pending. After
the account becomes free, use fresh run directories and request IDs, preserving
the cancelled attempt and its claims. The six baseline frames are not a
substitute for the required 30 interaction frames. The prior `/rules` adoption
in [the readiness record](client-lab-readiness.md) remains evidence for its
recorded revision only.

## First completed autonomous panel trial

After Steam became available, one fresh EN `ranks` interaction completed on
2026-10-31 UTC. Five native frames were independently reviewed. Readability,
both OK closes, alternate-alias reopening, cooldown and returned movement were
confirmed together with zero AMXX errors and client/configuration/fixture/
network/relay cleanup. No manual window selection was requested.

The first unified-controller run stopped at local evidence validation because
it expected generic frame labels instead of the runner's existing panel-specific
labels. Its fail-closed state and source pins were retained. A corrected
read-only verifier validated the same package; no second dispatch was made and
raw results were not edited. See [controller readiness](client-controller-readiness.md)
for the correction and its separate end-to-end gate. At that point, RU `ranks`,
both `profile` and both `topall` native acceptances were pending. Renderer,
production personal-data behavior, PvP and activity claims remain separate.

## RU ranks and live controller acceptance - 2026-11-01 (MSK)

One fresh RU `ranks` request completed through the corrected controller with
matching source/input pins and five reviewed native frames. Readable RU content,
both OK closes, alternate-alias reopening, cooldown and returned movement were
confirmed together with zero AMXX errors and client/configuration/fixture/
network/relay cleanup. A fresh post-completion heartbeat confirmed no game.
The ordinary hash-bound review operation reported `accepted`; raw results were
not edited. There was one dispatch and no manual window-selection request.

This also closes the corrected controller's first live end-to-end gate. Both
`ranks` languages are now independently accepted, with their original request
identities preserved. RU/EN `profile` and RU/EN `topall` were still pending. No
installed runner, addon, production, cloud or access change was performed.
The completed campaign is held before further work.

## Completed native interaction matrix - 2026-11-01 (MSK)

The four remaining cases each passed one fresh dispatch and a separate
five-frame review through the corrected controller. No manual window-selection
request or repeated game dispatch was needed.

| Panel | RU | EN | Reviewed fixture behavior |
| --- | --- | --- | --- |
| `/ranks` | Accepted earlier | Accepted earlier | Five thresholds and progression rules |
| `/profile` | Accepted | Accepted | Unavailable saved data and zero connection/map counters |
| `/topall` | Accepted | Accepted | Three synthetic rows, localized ranks and unavailable client place |

Each new case verified matching source/input pins and baseline, readable title
and body, both OK closes, alternate team-chat alias, cooldown and actual movement
after both closes. Zero AMXX errors, restored client configuration, game cleanup,
fixture/network/relay absence and a fresh post-result zero-game heartbeat passed.
All four ordinary hash-bound reviews reported `accepted`. Twenty new frames
complete the required 30 interaction frames across the six original scenarios;
capture-only baselines are not counted as interaction acceptance.

Before topall, signed maintenance archived only two owned accepted terminal
requests to free queue capacity. Result bytes, image identities, all nine runner
hashes and other requests were preserved and independently read back. No runner
code, trust engine, task, access rule, addon, production or cloud change occurred.
The old EN ranks reconciliation state and the separate Steam-conflict result
remain intact. The unused fresh EN ranks entry was not dispatched or populated
with another request's evidence. Raw acceptance/renderer flags stay false.

The campaign is held and the game is closed. This closes the bounded panel
interaction gate. Renderer verification, production saved personal-profile
contents, live arbitrary leaderboard rows, weapon menus, PvP and activity
aggregation remain separate. No unchanged build/unit/native suite was repeated;
this follow-up records live acceptance and uses the documentation-only CI path.
