# Controlled Game-Server Host Bootstrap

This directory contains the provider-independent host foundation for the first
controlled ReHLDS baseline. It prepares a minimal Ubuntu host before SteamCMD or
game-server artifacts are installed.

The bootstrap is plan-only by default. It must be reviewed before `--apply` is
used against a paid host.

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

It validates syntax, sanitized plan output, input rejection, and the non-root
apply guard. It does not mutate a host.

## Next Boundary

After this foundation passes on the target, install SteamCMD and the pinned
HLDS/ReHLDS/ReGameDLL_CS artifacts through a separate verified installer. That
installer must verify release hashes and signatures, keep the RCON secret out
of command history, install a constrained systemd unit, and leave public UDP
closed until the RCON source allowlist is non-empty and verified.
