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
```

Default origin is the public site. `--base-url` accepts an HTTPS origin without
credentials, query or fragment. The script uses the repository's existing built
Playwright package; `--playwright-package` may select another reviewed local
package. Select an already installed browser with `--browser-channel msedge` or
`--browser-channel chrome`; otherwise the existing bundled Chromium is used. It does not install a browser or dependency. Without `--execute`, no browser
or network operation occurs, and the Playwright package is not loaded.

A saved top-ten row is needed for exact-name acceptance. RU/EN with and without
JavaScript, keyboard submission, mobile/desktop widths and 200% text are checked.
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

Across all four contexts, the tool permits at most 10 search POSTs and 256 HTTP
requests. Each request must use the selected origin, GET or POST, and only the
`lang` query key. POST is restricted to `/leaderboard`; direct `/api` requests,
credentials, other origins and redirects are refused. Each intercepted request
is fetched once with zero redirect hops and zero transport retries, then fulfilled
to the browser. This buffers responses and disables browser HTTP caching: the
check proves rendered behavior, not native streaming or cache performance.

Browser launch is limited to 15 seconds, the subsequent browser run to 120 seconds,
and individual operations to 15 seconds (countdown completion to its advertised
seconds plus two). The deadline closes the browser and stops further traffic.
The final JSON includes per-language POST counts, retry duration, total requests,
limits and accounted cancellations, without names, page bodies or screenshots.

For container comparisons, [canonical_mounts](../tools/release/acceptance_checks.py)
compares mounts as a sorted multiset of complete objects. Ordering is irrelevant;
removed, duplicated or changed mounts, including write permissions, still differ.

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
