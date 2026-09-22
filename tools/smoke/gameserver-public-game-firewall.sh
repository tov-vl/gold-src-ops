#!/usr/bin/env bash

set -Eeuo pipefail
IFS=$'\n\t'

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
workflow="$repo_root/ops/gameserver/public-game-firewall.sh"
smoke_directory="$(mktemp -d)"

cleanup() { rm -rf -- "$smoke_directory"; }
trap cleanup EXIT
fail() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

expect_failure() {
    local name="$1"
    shift
    if "$@" >"$smoke_directory/$name.out" 2>&1; then
        fail "Public-game firewall case '$name' passed unexpectedly."
    fi
    printf "Public-game firewall case '%s' failed as expected.\n" "$name"
}

run_firewall_validation() {
    local status_file="$1" expected_mode="$2"
    (
        # shellcheck source=/dev/null
        source "$workflow"
        prepared_ssh_port=22
        prepared_game_port=27015
        rcon_source_cidr=203.0.113.10/32
        # shellcheck disable=SC2317,SC2329
        ufw() { cat "$status_file"; }
        validate_firewall_rules "$expected_mode"
    )
}

bash -n "$workflow"

plan_output="$smoke_directory/plan.out"
bash "$workflow" > "$plan_output"
for expected in \
    'verify public-classic-v1, guarded autostart, and the inactive game-event agent' \
    'bind the current control-plane SSH source to the existing exact ReHLDS RCON /32' \
    'preserve restricted SSH and RCON while opening only IPv4 game UDP to players' \
    'retain an owner-only rollback input and do not restart the game process' \
    'PLAN_ONLY: no host state was inspected or changed'; do
    grep -Fq "$expected" "$plan_output" || fail "Enable plan is missing '$expected'."
done
if grep -Eq '203\.0\.113\.|rcon_password|[0-9a-f]{64}' "$plan_output"; then
    fail "Enable plan exposed host-specific, secret, or hash material."
fi

disable_plan="$smoke_directory/disable-plan.out"
bash "$workflow" --disable > "$disable_plan"
grep -Fq 'remove only the public IPv4 game UDP rule' "$disable_plan" ||
    fail "Disable plan does not bound the rollback."

expect_failure non-root-apply bash "$workflow" --apply
expect_failure conflicting-actions bash "$workflow" --disable --enable
expect_failure verify-apply bash "$workflow" --verify --apply

restricted="$smoke_directory/restricted.out"
cat > "$restricted" <<'EOF'
Status: active
Default: deny (incoming), allow (outgoing), disabled (routed)
To                         Action      From
22/tcp                     ALLOW IN    203.0.113.10
27015/udp                  ALLOW IN    203.0.113.10
EOF
run_firewall_validation "$restricted" restricted

public="$smoke_directory/public.out"
cat > "$public" <<'EOF'
Status: active
Default: deny (incoming), allow (outgoing), disabled (routed)
To                         Action      From
22/tcp                     ALLOW IN    203.0.113.10
27015/udp                  ALLOW IN    203.0.113.10
27015/udp                  ALLOW IN    Anywhere
EOF
run_firewall_validation "$public" public

expect_failure restricted-rejects-public run_firewall_validation "$public" restricted
expect_failure public-requires-broad-rule run_firewall_validation "$restricted" public
expect_failure invalid-firewall-mode run_firewall_validation "$restricted" unsupported

unexpected="$smoke_directory/unexpected.out"
cat > "$unexpected" <<'EOF'
Status: active
Default: deny (incoming), allow (outgoing), disabled (routed)
To                         Action      From
22/tcp                     ALLOW IN    203.0.113.10
27015/udp                  ALLOW IN    203.0.113.10
27015/udp                  ALLOW IN    Anywhere
443/tcp                    ALLOW IN    Anywhere
EOF
expect_failure unexpected-rule run_firewall_validation "$unexpected" public

if grep -Eq 'systemctl[[:space:]]+(start|stop|restart|reload|try-restart)' "$workflow"; then
    fail "The public-game firewall workflow can change the game process."
fi
if grep -Eiq 'rcon_password[[:space:]]+"|--secret|authorization:' "$workflow"; then
    fail "The public-game firewall workflow contains a credential input or value."
fi
grep -Fq 'ufw allow proto udp from 0.0.0.0/0' "$workflow" ||
    fail "The workflow does not publish only the IPv4 game endpoint."
grep -Fq 'ufw --force delete allow proto udp from 0.0.0.0/0' "$workflow" ||
    fail "The workflow does not own an exact public-rule rollback."
grep -Fq 'rcon_source_sha256=' "$workflow" ||
    fail "The marker does not bind the RCON source without exposing it."
grep -Fq '"$AUTOSTART_GUARD" --verify' "$workflow" ||
    fail "The workflow does not re-run the installed guarded-autostart verification."
grep -Fq 'validate_directory_metadata "$backup_directory" root root 700' "$workflow" ||
    fail "The workflow does not verify owner-only rollback-directory metadata."

printf '%s\n' 'Game-server public-game firewall smoke passed.'
