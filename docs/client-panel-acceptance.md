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

At implementation time the new runner has not yet been installed or accepted.
The prior `/rules` adoption in [the readiness record](client-lab-readiness.md)
remains valid for its recorded revision only.
