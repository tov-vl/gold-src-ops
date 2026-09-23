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
cat > "$environment_source" <<'EOF'
GameEventAgent__Spool__Enabled=true
GameEventAgent__Delivery__Enabled=false
EOF

(
    # shellcheck source=/dev/null
    source "$workflow"
    render_producer_configuration "$producer_source" "$producer_enabled" 1
    render_delivery_environment "$environment_source" "$environment_enabled" true
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

activate_body="$(sed -n '/^run_activate() {$/,/^}$/p' "$workflow")"
assert_order "$activate_body" \
    '    require_accepted_boundary' \
    '    capture_baseline' \
    '    arm_transition_rollback' \
    '    "$autostart_workflow" --disable --apply' \
    '    GOLDSRCOPS_INHERITED_ACTIVATION_LOCK_FD=9 "$pilot_activator" \' \
    '    pilot_activation_present=true' \
    '    require_settled_activation_status "$(capture_agent_status)"' \
    '    render_producer_configuration "$producer_configuration" "$staging_directory/amxx.cfg" 1' \
    '    systemctl stop "$AGENT_SERVICE_NAME"' \
    '    systemctl stop "$GAME_SERVICE_NAME"' \
    '    install_persistent_policy' \
    '    verify_persistent_files' \
    '    systemctl start "$GAME_SERVICE_NAME"' \
    '    systemctl start "$AGENT_SERVICE_NAME"' \
    '    systemctl enable "$GAME_SERVICE_NAME" >/dev/null' \
    '    systemctl enable "$AGENT_SERVICE_NAME" >/dev/null'

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
