#!/usr/bin/env bash

set -Eeuo pipefail
IFS=$'\n\t'
umask 077

readonly ACTIVATION_SCHEMA_VERSION="1"
readonly PILOT_INSTALLER_SCHEMA_VERSION="1"
readonly PILOT_BUNDLE_SCHEMA_VERSION="1"
readonly AGENT_SERVICE_NAME="goldsrcops-game-event-agent.service"
readonly GAME_SERVICE_NAME="goldsrcops-gameserver.service"
readonly EXPECTED_API_BASE_URL="https://api.goldsrcops.com/"
readonly EXPECTED_API_AUDIENCE="https://api.goldsrcops.com"
readonly EXPECTED_PERMISSION="ingest:game-events"
readonly EXPECTED_SERVER_ID_CLAIM="https://goldsrcops.com/claims/server_id"

configuration_directory="${GOLDSRCOPS_CONFIGURATION_DIRECTORY:-/etc/goldsrcops/gameserver}"
installation_directory="${GOLDSRCOPS_INSTALLATION_DIRECTORY:-/opt/goldsrcops/gameserver}"
service_home="${GOLDSRCOPS_SERVICE_HOME:-/var/lib/goldsrc}"
agent_unit_file="${GOLDSRCOPS_GAME_EVENT_SYSTEMD_UNIT_FILE:-/etc/systemd/system/$AGENT_SERVICE_NAME}"
prepared_marker="$configuration_directory/host-prepared"
runtime_marker="$configuration_directory/runtime-installed"
runtime_enabled_marker="$configuration_directory/runtime-enabled"
pilot_marker="$configuration_directory/game-event-pilot-installed"
pilot_enabled_marker="$configuration_directory/game-event-pilot-enabled"
environment_file="$configuration_directory/game-event-agent.env"
client_secret_file="$configuration_directory/secrets/game-event-agent-client-secret"
pilot_root="$installation_directory/game-event-pilot"
pilot_releases_directory="$pilot_root/releases"
activation_root="$pilot_root/activation"
activation_backup_root="$activation_root/backup"
activation_state_file="$activation_root/state"
activation_lock="$configuration_directory/game-event-pilot-install.lock"
state_root="$service_home/game-event-agent"
game_root="$service_home/server"
live_addons_root="$game_root/cstrike/addons"
live_metamod_root="$live_addons_root/metamod"
live_amxx_root="$live_addons_root/amxmodx"
spool_root="$live_amxx_root/data/goldsrcops-spool"
liblist_file="$game_root/cstrike/liblist.gam"
producer_configuration="$live_amxx_root/configs/amxx.cfg"

operation="overview"
operation_selected=false
apply_changes=false
read_secret_from_stdin=false
identity_file=""
service_user="goldsrc"
service_group=""
prepared_operator_user=""
prepared_service_user=""
prepared_game_port=""
pilot_bundle_version=""
pilot_source_revision=""
pilot_bundle_sha256=""
pilot_manifest_sha256=""
pilot_environment_sha256=""
pilot_unit_sha256=""
release_path=""
manifest_file=""
identity_server_id=""
identity_api_base_url=""
identity_token_endpoint=""
identity_client_id=""
identity_audience=""
identity_permission=""
identity_server_id_claim=""
client_secret=""
staging_directory=""
activation_started=false

state_stage=""
state_pilot_marker_sha256=""
state_bundle_sha256=""
state_manifest_sha256=""
state_identity_sha256=""
state_liblist_original_sha256=""
state_liblist_active_sha256=""
state_liblist_owner=""
state_liblist_group=""
state_liblist_mode=""
state_environment_original_sha256=""
state_environment_current_sha256=""
state_producer_configuration_sha256=""
state_game_restart_count_before=""
state_game_invocation_before=""
state_addons_directory_was_present=""
state_addons_directory_owner=""
state_addons_directory_group=""
state_addons_directory_mode=""

agent_pending=0
agent_in_flight=0
agent_dead_letter=0
agent_spool_receipts=0
spool_ready=0
spool_processing=0
spool_accepted=0
spool_rejected=0
spool_temporary=0

usage() {
    cat <<'EOF'
Usage:
  game-event-pilot-activate.sh
  game-event-pilot-activate.sh --activate [--identity-file <absolute-path>]
  game-event-pilot-activate.sh --enable-producer
  game-event-pilot-activate.sh --seal-event
  game-event-pilot-activate.sh --enable-delivery
  game-event-pilot-activate.sh --rollback

Every operation is plan-only unless --apply is supplied. Base activation also
requires a root-owned 0600 identity file and a client secret through redirected
stdin:

  <secret-producer> | sudo --preserve-env=SSH_CONNECTION \
    bash ./game-event-pilot-activate.sh \
      --activate \
      --identity-file /root/game-event-agent.identity \
      --client-secret-stdin \
      --apply

The workflow advances through spool-only, one-event capture, sealed-event, and
delivery stages. It never enables either systemd unit. Any failed mutating
transition restores the dormant pilot, exact pre-pilot liblist and environment,
and the previous active/disabled game-service state.
EOF
}

fail() {
    printf 'ERROR: %s\n' "$*" >&2
    return 1
}

log() {
    printf '%s\n' "$*"
}

require_command() {
    command -v "$1" >/dev/null 2>&1 || fail "Required command '$1' is unavailable."
}

validate_user_name() {
    local value="$1"
    [[ "$value" =~ ^[a-z_][a-z0-9_-]{0,31}$ ]] ||
        fail "The service account name is invalid."
    [[ "$value" != "root" ]] || fail "The service account must not be root."
}

validate_sha256() {
    local name="$1"
    local value="$2"
    [[ "$value" =~ ^[0-9a-f]{64}$ ]] || fail "$name must be a lowercase SHA-256."
}

validate_uuid() {
    local value="$1"
    [[ "$value" =~ ^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$ ]] ||
        fail "The identity server_id must be a canonical lowercase UUID."
    [[ "$value" != "00000000-0000-0000-0000-000000000000" ]] ||
        fail "The identity server_id must not be empty."
}

select_operation() {
    local selected="$1"
    [[ "$operation_selected" == "false" ]] || fail "Select exactly one operation."
    operation="$selected"
    operation_selected=true
}

validate_cli() {
    if [[ -n "$identity_file" ]]; then
        [[ "$operation" == "activate" ]] ||
            fail "--identity-file is valid only with --activate."
        [[ "$identity_file" == /* ]] || fail "--identity-file must be absolute."
        [[ "$identity_file" != *$'\n'* && "$identity_file" != *$'\r'* ]] ||
            fail "--identity-file contains an unsupported character."
    fi

    if [[ "$read_secret_from_stdin" == "true" ]]; then
        [[ "$operation" == "activate" && "$apply_changes" == "true" ]] ||
            fail "--client-secret-stdin is valid only with --activate --apply."
    fi

    if [[ "$apply_changes" == "true" ]]; then
        [[ "$operation" != "overview" ]] || fail "--apply requires one operation."
        if [[ "$operation" == "activate" ]]; then
            [[ -n "$identity_file" ]] || fail "Activation apply requires --identity-file."
            [[ "$read_secret_from_stdin" == "true" ]] ||
                fail "Activation apply requires --client-secret-stdin."
        fi
    fi
}

read_prepared_marker() {
    local marker_path="${1:-$prepared_marker}"
    local key value schema_version="" prepared_ssh_port=""
    declare -A seen=()

    [[ -f "$marker_path" && ! -L "$marker_path" ]] ||
        fail "The reviewed game-host readiness marker is missing or unsafe."
    prepared_operator_user=""
    prepared_service_user=""
    prepared_game_port=""

    while IFS='=' read -r key value || [[ -n "${key:-}${value:-}" ]]; do
        [[ -n "${key:-}" ]] || fail "The game-host readiness marker contains an empty key."
        [[ -z "${seen[$key]+x}" ]] ||
            fail "The game-host readiness marker contains a duplicate key."
        seen[$key]=1
        case "$key" in
            schema_version) schema_version="$value" ;;
            operator_user) prepared_operator_user="$value" ;;
            service_user) prepared_service_user="$value" ;;
            ssh_port) prepared_ssh_port="$value" ;;
            game_port) prepared_game_port="$value" ;;
            *) fail "The game-host readiness marker contains an unknown key." ;;
        esac
    done < "$marker_path"

    [[ "$schema_version" == "1" && "${#seen[@]}" -eq 5 ]] ||
        fail "The game-host readiness marker is incomplete or unsupported."
    validate_user_name "$prepared_operator_user"
    validate_user_name "$prepared_service_user"
    [[ "$prepared_ssh_port" =~ ^[0-9]+$ && "$prepared_game_port" =~ ^[0-9]+$ ]] ||
        fail "The game-host readiness marker contains an invalid port."
    ((10#$prepared_game_port >= 1 && 10#$prepared_game_port <= 65535)) ||
        fail "The configured game port is invalid."
}

read_pilot_marker() {
    local marker_path="${1:-$pilot_marker}"
    local key value schema_version=""
    declare -A seen=()

    [[ -f "$marker_path" && ! -L "$marker_path" ]] ||
        fail "The dormant pilot installation marker is missing or unsafe."
    pilot_bundle_version=""
    pilot_source_revision=""
    pilot_bundle_sha256=""
    pilot_manifest_sha256=""
    pilot_environment_sha256=""
    pilot_unit_sha256=""

    while IFS='=' read -r key value || [[ -n "${key:-}${value:-}" ]]; do
        [[ -n "${key:-}" ]] || fail "The pilot marker contains an empty key."
        [[ -z "${seen[$key]+x}" ]] || fail "The pilot marker contains a duplicate key."
        seen[$key]=1
        case "$key" in
            schema_version) schema_version="$value" ;;
            bundle_version) pilot_bundle_version="$value" ;;
            source_revision) pilot_source_revision="$value" ;;
            bundle_sha256) pilot_bundle_sha256="$value" ;;
            manifest_sha256) pilot_manifest_sha256="$value" ;;
            environment_file_sha256) pilot_environment_sha256="$value" ;;
            service_unit_sha256) pilot_unit_sha256="$value" ;;
            *) fail "The pilot marker contains an unknown key." ;;
        esac
    done < "$marker_path"

    [[ "$schema_version" == "$PILOT_INSTALLER_SCHEMA_VERSION" && "${#seen[@]}" -eq 7 ]] ||
        fail "The pilot marker is incomplete or unsupported."
    [[ "$pilot_bundle_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z]+(\.[0-9A-Za-z]+)*)?$ ]] ||
        fail "The pilot marker bundle version is invalid."
    [[ "$pilot_source_revision" =~ ^[0-9a-f]{40}$ ]] ||
        fail "The pilot marker source revision is invalid."
    validate_sha256 "pilot bundle hash" "$pilot_bundle_sha256"
    validate_sha256 "pilot manifest hash" "$pilot_manifest_sha256"
    validate_sha256 "pilot environment hash" "$pilot_environment_sha256"
    validate_sha256 "pilot unit hash" "$pilot_unit_sha256"
}

read_identity_file() {
    local path="${1:-$identity_file}"
    local key value schema_version=""
    declare -A seen=()

    [[ -f "$path" && ! -L "$path" ]] || fail "The machine identity file is missing or unsafe."
    identity_server_id=""
    identity_api_base_url=""
    identity_token_endpoint=""
    identity_client_id=""
    identity_audience=""
    identity_permission=""
    identity_server_id_claim=""

    while IFS='=' read -r key value || [[ -n "${key:-}${value:-}" ]]; do
        [[ -n "${key:-}" ]] || fail "The machine identity file contains an empty key."
        [[ -z "${seen[$key]+x}" ]] || fail "The machine identity file contains a duplicate key."
        seen[$key]=1
        case "$key" in
            schema_version) schema_version="$value" ;;
            server_id) identity_server_id="$value" ;;
            api_base_url) identity_api_base_url="$value" ;;
            token_endpoint) identity_token_endpoint="$value" ;;
            client_id) identity_client_id="$value" ;;
            audience) identity_audience="$value" ;;
            permission) identity_permission="$value" ;;
            server_id_claim) identity_server_id_claim="$value" ;;
            *) fail "The machine identity file contains an unknown key." ;;
        esac
    done < "$path"

    [[ "$schema_version" == "1" && "${#seen[@]}" -eq 8 ]] ||
        fail "The machine identity file is incomplete or unsupported."
    validate_identity_values
}

read_activation_state() {
    local path="${1:-$activation_state_file}"
    local key value schema_version=""
    declare -A seen=()

    [[ -f "$path" && ! -L "$path" ]] || fail "The active-pilot state is missing or unsafe."

    state_stage=""
    state_pilot_marker_sha256=""
    state_bundle_sha256=""
    state_manifest_sha256=""
    state_identity_sha256=""
    state_liblist_original_sha256=""
    state_liblist_active_sha256=""
    state_liblist_owner=""
    state_liblist_group=""
    state_liblist_mode=""
    state_environment_original_sha256=""
    state_environment_current_sha256=""
    state_producer_configuration_sha256=""
    state_game_restart_count_before=""
    state_game_invocation_before=""
    state_addons_directory_was_present=""
    state_addons_directory_owner=""
    state_addons_directory_group=""
    state_addons_directory_mode=""

    while IFS='=' read -r key value || [[ -n "${key:-}${value:-}" ]]; do
        [[ -n "${key:-}" ]] || fail "The active-pilot state contains an empty key."
        [[ -z "${seen[$key]+x}" ]] || fail "The active-pilot state contains a duplicate key."
        seen[$key]=1
        case "$key" in
            schema_version) schema_version="$value" ;;
            stage) state_stage="$value" ;;
            pilot_marker_sha256) state_pilot_marker_sha256="$value" ;;
            bundle_sha256) state_bundle_sha256="$value" ;;
            manifest_sha256) state_manifest_sha256="$value" ;;
            identity_sha256) state_identity_sha256="$value" ;;
            liblist_original_sha256) state_liblist_original_sha256="$value" ;;
            liblist_active_sha256) state_liblist_active_sha256="$value" ;;
            liblist_owner) state_liblist_owner="$value" ;;
            liblist_group) state_liblist_group="$value" ;;
            liblist_mode) state_liblist_mode="$value" ;;
            environment_original_sha256) state_environment_original_sha256="$value" ;;
            environment_current_sha256) state_environment_current_sha256="$value" ;;
            producer_configuration_sha256) state_producer_configuration_sha256="$value" ;;
            game_restart_count_before) state_game_restart_count_before="$value" ;;
            game_invocation_before) state_game_invocation_before="$value" ;;
            addons_directory_was_present) state_addons_directory_was_present="$value" ;;
            addons_directory_owner) state_addons_directory_owner="$value" ;;
            addons_directory_group) state_addons_directory_group="$value" ;;
            addons_directory_mode) state_addons_directory_mode="$value" ;;
            *) fail "The active-pilot state contains an unknown key." ;;
        esac
    done < "$path"

    [[ "$schema_version" == "$ACTIVATION_SCHEMA_VERSION" && "${#seen[@]}" -eq 20 ]] ||
        fail "The active-pilot state is incomplete or unsupported."
    case "$state_stage" in
        activating|spool-only|capture|event-sealed|delivery|delivered) ;;
        *) fail "The active-pilot stage is invalid." ;;
    esac
    local hash_value
    for hash_value in \
        "$state_pilot_marker_sha256" \
        "$state_bundle_sha256" \
        "$state_manifest_sha256" \
        "$state_identity_sha256" \
        "$state_liblist_original_sha256" \
        "$state_liblist_active_sha256" \
        "$state_environment_original_sha256" \
        "$state_environment_current_sha256" \
        "$state_producer_configuration_sha256"; do
        validate_sha256 "active-pilot state hash" "$hash_value"
    done
    [[ "$state_game_restart_count_before" =~ ^[0-9]+$ ]] ||
        fail "The active-pilot restart baseline is invalid."
    [[ "$state_game_invocation_before" =~ ^[0-9a-f]{32}$ ]] ||
        fail "The active-pilot invocation baseline is invalid."
    [[ "$state_liblist_owner" =~ ^[a-z_][a-z0-9_-]{0,31}$ &&
        "$state_liblist_group" =~ ^[a-z_][a-z0-9_-]{0,31}$ &&
        "$state_liblist_mode" =~ ^[0-7]{3,4}$ ]] ||
        fail "The active-pilot liblist metadata is invalid."
    [[ "$state_addons_directory_was_present" == "true" ||
        "$state_addons_directory_was_present" == "false" ]] ||
        fail "The active-pilot addons-directory baseline is invalid."
    if [[ "$state_addons_directory_was_present" == "true" ]]; then
        [[ "$state_addons_directory_owner" =~ ^[a-z_][a-z0-9_-]{0,31}$ &&
            "$state_addons_directory_group" =~ ^[a-z_][a-z0-9_-]{0,31}$ &&
            "$state_addons_directory_mode" =~ ^[0-7]{3,4}$ ]] ||
            fail "The active-pilot addons-directory metadata is invalid."
    else
        [[ "$state_addons_directory_owner" == "none" &&
            "$state_addons_directory_group" == "none" &&
            "$state_addons_directory_mode" == "none" ]] ||
            fail "The absent addons-directory baseline is invalid."
    fi
}

read_activation_gate() {
    local path="${1:-$pilot_enabled_marker}"
    local key value schema_version="" gate_stage="" state_sha256=""
    declare -A seen=()

    [[ -f "$path" && ! -L "$path" ]] || fail "The pilot activation gate is missing or unsafe."
    while IFS='=' read -r key value || [[ -n "${key:-}${value:-}" ]]; do
        [[ -n "${key:-}" ]] || fail "The pilot activation gate contains an empty key."
        [[ -z "${seen[$key]+x}" ]] || fail "The pilot activation gate contains a duplicate key."
        seen[$key]=1
        case "$key" in
            schema_version) schema_version="$value" ;;
            stage) gate_stage="$value" ;;
            activation_state_sha256) state_sha256="$value" ;;
            *) fail "The pilot activation gate contains an unknown key." ;;
        esac
    done < "$path"

    [[ "$schema_version" == "$ACTIVATION_SCHEMA_VERSION" && "${#seen[@]}" -eq 3 ]] ||
        fail "The pilot activation gate is incomplete or unsupported."
    [[ "$gate_stage" == "$state_stage" ]] || fail "The pilot activation gate stage has drifted."
    validate_sha256 "pilot activation state hash" "$state_sha256"
    [[ "$(sha256sum "$activation_state_file" | awk '{ print $1 }')" == "$state_sha256" ]] ||
        fail "The pilot activation gate does not match active state."
}

validate_file_metadata() {
    local path="$1"
    local expected_owner="$2"
    local expected_group="$3"
    local expected_mode="$4"
    [[ -f "$path" && ! -L "$path" ]] || fail "A required file is missing or unsafe."
    [[ "$(stat -c '%U:%G:%a' "$path")" == "$expected_owner:$expected_group:$expected_mode" ]] ||
        fail "A required file owner or mode has drifted."
}

validate_directory_metadata() {
    local path="$1"
    local expected_owner="$2"
    local expected_group="$3"
    local expected_mode="$4"
    [[ -d "$path" && ! -L "$path" ]] || fail "A required directory is missing or unsafe."
    [[ "$(stat -c '%U:%G:%a' "$path")" == "$expected_owner:$expected_group:$expected_mode" ]] ||
        fail "A required directory owner or mode has drifted."
}

verify_sha256() {
    local path="$1"
    local expected="$2"
    [[ -f "$path" && ! -L "$path" ]] || fail "A hashed file is missing or unsafe."
    [[ "$(sha256sum "$path" | awk '{ print $1 }')" == "$expected" ]] ||
        fail "A reviewed file hash has drifted."
}

unit_state() {
    local verb="$1"
    local unit="$2"
    systemctl "$verb" "$unit" 2>/dev/null || true
}

require_unit_disabled() {
    [[ "$(unit_state is-enabled "$1")" == "disabled" ]] ||
        fail "A reviewed service must remain disabled across boot."
}

require_unit_active() {
    [[ "$(unit_state is-active "$1")" == "active" ]] || fail "A reviewed service is not active."
}

require_unit_inactive() {
    [[ "$(unit_state is-active "$1")" == "inactive" ]] || fail "A reviewed service is not inactive."
}

validate_overlay_manifest_scope() {
    local manifest="$1"
    jq -e '
        ([.payload[] | select(.path | startswith("gameserver/"))] | length) > 0 and
        all(.payload[] | select(.path | startswith("gameserver/"));
            (.path | startswith("gameserver/cstrike/addons/metamod/")) or
            (.path | startswith("gameserver/cstrike/addons/amxmodx/"))) and
        ([.payload[] |
            select(.path | endswith(".amxx")) |
            .path] == ["gameserver/cstrike/addons/amxmodx/plugins/goldsrcops_game_events.amxx"])
        ' "$manifest" >/dev/null ||
        fail "The installed pilot overlay escapes the two reviewed plugin roots."

    local required_path
    for required_path in \
        gameserver/cstrike/addons/metamod/metamod_i386.so \
        gameserver/cstrike/addons/metamod/plugins.ini \
        gameserver/cstrike/addons/amxmodx/dlls/amxmodx_mm_i386.so \
        gameserver/cstrike/addons/amxmodx/modules/reapi_amxx_i386.so \
        gameserver/cstrike/addons/amxmodx/plugins/goldsrcops_game_events.amxx \
        gameserver/cstrike/addons/amxmodx/configs/plugins.ini \
        gameserver/cstrike/addons/amxmodx/configs/modules.ini \
        gameserver/cstrike/addons/amxmodx/configs/amxx.cfg; do
        [[ "$(jq --arg path "$required_path" \
            '[.payload[] | select(.path == $path)] | length' "$manifest")" == "1" ]] ||
            fail "The installed pilot overlay is missing a required payload file."
    done
}

verify_release_payload() {
    local manifest_paths actual_paths duplicate_paths
    local payload_path payload_length payload_sha payload_mode actual_length release_directory

    verify_sha256 "$manifest_file" "$pilot_manifest_sha256"
    validate_file_metadata "$manifest_file" root "$service_group" 640
    while IFS= read -r release_directory; do
        validate_directory_metadata "$release_directory" root "$service_group" 750
    done < <(find "$release_path" -type d -print)
    jq -e \
        --argjson schema "$PILOT_BUNDLE_SCHEMA_VERSION" \
        --arg version "$pilot_bundle_version" \
        --arg revision "$pilot_source_revision" '
        .schemaVersion == $schema and
        .bundleVersion == $version and
        .sourceRevision == $revision and
        .sourceDirty == false and
        .productionEligible == true and
        .targetRuntime == "linux-x64" and
        .activation == {
            changesGameServerRuntime: false,
            producerEnabled: false,
            spoolImportEnabled: false,
            deliveryEnabled: false
        } and
        (.payload | type == "array" and length > 0) and
        all(.payload[];
            (.path | type == "string" and
                test("^[A-Za-z0-9][A-Za-z0-9._-]*(/[A-Za-z0-9][A-Za-z0-9._-]*)*$")) and
            (.length | type == "number" and floor == . and . > 0) and
            (.sha256 | type == "string" and test("^[0-9a-f]{64}$")) and
            (.mode == "0640" or .mode == "0750"))
        ' "$manifest_file" >/dev/null || fail "The installed pilot manifest is invalid."

    manifest_paths="$(jq -r '.payload[].path' "$manifest_file" | sort)"
    duplicate_paths="$(printf '%s\n' "$manifest_paths" | uniq -d)"
    [[ -z "$duplicate_paths" ]] || fail "The installed pilot manifest contains duplicate paths."
    actual_paths="$({
        cd "$release_path"
        find . -type f -printf '%P\n' | grep -vFx 'manifest.json' | sort
    })"
    [[ "$manifest_paths" == "$actual_paths" ]] ||
        fail "The installed pilot release does not exactly match its manifest."
    [[ -z "$(find "$release_path" -type l -print -quit)" ]] ||
        fail "The installed pilot release contains a symbolic link."

    while IFS=$'\t' read -r payload_path payload_length payload_sha payload_mode; do
        local full_path="$release_path/$payload_path"
        [[ -f "$full_path" && ! -L "$full_path" ]] ||
            fail "An installed pilot payload file is missing or unsafe."
        actual_length="$(stat -c '%s' "$full_path")"
        [[ "$actual_length" == "$payload_length" ]] ||
            fail "An installed pilot payload length has drifted."
        verify_sha256 "$full_path" "$payload_sha"
        [[ "$(stat -c '%U:%G:%a' "$full_path")" == "root:$service_group:$payload_mode" ]] ||
            fail "An installed pilot payload owner or mode has drifted."
    done < <(jq -r '.payload[] | [.path, .length, .sha256, .mode] | @tsv' "$manifest_file")

    validate_overlay_manifest_scope "$manifest_file"
}

validate_pristine_game_tree() {
    [[ -f "$liblist_file" && ! -L "$liblist_file" ]] ||
        fail "The game runtime liblist is missing or unsafe."
    [[ "$(grep -Ec '^[[:space:]]*gamedll_linux[[:space:]]+"?dlls/cs[.]so"?[[:space:]]*$' "$liblist_file")" == "1" ]] ||
        fail "The game runtime does not have one direct ReGameDLL_CS loader."
    ! grep -Eiq 'metamod|amxmodx|reapi|yapb|reunion' "$liblist_file" ||
        fail "The game runtime already references a plugin loader."
    [[ ! -e "$live_metamod_root" && ! -e "$live_amxx_root" ]] ||
        fail "The game runtime already contains a pilot overlay."
}

verify_game_service() {
    local expect_plugins="$1"
    local control_group listener_count listener_output listener_pid main_pid remaining restart_count
    declare -A listener_pids=()

    require_unit_active "$GAME_SERVICE_NAME"
    require_unit_disabled "$GAME_SERVICE_NAME"
    restart_count="$(systemctl show "$GAME_SERVICE_NAME" --property=NRestarts --value)"
    [[ "$restart_count" == "0" ]] || fail "The game service restarted unexpectedly."
    control_group="$(systemctl show "$GAME_SERVICE_NAME" --property=ControlGroup --value)"
    [[ "$control_group" == /* ]] || fail "The game service control group is invalid."
    listener_output="$(ss -H -lunp "sport = :$prepared_game_port")" ||
        fail "The configured game-server UDP listener could not be inspected."
    listener_count="$(awk 'NF { count++ } END { print count + 0 }' <<< "$listener_output")"
    [[ "$listener_count" == "1" ]] || fail "The game service does not own exactly one UDP listener."

    remaining="$listener_output"
    while [[ "$remaining" =~ pid=([0-9]+) ]]; do
        listener_pid="${BASH_REMATCH[1]}"
        listener_pids[$listener_pid]=1
        remaining="${remaining#*pid="$listener_pid"}"
    done
    ((${#listener_pids[@]} > 0)) || fail "The game-server UDP listener owner is unavailable."
    for listener_pid in "${!listener_pids[@]}"; do
        [[ -r "/proc/$listener_pid/cgroup" ]] ||
            fail "The game-server UDP listener process is unavailable."
        grep -Fq -- "$control_group" "/proc/$listener_pid/cgroup" ||
            fail "The configured UDP listener is outside the game service control group."
    done

    if [[ "$expect_plugins" == "true" ]]; then
        main_pid="$(systemctl show "$GAME_SERVICE_NAME" --property=MainPID --value)"
        [[ "$main_pid" =~ ^[1-9][0-9]*$ && -r "/proc/$main_pid/maps" ]] ||
            fail "The game service process map is unavailable."
        grep -Fq "$live_metamod_root/metamod_i386.so" "/proc/$main_pid/maps" ||
            fail "Metamod-R was not loaded by the active game process."
        grep -Fq "$live_amxx_root/dlls/amxmodx_mm_i386.so" "/proc/$main_pid/maps" ||
            fail "AMX Mod X was not loaded by the active game process."
        grep -Fq "$live_amxx_root/modules/reapi_amxx_i386.so" "/proc/$main_pid/maps" ||
            fail "ReAPI was not loaded by the active game process."
    fi
}

wait_for_game_service() {
    local expect_plugins="$1"
    local _
    for _ in {1..30}; do
        if systemctl is-active --quiet "$GAME_SERVICE_NAME" &&
            [[ "$(ss -H -lun "sport = :$prepared_game_port" | wc -l | tr -d '[:space:]')" == "1" ]]; then
            sleep 3
            verify_game_service "$expect_plugins"
            return
        fi
        sleep 1
    done
    fail "The game service did not reach its reviewed active state."
}

verify_agent_service() {
    require_unit_active "$AGENT_SERVICE_NAME"
    require_unit_disabled "$AGENT_SERVICE_NAME"
    [[ "$(systemctl show "$AGENT_SERVICE_NAME" --property=NRestarts --value)" == "0" ]] ||
        fail "The game-event agent restarted unexpectedly."
}

wait_for_agent_service() {
    local _
    for _ in {1..20}; do
        if systemctl is-active --quiet "$AGENT_SERVICE_NAME"; then
            sleep 2
            verify_agent_service
            return
        fi
        sleep 1
    done
    fail "The game-event agent did not reach its reviewed active state."
}

read_agent_status() {
    local output queue_line spool_line
    output="$(runuser -u "$service_user" -- env -i \
        HOME="$service_home" \
        PATH=/usr/bin:/bin \
        DOTNET_BUNDLE_EXTRACT_BASE_DIR="$state_root/dotnet-bundle" \
        GameEventAgent__QueuePath="$state_root/queue/game-event-agent.db" \
        GameEventAgent__Spool__RootPath="$spool_root" \
        "$release_path/agent/GoldSrcOps.GameEventAgent" status)" ||
        fail "The game-event agent status command failed."
    queue_line="$(grep -E '^Queue status: pending=[0-9]+, in-flight=[0-9]+, dead-letter=[0-9]+, spool-receipts=[0-9]+, next-sequence=[0-9]+[.]$' <<< "$output")"
    spool_line="$(grep -E '^Spool status: ready=[0-9]+, processing=[0-9]+, accepted=[0-9]+, rejected=[0-9]+, temporary=[0-9]+[.]$' <<< "$output")"
    [[ -n "$queue_line" && -n "$spool_line" ]] || fail "The game-event agent status output is invalid."

    [[ "$queue_line" =~ pending=([0-9]+),\ in-flight=([0-9]+),\ dead-letter=([0-9]+),\ spool-receipts=([0-9]+),\ next-sequence=([0-9]+) ]] ||
        fail "The game-event queue status could not be parsed."
    agent_pending="${BASH_REMATCH[1]}"
    agent_in_flight="${BASH_REMATCH[2]}"
    agent_dead_letter="${BASH_REMATCH[3]}"
    agent_spool_receipts="${BASH_REMATCH[4]}"

    [[ "$spool_line" =~ ready=([0-9]+),\ processing=([0-9]+),\ accepted=([0-9]+),\ rejected=([0-9]+),\ temporary=([0-9]+) ]] ||
        fail "The game-event spool status could not be parsed."
    spool_ready="${BASH_REMATCH[1]}"
    spool_processing="${BASH_REMATCH[2]}"
    spool_accepted="${BASH_REMATCH[3]}"
    spool_rejected="${BASH_REMATCH[4]}"
    spool_temporary="${BASH_REMATCH[5]}"
}

require_empty_agent_state() {
    read_agent_status
    [[ "$agent_pending" == "0" && "$agent_in_flight" == "0" &&
        "$agent_dead_letter" == "0" && "$agent_spool_receipts" == "0" &&
        "$spool_ready" == "0" && "$spool_processing" == "0" &&
        "$spool_accepted" == "0" && "$spool_rejected" == "0" &&
        "$spool_temporary" == "0" ]] ||
        fail "The game-event agent or spool is not empty."
}

require_one_sealed_event() {
    read_agent_status
    [[ "$agent_pending" == "1" && "$agent_in_flight" == "0" &&
        "$agent_dead_letter" == "0" && "$agent_spool_receipts" == "0" &&
        "$spool_ready" == "0" && "$spool_processing" == "0" &&
        "$spool_accepted" == "0" && "$spool_rejected" == "0" &&
        "$spool_temporary" == "0" ]] ||
        fail "The pilot did not produce exactly one sealed local event."
}

wait_for_one_sealed_event() {
    local _
    for _ in {1..30}; do
        read_agent_status
        if [[ "$agent_pending" == "1" && "$agent_in_flight" == "0" &&
            "$agent_dead_letter" == "0" && "$agent_spool_receipts" == "0" &&
            "$spool_ready" == "0" && "$spool_processing" == "0" &&
            "$spool_accepted" == "0" && "$spool_rejected" == "0" &&
            "$spool_temporary" == "0" ]]; then
            return
        fi
        if ((agent_pending > 1 || agent_dead_letter > 0 || spool_rejected > 0)); then
            break
        fi
        sleep 1
    done
    fail "The pilot did not converge to exactly one sealed local event."
}

wait_for_delivery_completion() {
    local _
    for _ in {1..60}; do
        read_agent_status
        if [[ "$agent_pending" == "0" && "$agent_in_flight" == "0" &&
            "$agent_dead_letter" == "0" && "$agent_spool_receipts" == "0" &&
            "$spool_ready" == "0" && "$spool_processing" == "0" &&
            "$spool_accepted" == "0" && "$spool_rejected" == "0" &&
            "$spool_temporary" == "0" ]]; then
            return
        fi
        if ((agent_dead_letter > 0 || spool_rejected > 0)); then
            break
        fi
        sleep 1
    done
    fail "The one-event delivery did not complete with empty local state."
}

render_liblist() {
    local source="$1"
    local destination="$2"
    awk '
        BEGIN { matches = 0 }
        /^[[:space:]]*gamedll_linux[[:space:]]+"?dlls\/cs[.]so"?[[:space:]]*$/ {
            matches++
            print "gamedll_linux \"addons/metamod/metamod_i386.so\""
            next
        }
        { print }
        END { if (matches != 1) exit 42 }
    ' "$source" > "$destination" || fail "The reviewed Metamod loader change could not be rendered."
}

render_producer_configuration() {
    local source="$1"
    local destination="$2"
    local enabled="$3"
    [[ "$enabled" == "0" || "$enabled" == "1" ]] || fail "The producer gate value is invalid."
    awk -v enabled="$enabled" '
        BEGIN { matches = 0 }
        /^[[:space:]]*goldsrcops_events_enabled[[:space:]]+[01][[:space:]]*$/ {
            matches++
            print "goldsrcops_events_enabled " enabled
            next
        }
        { print }
        END { if (matches != 1) exit 43 }
    ' "$source" > "$destination" || fail "The producer gate change could not be rendered."
}

render_agent_environment() {
    local destination="$1"
    local delivery_enabled="$2"
    [[ "$delivery_enabled" == "false" || "$delivery_enabled" == "true" ]] ||
        fail "The delivery gate value is invalid."
    cat > "$destination" <<EOF
GameEventAgent__QueuePath=$state_root/queue/game-event-agent.db
GameEventAgent__Spool__Enabled=true
GameEventAgent__Spool__RootPath=$spool_root
GameEventAgent__Delivery__Enabled=$delivery_enabled
GameEventAgent__Delivery__ServerId=$identity_server_id
GameEventAgent__Delivery__ApiBaseUrl=$identity_api_base_url
GameEventAgent__Delivery__OAuth__TokenEndpoint=$identity_token_endpoint
GameEventAgent__Delivery__OAuth__ClientId=$identity_client_id
GameEventAgent__Delivery__OAuth__Audience=$identity_audience
GameEventAgent__Delivery__OAuth__Scope=$identity_permission
EOF
    chmod 0640 "$destination"
}

render_delivery_gate() {
    local source="$1"
    local destination="$2"
    local enabled="$3"
    [[ "$enabled" == "false" || "$enabled" == "true" ]] ||
        fail "The delivery gate value is invalid."
    awk -v enabled="$enabled" '
        BEGIN { matches = 0 }
        /^GameEventAgent__Delivery__Enabled=(false|true)$/ {
            matches++
            print "GameEventAgent__Delivery__Enabled=" enabled
            next
        }
        { print }
        END { if (matches != 1) exit 44 }
    ' "$source" > "$destination" || fail "The delivery gate change could not be rendered."
    chmod 0640 "$destination"
}

read_active_identity_environment() {
    local key value
    declare -A values=()
    declare -A seen=()

    while IFS='=' read -r key value || [[ -n "${key:-}${value:-}" ]]; do
        [[ -n "${key:-}" ]] || fail "The active agent environment contains an empty key."
        [[ -z "${seen[$key]+x}" ]] || fail "The active agent environment contains a duplicate key."
        seen[$key]=1
        case "$key" in
            GameEventAgent__QueuePath|GameEventAgent__Spool__Enabled|\
            GameEventAgent__Spool__RootPath|GameEventAgent__Delivery__Enabled|\
            GameEventAgent__Delivery__ServerId|GameEventAgent__Delivery__ApiBaseUrl|\
            GameEventAgent__Delivery__OAuth__TokenEndpoint|\
            GameEventAgent__Delivery__OAuth__ClientId|\
            GameEventAgent__Delivery__OAuth__Audience|\
            GameEventAgent__Delivery__OAuth__Scope)
                values[$key]="$value"
                ;;
            *) fail "The active agent environment contains an unknown key." ;;
        esac
    done < "$environment_file"

    [[ "${#seen[@]}" -eq 10 ]] || fail "The active agent environment is incomplete."
    [[ "${values[GameEventAgent__QueuePath]}" == "$state_root/queue/game-event-agent.db" &&
        "${values[GameEventAgent__Spool__Enabled]}" == "true" &&
        "${values[GameEventAgent__Spool__RootPath]}" == "$spool_root" ]] ||
        fail "The active agent queue or spool boundary has drifted."
    identity_server_id="${values[GameEventAgent__Delivery__ServerId]}"
    identity_api_base_url="${values[GameEventAgent__Delivery__ApiBaseUrl]}"
    identity_token_endpoint="${values[GameEventAgent__Delivery__OAuth__TokenEndpoint]}"
    identity_client_id="${values[GameEventAgent__Delivery__OAuth__ClientId]}"
    identity_audience="${values[GameEventAgent__Delivery__OAuth__Audience]}"
    identity_permission="${values[GameEventAgent__Delivery__OAuth__Scope]}"
    identity_server_id_claim="$EXPECTED_SERVER_ID_CLAIM"
    validate_identity_values
}

validate_identity_values() {
    validate_uuid "$identity_server_id"
    [[ "$identity_api_base_url" == "$EXPECTED_API_BASE_URL" ]] ||
        fail "The machine identity API base URL is not the reviewed production origin."
    [[ "$identity_audience" == "$EXPECTED_API_AUDIENCE" ]] ||
        fail "The machine identity audience is not the reviewed API audience."
    [[ "$identity_permission" == "$EXPECTED_PERMISSION" ]] ||
        fail "The machine identity permission is broader than the pilot contract."
    [[ "$identity_server_id_claim" == "$EXPECTED_SERVER_ID_CLAIM" ]] ||
        fail "The machine identity server-binding claim is invalid."
    [[ "$identity_token_endpoint" =~ ^https://[a-z0-9]([a-z0-9.-]{0,251}[a-z0-9])?\.auth0\.com/oauth/token$ &&
        "$identity_token_endpoint" != *..* ]] ||
        fail "The machine identity token endpoint is not a canonical Auth0 HTTPS endpoint."
    [[ "$identity_client_id" =~ ^[A-Za-z0-9_-]{16,128}$ ]] ||
        fail "The machine identity client_id is invalid."
}

verify_issued_token_contract() {
    local secret_path="$1"
    local response access_token header_segment payload_segment signature_segment
    local normalized_payload padding payload_json issuer now

    response="$(curl \
        --silent \
        --show-error \
        --fail \
        --connect-timeout 5 \
        --max-time 20 \
        --max-redirs 0 \
        --proto '=https' \
        --proto-redir '=https' \
        --data-urlencode 'grant_type=client_credentials' \
        --data-urlencode "client_id=$identity_client_id" \
        --data-urlencode "client_secret@$secret_path" \
        --data-urlencode "audience=$identity_audience" \
        --data-urlencode "scope=$identity_permission" \
        "$identity_token_endpoint")" || fail "Auth0 rejected the pilot machine identity."
    ((${#response} > 0 && ${#response} <= 16384)) ||
        fail "The Auth0 token response size is invalid."
    access_token="$(jq -er '
        select(type == "object") |
        select(.token_type == "Bearer") |
        select(.access_token | type == "string" and length > 0 and length <= 12288) |
        .access_token
        ' <<< "$response")" || fail "The Auth0 token response contract is invalid."
    response=""
    unset response

    IFS='.' read -r header_segment payload_segment signature_segment padding <<< "$access_token"
    [[ -n "$header_segment" && -n "$payload_segment" && -n "$signature_segment" && -z "$padding" &&
        "$header_segment" =~ ^[A-Za-z0-9_-]+$ &&
        "$payload_segment" =~ ^[A-Za-z0-9_-]+$ &&
        "$signature_segment" =~ ^[A-Za-z0-9_-]+$ ]] ||
        fail "The Auth0 access token is not a compact JWT."
    normalized_payload="${payload_segment//-/+}"
    normalized_payload="${normalized_payload//_/\/}"
    case $((${#normalized_payload} % 4)) in
        0) ;;
        2) normalized_payload+="==" ;;
        3) normalized_payload+="=" ;;
        *) fail "The Auth0 access token payload encoding is invalid." ;;
    esac
    payload_json="$(printf '%s' "$normalized_payload" | base64 --decode 2>/dev/null)" ||
        fail "The Auth0 access token payload cannot be decoded."
    issuer="${identity_token_endpoint%oauth/token}"
    now="$(date -u +%s)"
    jq -e \
        --arg issuer "$issuer" \
        --arg audience "$identity_audience" \
        --arg permission "$identity_permission" \
        --arg serverClaim "$identity_server_id_claim" \
        --arg serverId "$identity_server_id" \
        --arg roleClaim "https://goldsrcops.com/roles" \
        --argjson now "$now" '
        type == "object" and
        .iss == $issuer and
        ((.aud == $audience) or
            (.aud | type == "array" and length == 1 and .[0] == $audience)) and
        (.sub | type == "string" and length > 0 and length <= 256) and
        (.exp | type == "number" and floor == . and . > ($now + 30)) and
        (.permissions | type == "array" and length == 1 and .[0] == $permission) and
        .[$serverClaim] == $serverId and
        (has($roleClaim) | not)
        ' <<< "$payload_json" >/dev/null ||
        fail "The issued token is not least-privilege and bound to the reviewed server."

    access_token=""
    payload_json=""
    normalized_payload=""
    header_segment=""
    payload_segment=""
    signature_segment=""
    unset access_token payload_json normalized_payload header_segment payload_segment signature_segment
}

install_atomic_file() {
    local source="$1"
    local destination="$2"
    local owner="$3"
    local group="$4"
    local mode="$5"
    local temporary
    temporary="$(dirname "$destination")/.$(basename "$destination").$$"
    install -m "$mode" -o "$owner" -g "$group" "$source" "$temporary"
    mv -- "$temporary" "$destination"
}

write_activation_state() {
    local stage="$1"
    local environment_sha256="$2"
    local producer_sha256="$3"
    local temporary_state="$activation_root/.state.$$"
    local temporary_gate="$configuration_directory/.game-event-pilot-enabled.$$"
    local state_sha256

    cat > "$temporary_state" <<EOF
schema_version=$ACTIVATION_SCHEMA_VERSION
stage=$stage
pilot_marker_sha256=$state_pilot_marker_sha256
bundle_sha256=$state_bundle_sha256
manifest_sha256=$state_manifest_sha256
identity_sha256=$state_identity_sha256
liblist_original_sha256=$state_liblist_original_sha256
liblist_active_sha256=$state_liblist_active_sha256
liblist_owner=$state_liblist_owner
liblist_group=$state_liblist_group
liblist_mode=$state_liblist_mode
environment_original_sha256=$state_environment_original_sha256
environment_current_sha256=$environment_sha256
producer_configuration_sha256=$producer_sha256
game_restart_count_before=$state_game_restart_count_before
game_invocation_before=$state_game_invocation_before
addons_directory_was_present=$state_addons_directory_was_present
addons_directory_owner=$state_addons_directory_owner
addons_directory_group=$state_addons_directory_group
addons_directory_mode=$state_addons_directory_mode
EOF
    chown root:root "$temporary_state"
    chmod 0600 "$temporary_state"
    mv -- "$temporary_state" "$activation_state_file"
    state_sha256="$(sha256sum "$activation_state_file" | awk '{ print $1 }')"

    cat > "$temporary_gate" <<EOF
schema_version=$ACTIVATION_SCHEMA_VERSION
stage=$stage
activation_state_sha256=$state_sha256
EOF
    chown root:"$service_group" "$temporary_gate"
    chmod 0640 "$temporary_gate"
    mv -- "$temporary_gate" "$pilot_enabled_marker"

    state_stage="$stage"
    state_environment_current_sha256="$environment_sha256"
    state_producer_configuration_sha256="$producer_sha256"
}

validate_secret_file() {
    validate_file_metadata "$client_secret_file" root root 600
    local size
    size="$(stat -c '%s' "$client_secret_file")"
    ((size >= 32 && size <= 256)) || fail "The OAuth client credential length is invalid."
    [[ "$(wc -l < "$client_secret_file")" == "0" ]] ||
        fail "The OAuth client credential must be one line without a terminator."
    LC_ALL=C grep -Eq '^[A-Za-z0-9._~+/=-]+$' "$client_secret_file" ||
        fail "The OAuth client credential contains an unsupported character."
}

read_client_secret() {
    [[ "$read_secret_from_stdin" == "true" ]] ||
        fail "Activation requires --client-secret-stdin."
    [[ ! -t 0 ]] || fail "The OAuth client secret must arrive through redirected stdin."
    client_secret="$(head -c 257)"
    ((${#client_secret} >= 32 && ${#client_secret} <= 256)) ||
        fail "The OAuth client secret must contain between 32 and 256 characters."
    [[ "$client_secret" =~ ^[A-Za-z0-9._~+/=-]+$ ]] ||
        fail "The OAuth client secret contains an unsupported character."
}

prepare_staging() {
    staging_directory="$(mktemp -d "$configuration_directory/.game-event-pilot-activate.XXXXXX")"
    chmod 0700 "$staging_directory"
    render_agent_environment "$staging_directory/agent-spool.env" false
    render_agent_environment "$staging_directory/agent-delivery.env" true
    printf '%s' "$client_secret" > "$staging_directory/client-secret"
    chmod 0600 "$staging_directory/client-secret"
    client_secret=""
    unset client_secret
}

capture_activation_baseline() {
    state_pilot_marker_sha256="$(sha256sum "$pilot_marker" | awk '{ print $1 }')"
    state_bundle_sha256="$pilot_bundle_sha256"
    state_manifest_sha256="$pilot_manifest_sha256"
    state_identity_sha256="$(sha256sum "$identity_file" | awk '{ print $1 }')"
    state_liblist_original_sha256="$(sha256sum "$liblist_file" | awk '{ print $1 }')"
    state_liblist_owner="$(stat -c '%U' "$liblist_file")"
    state_liblist_group="$(stat -c '%G' "$liblist_file")"
    state_liblist_mode="$(stat -c '%a' "$liblist_file")"
    [[ "$state_liblist_owner" =~ ^[a-z_][a-z0-9_-]{0,31}$ &&
        "$state_liblist_group" =~ ^[a-z_][a-z0-9_-]{0,31}$ &&
        "$state_liblist_mode" =~ ^[0-7]{3,4}$ ]] ||
        fail "The original liblist metadata is invalid."
    state_environment_original_sha256="$(sha256sum "$environment_file" | awk '{ print $1 }')"
    state_game_restart_count_before="$(systemctl show "$GAME_SERVICE_NAME" --property=NRestarts --value)"
    state_game_invocation_before="$(systemctl show "$GAME_SERVICE_NAME" --property=InvocationID --value)"
    [[ "$state_game_restart_count_before" =~ ^[0-9]+$ ]] ||
        fail "The game-service restart baseline is invalid."
    [[ "$state_game_invocation_before" =~ ^[0-9a-f]{32}$ ]] ||
        fail "The game-service invocation baseline is invalid."

    if [[ -d "$live_addons_root" && ! -L "$live_addons_root" ]]; then
        state_addons_directory_was_present=true
        state_addons_directory_owner="$(stat -c '%U' "$live_addons_root")"
        state_addons_directory_group="$(stat -c '%G' "$live_addons_root")"
        state_addons_directory_mode="$(stat -c '%a' "$live_addons_root")"
        [[ "$state_addons_directory_owner" =~ ^[a-z_][a-z0-9_-]{0,31}$ &&
            "$state_addons_directory_group" =~ ^[a-z_][a-z0-9_-]{0,31}$ &&
            "$state_addons_directory_mode" =~ ^[0-7]{3,4}$ ]] ||
            fail "The original addons-directory metadata is invalid."
    elif [[ ! -e "$live_addons_root" ]]; then
        state_addons_directory_was_present=false
        state_addons_directory_owner=none
        state_addons_directory_group=none
        state_addons_directory_mode=none
    else
        fail "The live addons path is unsafe."
    fi

    install -m 0600 "$liblist_file" "$staging_directory/liblist.original"
    install -m 0600 "$environment_file" "$staging_directory/game-event-agent.env.original"
    verify_sha256 "$staging_directory/liblist.original" "$state_liblist_original_sha256"
    verify_sha256 "$staging_directory/game-event-agent.env.original" \
        "$state_environment_original_sha256"
    render_liblist "$staging_directory/liblist.original" "$staging_directory/liblist.gam"
    state_liblist_active_sha256="$(sha256sum "$staging_directory/liblist.gam" | awk '{ print $1 }')"
    state_environment_current_sha256="$(sha256sum "$staging_directory/agent-spool.env" | awk '{ print $1 }')"
    state_producer_configuration_sha256="$(sha256sum \
        "$release_path/gameserver/cstrike/addons/amxmodx/configs/amxx.cfg" | awk '{ print $1 }')"
}

verify_activation_baseline_unchanged() {
    local current_game_invocation current_game_restart_count current_metadata
    verify_game_service false
    current_game_restart_count="$(systemctl show "$GAME_SERVICE_NAME" --property=NRestarts --value)"
    current_game_invocation="$(systemctl show "$GAME_SERVICE_NAME" --property=InvocationID --value)"
    [[ "$current_game_restart_count" == "$state_game_restart_count_before" ]] ||
        fail "The game-service restart count changed during activation preflight."
    [[ "$current_game_invocation" == "$state_game_invocation_before" ]] ||
        fail "The game-service invocation changed during activation preflight."
    validate_identity_file_metadata
    verify_sha256 "$identity_file" "$state_identity_sha256"
    verify_sha256 "$pilot_marker" "$state_pilot_marker_sha256"
    verify_sha256 "$agent_unit_file" "$pilot_unit_sha256"
    verify_release_payload
    verify_sha256 "$liblist_file" "$state_liblist_original_sha256"
    current_metadata="$(stat -c '%U:%G:%a' "$liblist_file")"
    [[ "$current_metadata" == "$state_liblist_owner:$state_liblist_group:$state_liblist_mode" ]] ||
        fail "The original liblist metadata changed during activation preflight."
    verify_sha256 "$environment_file" "$state_environment_original_sha256"
    validate_file_metadata "$environment_file" root "$service_group" 640
    validate_pristine_game_tree

    if [[ "$state_addons_directory_was_present" == "true" ]]; then
        current_metadata="$(stat -c '%U:%G:%a' "$live_addons_root")"
        [[ "$current_metadata" == "$state_addons_directory_owner:$state_addons_directory_group:$state_addons_directory_mode" ]] ||
            fail "The addons-directory metadata changed during activation preflight."
    else
        [[ ! -e "$live_addons_root" ]] ||
            fail "The addons directory appeared during activation preflight."
    fi
}

install_live_overlay() {
    local source_addons="$release_path/gameserver/cstrike/addons"
    [[ -d "$source_addons/metamod" && -d "$source_addons/amxmodx" ]] ||
        fail "The reviewed pilot overlay is incomplete."
    [[ ! -e "$live_metamod_root" && ! -e "$live_amxx_root" ]] ||
        fail "The live plugin roots are not empty."

    if [[ ! -e "$live_addons_root" ]]; then
        install -d -m 0750 -o root -g "$service_group" "$live_addons_root"
    fi
    cp -a -- "$source_addons/metamod" "$live_metamod_root"
    cp -a -- "$source_addons/amxmodx" "$live_amxx_root"
    find "$live_metamod_root" "$live_amxx_root" -type d -exec chown root:"$service_group" {} +
    find "$live_metamod_root" "$live_amxx_root" -type d -exec chmod 0750 {} +
    install -d -m 0700 -o "$service_user" -g "$service_group" \
        "$spool_root" \
        "$spool_root/incoming" \
        "$spool_root/processing" \
        "$spool_root/accepted" \
        "$spool_root/rejected"
}

verify_live_overlay() {
    local payload_path payload_sha payload_mode destination
    [[ -d "$live_metamod_root" && ! -L "$live_metamod_root" ]] ||
        fail "The live Metamod-R root is missing or unsafe."
    [[ -d "$live_amxx_root" && ! -L "$live_amxx_root" ]] ||
        fail "The live AMX Mod X root is missing or unsafe."
    [[ -z "$(find "$live_metamod_root" "$live_amxx_root" -type l -print -quit)" ]] ||
        fail "The live pilot overlay contains a symbolic link."

    while IFS=$'\t' read -r payload_path payload_sha payload_mode; do
        destination="$game_root/${payload_path#gameserver/}"
        [[ -f "$destination" && ! -L "$destination" ]] ||
            fail "A live pilot payload file is missing or unsafe."
        if [[ "$destination" == "$producer_configuration" ]]; then
            verify_sha256 "$destination" "$state_producer_configuration_sha256"
        else
            verify_sha256 "$destination" "$payload_sha"
        fi
        [[ "$(stat -c '%U:%G:%a' "$destination")" == "root:$service_group:$payload_mode" ]] ||
            fail "A live pilot payload owner or mode has drifted."
    done < <(jq -r '.payload[] | select(.path | startswith("gameserver/")) | [.path, .sha256, .mode] | @tsv' "$manifest_file")

    validate_directory_metadata "$spool_root" "$service_user" "$service_group" 700
    local spool_directory
    for spool_directory in incoming processing accepted rejected; do
        validate_directory_metadata "$spool_root/$spool_directory" "$service_user" "$service_group" 700
    done
}

validate_active_environment() {
    verify_sha256 "$environment_file" "$state_environment_current_sha256"
    read_active_identity_environment
    grep -Fxq 'GameEventAgent__Spool__Enabled=true' "$environment_file" ||
        fail "Spool import is not enabled in the active pilot."
    if [[ "$state_stage" == "delivery" || "$state_stage" == "delivered" ]]; then
        grep -Fxq 'GameEventAgent__Delivery__Enabled=true' "$environment_file" ||
            fail "Delivery is not enabled in the active delivery stage."
    else
        grep -Fxq 'GameEventAgent__Delivery__Enabled=false' "$environment_file" ||
            fail "Delivery crossed its reviewed stage boundary."
    fi
    ! grep -Eiq 'secret|password|bearer|access[_-]?token' "$environment_file" ||
        fail "The active environment contains secret-shaped configuration."
}

verify_activation_backups() {
    validate_file_metadata "$activation_backup_root/liblist.gam" root root 600
    validate_file_metadata "$activation_backup_root/game-event-agent.env" root root 600
    validate_file_metadata "$activation_backup_root/machine-identity" root root 600
    verify_sha256 "$activation_backup_root/liblist.gam" "$state_liblist_original_sha256"
    verify_sha256 "$activation_backup_root/game-event-agent.env" "$state_environment_original_sha256"
    verify_sha256 "$activation_backup_root/machine-identity" "$state_identity_sha256"
}

load_and_validate_active_state() {
    local expected_stage="$1"
    read_activation_state
    [[ "$state_stage" == "$expected_stage" ]] ||
        fail "The requested transition does not match the current pilot stage."
    read_activation_gate
    validate_file_metadata "$activation_state_file" root root 600
    validate_file_metadata "$pilot_enabled_marker" root "$service_group" 640
    validate_directory_metadata "$activation_root" root root 700
    validate_directory_metadata "$activation_backup_root" root root 700
    [[ "$state_pilot_marker_sha256" == "$(sha256sum "$pilot_marker" | awk '{ print $1 }')" &&
        "$state_bundle_sha256" == "$pilot_bundle_sha256" &&
        "$state_manifest_sha256" == "$pilot_manifest_sha256" ]] ||
        fail "The active pilot no longer matches the dormant installation."
    verify_activation_backups
    verify_sha256 "$liblist_file" "$state_liblist_active_sha256"
    validate_active_environment
    validate_secret_file
    verify_live_overlay
    verify_game_service true
    verify_agent_service
}

validate_identity_file_metadata() {
    [[ "$(realpath -e -- "$identity_file")" == "$identity_file" ]] ||
        fail "The machine identity path must be canonical and contain no symbolic-link component."
    validate_file_metadata "$identity_file" root root 600
    local parent parent_mode
    parent="$(dirname "$identity_file")"
    [[ "$(stat -c '%U' "$parent")" == "root" ]] ||
        fail "The machine identity parent must be owned by root."
    parent_mode="$(stat -c '%a' "$parent")"
    (( (8#$parent_mode & 8#022) == 0 )) ||
        fail "The machine identity parent must not be writable by group or others."
}

require_base_environment() {
    ((EUID == 0)) || fail "--apply must run as root."
    local command
    for command in \
        awk base64 basename cat chmod chown cmp cp curl cut date dirname env find findmnt flock getent grep head id install jq \
        mktemp mv ps realpath rm rmdir runuser sha256sum sleep sort ss stat systemctl \
        systemd-analyze tr uname uniq wc; do
        require_command "$command"
    done

    [[ -r /etc/os-release ]] || fail "The operating-system identity is unavailable."
    # shellcheck disable=SC1091
    source /etc/os-release
    [[ "${ID:-}" == "ubuntu" && "${VERSION_ID:-}" == "24.04" ]] ||
        fail "Pilot activation supports Ubuntu 24.04 only."
    [[ "$(uname -m)" == "x86_64" ]] || fail "Pilot activation requires x86-64."
    [[ "$(ps -p 1 -o comm= | tr -d '[:space:]')" == "systemd" ]] ||
        fail "Pilot activation requires systemd as PID 1."

    read_prepared_marker
    read_pilot_marker
    service_user="$prepared_service_user"
    service_group="$(id -gn "$service_user")"
    [[ "${SUDO_USER:-}" == "$prepared_operator_user" ]] ||
        fail "Run --apply through sudo from the operator recorded by host bootstrap."
    [[ "$(getent passwd "$service_user" | cut -d: -f6)" == "$service_home" ]] ||
        fail "The service account home directory has drifted."
    [[ "$(getent passwd "$service_user" | cut -d: -f7)" == "/usr/sbin/nologin" ]] ||
        fail "The service account must remain non-interactive."

    validate_file_metadata "$prepared_marker" root "$service_group" 640
    validate_file_metadata "$runtime_marker" root "$service_group" 640
    validate_file_metadata "$runtime_enabled_marker" root "$service_group" 640
    validate_file_metadata "$pilot_marker" root "$service_group" 640
    validate_file_metadata "$environment_file" root "$service_group" 640
    validate_file_metadata "$agent_unit_file" root root 644
    validate_directory_metadata "$configuration_directory" root "$service_group" 750
    validate_directory_metadata "$configuration_directory/secrets" root "$service_group" 710
    validate_directory_metadata "$installation_directory" root "$service_group" 750
    validate_directory_metadata "$pilot_root" root "$service_group" 750
    validate_directory_metadata "$pilot_releases_directory" root "$service_group" 750
    validate_directory_metadata "$service_home" "$service_user" "$service_group" 750
    validate_directory_metadata "$state_root" "$service_user" "$service_group" 750
    validate_directory_metadata "$state_root/queue" "$service_user" "$service_group" 750
    validate_directory_metadata "$state_root/dotnet-bundle" "$service_user" "$service_group" 750

    verify_sha256 "$agent_unit_file" "$pilot_unit_sha256"
    release_path="$pilot_releases_directory/$pilot_bundle_sha256"
    manifest_file="$release_path/manifest.json"
    [[ -d "$release_path" && ! -L "$release_path" ]] ||
        fail "The content-addressed pilot release is missing or unsafe."
    verify_release_payload
    require_unit_disabled "$GAME_SERVICE_NAME"
    require_unit_disabled "$AGENT_SERVICE_NAME"
    systemd-analyze verify "$agent_unit_file" >/dev/null ||
        fail "The game-event agent unit is invalid."
}

acquire_lock() {
    exec 9>"$activation_lock"
    flock --nonblock 9 || fail "Another game-event pilot operation is already in progress."
    chown root:"$service_group" "$activation_lock"
    chmod 0640 "$activation_lock"
}

require_dormant_activation_boundary() {
    verify_sha256 "$environment_file" "$pilot_environment_sha256"
    validate_pristine_game_tree
    [[ ! -e "$pilot_enabled_marker" && ! -e "$client_secret_file" && ! -e "$activation_root" ]] ||
        fail "A pilot activation, credential, or recovery state already exists."
    require_unit_inactive "$AGENT_SERVICE_NAME"
    verify_game_service false
    [[ -d "$state_root/queue" && -d "$state_root/dotnet-bundle" ]] ||
        fail "The dormant agent state directories are incomplete."
    [[ -z "$(find "$state_root" -mindepth 1 ! -type d -print -quit)" ]] ||
        fail "The dormant pilot state is not empty."
}

safe_remove_tree() {
    local path="$1"
    local required_prefix="$2"
    local mounted_target
    [[ "$path" == "$required_prefix" || "$path" == "$required_prefix/"* ]] ||
        fail "Refusing to remove a path outside the reviewed pilot boundary."
    [[ ! -L "$path" ]] || fail "Refusing to remove a symbolic-link tree."
    [[ ! -e "$path" || -d "$path" ]] || fail "Refusing to remove an unexpected non-directory tree."
    if [[ -d "$path" ]]; then
        while IFS= read -r mounted_target; do
            case "$mounted_target" in
                "$path"|"$path/"*)
                    fail "Refusing to remove a tree that contains a mount point."
                    ;;
            esac
        done < <(findmnt -rn -o TARGET)
    fi
    rm -rf -- "$path"
}

restore_dormant_state() {
    trap - ERR HUP INT TERM
    local rollback_failed=false
    local liblist_backup="$activation_backup_root/liblist.gam"
    local environment_backup="$activation_backup_root/game-event-agent.env"

    if [[ ! -f "$liblist_backup" && -n "$staging_directory" &&
        -f "$staging_directory/liblist.original" ]]; then
        liblist_backup="$staging_directory/liblist.original"
    fi
    if [[ ! -f "$environment_backup" &&
        -n "$staging_directory" &&
        -f "$staging_directory/game-event-agent.env.original" ]]; then
        environment_backup="$staging_directory/game-event-agent.env.original"
    fi

    systemctl stop "$AGENT_SERVICE_NAME" >/dev/null 2>&1 || true
    if systemctl is-active --quiet "$AGENT_SERVICE_NAME"; then
        rollback_failed=true
    fi
    if ! systemctl stop "$GAME_SERVICE_NAME" >/dev/null 2>&1; then
        rollback_failed=true
    fi
    if systemctl is-active --quiet "$GAME_SERVICE_NAME"; then
        rollback_failed=true
    fi
    [[ "$rollback_failed" == "false" ]] || {
        log "ROLLBACK_INCOMPLETE: services could not be stopped; recovery state was retained."
        return 1
    }

    rm -f -- "$pilot_enabled_marker" "$client_secret_file"
    safe_remove_tree "$live_metamod_root" "$live_addons_root"
    safe_remove_tree "$live_amxx_root" "$live_addons_root"

    if [[ -f "$liblist_backup" ]]; then
        install_atomic_file \
            "$liblist_backup" \
            "$liblist_file" \
            "$state_liblist_owner" \
            "$state_liblist_group" \
            "$state_liblist_mode"
    else
        rollback_failed=true
    fi
    if [[ -f "$environment_backup" ]]; then
        install_atomic_file \
            "$environment_backup" \
            "$environment_file" \
            root \
            "$service_group" \
            0640
    else
        rollback_failed=true
    fi

    if [[ -e "$state_root" ]]; then
        safe_remove_tree "$state_root" "$service_home"
    fi
    install -d -m 0750 -o "$service_user" -g "$service_group" \
        "$state_root" "$state_root/queue" "$state_root/dotnet-bundle"

    if [[ "$state_addons_directory_was_present" == "false" ]]; then
        rmdir --ignore-fail-on-non-empty "$live_addons_root" 2>/dev/null || true
    elif [[ -d "$live_addons_root" && ! -L "$live_addons_root" ]]; then
        chown "$state_addons_directory_owner:$state_addons_directory_group" "$live_addons_root"
        chmod "$state_addons_directory_mode" "$live_addons_root"
    else
        rollback_failed=true
    fi

    verify_sha256 "$liblist_file" "$state_liblist_original_sha256" || rollback_failed=true
    verify_sha256 "$environment_file" "$state_environment_original_sha256" || rollback_failed=true
    [[ "$rollback_failed" == "false" ]] || {
        log "ROLLBACK_INCOMPLETE: original files could not be restored; recovery state was retained."
        return 1
    }

    if ! systemctl start "$GAME_SERVICE_NAME"; then
        log "ROLLBACK_INCOMPLETE: the original game service did not start; recovery state was retained."
        return 1
    fi
    if ! wait_for_game_service false; then
        log "ROLLBACK_INCOMPLETE: the original game service failed verification; recovery state was retained."
        return 1
    fi
    require_unit_inactive "$AGENT_SERVICE_NAME" || return 1
    verify_sha256 "$environment_file" "$pilot_environment_sha256" || return 1
    validate_pristine_game_tree || return 1

    safe_remove_tree "$activation_root" "$pilot_root"
    log "ROLLED_BACK: dormant game-event pilot and original active game runtime restored."
    log "SERVICE_STATE: game active/disabled; agent inactive/disabled"
}

cleanup_staging() {
    client_secret=""
    unset client_secret 2>/dev/null || true
    if [[ -n "$staging_directory" && -d "$staging_directory" ]]; then
        rm -rf -- "$staging_directory"
    fi
}

rollback_on_failure() {
    local status="$1"
    trap - ERR HUP INT TERM
    if [[ "$activation_started" == "true" ]]; then
        restore_dormant_state || true
    fi
    trap - EXIT
    cleanup_staging
    exit "$status"
}

arm_transition_rollback() {
    activation_started=true
    trap 'rollback_on_failure $?' ERR
    trap 'rollback_on_failure 129' HUP
    trap 'rollback_on_failure 130' INT
    trap 'rollback_on_failure 143' TERM
}

disarm_transition_rollback() {
    activation_started=false
    trap - ERR HUP INT TERM
    trap - EXIT
    cleanup_staging
}

run_activate() {
    require_base_environment
    acquire_lock
    require_dormant_activation_boundary
    validate_identity_file_metadata
    read_identity_file
    read_client_secret
    trap cleanup_staging EXIT
    prepare_staging
    capture_activation_baseline
    verify_issued_token_contract "$staging_directory/client-secret"
    verify_activation_baseline_unchanged
    arm_transition_rollback

    install -d -m 0700 -o root -g root "$activation_root" "$activation_backup_root"
    install -m 0600 -o root -g root \
        "$staging_directory/liblist.original" \
        "$activation_backup_root/liblist.gam"
    install -m 0600 -o root -g root \
        "$staging_directory/game-event-agent.env.original" \
        "$activation_backup_root/game-event-agent.env"
    install -m 0600 -o root -g root \
        "$identity_file" \
        "$activation_backup_root/machine-identity"
    verify_activation_backups
    write_activation_state \
        activating \
        "$state_environment_current_sha256" \
        "$state_producer_configuration_sha256"

    systemctl stop "$GAME_SERVICE_NAME"
    require_unit_inactive "$GAME_SERVICE_NAME"
    install_live_overlay
    install_atomic_file "$staging_directory/liblist.gam" "$liblist_file" \
        "$state_liblist_owner" "$state_liblist_group" "$state_liblist_mode"
    install_atomic_file "$staging_directory/agent-spool.env" "$environment_file" \
        root "$service_group" 0640
    install_atomic_file "$staging_directory/client-secret" "$client_secret_file" root root 0600

    verify_sha256 "$environment_file" "$state_environment_current_sha256"
    verify_sha256 "$producer_configuration" "$state_producer_configuration_sha256"

    systemctl start "$GAME_SERVICE_NAME"
    wait_for_game_service true
    systemctl start "$AGENT_SERVICE_NAME"
    wait_for_agent_service
    require_empty_agent_state
    write_activation_state spool-only "$state_environment_current_sha256" \
        "$state_producer_configuration_sha256"
    load_and_validate_active_state spool-only

    disarm_transition_rollback
    log "ACTIVATED: pilot overlay loaded with spool import on and producer/delivery off."
    log "SERVICE_STATE: game active/disabled; agent active/disabled"
    log "NEXT_GATE: verify external A2S and zero bots before the one-event capture stage."
}

prepare_transition_staging() {
    staging_directory="$(mktemp -d "$configuration_directory/.game-event-pilot-transition.XXXXXX")"
    chmod 0700 "$staging_directory"
}

run_enable_producer() {
    require_base_environment
    acquire_lock
    load_and_validate_active_state spool-only
    require_empty_agent_state
    prepare_transition_staging
    arm_transition_rollback
    render_producer_configuration "$producer_configuration" "$staging_directory/amxx.cfg" 1

    install_atomic_file "$staging_directory/amxx.cfg" "$producer_configuration" \
        root "$service_group" 0640
    systemctl restart "$GAME_SERVICE_NAME"
    wait_for_game_service true
    state_producer_configuration_sha256="$(sha256sum "$producer_configuration" | awk '{ print $1 }')"
    write_activation_state capture "$state_environment_current_sha256" \
        "$state_producer_configuration_sha256"

    disarm_transition_rollback
    log "PRODUCER_ENABLED: delivery remains off; capture exactly one anonymous round.ended event."
    log "NEXT_GATE: run --seal-event immediately after the controlled round boundary."
}

run_seal_event() {
    require_base_environment
    acquire_lock
    load_and_validate_active_state capture
    prepare_transition_staging
    arm_transition_rollback
    render_producer_configuration "$producer_configuration" "$staging_directory/amxx.cfg" 0

    install_atomic_file "$staging_directory/amxx.cfg" "$producer_configuration" \
        root "$service_group" 0640
    systemctl restart "$GAME_SERVICE_NAME"
    wait_for_game_service true
    wait_for_one_sealed_event
    state_producer_configuration_sha256="$(sha256sum "$producer_configuration" | awk '{ print $1 }')"
    write_activation_state event-sealed "$state_environment_current_sha256" \
        "$state_producer_configuration_sha256"

    disarm_transition_rollback
    log "EVENT_SEALED: exactly one local event is pending; producer and delivery are off."
    log "NEXT_GATE: verify A2S and zero bots, then review --enable-delivery."
}

run_enable_delivery() {
    require_base_environment
    acquire_lock
    load_and_validate_active_state event-sealed
    require_one_sealed_event
    verify_issued_token_contract "$client_secret_file"
    prepare_transition_staging
    arm_transition_rollback
    render_delivery_gate "$environment_file" "$staging_directory/agent-delivery.env" true

    install_atomic_file "$staging_directory/agent-delivery.env" "$environment_file" \
        root "$service_group" 0640
    state_environment_current_sha256="$(sha256sum "$environment_file" | awk '{ print $1 }')"
    write_activation_state delivery "$state_environment_current_sha256" \
        "$state_producer_configuration_sha256"
    systemctl restart "$AGENT_SERVICE_NAME"
    wait_for_agent_service
    wait_for_delivery_completion
    write_activation_state delivered "$state_environment_current_sha256" \
        "$state_producer_configuration_sha256"
    load_and_validate_active_state delivered
    require_empty_agent_state

    disarm_transition_rollback
    log "DELIVERED: one local event completed with no residual queue, receipt, or dead letter."
    log "NEXT_GATE: verify API receipt/idempotency, Reader projection, A2S, and zero bots; then run --rollback."
}

run_explicit_rollback() {
    require_base_environment
    acquire_lock
    read_activation_state
    if [[ -e "$pilot_enabled_marker" ]]; then
        # Recovery remains available if interruption left the gate one atomic
        # write behind the root-only activation state.
        validate_file_metadata "$pilot_enabled_marker" root "$service_group" 640
    fi
    validate_file_metadata "$activation_state_file" root root 600
    validate_directory_metadata "$activation_root" root root 700
    validate_directory_metadata "$activation_backup_root" root root 700
    verify_activation_backups
    [[ "$state_pilot_marker_sha256" == "$(sha256sum "$pilot_marker" | awk '{ print $1 }')" &&
        "$state_bundle_sha256" == "$pilot_bundle_sha256" &&
        "$state_manifest_sha256" == "$pilot_manifest_sha256" ]] ||
        fail "The rollback state no longer matches the installed pilot."
    arm_transition_rollback
    restore_dormant_state
    disarm_transition_rollback
}

print_overview_plan() {
    log "PLAN: activate the verified dormant bundle with a strict server-bound machine identity and stdin-only credential"
    log "PLAN: preserve exact liblist, agent environment, game invocation, restart count, and addons-directory metadata"
    log "PLAN: load the manifest-pinned overlay with spool import on while producer and delivery remain off"
    log "PLAN: enable one producer boundary, seal exactly one local event, and only then enable HTTP delivery"
    log "PLAN: keep both services boot-disabled and restore the dormant pilot on any failed transition"
    log "PLAN: require external A2S, zero-bot, API receipt/idempotency, and Reader checks at their named gates"
    log "PLAN_ONLY: no host state, stdin, identity file, service, or network endpoint was touched."
}

print_operation_plan() {
    case "$operation" in
        activate)
            log "PLAN: verify the dormant bundle, plugin-free active game runtime, empty agent state, and strict machine identity"
            log "PLAN: accept the OAuth client secret only through redirected stdin during apply"
            log "PLAN: stop the game, preserve rollback inputs, install the exact overlay and Metamod loader, then start spool-only"
            log "PLAN: leave producer and delivery off and keep both units disabled across boot"
            ;;
        enable-producer)
            log "PLAN: require a healthy empty spool-only stage and keep HTTP delivery off"
            log "PLAN: enable the producer for one controlled anonymous round boundary and restart only the game service"
            ;;
        seal-event)
            log "PLAN: disable the producer, restart only the game service, and require exactly one pending local event"
            log "PLAN: reject multiple events, dead letters, rejected spool files, or any residual spool state"
            ;;
        enable-delivery)
            log "PLAN: require one sealed event and a disabled producer before enabling HTTP delivery"
            log "PLAN: require local completion with zero pending, in-flight, dead-letter, receipt, and spool counts"
            ;;
        rollback)
            log "PLAN: stop both services, remove only the pilot-owned overlay and credential, and discard pilot-local queue state"
            log "PLAN: restore exact pre-pilot liblist and agent environment, restart the original game runtime, and keep the agent dormant"
            ;;
        *) fail "The requested plan is unsupported." ;;
    esac
    log "PLAN: on any failed mutating transition, execute the same active rollback before returning failure"
    log "PLAN_ONLY: no host state, stdin, identity file, service, or network endpoint was touched; add --apply to execute."
}

main() {
    while (($# > 0)); do
        case "$1" in
            --activate)
                select_operation activate
                shift
                ;;
            --enable-producer)
                select_operation enable-producer
                shift
                ;;
            --seal-event)
                select_operation seal-event
                shift
                ;;
            --enable-delivery)
                select_operation enable-delivery
                shift
                ;;
            --rollback)
                select_operation rollback
                shift
                ;;
            --identity-file)
                (($# >= 2)) || fail "--identity-file requires a value."
                identity_file="$2"
                shift 2
                ;;
            --client-secret-stdin)
                read_secret_from_stdin=true
                shift
                ;;
            --apply)
                apply_changes=true
                shift
                ;;
            --help|-h)
                usage
                exit 0
                ;;
            *)
                fail "Unknown argument."
                ;;
        esac
    done

    validate_cli
    if [[ "$apply_changes" == "false" ]]; then
        if [[ "$operation" == "overview" ]]; then
            print_overview_plan
        else
            print_operation_plan
        fi
        return
    fi

    case "$operation" in
        activate) run_activate ;;
        enable-producer) run_enable_producer ;;
        seal-event) run_seal_event ;;
        enable-delivery) run_enable_delivery ;;
        rollback) run_explicit_rollback ;;
        *) fail "The requested operation is unsupported." ;;
    esac
}

if [[ "${BASH_SOURCE[0]:-$0}" == "$0" ]]; then
    main "$@"
fi
