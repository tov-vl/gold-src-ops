# Controlled Game-Server Operations

This directory contains the provider-independent host foundation and pinned
runtime installer for the first controlled ReHLDS baseline.

Both scripts are plan-only by default. Each plan must be reviewed from the same
Git revision that will be applied to a paid host.

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
the process command line or environment.

The deterministic repository smoke is:

```bash
bash ./tools/smoke/gameserver-runtime-install.sh
```

It covers syntax, pinned values, plan sanitization, strict marker parsing,
checksum failure, the signing-key fingerprint, apply order, service activation
guards, and systemd hardening without downloading artifacts or changing a host.

## Next Boundary

The runtime installer is implemented and locally verified but has not yet been
applied to the bounded-trial host. After a reviewed apply leaves the service
disabled and inactive, use a separate activation workflow to create the public
configuration, inject the RCON secret, persist an exact `/32` ReHLDS RCON user,
create `runtime-enabled`, and perform the first controlled start. Public UDP
must remain closed until that source allowlist is non-empty and verified.
