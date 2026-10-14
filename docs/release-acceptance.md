# Safe release acceptance

This operator tooling checks an already running weapon addon and public search.
It does not install code, stop services, change a CVAR, remove a ban, restore a
vault or create a backup. A lookup consumes the existing short cooldown; a busy
response is a failed check, not permission to retry automatically. The tools are
opt-in and CI runs their synthetic checks without contacting production.

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
   the current public server projection.
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
package. It does not install a browser or dependency. Without `--execute`, no
browser/network operation occurs.

A saved top-ten row is needed for exact-name acceptance. RU/EN with and without
JavaScript, keyboard submission, mobile/desktop widths and 200% text are checked.
The form uses the existing name in memory only. POST results must be fresh and
bounded; refresh must preserve them without another POST or a query in the URL.
An empty live leaderboard cannot prove the matching-player scenario.

Cache checks look for the `no-store` directive, accepting `no-cache, no-store`.
Manual refresh requires HTTP 200, no-store and the application's successful
full-body parse/render state. A Chromium `ERR_ABORTED` is accounted for only when
it belongs to that exact completed refresh request. Other request failures,
incomplete rendering, page/console errors and direct browser API calls still
fail acceptance. Waiting for transport completion alone is insufficient after
the bounded reader's cleanup cancellation. The whole browser run is bounded to
90 seconds and outputs only fixed checks and an accounted-cancellation count.

For container comparisons, [canonical_mounts](../tools/release/acceptance_checks.py)
compares mounts as a sorted multiset of complete objects. Ordering is irrelevant;
removed, duplicated or changed mounts, including write permissions, still differ.

## Verification and release limits

The focused Python/Node regressions cover guards before secret/network access,
no retries, stale/partial replies, runtime drift, mount changes, cache directives
and exact-request cancellation correlation. The real-engine fixture separately
checks that explicitly isolated local RCON still accepts upgrade and rollback.
Full application CI remains required for this tooling/workflow change; it is not
classified as documentation-only or addon-only.

Local and target results are recorded in the backlog. No application tag or
production rollout is required to merge these operator-tooling improvements.
