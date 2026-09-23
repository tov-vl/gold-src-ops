#!/usr/bin/env bash

set -Eeuo pipefail
IFS=$'\n\t'

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
activator="$repo_root/ops/gameserver/game-event-pilot-activate.sh"
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
        fail "Game-event pilot activation case '$name' passed unexpectedly."
    fi
    printf "Game-event pilot activation case '%s' failed as expected.\n" "$name"
}

expect_stdin_failure() {
    local name="$1"
    local input="$2"
    shift 2
    if printf '%s' "$input" | "$@" >"$smoke_directory/$name.out" 2>&1; then
        fail "Game-event pilot activation case '$name' passed unexpectedly."
    fi
    printf "Game-event pilot activation case '%s' failed as expected.\n" "$name"
}

write_identity() {
    local path="$1"
    cat > "$path" <<'EOF'
schema_version=1
server_id=11111111-1111-4111-8111-111111111111
api_base_url=https://api.goldsrcops.com/
token_endpoint=https://tenant.us.auth0.com/oauth/token
client_id=FixtureClientId_1234567890
audience=https://api.goldsrcops.com
permission=ingest:game-events
server_id_claim=https://goldsrcops.com/claims/server_id
EOF
}

write_state() {
    local path="$1"
    local hash="1111111111111111111111111111111111111111111111111111111111111111"
    cat > "$path" <<EOF
schema_version=1
stage=event-sealed
pilot_marker_sha256=$hash
bundle_sha256=$hash
manifest_sha256=$hash
identity_sha256=$hash
liblist_original_sha256=$hash
liblist_active_sha256=$hash
liblist_owner=goldsrc
liblist_group=goldsrc
liblist_mode=640
environment_original_sha256=$hash
environment_current_sha256=$hash
producer_configuration_sha256=$hash
game_restart_count_before=0
game_invocation_before=11111111111111111111111111111111
addons_directory_was_present=false
addons_directory_owner=none
addons_directory_group=none
addons_directory_mode=none
EOF
}

"$BASH" -n "$activator"

(
    # shellcheck source=/dev/null
    source "$activator"
    activation_lock="$smoke_directory/inherited-transition.lock"
    exec 9>"$activation_lock"
    flock --nonblock 9
    GOLDSRCOPS_INHERITED_ACTIVATION_LOCK_FD=9 acquire_lock
) || fail "Pilot activation did not accept the exact inherited transition lock."
# shellcheck disable=SC2016
expect_failure wrong-inherited-lock "$BASH" -c '
    source "$1"
    activation_lock="$2/expected.lock"
    exec 9>"$2/other.lock"
    flock --nonblock 9
    GOLDSRCOPS_INHERITED_ACTIVATION_LOCK_FD=9 acquire_lock
' _ "$activator" "$smoke_directory"

(
    # shellcheck source=/dev/null
    source "$activator"
    [[ "$(stat_mode_from_manifest 0640)" == 640 ]]
    [[ "$(stat_mode_from_manifest 0750)" == 750 ]]
)
# shellcheck disable=SC2016
expect_failure invalid-manifest-mode "$BASH" -c \
    'source "$1"; stat_mode_from_manifest 640' _ "$activator"
# shellcheck disable=SC2016
[[ "$(grep -Fc 'expected_mode="$(stat_mode_from_manifest "$payload_mode")"' "$activator")" == 2 ]] ||
    fail "Pilot payload metadata checks do not normalize manifest modes consistently."

(
    # shellcheck source=/dev/null
    source "$activator"
    fixture_listener_pid="$BASHPID"
    fixture_control_group="$(awk -F: 'NR == 1 { print $3 }' "/proc/$fixture_listener_pid/cgroup")"
    [[ -n "$fixture_control_group" ]] || fixture_control_group=/
    # Consumed by the sourced verify_game_service function.
    # shellcheck disable=SC2034
    prepared_game_port=27015
    # shellcheck disable=SC2034
    live_metamod_root=/fixture/metamod
    # shellcheck disable=SC2034
    live_amxx_root=/fixture/amxmodx

    # Called indirectly by verify_game_service.
    # shellcheck disable=SC2329
    systemctl() {
        case "$1" in
            is-active) printf '%s\n' active ;;
            is-enabled) printf '%s\n' disabled ;;
            show)
                case "$3" in
                    --property=NRestarts) printf '%s\n' 0 ;;
                    --property=ControlGroup) printf '%s\n' "$fixture_control_group" ;;
                    --property=MainPID) printf '%s\n' 1 ;;
                    *) return 1 ;;
                esac
                ;;
            *) return 1 ;;
        esac
    }
    # Called indirectly by verify_game_service.
    # shellcheck disable=SC2329
    ss() {
        printf 'fixture-listener pid=%s\n' "$fixture_listener_pid"
    }
    # Called indirectly by verify_game_service.
    # shellcheck disable=SC2329
    grep() {
        local final_argument="${!#}"
        if [[ "$final_argument" == "/proc/$fixture_listener_pid/maps" ]]; then
            return 0
        fi
        command grep "$@"
    }

    verify_game_service true
) || fail "Plugin verification did not inspect the UDP listener process map."

overview_output="$smoke_directory/overview.out"
"$BASH" "$activator" > "$overview_output"
for expected in \
    'strict server-bound machine identity and stdin-only credential' \
    'preserve exact liblist, agent environment, game invocation, restart count, and addons-directory metadata' \
    'spool import on while producer and delivery remain off' \
    'enable one producer boundary, seal exactly one local event, and only then enable HTTP delivery' \
    'restore the dormant pilot on any failed transition' \
    'external A2S, zero-bot, API receipt/idempotency, and Reader checks' \
    'PLAN_ONLY: no host state, stdin, identity file, service, or network endpoint was touched'; do
    grep -Fq "$expected" "$overview_output" ||
        fail "Pilot activation overview is missing '$expected'."
done

declare -A operation_expectations=(
    [activate]='install the exact overlay and Metamod loader, then start spool-only'
    [enable-producer]='enable the producer for one controlled anonymous round boundary'
    [seal-event]='require exactly one pending local event'
    [enable-delivery]='zero pending, in-flight, dead-letter, receipt, and spool counts'
    [rollback]='restore exact pre-pilot liblist and agent environment'
)
for operation in activate enable-producer seal-event enable-delivery rollback; do
    option="--$operation"
    output="$smoke_directory/$operation-plan.out"
    "$BASH" "$activator" "$option" > "$output"
    grep -Fq "${operation_expectations[$operation]}" "$output" ||
        fail "Pilot activation plan for '$operation' is incomplete."
    grep -Fq 'on any failed mutating transition, execute the same active rollback' "$output" ||
        fail "Pilot activation plan for '$operation' omits rollback."
    grep -Fq 'PLAN_ONLY: no host state, stdin, identity file, service, or network endpoint was touched' "$output" ||
        fail "Pilot activation operation '$operation' did not remain plan-only."
done

if grep -Eiq '203[.]0[.]113[.]|198[.]51[.]100[.]|client[_-]?secret=|bearer[[:space:]]+[A-Za-z0-9]' \
    "$overview_output" "$smoke_directory"/*-plan.out; then
    fail "Pilot activation plans exposed host-specific or secret-shaped content."
fi

missing_identity_plan="$smoke_directory/missing-identity-plan.out"
"$BASH" "$activator" --activate --identity-file /does/not/exist > "$missing_identity_plan"
grep -Fq 'PLAN_ONLY:' "$missing_identity_plan" ||
    fail "Plan mode unexpectedly tried to read the identity file."

expect_failure multiple-operations "$BASH" "$activator" --activate --rollback
expect_failure identity-on-transition "$BASH" "$activator" --enable-producer --identity-file /tmp/identity
expect_failure secret-without-apply "$BASH" "$activator" --activate --client-secret-stdin
expect_failure apply-without-operation "$BASH" "$activator" --apply
expect_failure activate-apply-without-identity "$BASH" "$activator" --activate --apply --client-secret-stdin
expect_failure activate-apply-without-secret "$BASH" "$activator" --activate --apply --identity-file /tmp/identity
expect_failure unknown-option "$BASH" "$activator" --force

identity_fixture="$smoke_directory/identity"
write_identity "$identity_fixture"
(
    # shellcheck source=/dev/null
    source "$activator"
    read_identity_file "$identity_fixture"
    # Assigned by the sourced identity parser.
    # shellcheck disable=SC2154
    [[ "$identity_server_id" == 11111111-1111-4111-8111-111111111111 ]]
    # shellcheck disable=SC2154
    [[ "$identity_api_base_url" == https://api.goldsrcops.com/ ]]
    # shellcheck disable=SC2154
    [[ "$identity_token_endpoint" == https://tenant.us.auth0.com/oauth/token ]]
    # shellcheck disable=SC2154
    [[ "$identity_client_id" == FixtureClientId_1234567890 ]]
    # shellcheck disable=SC2154
    [[ "$identity_audience" == https://api.goldsrcops.com ]]
    # shellcheck disable=SC2154
    [[ "$identity_permission" == ingest:game-events ]]
    # shellcheck disable=SC2154
    [[ "$identity_server_id_claim" == https://goldsrcops.com/claims/server_id ]]
)

duplicate_identity="$smoke_directory/identity-duplicate"
cp "$identity_fixture" "$duplicate_identity"
printf 'permission=ingest:game-events\n' >> "$duplicate_identity"
# shellcheck disable=SC2016
expect_failure duplicate-identity "$BASH" -c 'source "$1"; read_identity_file "$2"' \
    _ "$activator" "$duplicate_identity"

unknown_identity="$smoke_directory/identity-unknown"
cp "$identity_fixture" "$unknown_identity"
printf 'provider_id=forbidden\n' >> "$unknown_identity"
# shellcheck disable=SC2016
expect_failure unknown-identity "$BASH" -c 'source "$1"; read_identity_file "$2"' \
    _ "$activator" "$unknown_identity"

wrong_audience="$smoke_directory/identity-wrong-audience"
sed 's#^audience=.*#audience=https://other.example/#' "$identity_fixture" > "$wrong_audience"
# shellcheck disable=SC2016
expect_failure wrong-audience "$BASH" -c 'source "$1"; read_identity_file "$2"' \
    _ "$activator" "$wrong_audience"

wrong_permission="$smoke_directory/identity-wrong-permission"
sed 's/^permission=.*/permission=read:servers ingest:game-events/' \
    "$identity_fixture" > "$wrong_permission"
# shellcheck disable=SC2016
expect_failure broader-permission "$BASH" -c 'source "$1"; read_identity_file "$2"' \
    _ "$activator" "$wrong_permission"

wrong_binding="$smoke_directory/identity-wrong-binding"
sed 's#^server_id_claim=.*#server_id_claim=https://example.test/server_id#' \
    "$identity_fixture" > "$wrong_binding"
# shellcheck disable=SC2016
expect_failure wrong-binding "$BASH" -c 'source "$1"; read_identity_file "$2"' \
    _ "$activator" "$wrong_binding"

insecure_token_endpoint="$smoke_directory/identity-insecure-token"
sed 's#^token_endpoint=https:#token_endpoint=http:#' \
    "$identity_fixture" > "$insecure_token_endpoint"
# shellcheck disable=SC2016
expect_failure insecure-token-endpoint "$BASH" -c 'source "$1"; read_identity_file "$2"' \
    _ "$activator" "$insecure_token_endpoint"

invalid_server_id="$smoke_directory/identity-invalid-server"
sed 's/^server_id=.*/server_id=00000000-0000-0000-0000-000000000000/' \
    "$identity_fixture" > "$invalid_server_id"
# shellcheck disable=SC2016
expect_failure invalid-server-id "$BASH" -c 'source "$1"; read_identity_file "$2"' \
    _ "$activator" "$invalid_server_id"

valid_secret='FixtureSecret_12345678901234567890'
# shellcheck disable=SC2016
printf '%s' "$valid_secret" | "$BASH" -c '
    source "$1"
    read_secret_from_stdin=true
    read_client_secret
    [[ "$client_secret" == FixtureSecret_12345678901234567890 ]]
' _ "$activator"
oversized_secret="$(printf '%257s' '' | tr ' ' a)"
# shellcheck disable=SC2016
expect_stdin_failure oversized-secret "$oversized_secret" "$BASH" -c '
    source "$1"
    read_secret_from_stdin=true
    read_client_secret
' _ "$activator"

liblist_fixture="$smoke_directory/liblist.gam"
cat > "$liblist_fixture" <<'EOF'
game "Counter-Strike"
gamedll "dlls/mp.dll"
gamedll_linux "dlls/cs.so"
url_info "www.counter-strike.net"
EOF
active_liblist="$smoke_directory/liblist-active.gam"
(
    # shellcheck source=/dev/null
    source "$activator"
    render_liblist "$liblist_fixture" "$active_liblist"
)
grep -Fxq 'gamedll_linux "addons/metamod/metamod_i386.so"' "$active_liblist" ||
    fail "The activator did not render the Metamod-R loader."
! grep -Eq 'gamedll_linux[[:space:]]+"?dlls/cs[.]so' "$active_liblist" ||
    fail "The activator retained the direct ReGameDLL_CS loader."
[[ "$(wc -l < "$liblist_fixture")" == "$(wc -l < "$active_liblist")" ]] ||
    fail "The activator changed unrelated liblist structure."

duplicate_liblist="$smoke_directory/liblist-duplicate.gam"
cp "$liblist_fixture" "$duplicate_liblist"
printf 'gamedll_linux "dlls/cs.so"\n' >> "$duplicate_liblist"
# shellcheck disable=SC2016
expect_failure duplicate-loader "$BASH" -c \
    'source "$1"; render_liblist "$2" "$3"' \
    _ "$activator" "$duplicate_liblist" "$smoke_directory/liblist-invalid.out"

producer_fixture="$smoke_directory/amxx.cfg"
cat > "$producer_fixture" <<'EOF'
// Fixed pilot configuration.
goldsrcops_events_enabled 0
goldsrcops_spool_incoming "addons/amxmodx/data/goldsrcops-spool/incoming"
goldsrcops_spool_max_pending 1000
EOF
producer_enabled="$smoke_directory/amxx-enabled.cfg"
producer_disabled="$smoke_directory/amxx-disabled.cfg"
(
    # shellcheck source=/dev/null
    source "$activator"
    render_producer_configuration "$producer_fixture" "$producer_enabled" 1
    render_producer_configuration "$producer_enabled" "$producer_disabled" 0
)
grep -Fxq 'goldsrcops_events_enabled 1' "$producer_enabled" ||
    fail "The producer enable transition was not rendered."
grep -Fxq 'goldsrcops_spool_max_pending 1000' "$producer_enabled" ||
    fail "The producer spool limit was not preserved."
cmp -s "$producer_fixture" "$producer_disabled" ||
    fail "The producer disable transition did not restore the reviewed bytes."

environment_spool="$smoke_directory/agent-spool.env"
environment_delivery="$smoke_directory/agent-delivery.env"
(
    # shellcheck source=/dev/null
    source "$activator"
    read_identity_file "$identity_fixture"
    render_agent_environment "$environment_spool" false
    render_delivery_gate "$environment_spool" "$environment_delivery" true
)
for expected in \
    'GameEventAgent__Spool__Enabled=true' \
    'GameEventAgent__Delivery__Enabled=false' \
    'GameEventAgent__Delivery__ApiBaseUrl=https://api.goldsrcops.com/' \
    'GameEventAgent__Delivery__OAuth__Audience=https://api.goldsrcops.com' \
    'GameEventAgent__Delivery__OAuth__Scope=ingest:game-events'; do
    grep -Fxq "$expected" "$environment_spool" ||
        fail "Spool-only environment is missing '$expected'."
done
grep -Fxq 'GameEventAgent__Delivery__Enabled=true' "$environment_delivery" ||
    fail "Delivery environment did not cross the explicit gate."
[[ "$(diff -U 0 "$environment_spool" "$environment_delivery" | grep -Ec '^[+-]GameEventAgent__Delivery__Enabled=')" == "2" ]] ||
    fail "Delivery activation changed more than its Boolean gate."
if grep -Eiq 'clientsecret|password|bearer|access[_-]?token' \
    "$environment_spool" "$environment_delivery"; then
    fail "Rendered agent configuration contains secret material or a secret path."
fi

if command -v jq >/dev/null 2>&1; then
    overlay_manifest="$smoke_directory/overlay-manifest.json"
    jq -n '{payload: [
        {path: "gameserver/cstrike/addons/amxmodx/configs/amxx.cfg"},
        {path: "gameserver/cstrike/addons/amxmodx/configs/core.ini"},
        {path: "gameserver/cstrike/addons/amxmodx/configs/modules.ini"},
        {path: "gameserver/cstrike/addons/amxmodx/configs/plugins.ini"},
        {path: "gameserver/cstrike/addons/amxmodx/data/csstats.amxx"},
        {path: "gameserver/cstrike/addons/amxmodx/dlls/amxmodx_mm_i386.so"},
        {path: "gameserver/cstrike/addons/amxmodx/modules/reapi_amxx_i386.so"},
        {path: "gameserver/cstrike/addons/amxmodx/plugins/goldsrcops_game_events.amxx"},
        {path: "gameserver/cstrike/addons/metamod/metamod_i386.so"},
        {path: "gameserver/cstrike/addons/metamod/plugins.ini"}
    ]}' > "$overlay_manifest"
    (
        # shellcheck source=/dev/null
        source "$activator"
        validate_overlay_manifest_scope "$overlay_manifest"
    )

    escaped_overlay="$smoke_directory/overlay-manifest-escaped.json"
    jq '.payload += [{path: "gameserver/cstrike/dlls/foreign.so"}]' \
        "$overlay_manifest" > "$escaped_overlay"
    # shellcheck disable=SC2016
    expect_failure escaped-overlay "$BASH" -c \
        'source "$1"; validate_overlay_manifest_scope "$2"' \
        _ "$activator" "$escaped_overlay"

    extra_plugin="$smoke_directory/overlay-manifest-extra-plugin.json"
    jq '.payload += [{path: "gameserver/cstrike/addons/amxmodx/plugins/foreign.amxx"}]' \
        "$overlay_manifest" > "$extra_plugin"
    # shellcheck disable=SC2016
    expect_failure extra-plugin "$BASH" -c \
        'source "$1"; validate_overlay_manifest_scope "$2"' \
        _ "$activator" "$extra_plugin"

    extra_data_amxx="$smoke_directory/overlay-manifest-extra-data-amxx.json"
    jq '.payload += [{path: "gameserver/cstrike/addons/amxmodx/data/foreign.amxx"}]' \
        "$overlay_manifest" > "$extra_data_amxx"
    # shellcheck disable=SC2016
    expect_failure extra-data-amxx "$BASH" -c \
        'source "$1"; validate_overlay_manifest_scope "$2"' \
        _ "$activator" "$extra_data_amxx"

    base64_url() {
        base64 | tr '/+' '_-' | tr -d '=\r\n'
    }

    token_header="$(printf '%s' '{"alg":"RS256","typ":"JWT"}' | base64_url)"
    token_payload="$(printf '%s' '{"iss":"https://tenant.us.auth0.com/","aud":"https://api.goldsrcops.com","sub":"FixtureClientId_1234567890@clients","exp":4102444800,"permissions":["ingest:game-events"],"https://goldsrcops.com/claims/server_id":"11111111-1111-4111-8111-111111111111"}' | base64_url)"
    token_signature="$(printf '%s' 'fixture-signature' | base64_url)"
    token_response="$smoke_directory/token-response.json"
    printf '{"access_token":"%s.%s.%s","token_type":"Bearer"}\n' \
        "$token_header" "$token_payload" "$token_signature" > "$token_response"
    secret_fixture="$smoke_directory/client-secret"
    printf '%s' 'FixtureSecret_12345678901234567890' > "$secret_fixture"

    (
        # shellcheck source=/dev/null
        source "$activator"
        read_identity_file "$identity_fixture"
        # Called indirectly by verify_issued_token_contract.
        # shellcheck disable=SC2329
        curl() {
            cat "$token_response"
        }
        verify_issued_token_contract "$secret_fixture"
    )

    broad_payload="$(printf '%s' '{"iss":"https://tenant.us.auth0.com/","aud":"https://api.goldsrcops.com","sub":"FixtureClientId_1234567890@clients","exp":4102444800,"permissions":["ingest:game-events","read:servers"],"https://goldsrcops.com/claims/server_id":"11111111-1111-4111-8111-111111111111"}' | base64_url)"
    broad_response="$smoke_directory/token-response-broad.json"
    printf '{"access_token":"%s.%s.%s","token_type":"Bearer"}\n' \
        "$token_header" "$broad_payload" "$token_signature" > "$broad_response"
    # shellcheck disable=SC2016
    expect_failure broader-issued-token "$BASH" -c '
        source "$1"
        read_identity_file "$2"
        curl() { cat "$3"; }
        verify_issued_token_contract "$4"
    ' _ "$activator" "$identity_fixture" "$broad_response" "$secret_fixture"
else
    printf 'jq is unavailable; issued-token contract cases are deferred to CI.\n'
fi

state_fixture="$smoke_directory/state"
gate_fixture="$smoke_directory/gate"
write_state "$state_fixture"
state_hash="$(sha256sum "$state_fixture" | awk '{ print $1 }')"
cat > "$gate_fixture" <<EOF
schema_version=1
stage=event-sealed
activation_state_sha256=$state_hash
EOF
(
    # shellcheck source=/dev/null
    source "$activator"
    # Consumed by the sourced activation-state and gate parsers.
    # shellcheck disable=SC2034
    activation_state_file="$state_fixture"
    # shellcheck disable=SC2034
    pilot_enabled_marker="$gate_fixture"
    read_activation_state
    # Assigned by the sourced activation-state parser.
    # shellcheck disable=SC2154
    [[ "$state_stage" == event-sealed ]]
    # shellcheck disable=SC2154
    [[ "$state_liblist_owner" == goldsrc ]]
    # shellcheck disable=SC2154
    [[ "$state_addons_directory_was_present" == false ]]
    read_activation_gate
)

tampered_gate="$smoke_directory/gate-tampered"
cp "$gate_fixture" "$tampered_gate"
sed -i 's/stage=event-sealed/stage=delivery/' "$tampered_gate"
# shellcheck disable=SC2016
expect_failure mismatched-gate "$BASH" -c '
    source "$1"
    activation_state_file="$2"
    pilot_enabled_marker="$3"
    read_activation_state
    read_activation_gate
' _ "$activator" "$state_fixture" "$tampered_gate"

run_activate_body="$(sed -n '/^run_activate() {$/,/^}$/p' "$activator")"
previous_line=0
# Expected source lines intentionally remain literal.
# shellcheck disable=SC2016
for expected_call in \
    '    require_dormant_activation_boundary' \
    '    validate_identity_file_metadata' \
    '    read_identity_file' \
    '    read_client_secret' \
    '    prepare_staging' \
    '    capture_activation_baseline' \
    '    verify_issued_token_contract "$staging_directory/client-secret"' \
    '    verify_activation_baseline_unchanged' \
    '    arm_transition_rollback' \
    "    write_activation_state \\" \
    '    systemctl stop "$GAME_SERVICE_NAME"' \
    '    install_live_overlay' \
    '    systemctl start "$GAME_SERVICE_NAME"' \
    '    systemctl start "$AGENT_SERVICE_NAME"' \
    '    require_empty_agent_state' \
    '    load_and_validate_active_state spool-only'; do
    current_line="$(grep -nFx "$expected_call" <<< "$run_activate_body" | cut -d: -f1)"
    [[ -n "$current_line" && "$current_line" -gt "$previous_line" ]] ||
        fail "Pilot base activation order is invalid at '$expected_call'."
    previous_line="$current_line"
done

for transition in run_enable_producer run_seal_event run_enable_delivery; do
    body="$(sed -n "/^$transition() {$/,/^}$/p" "$activator")"
    grep -Fq '    arm_transition_rollback' <<< "$body" ||
        fail "$transition does not arm active rollback."
    grep -Fq '    disarm_transition_rollback' <<< "$body" ||
        fail "$transition does not close active rollback after verification."
done

rollback_body="$(sed -n '/^restore_dormant_state() {$/,/^}$/p' "$activator")"
explicit_rollback_body="$(sed -n '/^run_explicit_rollback() {$/,/^}$/p' "$activator")"
backup_verification_body="$(sed -n '/^verify_activation_backups() {$/,/^}$/p' "$activator")"
# Expected source fragments intentionally remain literal.
# shellcheck disable=SC2016
for expected in \
    'systemctl stop "$AGENT_SERVICE_NAME"' \
    'systemctl stop "$GAME_SERVICE_NAME"' \
    'safe_remove_tree "$live_metamod_root"' \
    'safe_remove_tree "$live_amxx_root"' \
    '"$activation_backup_root/liblist.gam"' \
    '"$activation_backup_root/game-event-agent.env"' \
    'systemctl start "$GAME_SERVICE_NAME"' \
    'wait_for_game_service false' \
    'safe_remove_tree "$activation_root"'; do
    grep -Fq "$expected" <<< "$rollback_body" ||
        fail "Active rollback is missing '$expected'."
done

# Expected source fragments intentionally remain literal.
# shellcheck disable=SC2016
grep -Fq 'validate_file_metadata "$pilot_enabled_marker"' <<< "$explicit_rollback_body" ||
    fail "Explicit rollback does not validate an existing activation gate."
! grep -Fq 'read_activation_gate' <<< "$explicit_rollback_body" ||
    fail "Explicit rollback can be blocked by a stale gate after an interrupted atomic transition."
grep -Fq 'verify_activation_backups' <<< "$explicit_rollback_body" ||
    fail "Explicit rollback does not verify its protected recovery files."
# shellcheck disable=SC2016
grep -Fq '"$activation_backup_root/machine-identity"' <<< "$backup_verification_body" ||
    fail "Explicit rollback does not verify the staged machine identity."

grep -Fq "trap 'rollback_on_failure 129' HUP" "$activator" ||
    fail "Pilot activation does not rollback after a lost SSH session."
if grep -Eq 'systemctl[[:space:]]+enable' "$activator"; then
    fail "Pilot activation must never enable a service across boot."
fi
if grep -Eq -- '--(client-)?secret([[:space:]]|=)' "$activator"; then
    fail "Pilot activation accepts an OAuth secret through an unsafe argument."
fi
if grep -Eq 'set[[:space:]]+-[^[:space:]]*x|Environment=.*(secret|password|token)=' "$activator"; then
    fail "Pilot activation contains unsafe tracing or inline secret transport."
fi
if grep -Eq '\b(wget|ssh|scp|rcon)\b' "$activator"; then
    fail "Pilot activation unexpectedly performs a remote shell, download, or RCON operation."
fi
grep -Fq -- "--data-urlencode \"client_secret@\$secret_path\"" "$activator" ||
    fail "Pilot activation does not read the OAuth secret from its owner-only file."
! grep -Eq -- '--data[^\n]*client_secret=' "$activator" ||
    fail "Pilot activation can place the OAuth secret itself in a process argument."

printf 'Game-server game-event pilot activation smoke test passed.\n'
