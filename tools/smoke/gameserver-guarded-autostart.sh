#!/usr/bin/env bash

set -Eeuo pipefail
IFS=$'\n\t'
umask 077

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
workflow="$repo_root/ops/gameserver/guarded-autostart.sh"
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
        fail "Guarded-autostart case '$name' passed unexpectedly."
    fi
}

bash -n "$workflow"

enable_plan="$smoke_directory/enable-plan.out"
bash "$workflow" > "$enable_plan"
for expected in \
    'verify the active public-classic-v1 profile' \
    'preserve exact runtime and profile markers' \
    'fail-closed ExecCondition guard without a restart loop' \
    'enable only the game service' \
    'do not stop or restart the current game process' \
    'PLAN_ONLY: no host state was inspected or changed'; do
    grep -Fq "$expected" "$enable_plan" || fail "The enable plan is missing '$expected'."
done

disable_plan="$smoke_directory/disable-plan.out"
bash "$workflow" --disable > "$disable_plan"
for expected in \
    'verify the installed guard, drop-in, active profile, and exact rollback backup' \
    'disable boot startup without stopping or restarting the current game process' \
    'restore the exact pre-policy runtime and profile markers' \
    'PLAN_ONLY: no host state was inspected or changed'; do
    grep -Fq "$expected" "$disable_plan" || fail "The disable plan is missing '$expected'."
done

expect_failure unknown-option bash "$workflow" --profile arbitrary
expect_failure conflicting-actions bash "$workflow" --disable --enable
expect_failure verify-apply bash "$workflow" --verify --apply
set +e
bash "$workflow" --verify >"$smoke_directory/rejected-verify.out" 2>&1
verify_exit=$?
set -e
[[ "$verify_exit" -eq 78 ]] ||
    fail "A rejected boot guard returned '$verify_exit' instead of the fixed condition code 78."

hash_a="0000000000000000000000000000000000000000000000000000000000000000"
hash_b="1111111111111111111111111111111111111111111111111111111111111111"
runtime_marker="$smoke_directory/runtime-enabled"
profile_marker="$smoke_directory/managed-profile-active"
drop_in="$smoke_directory/20-guarded-autostart.conf"
policy_marker="$smoke_directory/guarded-autostart-active"

(
    # shellcheck source=/dev/null
    source "$workflow"
    runtime_marker_sha256="$hash_a"
    service_unit_sha256="$hash_b"
    public_config_sha256="$hash_a"
    rcon_source_policy="ssh-ufw-exact-ipv4-32"
    rcon_secret_transport="stdin"
    render_runtime_enabled_marker "$runtime_marker" enabled
    read_runtime_enabled_marker "$runtime_marker" enabled
    [[ "$service_autostart" == "enabled" ]]

    profile_backup_name="managed-profile-20260922T120000Z-Ab12Cd"
    baseline_public_sha256="$hash_a"
    baseline_runtime_enabled_sha256="$hash_b"
    active_public_sha256="$hash_a"
    active_profile_sha256="$hash_b"
    active_mapcycle_sha256="$hash_a"
    render_active_profile_marker "$profile_marker" "$hash_b"
    read_active_profile_marker "$profile_marker"
    [[ "$active_runtime_enabled_sha256" == "$hash_b" ]]

    render_drop_in "$drop_in"
    render_policy_marker "$policy_marker" \
        'guarded-autostart-20260922T120000Z-Ef34Gh' \
        "$hash_a" "$hash_b" "$hash_a" "$hash_b" "$hash_a" "$hash_b"
    read_policy_marker "$policy_marker"
    [[ "$policy_backup_name" == 'guarded-autostart-20260922T120000Z-Ef34Gh' ]]
) || fail "Marker rendering and parsing did not preserve the guarded policy contract."

grep -Fqx 'service_autostart=enabled' "$runtime_marker" ||
    fail "The rendered runtime marker does not record enabled autostart."
grep -Fqx "runtime_enabled_sha256=$hash_b" "$profile_marker" ||
    fail "The managed-profile marker was not rebound to the updated runtime marker."
grep -Fqx 'ExecCondition=/usr/local/libexec/goldsrcops-gameserver-boot-guard --verify' "$drop_in" ||
    fail "The drop-in does not run the installed guard as a start condition."
! grep -Fq 'ExecStartPre=' "$drop_in" ||
    fail "The drop-in uses a restartable pre-start failure instead of ExecCondition."

if command -v systemd-analyze >/dev/null 2>&1; then
    unit_fixture="$smoke_directory/goldsrcops-gameserver.service"
    cat > "$unit_fixture" <<'EOF'
[Unit]
Description=GoldSrcOps guarded-autostart smoke fixture

[Service]
Type=simple
ExecCondition=/bin/true
ExecStart=/bin/true

[Install]
WantedBy=multi-user.target
EOF
    systemd-analyze verify "$unit_fixture" >"$smoke_directory/systemd-analyze.out" 2>&1 ||
        fail "systemd-analyze rejected the guarded-autostart service contract."
fi

runtime_tampered="$smoke_directory/runtime-enabled-tampered"
cp -- "$runtime_marker" "$runtime_tampered"
sed -i 's/service_autostart=enabled/service_autostart=disabled/' "$runtime_tampered"
# shellcheck disable=SC2016
expect_failure wrong-autostart bash -c \
    'source "$1"; read_runtime_enabled_marker "$2" enabled' \
    _ "$workflow" "$runtime_tampered"

run_enable_body="$(sed -n '/^run_enable() {$/,/^}$/p' "$workflow")"
previous_line=0
# shellcheck disable=SC2016
for expected_call in \
    '    require_apply_environment' \
    '    acquire_transition_lock' \
    '    verify_active_profile_state disabled' \
    '    systemctl daemon-reload' \
    '    runuser -u "$prepared_service_user" -- "$INSTALLED_GUARD" --verify' \
    '    systemctl enable "$SERVICE_NAME"'; do
    current_line="$(grep -nFx "$expected_call" <<< "$run_enable_body" | cut -d: -f1)"
    [[ -n "$current_line" && "$current_line" -gt "$previous_line" ]] ||
        fail "The enable order is invalid at '$expected_call'."
    previous_line="$current_line"
done

grep -Fq "trap 'rollback_failed_enable 129' HUP" "$workflow" ||
    fail "Enable does not rollback after a lost controlling session."
grep -Fq "trap 'restore_enabled_policy 129' HUP" "$workflow" ||
    fail "Disable does not restore the prior policy after a lost controlling session."
grep -Fq 'systemctl is-active "$AGENT_SERVICE_NAME"' "$workflow" ||
    fail "The workflow does not require the game-event agent to remain inactive."
grep -Fq 'systemctl is-enabled "$AGENT_SERVICE_NAME"' "$workflow" ||
    fail "The workflow does not require the game-event agent to remain boot-disabled."
grep -Fq 'systemctl show "$SERVICE_NAME" -p InvocationID --value' "$workflow" ||
    fail "The workflow does not preserve the active service invocation."
grep -Fq 'systemctl show "$SERVICE_NAME" -p NRestarts --value' "$workflow" ||
    fail "The workflow does not preserve the restart count."

if grep -Eq 'systemctl[[:space:]]+(start|stop|restart)' "$workflow"; then
    fail "The boot-policy workflow can change the current game process."
fi
if grep -Eiq 'rcon_password[[:space:]]+"|--secret|authorization:' "$workflow"; then
    fail "The workflow contains a credential input or value."
fi

printf '%s\n' 'Game-server guarded-autostart smoke passed.'
