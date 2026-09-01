#!/usr/bin/env bash

set -Eeuo pipefail
IFS=$'\n\t'

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
activator="$repo_root/ops/gameserver/runtime-activate.sh"
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
        fail "Game-runtime activation case '$name' passed unexpectedly."
    fi
    printf "Game-runtime activation case '%s' failed as expected.\n" "$name"
}

write_prepared_marker() {
    local path="$1"
    cat > "$path" <<'EOF'
schema_version=1
operator_user=gsoadmin
service_user=goldsrc
ssh_port=22
game_port=27015
EOF
}

write_runtime_marker() {
    local path="$1"
    local hash="0000000000000000000000000000000000000000000000000000000000000000"
    cat > "$path" <<EOF
schema_version=1
steam_app_id=90
steam_branch=steam_legacy
steamcmd_bootstrap_sha256=$hash
steamcmd_script_sha256=$hash
steamcmd_binary_sha256=$hash
steamclient_binary_sha256=$hash
hlds_build_id=5433925
hlds_app_manifest_sha256=$hash
base_hlds_linux_sha256=$hash
rehlds_version=3.15.0.896
rehlds_archive_sha256=$hash
rehlds_hlds_linux_sha256=$hash
rehlds_engine_sha256=$hash
regamedll_version=5.30.0.814
regamedll_archive_sha256=$hash
regamedll_binary_sha256=$hash
service_unit_sha256=$hash
EOF
}

bash -n "$activator"

plan_output="$smoke_directory/plan.out"
bash "$activator" > "$plan_output"
for expected in \
    'require the reviewed host foundation, installed runtime identity, and unchanged systemd unit' \
    'derive one exact IPv4 /32 from preserved SSH metadata and the two-rule UFW boundary' \
    'accept one bounded Base64-safe RCON secret through stdin only and never print it' \
    'atomically install root-controlled public/private configuration and runtime-enabled' \
    'start but do not enable goldsrcops-gameserver.service, with rollback on any first-start failure' \
    'verify stable active state, process ownership, zero restarts, and one UDP listener' \
    'PLAN_ONLY: no stdin was read and no host changes were made'; do
    grep -Fq "$expected" "$plan_output" ||
        fail "Game-runtime activation plan is missing '$expected'."
done

if grep -Eiq '203\.0\.113\.|198\.51\.100\.|rcon_password|[A-Za-z0-9+/=_-]{32,}' "$plan_output"; then
    fail "Game-runtime activation plan exposed host-specific or secret-shaped data."
fi

stdin_plan_output="$smoke_directory/stdin-plan.out"
bash -s -- < "$activator" > "$stdin_plan_output"
grep -Fq 'PLAN_ONLY: no stdin was read and no host changes were made' "$stdin_plan_output" ||
    fail "Game-runtime activator did not execute its plan when read from stdin."

expect_failure stdin-without-apply bash "$activator" --rcon-secret-stdin
expect_failure unknown-option bash "$activator" --secret value

(
    # shellcheck source=/dev/null
    source "$activator"
    validate_ipv4_address 0.0.0.0
    validate_ipv4_address 203.0.113.10
    validate_ipv4_address 255.255.255.255
)
# shellcheck disable=SC2016
expect_failure invalid-ipv4 bash -c 'source "$1"; validate_ipv4_address "$2"' \
    _ "$activator" 203.0.113.256
# shellcheck disable=SC2016
expect_failure ambiguous-ipv4 bash -c 'source "$1"; validate_ipv4_address "$2"' \
    _ "$activator" 203.0.113.010

(
    # shellcheck source=/dev/null
    source "$activator"
    # Consumed by derive_and_validate_rcon_source from the sourced activator.
    # shellcheck disable=SC2030,SC2034
    prepared_ssh_port=22
    # shellcheck disable=SC2030,SC2034
    prepared_game_port=27015
    # Called indirectly by derive_and_validate_rcon_source.
    # shellcheck disable=SC2329
    read_firewall_status() {
        cat <<'EOF'
Status: active
Logging: on (low)
Default: deny (incoming), allow (outgoing), disabled (routed)

To                         Action      From
--                         ------      ----
22/tcp                     ALLOW IN    203.0.113.10
27015/udp                  ALLOW IN    203.0.113.10
EOF
    }
    SSH_CONNECTION='203.0.113.10 49152 198.51.100.20 22' \
        derive_and_validate_rcon_source
    # Assigned by derive_and_validate_rcon_source.
    # shellcheck disable=SC2154
    [[ "$approved_rcon_cidr" == 203.0.113.10/32 ]]
)

# shellcheck disable=SC2016
expect_failure mismatched-firewall-source bash -c '
    source "$1"
    prepared_ssh_port=22
    prepared_game_port=27015
    read_firewall_status() {
        cat <<EOF
Status: active
Default: deny (incoming), allow (outgoing), disabled (routed)
22/tcp                     ALLOW IN    203.0.113.11
27015/udp                  ALLOW IN    203.0.113.11
EOF
    }
    SSH_CONNECTION="203.0.113.10 49152 198.51.100.20 22" \
        derive_and_validate_rcon_source
' _ "$activator"

# shellcheck disable=SC2016
expect_failure permissive-firewall-default bash -c '
    source "$1"
    prepared_ssh_port=22
    prepared_game_port=27015
    read_firewall_status() {
        cat <<EOF
Status: active
Default: allow (incoming), allow (outgoing), disabled (routed)
22/tcp                     ALLOW IN    203.0.113.10
27015/udp                  ALLOW IN    203.0.113.10
EOF
    }
    SSH_CONNECTION="203.0.113.10 49152 198.51.100.20 22" \
        derive_and_validate_rcon_source
' _ "$activator"

safe_secret='AbCdEfGhIjKlMnOpQrStUvWxYz012345+/=_-'
(
    # shellcheck source=/dev/null
    source "$activator"
    validate_rcon_secret "$safe_secret"
)
# shellcheck disable=SC2016
expect_failure short-secret bash -c 'source "$1"; validate_rcon_secret "$2"' \
    _ "$activator" short
# shellcheck disable=SC2016
expect_failure quoted-secret bash -c 'source "$1"; validate_rcon_secret "$2"' \
    _ "$activator" 'AbCdEfGhIjKlMnOpQrStUvWxYz012345"bad'
# shellcheck disable=SC2016
expect_failure whitespace-secret bash -c 'source "$1"; validate_rcon_secret "$2"' \
    _ "$activator" 'AbCdEfGhIjKlMnOpQrStUvWxYz012345 bad'

(
    # shellcheck source=/dev/null
    source "$activator"
    # Consumed by read_rcon_secret from the sourced activator.
    # shellcheck disable=SC2034
    read_secret_from_stdin=true
    read_rcon_secret
    [[ "${#rcon_secret}" -eq "${#safe_secret}" ]]
) <<< "$safe_secret"

prepared_marker="$smoke_directory/host-prepared"
write_prepared_marker "$prepared_marker"
(
    # shellcheck source=/dev/null
    source "$activator"
    read_prepared_marker "$prepared_marker"
    # Assigned by the sourced marker parser.
    # shellcheck disable=SC2154
    [[ "$prepared_operator_user" == gsoadmin ]]
    # shellcheck disable=SC2154
    [[ "$prepared_service_user" == goldsrc ]]
    # shellcheck disable=SC2031,SC2154
    [[ "$prepared_ssh_port" == 22 ]]
    # shellcheck disable=SC2031,SC2154
    [[ "$prepared_game_port" == 27015 ]]
)

duplicate_prepared_marker="$smoke_directory/host-prepared-duplicate"
write_prepared_marker "$duplicate_prepared_marker"
printf 'game_port=27015\n' >> "$duplicate_prepared_marker"
# shellcheck disable=SC2016
expect_failure duplicate-prepared-marker bash -c \
    'source "$1"; read_prepared_marker "$2"' \
    _ "$activator" "$duplicate_prepared_marker"

runtime_marker="$smoke_directory/runtime-installed"
write_runtime_marker "$runtime_marker"
(
    # shellcheck source=/dev/null
    source "$activator"
    read_runtime_marker "$runtime_marker"
    # Assigned by the sourced marker parser.
    # shellcheck disable=SC2154
    [[ "${runtime_values[hlds_build_id]}" == 5433925 ]]
    # shellcheck disable=SC2154
    [[ "${runtime_values[rehlds_version]}" == 3.15.0.896 ]]
    # shellcheck disable=SC2154
    [[ "${runtime_values[regamedll_version]}" == 5.30.0.814 ]]
)

unknown_runtime_marker="$smoke_directory/runtime-installed-unknown"
write_runtime_marker "$unknown_runtime_marker"
printf 'provider_id=forbidden\n' >> "$unknown_runtime_marker"
# shellcheck disable=SC2016
expect_failure unknown-runtime-marker bash -c \
    'source "$1"; read_runtime_marker "$2"' \
    _ "$activator" "$unknown_runtime_marker"

public_configuration="$smoke_directory/server-public.cfg"
private_configuration="$smoke_directory/server-private.cfg"
(
    # shellcheck source=/dev/null
    source "$activator"
    # Consumed by render_public_configuration from the sourced activator.
    # shellcheck disable=SC2034
    approved_rcon_cidr=203.0.113.10/32
    rcon_secret="$safe_secret"
    render_public_configuration "$public_configuration"
    render_private_configuration "$private_configuration"
)

cat > "$smoke_directory/server-public.expected" <<'EOF'
hostname "GoldSrcOps Controlled Baseline"
sv_lan "0"
sv_password ""
sv_rcon_condebug "0"
rcon_adduser 203.0.113.10/32
EOF
printf 'rcon_password "%s"\n' "$safe_secret" > "$smoke_directory/server-private.expected"
cmp -s "$public_configuration" "$smoke_directory/server-public.expected" ||
    fail "The rendered public runtime configuration changed."
cmp -s "$private_configuration" "$smoke_directory/server-private.expected" ||
    fail "The rendered private runtime configuration changed."
[[ "$(stat -c '%a' "$public_configuration")" == 640 ]] ||
    fail "The staged public configuration mode is unsafe."
[[ "$(stat -c '%a' "$private_configuration")" == 600 ]] ||
    fail "The staged private configuration mode is unsafe."
! grep -Fq 'rcon_password' "$public_configuration" ||
    fail "The public runtime configuration contains the RCON password command."
! grep -Fq 'rcon_adduser' "$private_configuration" ||
    fail "The private runtime configuration contains source policy."

run_apply_body="$(sed -n '/^run_apply() {$/,/^}$/p' "$activator")"
previous_line=0
# The expected source lines must remain literal.
# shellcheck disable=SC2016
for expected_call in \
    '    require_apply_environment' \
    '    read_rcon_secret' \
    '    prepare_activation_files' \
    '    install_activation_files' \
    '    systemctl start "$SERVICE_NAME"' \
    '    verify_first_start'; do
    current_line="$(grep -nFx "$expected_call" <<< "$run_apply_body" | cut -d: -f1)"
    [[ -n "$current_line" && "$current_line" -gt "$previous_line" ]] ||
        fail "Game-runtime activation order is invalid at '$expected_call'."
    previous_line="$current_line"
done

# shellcheck disable=SC2016
grep -Fq 'systemctl stop "$SERVICE_NAME"' "$activator" ||
    fail "Activation rollback does not stop the service."
grep -Fq "trap 'rollback_activation 129' HUP" "$activator" ||
    fail "Activation rollback does not cover a lost SSH session."
# The expected source paths must remain literal.
# shellcheck disable=SC2016
for rollback_path in \
    '"$RUNTIME_ENABLED_MARKER"' \
    '"$PUBLIC_CONFIGURATION"' \
    '"$PRIVATE_CONFIGURATION"'; do
    grep -Fq "$rollback_path" "$activator" ||
        fail "Activation rollback is missing '$rollback_path'."
done

if grep -Eq 'systemctl[[:space:]]+(enable|restart)' "$activator"; then
    fail "First activation must not enable or restart the game-server unit."
fi
if grep -Eq -- '--rcon-(password|secret)([[:space:]]|=)' "$activator"; then
    fail "The activator accepts an RCON secret through an unsafe argument."
fi
# shellcheck disable=SC2016
if grep -Eq '(^|[[:space:]])(echo|printf)[^\n]*\$rcon_secret' "$activator"; then
    fail "The activator can print the RCON secret."
fi
if grep -Eq 'set[[:space:]]+-[^[:space:]]*x|Environment=.*RCON|export.*rcon_secret' "$activator"; then
    fail "The activator contains an unsafe secret transport or tracing mode."
fi

printf 'Game-server runtime activation smoke test passed.\n'
