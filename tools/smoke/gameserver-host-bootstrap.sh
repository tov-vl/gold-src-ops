#!/usr/bin/env bash

set -Eeuo pipefail
IFS=$'\n\t'

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
bootstrap_script="$repo_root/ops/gameserver/host-bootstrap.sh"
smoke_directory="$(mktemp -d)"

cleanup() {
    rm -rf -- "$smoke_directory"
}
trap cleanup EXIT

fail() {
    printf 'ERROR: %s\n' "$*" >&2
    exit 1
}

expect_failure() {
    local name="$1"
    shift
    if "$@" >"$smoke_directory/$name.out" 2>&1; then
        fail "Game-host bootstrap case '$name' passed unexpectedly."
    fi
    printf "Game-host bootstrap case '%s' failed as expected.\n" "$name"
}

run_ufw_validation() {
    local status_file="$1"
    local require_rules="$2"
    (
        # shellcheck source=/dev/null
        source "$bootstrap_script"
        export control_plane_ipv4_cidr="203.0.113.10/32"
        export ssh_port="22"
        export game_port="27015"
        # shellcheck disable=SC2317,SC2329
        ufw() {
            cat "$status_file"
        }
        validate_inbound_rules "$require_rules"
    )
}

bash -n "$bootstrap_script"

plan_output="$smoke_directory/plan.out"
bash "$bootstrap_script" \
    --control-plane-ipv4-cidr 203.0.113.10/32 \
    --operator-user gsoadmin \
    --service-user goldsrc \
    --ssh-port 22 \
    --game-port 27015 \
    > "$plan_output"

for expected in \
    'validate Ubuntu 24.04 x86-64 and the current control-plane SSH source' \
    'require effective key-only SSH hardening for the verified operator' \
    'require active UFW and preserve only exact-source SSH and game UDP rules' \
    'install security updates and minimal 32-bit GoldSrc runtime dependencies' \
    'create a locked non-login service account without sudo membership' \
    'leave SteamCMD, server artifacts, plugins, public UDP, and secrets unchanged' \
    'PLAN_ONLY: no host changes were made'; do
    grep -Fq "$expected" "$plan_output" ||
        fail "Game-host plan is missing '$expected'."
done

if grep -Fq '203.0.113.10' "$plan_output"; then
    fail "Game-host plan exposed the control-plane address."
fi

stdin_plan_output="$smoke_directory/stdin-plan.out"
bash -s -- \
    --control-plane-ipv4-cidr 203.0.113.10/32 \
    --operator-user gsoadmin \
    --service-user goldsrc \
    --ssh-port 22 \
    --game-port 27015 \
    < "$bootstrap_script" \
    > "$stdin_plan_output"
grep -Fq 'PLAN_ONLY: no host changes were made' "$stdin_plan_output" ||
    fail "Game-host bootstrap did not execute its plan when read from stdin."

sshd_runtime_call="$smoke_directory/sshd-runtime-call.out"
sshd_runtime_expected="$smoke_directory/sshd-runtime-expected.out"
(
    # shellcheck source=/dev/null
    source "$bootstrap_script"
    # shellcheck disable=SC2317,SC2329
    install() {
        printf '%s\n' "$@" > "$sshd_runtime_call"
    }
    ensure_sshd_runtime_directory
)
cat > "$sshd_runtime_expected" <<'EOF'
-d
-m
0755
-o
root
-g
root
/run/sshd
EOF
cmp -s "$sshd_runtime_expected" "$sshd_runtime_call" ||
    fail "Game-host bootstrap did not restore the OpenSSH runtime directory safely."

expect_failure missing-cidr \
    bash "$bootstrap_script"

expect_failure invalid-cidr \
    bash "$bootstrap_script" \
        --control-plane-ipv4-cidr 203.0.113.999/32

expect_failure root-operator \
    bash "$bootstrap_script" \
        --control-plane-ipv4-cidr 203.0.113.10/32 \
        --operator-user root

expect_failure root-service \
    bash "$bootstrap_script" \
        --control-plane-ipv4-cidr 203.0.113.10/32 \
        --service-user root

expect_failure shared-account \
    bash "$bootstrap_script" \
        --control-plane-ipv4-cidr 203.0.113.10/32 \
        --operator-user gsoadmin \
        --service-user gsoadmin

expect_failure invalid-game-port \
    bash "$bootstrap_script" \
        --control-plane-ipv4-cidr 203.0.113.10/32 \
        --game-port 70000

expect_failure shared-port \
    bash "$bootstrap_script" \
        --control-plane-ipv4-cidr 203.0.113.10/32 \
        --ssh-port 27015 \
        --game-port 27015

expect_failure non-root-apply \
    bash "$bootstrap_script" \
        --control-plane-ipv4-cidr 203.0.113.10/32 \
        --apply

exact_ufw_status="$smoke_directory/ufw-exact.out"
cat > "$exact_ufw_status" <<'EOF'
Status: active

To                         Action      From
--                         ------      ----
22/tcp                     ALLOW IN    203.0.113.10
27015/udp                  ALLOW IN    203.0.113.10
EOF
run_ufw_validation "$exact_ufw_status" yes

ssh_only_ufw_status="$smoke_directory/ufw-ssh-only.out"
cat > "$ssh_only_ufw_status" <<'EOF'
Status: active

To                         Action      From
--                         ------      ----
22/tcp                     ALLOW IN    203.0.113.10
EOF
run_ufw_validation "$ssh_only_ufw_status" no
expect_failure incomplete-final-ufw \
    run_ufw_validation "$ssh_only_ufw_status" yes

broad_ufw_status="$smoke_directory/ufw-broad.out"
cat > "$broad_ufw_status" <<'EOF'
Status: active

To                         Action      From
--                         ------      ----
22/tcp                     ALLOW IN    203.0.113.10
27015/udp                  ALLOW IN    203.0.113.10
OpenSSH                    ALLOW IN    Anywhere
EOF
expect_failure broad-ufw \
    run_ufw_validation "$broad_ufw_status" no

ipv6_ufw_status="$smoke_directory/ufw-ipv6.out"
cat > "$ipv6_ufw_status" <<'EOF'
Status: active

To                         Action      From
--                         ------      ----
22/tcp                     ALLOW IN    203.0.113.10
27015/udp                  ALLOW IN    203.0.113.10
22/tcp (v6)                ALLOW IN    Anywhere (v6)
EOF
expect_failure broad-ipv6-ufw \
    run_ufw_validation "$ipv6_ufw_status" no

duplicate_ufw_status="$smoke_directory/ufw-duplicate.out"
cat > "$duplicate_ufw_status" <<'EOF'
Status: active

To                         Action      From
--                         ------      ----
22/tcp                     ALLOW IN    203.0.113.10
22/tcp                     ALLOW IN    203.0.113.10
27015/udp                  ALLOW IN    203.0.113.10
EOF
expect_failure duplicate-ufw \
    run_ufw_validation "$duplicate_ufw_status" no

printf 'Game-host bootstrap smoke passed.\n'
