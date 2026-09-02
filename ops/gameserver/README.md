# Controlled Game-Server Operations

This directory contains the provider-independent host foundation, pinned
runtime installer, and first-start activation workflow for the controlled
ReHLDS baseline.

All three workflows are plan-only by default. Each plan must be reviewed from
the same Git revision that will be applied to a paid host.

## Scope

`host-bootstrap.sh`:

- validates Ubuntu 24.04, x86-64, and systemd;
- requires an existing verified key-only operator account;
- requires the current SSH source to match one reviewed control-plane IPv4
  `/32`;
- requires UFW to be active and rejects every inbound allow rule except the
  exact SSH and game endpoint rules;
- preserves an allowlisted, non-public UDP game endpoint;
- installs security updates and the minimal 32-bit GoldSrc runtime libraries;
- creates a locked `goldsrc` system account with `/usr/sbin/nologin` and no
  `sudo` membership;
- creates owner-scoped artifact, runtime, configuration, secret, and backup
  directories;
- enables unattended updates without automatic reboot, UTC/NTP, and baseline
  kernel hardening.

The bootstrap does not:

- create or modify the operator SSH key;
- weaken or replace the existing SSH hardening;
- install SteamCMD, HLDS, ReHLDS, ReGameDLL_CS, Metamod, AMX Mod X, ReAPI, or
  YaPB;
- generate an RCON password or place secrets in shell arguments;
- publish UDP 27015 to arbitrary sources;
- prove A2S, RCON, restart, restore, or DDoS source-address behavior.

## Prerequisites

Before applying the script:

1. Provision Ubuntu 24.04 on an x86-64 host.
2. Create a dedicated operator with key-only `sudo` access.
3. Disable new root, password, and keyboard-interactive SSH authentication.
4. Disable SSH agent forwarding, TCP forwarding, tunnels, and X11 forwarding.
5. Verify a second operator session before closing the provider-created login.
6. Activate UFW with no inbound allow rules outside the reviewed SSH and game
   endpoint boundary.
7. Keep the exact control-plane address and host endpoint in deployment records,
   not in Git.

Apply must run through `sudo` from an SSH session whose source is the reviewed
control-plane `/32`. The script deliberately rejects local-console apply and
SSH sessions from another source.

## Review The Plan

Use documentation-only addresses when reviewing the command shape:

```bash
bash ./ops/gameserver/host-bootstrap.sh \
  --control-plane-ipv4-cidr 203.0.113.10/32 \
  --operator-user gsoadmin \
  --service-user goldsrc \
  --ssh-port 22 \
  --game-port 27015
```

The output must end with:

```text
PLAN_ONLY: no host changes were made; add --apply to execute this plan.
```

The plan does not print the supplied address.

## Apply

Transfer the reviewed script from the same Git revision as the approved plan,
then run it from the verified operator session:

```bash
sudo --preserve-env=SSH_CONNECTION bash /tmp/goldsrcops-gameserver-host-bootstrap.sh \
  --control-plane-ipv4-cidr <control-plane-ipv4>/32 \
  --operator-user gsoadmin \
  --service-user goldsrc \
  --ssh-port 22 \
  --game-port 27015 \
  --apply
```

Do not put a private key, password, provider identifier, or RCON secret in this
command. Keep the current session open until a new operator session succeeds
after apply.

Package upgrades may create `/var/run/reboot-required`. Reboot only in an
approved window, then verify operator access and the firewall again before any
runtime installation.

## Verify

Run these checks on the target without copying their host-specific output into
the repository:

```bash
sudo cat /etc/goldsrcops/gameserver/host-prepared
getent passwd goldsrc
sudo ufw status verbose
timedatectl show -p Timezone -p NTPSynchronized
sudo /usr/sbin/sshd -T | grep -E \
  '^(permitrootlogin|passwordauthentication|kbdinteractiveauthentication|allowusers|allowtcpforwarding) '
```

Expected properties:

- `goldsrc` is locked, uses `/usr/sbin/nologin`, and is not in `sudo`;
- the marker records only schema, account names, and port numbers;
- SSH and UDP 27015 each have exactly one allow rule for the reviewed source;
- root and interactive SSH authentication remain disabled;
- the host reports UTC with synchronized network time.

The repository smoke test is:

```bash
bash ./tools/smoke/gameserver-host-bootstrap.sh
```

It validates syntax, sanitized plan and stdin execution, input rejection,
dependency-bearing security upgrades, OpenSSH runtime-directory restoration,
stale-marker invalidation, exact UFW pass/fail decisions, and the non-root apply
guard. It does not mutate a host.

## Live Evidence

On 2026-09-01, this foundation was applied from reviewed, versioned source to the
bounded-trial game host. A controlled reboot and a fresh key-only session then
verified the installed security kernel, marker, account and path ownership,
effective SSH hardening, exact UFW policy, package state, unattended updates,
UTC/NTP, kernel settings, and zero failed systemd units. SteamCMD and all game
runtime artifacts and secrets remained absent. Host addresses, provider
identifiers, key fingerprints, and raw evidence are retained outside Git.

Later on 2026-09-01, the runtime installer from the merged revision was copied to
the same host and its SHA-256 was matched before execution. Plan-only mode was
reverified as non-mutating, then apply validated the pinned checksums and both
detached ReHLDS Team signatures, stabilized HLDS app `90` after four SteamCMD
passes, and installed ReHLDS `3.15.0.896` plus ReGameDLL_CS `5.30.0.814`. The
recorded HLDS build id is `5433925`. Independent post-install checks verified all
12 recorded artifact and unit hashes, the plugin-free baseline, zero failed
systemd units, and an active firewall. The service remained `disabled` and
`inactive`; configuration, activation marker, game process, and UDP listener
remained absent. Raw target evidence remains outside Git.

## Runtime Installer

`runtime-install.sh` is the separate first-install workflow required after the
host foundation passes. It:

- requires the root-owned `host-prepared` marker and exact foundation account,
  path, and mode contract;
- verifies the pinned official SteamCMD bootstrap with SHA-256;
- installs HLDS app `90` from the `steam_legacy` branch as the locked `goldsrc`
  account and repeats SteamCMD until the required tree is stable;
- verifies the pinned ReHLDS `3.15.0.896` and ReGameDLL_CS `5.30.0.814` ZIP
  hashes and both detached signatures against the embedded ReHLDS Team public
  key fingerprint;
- rejects Metamod, AMX Mod X, ReAPI, YaPB, Reunion, and an indirect GameDLL
  loader from the minimal baseline;
- records the updated SteamCMD hashes, actual Steam build id, app-manifest hash,
  release versions, installed binary hashes, and systemd unit hash in
  `runtime-installed`;
- installs a resource-bounded and privilege-restricted systemd unit without
  enabling or starting it.

The official SteamCMD bootstrap URL and the `steam_legacy` depot are mutable
upstream distribution channels. The installer fails if the bootstrap bytes no
longer match the reviewed SHA-256, while the delivered HLDS build is recorded as
deployment evidence rather than misrepresented as an immutable artifact. A
changed bootstrap or unexpected HLDS build requires a new review before apply.

Review the runtime plan locally:

```bash
bash ./ops/gameserver/runtime-install.sh \
  --service-user goldsrc \
  --game-port 27015
```

Transfer the reviewed self-contained script from the same commit to the target,
review the plan there, and apply it through the recorded operator account:

```bash
bash /tmp/goldsrcops-gameserver-runtime-install.sh \
  --service-user goldsrc \
  --game-port 27015

sudo bash /tmp/goldsrcops-gameserver-runtime-install.sh \
  --service-user goldsrc \
  --game-port 27015 \
  --apply
```

Do not add an RCON password, host address, provider identifier, or unreviewed
artifact URL to the command. Apply downloads only the hard-coded HTTPS sources
and refuses a non-empty or previously marked runtime.

After apply, retain host-specific output outside Git and verify:

```bash
sudo cat /etc/goldsrcops/gameserver/runtime-installed
sudo systemctl cat goldsrcops-gameserver.service
sudo systemctl is-enabled goldsrcops-gameserver.service
sudo systemctl is-active goldsrcops-gameserver.service
sudo test ! -e /etc/goldsrcops/gameserver/runtime-enabled
sudo ss -H -lntu
```

Expected state is `disabled`, `inactive`, no `runtime-enabled` marker, no
private configuration, and no game listener. The unit additionally requires a
root-controlled public configuration and private RCON configuration. It exposes
their contents to the service only through systemd credentials, never through
the process command line or environment. The installer also normalizes the
service-owned SteamCMD and game-server roots to mode `0750` after upstream tools
finish writing them. `ProtectProc=invisible` continues hiding other users'
process metadata, while `ProcSubset=all` keeps non-process APIs such as
`/proc/cpuinfo` available to the legacy engine.

The deterministic repository smoke is:

```bash
bash ./tools/smoke/gameserver-runtime-install.sh
```

It covers syntax, pinned values, plan sanitization, strict marker parsing,
checksum failure, the signing-key fingerprint, apply order, service activation
guards, and systemd hardening without downloading artifacts or changing a host.

## Runtime Activation

`runtime-activate.sh` is the separate all-or-nothing first-start workflow. It:

- rereads the strict host and runtime markers and verifies the installed
  SteamCMD, ReHLDS, ReGameDLL_CS, and systemd-unit hashes before configuration;
- requires the operator recorded by the host foundation and preserved
  `SSH_CONNECTION` metadata;
- derives the approved RCON source as an exact IPv4 `/32` only when UFW is
  active with default-deny incoming policy and the current SSH source matches
  the sole SSH and game-port allow rules;
- accepts one 32-128 character Base64-safe RCON secret through stdin only;
- assigns the public credential as ReHLDS `servercfgfile`, writes a fixed
  minimal baseline containing `sv_rcon_condebug 0` and the exact
  `rcon_adduser` command, and loads `rcon_password` from a root-only private
  credential;
- creates `runtime-enabled` only after both configuration files are ready;
- starts the service, requires public and private config-load markers from the
  current systemd invocation, and deliberately leaves it disabled across
  reboot;
- stops the service and removes every activation file if first start fails or
  the controlling SSH session is lost.

The source restriction follows the command contract documented by the pinned
[ReHLDS `3.15.0.896` README](https://github.com/rehlds/ReHLDS/blob/3.15.0.896/README.md).
An empty ReHLDS RCON user list permits every source that knows the password, so
activation refuses to create configuration without the exact non-empty `/32`.
The same pinned engine defines
[`servercfgfile`](https://github.com/rehlds/ReHLDS/blob/3.15.0.896/rehlds/engine/sv_main.cpp)
as the dedicated-server configuration selector; activation uses it so the
credential-backed public configuration is loaded during map startup.

Review the sanitized plan locally and on the target:

```bash
bash ./ops/gameserver/runtime-activate.sh
bash /tmp/goldsrcops-gameserver-runtime-activate.sh
```

Apply only by streaming the already escrowed control-plane value. Do not replace
the producer with `echo`, a literal, an environment variable, or a command-line
argument:

```bash
<approved-secret-producer> | \
  sudo --preserve-env=SSH_CONNECTION \
  bash /tmp/goldsrcops-gameserver-runtime-activate.sh \
    --apply \
    --rcon-secret-stdin
```

Expected successful state is `active` and `disabled`: the first controlled
process is running, but a reboot cannot silently cross the later restart gate.
Keep target-specific configuration, addresses, and raw output outside Git.

The deterministic repository smoke is:

```bash
bash ./tools/smoke/gameserver-runtime-activate.sh
```

It covers plan sanitization, stdin-only secret validation, strict marker
parsing, exact-source and default-deny firewall gates, public/private rendering,
activation order, disconnect rollback coverage, and the prohibition on service
enablement or secret arguments.

## Release-Soak Continuity

`soak-readiness.sh` performs the read-only game-host half of the v2.3 release
soak evaluation. It compares the owner-only baseline with the current systemd
service state, boot enablement, restart count, invocation, main process, and the
single configured UDP listener. The listener process must remain inside the
service control group. The script never starts, stops, restarts, enables, or
reconfigures the service and does not perform A2S or RCON.

Run the final check as root from the exact reviewed source after the baseline
completion time:

```bash
sudo bash ./ops/gameserver/soak-readiness.sh \
  --baseline-file /var/lib/goldsrcops/evidence/v2.3-soak-baseline.json \
  --evidence-file /var/lib/goldsrcops/evidence/v2.3-gameserver-soak-readiness.json
```

The baseline must be a root-owned regular file with mode `0600`. Evidence is
atomically written outside the repository with mode `0600` and contains only
sanitized booleans and interval metadata. It excludes the endpoint, port,
process and invocation identifiers, credentials, and raw command output.

During the active interval, `--allow-incomplete` may be used for diagnostics.
It reports `InProgress` and cannot close the release gate. Snapshot mode is for
deterministic CI only and always records `TargetEvidence: false`:

```bash
bash ./tools/smoke/gameserver-soak-readiness.sh
```

External A2S, persisted bot counts, and trial-period address stability remain
separate control-plane checks. A terminal host observation must not be presented
as continuous external availability.

## Next Boundary

Runtime activation, external `A2S_INFO`, authenticated `rcon_users`, controlled
restart/restore, guarded RCON, and the short post-restart observation have
passed on the bounded-trial host. Complete the integrated 24-hour release soak
with matching control-plane and game-host evidence, then record the separate
trial-period address-stability outcome. Keep target addresses, identifiers,
credentials, and raw evidence outside Git.
