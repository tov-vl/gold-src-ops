#!/usr/bin/env bash

set -Eeuo pipefail
IFS=$'\n\t'
umask 077

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
workflow="$repo_root/ops/gameserver/game-event-persistent.sh"
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
        fail "Persistent game-event case '$name' passed unexpectedly."
    fi
}

assert_order() {
    local body="$1"
    shift
    local expected current_line previous_line=0
    for expected in "$@"; do
        current_line="$(grep -nFx "$expected" <<< "$body" | cut -d: -f1 | awk -v previous="$previous_line" '$1 > previous { print; exit }')"
        [[ -n "$current_line" && "$current_line" -gt "$previous_line" ]] ||
            fail "Persistent transition order is invalid at '$expected'."
        previous_line="$current_line"
    done
}

"$BASH" -n "$workflow"

overview_output="$smoke_directory/overview.out"
"$BASH" "$workflow" > "$overview_output"
for expected in \
    'accepted public-classic-v1 and guarded-autostart-v1' \
    'reviewed pilot for identity validation, stdin-only credential transport' \
    'bind both independent services to exact hashes' \
    'retain queue/spool evidence before any rollback cleanup' \
    'PLAN_ONLY: no host state, stdin, identity file, service, or network endpoint was inspected or changed'; do
    grep -Fq "$expected" "$overview_output" ||
        fail "Persistent overview is missing '$expected'."
done

activate_plan="$smoke_directory/activate-plan.out"
"$BASH" "$workflow" --activate --identity-file /does/not/exist > "$activate_plan"
for expected in \
    'disable the old guard without stopping the game' \
    'wait for a fresh root-only external A2S/RCON gate receipt' \
    'enable producer, spool import, delivery, game boot startup, and agent boot startup without coupling the two services' \
    'disable intake, seal durable evidence, restore the pilot baseline' \
    'PLAN_ONLY:'; do
    grep -Fq "$expected" "$activate_plan" ||
        fail "Persistent activation plan is missing '$expected'."
done

rollback_plan="$smoke_directory/rollback-plan.out"
"$BASH" "$workflow" --rollback > "$rollback_plan"
for expected in \
    'disable producer and both boot entries' \
    'atomically seal local queue and spool evidence' \
    'execute reviewed pilot rollback' \
    'restore the exact pre-activation guard and marker bytes'; do
    grep -Fq "$expected" "$rollback_plan" ||
        fail "Persistent rollback plan is missing '$expected'."
done

if grep -Eiq 'client[_-]?secret=|authorization:[[:space:]]*bearer|203[.]0[.]113[.]|198[.]51[.]100[.]' \
    "$overview_output" "$activate_plan" "$rollback_plan"; then
    fail "Persistent plans exposed host-specific or secret-shaped content."
fi

expect_failure multiple-operations "$BASH" "$workflow" --activate --rollback
expect_failure multiple-guards "$BASH" "$workflow" --guard-game --guard-agent
expect_failure relative-identity "$BASH" "$workflow" --activate --identity-file relative
expect_failure secret-without-apply "$BASH" "$workflow" --activate --client-secret-stdin
expect_failure verify-apply "$BASH" "$workflow" --verify --apply
expect_failure guard-apply "$BASH" "$workflow" --guard-game --apply
expect_failure apply-without-operation "$BASH" "$workflow" --apply
expect_failure activate-apply-without-identity "$BASH" "$workflow" --activate --apply --client-secret-stdin
expect_failure activate-apply-without-secret "$BASH" "$workflow" --activate --apply --identity-file /tmp/identity
expect_failure unknown-option "$BASH" "$workflow" --force

producer_source="$smoke_directory/amxx-source.cfg"
producer_enabled="$smoke_directory/amxx-enabled.cfg"
cat > "$producer_source" <<'EOF'
goldsrcops_events_enabled 0
goldsrcops_spool_max_pending 1000
EOF

environment_source="$smoke_directory/agent-source.env"
environment_enabled="$smoke_directory/agent-enabled.env"
game_drop_in="$smoke_directory/game.conf"
agent_drop_in="$smoke_directory/agent.conf"
cat > "$environment_source" <<'EOF'
GameEventAgent__Spool__Enabled=true
GameEventAgent__Delivery__Enabled=false
EOF

(
    # shellcheck source=/dev/null
    source "$workflow"
    render_producer_configuration "$producer_source" "$producer_enabled" 1
    render_delivery_environment "$environment_source" "$environment_enabled" true
    render_game_drop_in "$game_drop_in"
    render_agent_drop_in "$agent_drop_in"
    grep -Fxq "ExecCondition=$installed_persistent_guard --guard-game" "$game_drop_in" ||
        fail "The game guard must retain the service identity."
    grep -Fxq "ExecCondition=!$installed_persistent_guard --guard-agent" "$agent_drop_in" ||
        fail "The agent guard must read the root-only activation state."
)
grep -Fxq 'goldsrcops_events_enabled 1' "$producer_enabled" ||
    fail "Producer rendering did not enable exactly the reviewed gate."
grep -Fxq 'goldsrcops_spool_max_pending 1000' "$producer_enabled" ||
    fail "Producer rendering did not preserve the bounded spool limit."
grep -Fxq 'GameEventAgent__Delivery__Enabled=true' "$environment_enabled" ||
    fail "Delivery rendering did not enable the reviewed gate."

duplicate_producer="$smoke_directory/amxx-duplicate.cfg"
cat "$producer_source" "$producer_source" > "$duplicate_producer"
# shellcheck disable=SC2016
expect_failure duplicate-producer "$BASH" -c \
    'source "$1"; render_producer_configuration "$2" "$3" 1' \
    _ "$workflow" "$duplicate_producer" "$smoke_directory/unused.cfg"

secret_environment="$smoke_directory/agent-secret.env"
cat > "$secret_environment" <<'EOF'
GameEventAgent__Spool__Enabled=true
GameEventAgent__Delivery__Enabled=false
ClientSecret=forbidden
EOF
# shellcheck disable=SC2016
expect_failure secret-environment "$BASH" -c \
    'source "$1"; render_delivery_environment "$2" "$3" true' \
    _ "$workflow" "$secret_environment" "$smoke_directory/unused.env"

(
    # shellcheck source=/dev/null
    source "$workflow"
    guarded_backup_root="$smoke_directory/guarded-backups"
    backup_root="$smoke_directory/persistent-records"
    guarded_name=guarded-autostart-20260923T000000Z-abcdef
    original_backup="$guarded_backup_root/$guarded_name"
    mkdir -p -- "$original_backup"
    chmod 0700 "$guarded_backup_root" "$original_backup"
    printf 'runtime-before-guard\n' > "$original_backup/runtime-enabled"
    printf 'profile-before-guard\n' > "$original_backup/managed-profile-active"
    chmod 0600 "$original_backup/runtime-enabled" "$original_backup/managed-profile-active"
    runtime_hash="$(sha256sum "$original_backup/runtime-enabled" | awk '{print $1}')"
    profile_hash="$(sha256sum "$original_backup/managed-profile-active" | awk '{print $1}')"
    fixture="$smoke_directory/guarded-fixture"
    mkdir -p -- "$fixture"
    installed_autostart_guard="$fixture/guard"
    autostart_drop_in="$fixture/drop-in"
    autostart_marker="$fixture/autostart-marker"
    runtime_enabled_marker="$fixture/runtime-enabled"
    active_profile_marker="$fixture/managed-profile-active"
    printf 'guard\n' > "$installed_autostart_guard"
    printf 'drop-in\n' > "$autostart_drop_in"
    printf 'runtime-after-guard\n' > "$runtime_enabled_marker"
    printf 'profile-after-guard\n' > "$active_profile_marker"
    cat > "$autostart_marker" <<EOF
backup_name=$guarded_name
baseline_runtime_enabled_sha256=$runtime_hash
baseline_active_profile_sha256=$profile_hash
EOF
    chmod 0755 "$installed_autostart_guard"
    chmod 0644 "$autostart_drop_in"
    chmod 0640 "$autostart_marker" "$runtime_enabled_marker" "$active_profile_marker"
    install() {
        if [[ "$1" == -d ]]; then
            mkdir -p -- "${@: -2}"
            chmod 0700 -- "${@: -2}"
        else
            command install "$@"
        fi
    }
    # shellcheck disable=SC2329
    validate_directory_metadata() {
        [[ -d "$1" && ! -L "$1" && "$(stat -c '%a' "$1")" == "$4" ]] || return 1
    }
    # shellcheck disable=SC2329
    validate_file_metadata() {
        [[ -f "$1" && ! -L "$1" && "$(stat -c '%a' "$1")" == "$4" ]] || return 1
    }

    printf 'tampered\n' > "$original_backup/runtime-enabled"
    if capture_baseline 2>/dev/null; then
        fail "Persistent capture accepted a drifted guarded rollback input."
    fi
    printf 'runtime-before-guard\n' > "$original_backup/runtime-enabled"
    capture_baseline
    [[ "$(sha256sum "$backup_directory/guarded-autostart-backup/runtime-enabled" | awk '{print $1}')" == "$runtime_hash" ]] ||
        fail "Persistent capture omitted the exact guarded rollback input."
    printf 'tampered\n' > "$backup_directory/autostart-guard"
    if verify_baseline_record 2>/dev/null; then
        fail "Persistent baseline accepted a drifted guarded script."
    fi
    printf 'guard\n' > "$backup_directory/autostart-guard"
    rm -rf -- "$original_backup"
    restore_guarded_backup
    [[ "$(sha256sum "$guarded_backup_root/$guarded_name/runtime-enabled" | awk '{print $1}')" == "$runtime_hash" ]] ||
        fail "Guarded rollback did not restore the exact runtime marker."
    restore_guarded_backup
    printf 'tampered\n' > "$guarded_backup_root/$guarded_name/runtime-enabled"
    if restore_guarded_backup 2>/dev/null; then
        fail "Guarded rollback accepted a drifted existing backup."
    fi
    [[ "$(cat "$guarded_backup_root/$guarded_name/runtime-enabled")" == tampered ]] ||
        fail "Guarded rollback overwrote a drifted existing backup."
)

activate_body="$(sed -n '/^run_activate() {$/,/^}$/p' "$workflow")"
assert_order "$activate_body" \
    '    require_accepted_boundary' \
    '    capture_baseline' \
    '    arm_transition_rollback' \
    '    "$autostart_workflow" --disable --apply' \
    '    GOLDSRCOPS_INHERITED_ACTIVATION_LOCK_FD=9 "$pilot_activator" \' \
    '    pilot_activation_present=true' \
    '    require_settled_activation_status "$(capture_agent_status)"' \
    '    await_external_gate' \
    '    render_producer_configuration "$producer_configuration" "$staging_directory/amxx.cfg" 1' \
    '    systemctl stop "$AGENT_SERVICE_NAME"' \
    '    systemctl stop "$GAME_SERVICE_NAME"' \
    '    install_persistent_policy' \
    '    verify_persistent_files' \
    '    systemctl start "$GAME_SERVICE_NAME"' \
    '    systemctl start "$AGENT_SERVICE_NAME"' \
    '    systemctl enable "$GAME_SERVICE_NAME" >/dev/null' \
    '    systemctl enable "$AGENT_SERVICE_NAME" >/dev/null'

run_external_gate_case() {
    local name="$1" response_kind="$2" gate_directory
    gate_directory="$smoke_directory/gate-$name"
    local output="$smoke_directory/gate-$name.out" process_id challenge attempt
    mkdir -- "$gate_directory"
    "$BASH" -c '
        source "$1"
        backup_directory="$2"
        backup_name=persistent-gameplay-20260923T000000Z-abcdef
        if [[ "$3" == timeout ]]; then
            external_gate_timeout=1
        else
            external_gate_timeout=5
        fi
        systemctl() {
            if [[ -e "$backup_directory/restarted" ]]; then
                printf "%s\n" "fedcba9876543210fedcba9876543210"
            else
                printf "%s\n" "0123456789abcdef0123456789abcdef"
            fi
        }
        require_unit_state() { :; }
        capture_agent_status() { printf "{}\n"; }
        require_settled_activation_status() { :; }
        if [[ "$3" != valid ]]; then
            rollback_transition() { printf "MOCK_ROLLBACK=called\n"; }
            arm_transition_rollback
        fi
        await_external_gate
    ' _ "$workflow" "$gate_directory" "$response_kind" >"$output" 2>&1 &
    process_id=$!
    for attempt in {1..100}; do
        [[ -p "$gate_directory/external-gate.fifo" ]] && break
        sleep 0.02
    done
    [[ -p "$gate_directory/external-gate.fifo" ]] || fail "External gate '$name' did not open."
    [[ "$(stat -c '%a' "$gate_directory/external-gate.fifo")" == 600 ]] ||
        fail "External gate '$name' is not root-only."
    if [[ "$response_kind" == interrupted ]]; then
        kill -TERM "$process_id"
    elif [[ "$response_kind" != timeout ]]; then
        for attempt in {1..100}; do
            challenge="$(awk '/^EXTERNAL_GATE_READY: / { print $3 }' "$output")"
            [[ -n "$challenge" ]] && break
            sleep 0.02
        done
        [[ "$challenge" =~ ^[0-9a-f]{32}$ ]] || fail "External gate '$name' challenge is invalid."
        if [[ "$response_kind" == restarted ]]; then
            : > "$gate_directory/restarted"
        elif [[ "$response_kind" != valid ]]; then
            challenge=00000000000000000000000000000000
        fi
        timeout 2 bash -c 'printf "%s\n" "$1" > "$2"' _ \
            "$challenge" "$gate_directory/external-gate.fifo" ||
            fail "External gate '$name' did not accept a receipt writer."
    fi
    if [[ "$response_kind" == valid ]]; then
        wait "$process_id" || fail "External gate '$name' rejected a valid receipt."
        grep -Fxq 'EXTERNAL_GATE=operator-attested' "$output" ||
            fail "External gate '$name' omitted its accepted marker."
        [[ ! -e "$gate_directory/external-gate.fifo" ]] ||
            fail "External gate '$name' retained its FIFO."
    elif wait "$process_id"; then
        fail "External gate '$name' accepted an invalid or missing receipt."
    else
        grep -Fxq 'MOCK_ROLLBACK=called' "$output" ||
            fail "External gate '$name' did not invoke rollback."
    fi
}

run_external_gate_case accepted valid
run_external_gate_case invalid invalid
run_external_gate_case restarted restarted
run_external_gate_case expired timeout
run_external_gate_case interrupted interrupted

rollback_body="$(sed -n '/^rollback_transition() {$/,/^}$/p' "$workflow")"
assert_order "$rollback_body" \
    '    systemctl disable "$AGENT_SERVICE_NAME" >/dev/null 2>&1 || rollback_failed=true' \
    '    systemctl stop "$AGENT_SERVICE_NAME" >/dev/null 2>&1 || rollback_failed=true' \
    '    systemctl disable "$GAME_SERVICE_NAME" >/dev/null 2>&1 || rollback_failed=true' \
    '    systemctl stop "$GAME_SERVICE_NAME" >/dev/null 2>&1 || rollback_failed=true' \
    '        seal_local_evidence || {' \
    '        if ! GOLDSRCOPS_INHERITED_ACTIVATION_LOCK_FD=9 "$pilot_activator" --rollback --apply; then' \
    '    restore_prior_policy || return 1'

grep -Fq "trap 'rollback_on_failure 129' HUP" "$workflow" ||
    fail "Persistent activation does not rollback after a lost controlling session."
grep -Fq 'install -d -m 0750 -o "$service_user" -g "$service_group"' "$workflow" ||
    fail "Rollback sealing does not recreate an empty agent state boundary."
grep -Fq 'expected_sha="$marker_producer_sha256"' "$workflow" ||
    fail "Persistent overlay verification does not account for the enabled producer configuration."

for drop_in_renderer in render_game_drop_in render_agent_drop_in; do
    body="$(sed -n "/^$drop_in_renderer() {$/,/^}$/p" "$workflow")"
    ! grep -Eq '^(Requires|PartOf)=' <<< "$body" ||
        fail "The persistent drop-ins couple the game and agent services."
done

game_guard_body="$(sed -n '/^verify_persistent_game_files() {$/,/^}$/p' "$workflow")"
agent_guard_body="$(sed -n '/^verify_persistent_agent_files() {$/,/^}$/p' "$workflow")"
if grep -Eq 'client_secret_file|environment_file|agent_unit_file|pilot_enabled_marker|activation_state_file' \
    <<< "$game_guard_body"; then
    fail "The game boot guard depends on agent identity or runtime state."
fi
if grep -Eq 'game_unit_file|liblist_file|producer_configuration|verify_overlay_payload' \
    <<< "$agent_guard_body"; then
    fail "The agent boot guard depends on active game runtime state."
fi
grep -Fq 'verify_persistent_game_files' "$workflow" ||
    fail "The game boot guard scope is missing."
grep -Fq 'verify_persistent_agent_files' "$workflow" ||
    fail "The agent boot guard scope is missing."
grep -Fq 'verify_persistent_files' "${workflow}" ||
    fail "The complete owner-only verification scope is missing."

grep -Fq -- '--client-secret-stdin' <<< "$activate_body" ||
    fail "Activation does not forward the OAuth secret through inherited stdin."
if grep -Eiq -- '--client-secret([[:space:]]|=)|authorization:[[:space:]]*bearer' "$workflow"; then
    fail "Persistent tooling can expose a credential through arguments or environment."
fi

printf '%s\n' 'Persistent game-event runtime smoke passed.'
