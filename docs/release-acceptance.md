# Safe release acceptance

This operator tooling checks an already running weapon addon and public search.
It does not install code, stop services, change a CVAR, remove a ban, restore a
vault or create a backup. A lookup consumes the existing short cooldown. Game
acceptance rejects busy; browser acceptance intentionally checks one busy countdown
per language and then submits one manual retry. Other failures terminate the run.
The tools are opt-in and CI runs their synthetic checks without contacting production.

## Game and control-plane boundary

Use [check-game-acceptance.py](../tools/release/check-game-acceptance.py) from the
operator machine with the existing SSH configuration and two distinct aliases.
It sends reviewed Python source over SSH stdin and leaves no source/profile file
on either host. Host keys must already be trusted; there is no password prompt,
key provisioning or fallback transport. Root access uses existing `sudo -n`.

Keep the hash-bound JSON profile in the owner-only operator record outside Git:

| Field | Meaning |
| --- | --- |
| `schema` | Integer `1` |
| `sshConfig` | Absolute path to the existing operator SSH configuration |
| `gameAlias`, `controlPlaneAlias` | Distinct existing SSH aliases |
| `host`, `port`, `expectedName` | Reviewed game endpoint and expected A2S name |
| `version` | Expected loaded weapon-addon version |
| `manifestSha256` | Accepted enabled package manifest hash |
| `baselineSha256` | Expected accepted game backup-baseline hash |

Freeze the expectations from the reviewed release record. Do not refresh the
expected hash from a drifting host merely to make acceptance pass. The profile
contains no password, token or private key. Review the current source and retain
its Git revision with the profile hash in the operator record.

```powershell
python -B ./tools/release/check-game-acceptance.py --profile $Profile --profile-sha256 $ProfileSha
python -B ./tools/release/check-game-acceptance.py --profile $Profile --profile-sha256 $ProfileSha --execute
```

The first command is local validation only. The second performs, in order:

1. Game-host receipt/baseline hash and metadata checks, pending-state refusal,
   local A2S, installed backup preflight/postflight, error-log and service checks.
   Identical repeated baseline entries are allowed; conflicting hashes for one
   path are rejected. The complete baseline file must match its frozen hash.
2. Control-plane refusal if a managed game-host directory is present or the
   target is loopback/unspecified/multicast/an address assigned locally. This
   happens before secret access or any RCON packet. The target must also match
   the current online public server projection and the running API configuration.
3. Read the already running API container's existing read-only secret mount into
   memory. Require its established file owner/mode. Send only the two status
   queries and one generated, nonce-correlated empty lookup. Reject wrong
   versions, unhealthy storage/index, authentication errors, partial/stale lookup
   replies, busy or timeouts. Never accept arbitrary commands or retry a command.
4. Repeat game checks and require unchanged game/agent invocations and baseline.
   The API container identity and mounts are checked across control-plane work.

The final JSON reports version, A2S, lookup, baseline count, pending state and
runtime continuity. It does not report names, addresses, raw RCON responses,
credentials or vault data. Each SSH leg has a 65-second deadline; individual
commands and datagrams have tighter limits. This operator coordinator is not a
drop-in replacement for the 45-second on-host upgrade hook. That hook must still
use the reviewed external nonce/manifest/invocation receipt bridge and its own
hash-bound acceptance contract. No reverse SSH trust is introduced.

The bridge must use `invoke_report('control-plane-hook', profile)` for its
control-plane leg. This dedicated role binds host/port to the running API's
unique `PublicServerJoin__Host` / `PublicServerJoin__Port` values before reading
the secret. It verifies the same live status/version/storage and nonce-correlated
lookup, then rechecks API identity, mounts and endpoint configuration. It does
not consult the public online projection: that minute-scale polling view can
still describe the planned restart after local A2S has recovered. The ordinary
CLI continues to require public online state for final acceptance.

The hook role has a 15-second remote budget, at most five seconds per local
command, four seconds per RCON query and a 20-second outer SSH deadline. The
bridge must account for cooldown and receipt delivery inside its existing
35-second receipt wait. After any cooldown wait, require at least 25 seconds
remaining before starting the probe; otherwise reject without probing. Reserve
five seconds for receipt delivery and never reuse an expired request. Keep the
manifest, nonce, game invocation and timestamp binding unchanged. None of these
checks permits local game-host RCON, an automatic query retry or acceptance of
busy, a wrong version or unhealthy storage.

`invoke_report` returns only bounded stage/category diagnostics on failure;
it does not return exception text or raw transport output. The bridge must retain
that report before attempting receipt delivery so a delivery failure cannot
erase the original check failure. Failed or late reports cannot create an
accepted receipt. A new reviewed plan/claim and updated hook hashes are required
after a failed claimed attempt; do not reuse its plan or edit its evidence.

Read-only acceptance allows players to be online. The upgrade workflow retains
its separate empty-server requirement immediately before stopping the game.
Successful host guards prove the checked backup boundary, not backup freshness,
a restore rehearsal or a successful scheduled capture.

## Isolated local RCON

[addon-runtime-probe.py](../ops/gameserver/addon-runtime-probe.py) is now restricted
to disposable container fixtures: `--isolated-fixture`, a container marker and
absence of the managed game-host configuration directory are all required before
reading stdin or sending packets. The existing network-isolated engine runner
supplies that flag. Do not use this probe on a production game host or read a
password from its server configuration for release acceptance.

The shared [bounded protocol](../ops/gameserver/addon_runtime_protocol.py) keeps
the existing status parser and datagram limits. Authentication refusal terminates
the first query; it cannot lead to the next status query. These entrypoint guards
do not prevent a separately authored arbitrary root script from sending RCON.
Existing installed tooling is not replaced by this repository-only change.

## Public browser acceptance

```powershell
node ./tools/release/check-player-search-browser.cjs
node ./tools/release/check-player-search-browser.cjs --execute
node ./tools/release/check-player-search-browser.cjs --partial --execute
node ./tools/release/check-player-search-browser.cjs --empty-fallback --execute
```

Default origin is the public site. `--base-url` accepts an HTTPS origin without
credentials, query or fragment. The script uses the repository's existing built
Playwright package; `--playwright-package` may select another reviewed local
package. Select an already installed browser with `--browser-channel msedge` or
`--browser-channel chrome`; otherwise the existing bundled Chromium is used. It does not install a browser or dependency. Without `--execute`, no browser
or network operation occurs, and the Playwright package is not loaded.

The default preserves exact-only acceptance and its ten-POST/120-second budget.
`--partial` explicitly selects both modes for an API/addon that supports v2;
it permits fourteen POSTs and 180 seconds, retaining the 256-request limit.
It requires a saved name with at least three Unicode scalars, checks exact and
contains independently, and switches to exact during busy without resetting the
shared countdown. Refresh must preserve the selected mode and input. Unsupported
partial search is a failure, never a fallback to exact.

The default and `--partial` scenarios need a saved top-ten row for exact-name
acceptance. RU/EN with and without JavaScript, keyboard submission, mobile/desktop
widths and 200% text are checked.
The form uses the existing name in memory only. The matching-player result must be
fresh and bounded; refresh must preserve it without another POST or a query in
the URL. An empty live leaderboard cannot prove the matching-player scenario.

For each JavaScript language, a generated name triggers a natural busy response.
If that lookup succeeds because the preceding matching-player search was cached,
only one additional generated-name probe is allowed. Busy must advertise 1-10
seconds, disable the submit button and block Enter. Refresh must preserve the
input. Completion must enable the button and publish the ready announcement
without another POST; only the tool's subsequent keyboard submission may retry.
A failed or busy manual retry terminates acceptance. JavaScript scenarios are
separated by an 11-second pause, without changing the server cooldown. Both
no-JavaScript scenarios still require a fresh matching-player result. Browser
storage must remain empty; names and raw page content are never reported.

Cache checks look for the `no-store` directive, accepting `no-cache, no-store`.
Manual refresh requires HTTP 200, no-store and the application's successful
full-body parse/render state. A Chromium `ERR_ABORTED` is accounted for only when
it belongs to that exact completed refresh request. Other request failures,
incomplete rendering, page/console errors and direct browser API calls still
fail acceptance. Waiting for transport completion alone is insufficient after
the bounded reader's cleanup cancellation.

Before closing a scenario context, the checker waits for all guarded route
handlers, including response disposal, with a fifteen-second bound. Interception
stays installed while draining; neither budgets nor failure state are reset
between languages. It checks again after context closure and only then records
the scenario as passed. A late cleanup failure is reported in `context-close`
for that scenario, rather than being attributed to the next language.

Across all four contexts, the default tool permits at most 10 search POSTs and
256 HTTP requests (`--partial`: fourteen POSTs). Each request must use the
selected origin, GET or POST, and only the
`lang` query key. POST is restricted to `/leaderboard`; direct `/api` requests,
credentials, other origins and redirects are refused. Each intercepted request
is fetched once with zero redirect hops and zero transport retries, then fulfilled
to the browser. This buffers responses and disables browser HTTP caching: the
check proves rendered behavior, not native streaming or cache performance.

Browser launch is limited to 15 seconds, the subsequent browser run to 120 seconds,
or 180 seconds with `--partial` / `--empty-fallback`, and individual operations
to 15 seconds
(countdown completion to its advertised seconds plus two). The deadline closes the browser and stops further traffic.
The final JSON includes per-language POST counts, retry duration, total requests,
limits and accounted cancellations, without names, page bodies or screenshots.

Once browser execution starts, failure also returns bounded JSON and exit code 1.
The report retains the last stage, language, JavaScript setting, mode, viewport,
completed scenarios, attempted request/POST counts, response-state counts, elapsed
time and browser-error counts. `firstRequestFailure` retains only the first fixed
phase (`policy`, `fetch`, `redirect`, `fulfill`, `dispose`, `drain`) and category;
later blocked requests cannot replace it. Unknown response states collapse to
`unexpected`.
Exception messages/stacks, URLs, selectors, player names, page content and console
text are never serialized. Launch/navigation errors retain the same report shape;
a cleanup error cannot replace an earlier failure. CLI validation/package-loading
errors remain a generic nonzero refusal before browser execution.

For container comparisons, [canonical_mounts](../tools/release/acceptance_checks.py)
compares mounts as a sorted multiset of complete objects. Ordering is irrelevant;
removed, duplicated or changed mounts, including write permissions, still differ.

### Empty exact-result action

`--empty-fallback` selects the v2.79 action acceptance instead of the saved-player
matrix. It cannot be combined with `--partial`. Its fixed budget is ten search
POSTs, 256 total HTTP requests and 180 seconds, with fifteen-second operations.
It uses a new generated ASCII nickname per context, retained only in memory.
A collision, busy initial exact lookup or unexpected state stops the run; the
checker never regenerates the nickname to retry a failed scenario.

For RU and EN with JavaScript, keyboard exact search must yield a fresh empty
result and the localized action. Tab from the regular submit button must focus
that action. Enter submits the same nickname with contains intent through the
existing antiforgery form. The natural shared busy response must preserve input
and selected contains mode, remove the shortcut and disable early submission.
Countdown expiry sends no POST; only an explicit manual Enter retries. Without
JavaScript, the checker waits eleven seconds before activating the shortcut and
requires a fresh empty contains result. These contexts check widths 320/390/1440
and 200% text after full page load, outside the short JavaScript busy window.
Contexts are separated by eleven seconds and share one request budget.

Only one expected form POST is admitted per deliberate keyboard activation.
Changed name/mode/action, duplicate relevant fields, missing antiforgery or a
body over 4096 bytes is refused before network access. The existing origin,
redirect, retry and route-drain guards remain in force. No browser API request,
query in URL, browser storage, page/console/request error or automatic retry is
accepted. The first failed stage survives cleanup errors. A complete run requires
four contexts, ten POSTs and exactly eight fresh/two busy outcomes. Reports contain
only fixed phases, counts and outcomes, never generated names or form content.

This scenario proves the empty-result action, not positive matching, short-name
validation or production metrics. Use the separate existing checks for those
claims; a local fixture run is not production acceptance.

## Local browser diagnostic rehearsal

After building Web and restoring the existing Playwright package, run the opt-in
[local rehearsal](../tools/smoke/player-search-browser-rehearsal.py):

```powershell
python tools/smoke/player-search-browser-rehearsal.py --browser-channel msedge
python tools/smoke/player-search-browser-rehearsal.py --browser-channel msedge --reject-contains
python tools/smoke/player-search-browser-rehearsal.py --browser-channel msedge --delay-disposal
python tools/smoke/player-search-browser-rehearsal.py --empty-fallback --delay-disposal
python tools/smoke/player-search-browser-rehearsal.py --empty-fallback --reject-contains
```

Use `--node` only to select an existing Node executable when it is absent from PATH.
No browser, package or dependency is downloaded. The runner publishes the existing
Debug build without rebuilding, starts the actual Web on loopback, and supplies
synthetic ranking/search responses from its own loopback API. The fixture models
separate sixty-second mode caches and a shared ten-second cooldown. It is not
another proof of the actual API, RCON, addon, TLS or production proxy boundary.
HTTP is allowed only for this locally allocated fixture through the exported check
function; the production CLI continues to require HTTPS.

`--delay-disposal` delays native response disposal by one second after the fourth
RU POST (third RU POST with `--empty-fallback`). Expect one delayed disposal
and a passing complete matrix: the checker must keep the context alive until
disposal finishes. This reproduces the old
context-close race locally without adding any production request or changing
the application. The retained report includes the injected-mode flags and count.

`--reject-contains` deliberately returns unavailable: expect a failing exit code,
`search-submit`, `ru`, `contains`, two POSTs and one fresh/one unavailable outcome.
With `--empty-fallback`, expect `busy`, `ru`, `contains`, two POSTs, one fresh
and one unavailable outcome, no completed contexts and no automatic retry.
This tests diagnostic retention without weakening the check. The default run must
pass all four language/JavaScript combinations and both modes. Each invocation
retains synthetic logs and a bounded result under a unique ignored artifacts
folder; it stops its owned process/server and removes only its own publish folder.
It accepts no production endpoint or credential. A live failure lacking stage
evidence cannot be retroactively diagnosed from a successful synthetic run.

## Verification and release limits

The focused Python/Node regressions cover guards before secret/network access,
no retries, stale/partial replies, runtime drift, mount changes, cache directives
and exact-request cancellation correlation. Browser-tool regressions also check
origin/query/method refusal, request budgets, redirects, timeout refusal and
countdown bounds. Existing application browser tests retain controlled-clock
coverage for timer progress and page restoration; the operator suite does not
duplicate those tests. The real-engine fixture separately
checks that explicitly isolated local RCON still accepts upgrade and rollback.
Full application CI remains required for this tooling/workflow change; it is not
classified as documentation-only or addon-only.

Local and target results are recorded in the backlog. No application tag or
production rollout is required to merge these operator-tooling improvements.
