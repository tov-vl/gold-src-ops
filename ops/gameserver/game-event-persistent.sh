#!/usr/bin/env bash

set -Eeuo pipefail
IFS=$'\n\t'
umask 077

readonly POLICY_SCHEMA_VERSION="1"
readonly POLICY_ID="persistent-gameplay-v1"
readonly GAME_SERVICE_NAME="goldsrcops-gameserver.service"
readonly AGENT_SERVICE_NAME="goldsrcops-game-event-agent.service"
readonly EXPECTED_PROFILE_ID="public-classic-v1"
readonly EXPECTED_AUTOSTART_POLICY_ID="guarded-autostart-v1"
readonly EXPECTED_SPOOL_LIMIT="1000"
readonly GUARD_REJECTED_EXIT=78

script_path="$(realpath -e -- "${BASH_SOURCE[0]}")"
script_directory="$(dirname "$script_path")"
pilot_activator="${GOLDSRCOPS_PILOT_ACTIVATOR:-$script_directory/game-event-pilot-activate.sh}"
autostart_workflow="${GOLDSRCOPS_AUTOSTART_WORKFLOW:-$script_directory/guarded-autostart.sh}"

configuration_directory="${GOLDSRCOPS_CONFIGURATION_DIRECTORY:-/etc/goldsrcops/gameserver}"
installation_directory="${GOLDSRCOPS_INSTALLATION_DIRECTORY:-/opt/goldsrcops/gameserver}"
service_home="${GOLDSRCOPS_SERVICE_HOME:-/var/lib/goldsrc}"
backup_root="${GOLDSRCOPS_BACKUP_ROOT:-/var/backups/goldsrcops/gameserver/persistent-gameplay}"
guarded_backup_root="$(dirname "$backup_root")"
libexec_directory="${GOLDSRCOPS_LIBEXEC_DIRECTORY:-/usr/local/libexec}"
systemd_directory="${GOLDSRCOPS_SYSTEMD_DIRECTORY:-/etc/systemd/system}"

prepared_marker="$configuration_directory/host-prepared"
runtime_marker="$configuration_directory/runtime-installed"
runtime_enabled_marker="$configuration_directory/runtime-enabled"
active_profile_marker="$configuration_directory/managed-profile-active"
autostart_marker="$configuration_directory/guarded-autostart-active"
pilot_marker="$configuration_directory/game-event-pilot-installed"
pilot_enabled_marker="$configuration_directory/game-event-pilot-enabled"
persistent_marker="$configuration_directory/game-event-persistent-active"
environment_file="$configuration_directory/game-event-agent.env"
client_secret_file="$configuration_directory/secrets/game-event-agent-client-secret"
activation_state_file="$installation_directory/game-event-pilot/activation/state"
pilot_releases_directory="$installation_directory/game-event-pilot/releases"
transition_lock="$configuration_directory/game-event-pilot-install.lock"

game_unit_file="$systemd_directory/$GAME_SERVICE_NAME"
agent_unit_file="$systemd_directory/$AGENT_SERVICE_NAME"
game_drop_in_directory="$systemd_directory/$GAME_SERVICE_NAME.d"
agent_drop_in_directory="$systemd_directory/$AGENT_SERVICE_NAME.d"
autostart_drop_in="$game_drop_in_directory/20-guarded-autostart.conf"
persistent_game_drop_in="$game_drop_in_directory/30-game-event-persistent.conf"
persistent_agent_drop_in="$agent_drop_in_directory/30-game-event-persistent.conf"
installed_autostart_guard="$libexec_directory/goldsrcops-gameserver-boot-guard"
installed_persistent_guard="$libexec_directory/goldsrcops-game-event-persistent"

state_root="$service_home/game-event-agent"
game_root="$service_home/server"
liblist_file="$game_root/cstrike/liblist.gam"
live_addons_root="$game_root/cstrike/addons"
live_metamod_root="$live_addons_root/metamod"
live_amxx_root="$live_addons_root/amxmodx"
producer_configuration="$live_amxx_root/configs/amxx.cfg"
spool_root="$live_amxx_root/data/goldsrcops-spool"

operation="overview"
operation_selected=false
apply_changes=false
read_secret_from_stdin=false
identity_file=""
prepared_operator_user=""
service_user=""
service_group=""
pilot_bundle_sha256=""
pilot_manifest_sha256=""
pilot_release_path=""
pilot_manifest_file=""
backup_name=""
backup_directory=""
staging_directory=""
external_gate_fifo=""
external_gate_timeout=300
transition_started=false
pilot_activation_present=false

marker_backup_name=""
marker_guard_sha256=""
marker_game_drop_in_sha256=""
marker_agent_drop_in_sha256=""
marker_pilot_marker_sha256=""
marker_pilot_gate_sha256=""
marker_activation_state_sha256=""
marker_manifest_sha256=""
marker_liblist_sha256=""
marker_environment_sha256=""
marker_producer_sha256=""
marker_game_unit_sha256=""
marker_agent_unit_sha256=""

usage() {
    cat <<'EOF'
Usage:
  game-event-persistent.sh
  game-event-persistent.sh --activate --identity-file <absolute-path>
  game-event-persistent.sh --verify
  game-event-persistent.sh --rollback

Every mutating operation is plan-only unless --apply is supplied. Activation
also requires the existing machine identity and the OAuth client secret through
redirected stdin:

  <secret-producer> | sudo --preserve-env=SSH_CONNECTION \
    bash ./game-event-persistent.sh \
      --activate \
      --identity-file /root/game-event-agent.identity \
      --client-secret-stdin \
      --apply

Activation requires the accepted public-classic-v1 and guarded-autostart-v1
boundary. After spool-only startup it waits up to five minutes for a root-only
external A2S and authenticated RCON gate receipt. Without that receipt it rolls
back before enabling producer or delivery. After the gate it installs a
hash-bound persistent guard and enables the game and agent independently across
boot. Rollback disables new intake first, moves the complete local queue and
spool into the owner-only rollback record, restores the pilot baseline, and
reinstates the exact pre-activation guarded game policy.
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

validate_sha256() {
    local value="$1"
    [[ "$value" =~ ^[0-9a-f]{64}$ ]] || fail "A recorded SHA-256 is invalid."
}

validate_user_name() {
    local value="$1"
    [[ "$value" =~ ^[a-z_][a-z0-9_-]{0,31}$ && "$value" != root ]] ||
        fail "The recorded service account is invalid."
}

sha256_file() {
    sha256sum "$1" | awk '{ print $1 }'
}

validate_file_metadata() {
    local path="$1"
    local owner="$2"
    local group="$3"
    local mode="$4"

    [[ -f "$path" && ! -L "$path" ]] || fail "A required file is missing or unsafe."
    [[ "$(stat -c '%U:%G:%a' "$path")" == "$owner:$group:$mode" ]] ||
        fail "A required file owner or mode has drifted."
}

validate_directory_metadata() {
    local path="$1"
    local owner="$2"
    local group="$3"
    local mode="$4"

    [[ -d "$path" && ! -L "$path" ]] || fail "A required directory is missing or unsafe."
    [[ "$(stat -c '%U:%G:%a' "$path")" == "$owner:$group:$mode" ]] ||
        fail "A required directory owner or mode has drifted."
}

verify_sha256() {
    local path="$1"
    local expected="$2"
    validate_sha256 "$expected"
    [[ -f "$path" && ! -L "$path" ]] || fail "A hashed file is missing or unsafe."
    [[ "$(sha256_file "$path")" == "$expected" ]] || fail "A file hash has drifted."
}

marker_value() {
    local path="$1"
    local key="$2"
    local count value

    [[ -f "$path" && ! -L "$path" ]] || fail "A required marker is missing or unsafe."
    count="$(grep -Ec "^${key}=" "$path")"
    [[ "$count" == 1 ]] || fail "A required marker key is missing or duplicated."
    value="$(grep -E "^${key}=" "$path" | cut -d= -f2-)"
    [[ -n "$value" ]] || fail "A required marker value is empty."
    printf '%s\n' "$value"
}

guarded_backup_name() {
    local name
    name="$(marker_value "$1" backup_name)" || return 1
    [[ "$name" =~ ^guarded-autostart-[0-9]{8}T[0-9]{6}Z-[A-Za-z0-9]{6}$ ]] ||
        { fail "The guarded-autostart backup reference is invalid."; return 1; }
    printf '%s\n' "$name"
}

verify_guarded_backup() {
    local directory="$1"
    local marker="$2"
    validate_directory_metadata "$directory" root root 700 || return 1
    validate_file_metadata "$directory/runtime-enabled" root root 600 || return 1
    validate_file_metadata "$directory/managed-profile-active" root root 600 || return 1
    verify_sha256 "$directory/runtime-enabled" "$(marker_value "$marker" baseline_runtime_enabled_sha256)" || return 1
    verify_sha256 "$directory/managed-profile-active" "$(marker_value "$marker" baseline_active_profile_sha256)" || return 1
}

read_prepared_marker() {
    [[ "$(marker_value "$prepared_marker" schema_version)" == 1 ]] ||
        fail "The host marker schema is unsupported."
    prepared_operator_user="$(marker_value "$prepared_marker" operator_user)"
    service_user="$(marker_value "$prepared_marker" service_user)"
    validate_user_name "$prepared_operator_user"
    validate_user_name "$service_user"
    service_group="$(id -gn "$service_user")"
}

read_pilot_marker() {
    [[ "$(marker_value "$pilot_marker" schema_version)" == 1 ]] ||
        fail "The pilot marker schema is unsupported."
    pilot_bundle_sha256="$(marker_value "$pilot_marker" bundle_sha256)"
    pilot_manifest_sha256="$(marker_value "$pilot_marker" manifest_sha256)"
    validate_sha256 "$pilot_bundle_sha256"
    validate_sha256 "$pilot_manifest_sha256"
    pilot_release_path="$pilot_releases_directory/$pilot_bundle_sha256"
    pilot_manifest_file="$pilot_release_path/manifest.json"
    verify_sha256 "$pilot_manifest_file" "$pilot_manifest_sha256"
}

read_persistent_marker() {
    local key value
    local schema_version="" policy_id=""
    declare -A seen=()

    marker_backup_name=""
    marker_guard_sha256=""
    marker_game_drop_in_sha256=""
    marker_agent_drop_in_sha256=""
    marker_pilot_marker_sha256=""
    marker_pilot_gate_sha256=""
    marker_activation_state_sha256=""
    marker_manifest_sha256=""
    marker_liblist_sha256=""
    marker_environment_sha256=""
    marker_producer_sha256=""
    marker_game_unit_sha256=""
    marker_agent_unit_sha256=""

    while IFS='=' read -r key value || [[ -n "${key:-}${value:-}" ]]; do
        [[ -n "${key:-}" && -n "${value:-}" && -z "${seen[$key]+x}" ]] ||
            fail "The persistent marker is malformed."
        seen[$key]=1
        case "$key" in
            schema_version) schema_version="$value" ;;
            policy_id) policy_id="$value" ;;
            backup_name) marker_backup_name="$value" ;;
            guard_sha256) marker_guard_sha256="$value" ;;
            game_drop_in_sha256) marker_game_drop_in_sha256="$value" ;;
            agent_drop_in_sha256) marker_agent_drop_in_sha256="$value" ;;
            pilot_marker_sha256) marker_pilot_marker_sha256="$value" ;;
            pilot_gate_sha256) marker_pilot_gate_sha256="$value" ;;
            activation_state_sha256) marker_activation_state_sha256="$value" ;;
            manifest_sha256) marker_manifest_sha256="$value" ;;
            liblist_sha256) marker_liblist_sha256="$value" ;;
            environment_sha256) marker_environment_sha256="$value" ;;
            producer_sha256) marker_producer_sha256="$value" ;;
            game_unit_sha256) marker_game_unit_sha256="$value" ;;
            agent_unit_sha256) marker_agent_unit_sha256="$value" ;;
            *) fail "The persistent marker contains an unknown key." ;;
        esac
    done < "$persistent_marker"

    [[ "$schema_version" == "$POLICY_SCHEMA_VERSION" && "$policy_id" == "$POLICY_ID" &&
        "${#seen[@]}" -eq 15 ]] || fail "The persistent marker contract is invalid."
    [[ "$marker_backup_name" =~ ^persistent-gameplay-[0-9]{8}T[0-9]{6}Z-[A-Za-z0-9]{6}$ ]] ||
        fail "The persistent rollback reference is invalid."
    local digest
    for digest in \
        "$marker_guard_sha256" \
        "$marker_game_drop_in_sha256" \
        "$marker_agent_drop_in_sha256" \
        "$marker_pilot_marker_sha256" \
        "$marker_pilot_gate_sha256" \
        "$marker_activation_state_sha256" \
        "$marker_manifest_sha256" \
        "$marker_liblist_sha256" \
        "$marker_environment_sha256" \
        "$marker_producer_sha256" \
        "$marker_game_unit_sha256" \
        "$marker_agent_unit_sha256"; do
        validate_sha256 "$digest"
    done
    backup_name="$marker_backup_name"
    backup_directory="$backup_root/$backup_name"
}

require_unit_state() {
    local service="$1"
    local expected_active="$2"
    local expected_enabled="$3"
    [[ "$(systemctl is-active "$service" 2>/dev/null || true)" == "$expected_active" ]] ||
        fail "$service is not $expected_active."
    [[ "$(systemctl is-enabled "$service" 2>/dev/null || true)" == "$expected_enabled" ]] ||
        fail "$service is not $expected_enabled."
}

validate_reviewed_script() {
    local path="$1"
    [[ "$(realpath -e -- "$path")" == "$path" ]] ||
        fail "A reviewed workflow path is not canonical."
    validate_file_metadata "$path" root root 700
}

render_producer_configuration() {
    local source="$1"
    local destination="$2"
    local enabled="$3"

    [[ "$(grep -Ec '^[[:space:]]*goldsrcops_events_enabled[[:space:]]+[01]([[:space:]]|$)' "$source")" == 1 ]] ||
        fail "The producer gate is missing or duplicated."
    [[ "$(grep -Ec "^[[:space:]]*goldsrcops_spool_max_pending[[:space:]]+$EXPECTED_SPOOL_LIMIT([[:space:]]|$)" "$source")" == 1 ]] ||
        fail "The producer spool limit has drifted."
    awk -v enabled="$enabled" '
        /^[[:space:]]*goldsrcops_events_enabled[[:space:]]+[01]([[:space:]]|$)/ {
            print "goldsrcops_events_enabled " enabled
            next
        }
        { print }
    ' "$source" > "$destination"
    chmod 0640 "$destination"
}

render_delivery_environment() {
    local source="$1"
    local destination="$2"
    local enabled="$3"

    [[ "$(grep -Fc 'GameEventAgent__Spool__Enabled=true' "$source")" == 1 ]] ||
        fail "Spool import is not enabled exactly once."
    [[ "$(grep -Ec '^GameEventAgent__Delivery__Enabled=(true|false)$' "$source")" == 1 ]] ||
        fail "The delivery gate is missing or duplicated."
    sed -E "s/^GameEventAgent__Delivery__Enabled=(true|false)$/GameEventAgent__Delivery__Enabled=$enabled/" \
        "$source" > "$destination"
    chmod 0640 "$destination"
    ! grep -Eiq 'secret|password|bearer|access[_-]?token' "$destination" ||
        fail "The rendered environment contains secret-shaped configuration."
}

render_game_drop_in() {
    local destination="$1"
    cat > "$destination" <<EOF
[Service]
ExecCondition=
ExecCondition=$installed_persistent_guard --guard-game
EOF
    chmod 0644 "$destination"
}

render_agent_drop_in() {
    local destination="$1"
    cat > "$destination" <<EOF
[Service]
ExecCondition=
ExecCondition=!$installed_persistent_guard --guard-agent
EOF
    chmod 0644 "$destination"
}

verify_overlay_payload() {
    local payload_path payload_sha payload_mode destination expected_sha expected_mode
    while IFS=$'\t' read -r payload_path payload_sha payload_mode; do
        destination="$game_root/${payload_path#gameserver/}"
        expected_sha="$payload_sha"
        if [[ "$destination" == "$producer_configuration" ]]; then
            expected_sha="$marker_producer_sha256"
        fi
        expected_mode="${payload_mode#0}"
        verify_sha256 "$destination" "$expected_sha"
        validate_file_metadata "$destination" root "$service_group" "$expected_mode"
    done < <(jq -r '.payload[] | select(.path | startswith("gameserver/")) | [.path, .sha256, .mode] | @tsv' "$pilot_manifest_file")
}

verify_agent_payload() {
    local payload_path payload_sha payload_mode destination expected_mode
    while IFS=$'\t' read -r payload_path payload_sha payload_mode; do
        destination="$pilot_release_path/$payload_path"
        expected_mode="${payload_mode#0}"
        verify_sha256 "$destination" "$payload_sha"
        validate_file_metadata "$destination" root "$service_group" "$expected_mode"
    done < <(jq -r '.payload[] | select(.path | startswith("agent/")) | [.path, .sha256, .mode] | @tsv' "$pilot_manifest_file")
}

prepare_persistent_verification() {
    read_prepared_marker
    read_pilot_marker
    validate_file_metadata "$persistent_marker" root "$service_group" 640
    read_persistent_marker

    verify_sha256 "$installed_persistent_guard" "$marker_guard_sha256"
    verify_sha256 "$pilot_marker" "$marker_pilot_marker_sha256"
    verify_sha256 "$pilot_manifest_file" "$marker_manifest_sha256"
    validate_file_metadata "$installed_persistent_guard" root root 755
    validate_file_metadata "$pilot_marker" root "$service_group" 640
}

verify_persistent_game_files() {
    verify_sha256 "$persistent_game_drop_in" "$marker_game_drop_in_sha256"
    verify_sha256 "$liblist_file" "$marker_liblist_sha256"
    verify_sha256 "$producer_configuration" "$marker_producer_sha256"
    verify_sha256 "$game_unit_file" "$marker_game_unit_sha256"

    validate_file_metadata "$persistent_game_drop_in" root root 644
    validate_file_metadata "$producer_configuration" root "$service_group" 640
    grep -Fxq 'goldsrcops_events_enabled 1' "$producer_configuration" ||
        fail "Persistent producer intake is not enabled."
    grep -Fxq "goldsrcops_spool_max_pending $EXPECTED_SPOOL_LIMIT" "$producer_configuration" ||
        fail "Persistent producer intake is not bounded by the reviewed value."
    grep -Fxq "ExecCondition=$installed_persistent_guard --guard-game" "$persistent_game_drop_in" ||
        fail "The game service is not bound to the persistent guard."
    if grep -Eq "^(Requires|PartOf)=.*$AGENT_SERVICE_NAME" "$game_unit_file" "$persistent_game_drop_in"; then
        fail "The game service is coupled to the agent service."
    fi
    verify_overlay_payload
}

verify_persistent_agent_files() {
    verify_sha256 "$persistent_agent_drop_in" "$marker_agent_drop_in_sha256"
    verify_sha256 "$pilot_enabled_marker" "$marker_pilot_gate_sha256"
    verify_sha256 "$activation_state_file" "$marker_activation_state_sha256"
    verify_sha256 "$environment_file" "$marker_environment_sha256"
    verify_sha256 "$agent_unit_file" "$marker_agent_unit_sha256"

    validate_file_metadata "$persistent_agent_drop_in" root root 644
    validate_file_metadata "$pilot_enabled_marker" root "$service_group" 640
    validate_file_metadata "$activation_state_file" root root 600
    validate_file_metadata "$client_secret_file" root root 600
    validate_file_metadata "$environment_file" root "$service_group" 640
    grep -Fxq 'GameEventAgent__Spool__Enabled=true' "$environment_file" ||
        fail "Persistent spool import is not enabled."
    grep -Fxq 'GameEventAgent__Delivery__Enabled=true' "$environment_file" ||
        fail "Persistent delivery is not enabled."
    ! grep -Eiq 'secret|password|bearer|access[_-]?token' "$environment_file" ||
        fail "The persistent environment contains secret-shaped configuration."
    grep -Fxq "ExecCondition=!$installed_persistent_guard --guard-agent" "$persistent_agent_drop_in" ||
        fail "The agent service is not bound to the persistent guard."
    if grep -Eq "^(Requires|PartOf)=.*$GAME_SERVICE_NAME" "$agent_unit_file" "$persistent_agent_drop_in"; then
        fail "The agent service is coupled to the game service."
    fi
    verify_agent_payload
}

verify_persistent_files() {
    prepare_persistent_verification
    verify_persistent_game_files
    verify_persistent_agent_files
}

capture_agent_status() {
    local -a environment_arguments=()
    local key value status_output expected_delivery

    while IFS='=' read -r key value || [[ -n "${key:-}${value:-}" ]]; do
        if [[ ! "$key" =~ ^[A-Za-z_][A-Za-z0-9_]*$ || -z "$value" ]]; then
            fail "The agent environment is malformed."
            return 1
        fi
        environment_arguments+=("$key=$value")
    done < "$environment_file"
    if [[ "$(grep -Ec '^GameEventAgent__Delivery__Enabled=(true|false)$' "$environment_file")" != 1 ]]; then
        fail "The agent delivery gate is missing or duplicated."
        return 1
    fi
    expected_delivery=false
    if grep -Fxq 'GameEventAgent__Delivery__Enabled=true' "$environment_file"; then
        expected_delivery=true
        # Status parses this path but never reads the root-only secret file.
        environment_arguments+=("GameEventAgent__Delivery__OAuth__ClientSecretFile=$client_secret_file")
    fi
    environment_arguments+=("DOTNET_BUNDLE_EXTRACT_BASE_DIR=$state_root/dotnet-bundle")
    if ! status_output="$(runuser -u "$service_user" -- env "${environment_arguments[@]}" \
        "$pilot_release_path/agent/GoldSrcOps.GameEventAgent" status --json)"; then
        fail "The aggregate agent status command failed."
        return 1
    fi
    if ! jq -s -e --argjson expectedDelivery "$expected_delivery" '
        length == 1 and (.[0] |
            .schemaVersion == 1 and
            .deliveryEnabled == $expectedDelivery and
            (.queue.pending | type == "number") and
            (.queue.inFlight | type == "number") and
            (.queue.deadLetter | type == "number") and
            (.spool.ready | type == "number") and
            (.spool.processing | type == "number") and
            (.spool.rejected | type == "number"))
    ' <<< "$status_output" >/dev/null; then
        fail "The aggregate agent status contract is invalid."
        return 1
    fi
    printf '%s\n' "$status_output"
}

require_settled_activation_status() {
    local status_output="$1"
    if ! jq -s -e '
        length == 1 and (.[0] |
            .queue.pending == 0 and
            .queue.inFlight == 0 and
            .queue.deadLetter == 0 and
            .spool.ready == 0 and
            .spool.processing == 0 and
            .spool.rejected == 0)
    ' <<< "$status_output" >/dev/null; then
        fail "The persistent activation boundary is not settled and empty."
        return 1
    fi
}

require_settled_agent_status() {
    local status_output
    status_output="$(capture_agent_status)" || return 1
    require_settled_activation_status "$status_output"
}

write_baseline_record() {
    local destination="$1"
    cat > "$destination" <<EOF
schema_version=1
backup_name=$backup_name
autostart_guard_sha256=$(sha256_file "$installed_autostart_guard")
autostart_drop_in_sha256=$(sha256_file "$autostart_drop_in")
autostart_marker_sha256=$(sha256_file "$autostart_marker")
runtime_enabled_sha256=$(sha256_file "$runtime_enabled_marker")
active_profile_sha256=$(sha256_file "$active_profile_marker")
EOF
    chmod 0600 "$destination"
}

verify_baseline_record() {
    local baseline="$backup_directory/baseline"
    validate_directory_metadata "$backup_directory" root root 700 || return 1
    validate_file_metadata "$baseline" root root 600 || return 1
    [[ "$(marker_value "$baseline" schema_version)" == 1 ]] ||
        { fail "The persistent baseline schema is unsupported."; return 1; }
    [[ "$(marker_value "$baseline" backup_name)" == "$backup_name" ]] ||
        { fail "The persistent baseline reference has drifted."; return 1; }
    validate_file_metadata "$backup_directory/autostart-guard" root root 755 || return 1
    validate_file_metadata "$backup_directory/autostart-drop-in.conf" root root 644 || return 1
    validate_file_metadata "$backup_directory/autostart-marker" root "$service_group" 640 || return 1
    validate_file_metadata "$backup_directory/runtime-enabled" root "$service_group" 640 || return 1
    validate_file_metadata "$backup_directory/managed-profile-active" root "$service_group" 640 || return 1
    verify_sha256 "$backup_directory/autostart-guard" "$(marker_value "$baseline" autostart_guard_sha256)" || return 1
    verify_sha256 "$backup_directory/autostart-drop-in.conf" "$(marker_value "$baseline" autostart_drop_in_sha256)" || return 1
    verify_sha256 "$backup_directory/autostart-marker" "$(marker_value "$baseline" autostart_marker_sha256)" || return 1
    verify_sha256 "$backup_directory/runtime-enabled" "$(marker_value "$baseline" runtime_enabled_sha256)" || return 1
    verify_sha256 "$backup_directory/managed-profile-active" "$(marker_value "$baseline" active_profile_sha256)" || return 1
    guarded_backup_name "$backup_directory/autostart-marker" >/dev/null || return 1
    verify_guarded_backup "$backup_directory/guarded-autostart-backup" "$backup_directory/autostart-marker" || return 1
}

capture_baseline() {
    local random_suffix guarded_name
    guarded_name="$(guarded_backup_name "$autostart_marker")" || return 1
    validate_directory_metadata "$guarded_backup_root" root root 700 || return 1
    verify_guarded_backup "$guarded_backup_root/$guarded_name" "$autostart_marker" || return 1
    printf -v random_suffix '%05d%05d' "$RANDOM" "$RANDOM"
    backup_name="persistent-gameplay-$(date -u +%Y%m%dT%H%M%SZ)-${random_suffix:0:6}"
    backup_directory="$backup_root/$backup_name"
    install -d -m 0700 -o root -g root "$backup_root" "$backup_directory"
    cp -a -- "$installed_autostart_guard" "$backup_directory/autostart-guard"
    cp -a -- "$autostart_drop_in" "$backup_directory/autostart-drop-in.conf"
    cp -a -- "$autostart_marker" "$backup_directory/autostart-marker"
    cp -a -- "$runtime_enabled_marker" "$backup_directory/runtime-enabled"
    cp -a -- "$active_profile_marker" "$backup_directory/managed-profile-active"
    cp -a -- "$guarded_backup_root/$guarded_name" "$backup_directory/guarded-autostart-backup" || return 1
    write_baseline_record "$backup_directory/baseline"
    verify_baseline_record
}

prepare_staging() {
    staging_directory="$(mktemp -d "$configuration_directory/.game-event-persistent.XXXXXX")"
    chmod 0700 "$staging_directory"
}

write_persistent_marker() {
    local temporary_marker="$configuration_directory/.game-event-persistent-active.$$"
    cat > "$temporary_marker" <<EOF
schema_version=$POLICY_SCHEMA_VERSION
policy_id=$POLICY_ID
backup_name=$backup_name
guard_sha256=$(sha256_file "$installed_persistent_guard")
game_drop_in_sha256=$(sha256_file "$persistent_game_drop_in")
agent_drop_in_sha256=$(sha256_file "$persistent_agent_drop_in")
pilot_marker_sha256=$(sha256_file "$pilot_marker")
pilot_gate_sha256=$(sha256_file "$pilot_enabled_marker")
activation_state_sha256=$(sha256_file "$activation_state_file")
manifest_sha256=$(sha256_file "$pilot_manifest_file")
liblist_sha256=$(sha256_file "$liblist_file")
environment_sha256=$(sha256_file "$environment_file")
producer_sha256=$(sha256_file "$producer_configuration")
game_unit_sha256=$(sha256_file "$game_unit_file")
agent_unit_sha256=$(sha256_file "$agent_unit_file")
EOF
    chown root:"$service_group" "$temporary_marker"
    chmod 0640 "$temporary_marker"
    mv -- "$temporary_marker" "$persistent_marker"
}

require_apply_environment() {
    local command
    ((EUID == 0)) || fail "--apply must run as root."
    for command in awk cat chmod chown cp cut date dirname env find flock getent grep id install jq \
        mkfifo mktemp mv od ps realpath rm runuser sed sha256sum stat systemctl systemd-analyze tr uname; do
        require_command "$command"
    done
    [[ -r /etc/os-release ]] || fail "The operating-system identity is unavailable."
    # shellcheck disable=SC1091
    source /etc/os-release
    [[ "${ID:-}" == ubuntu && "${VERSION_ID:-}" == 24.04 ]] ||
        fail "Persistent activation supports Ubuntu 24.04 only."
    [[ "$(uname -m)" == x86_64 ]] || fail "Persistent activation requires x86-64."
    [[ "$(ps -p 1 -o comm= | tr -d '[:space:]')" == systemd ]] ||
        fail "Persistent activation requires systemd as PID 1."
    read_prepared_marker
    read_pilot_marker
    [[ "${SUDO_USER:-}" == "$prepared_operator_user" ]] ||
        fail "Run --apply through sudo from the operator recorded by host bootstrap."
    [[ -n "${SSH_CONNECTION:-}" ]] || fail "Apply requires preserved SSH_CONNECTION metadata."
    validate_reviewed_script "$pilot_activator"
    validate_reviewed_script "$autostart_workflow"
    validate_directory_metadata "$configuration_directory" root "$service_group" 750
    validate_directory_metadata "$configuration_directory/secrets" root "$service_group" 710
    validate_directory_metadata "$installation_directory" root "$service_group" 750
    validate_file_metadata "$prepared_marker" root "$service_group" 640
    validate_file_metadata "$runtime_marker" root "$service_group" 640
    validate_file_metadata "$pilot_marker" root "$service_group" 640
    validate_file_metadata "$agent_unit_file" root root 644
}

require_accepted_boundary() {
    [[ ! -e "$persistent_marker" && ! -e "$persistent_game_drop_in" &&
        ! -e "$persistent_agent_drop_in" && ! -e "$installed_persistent_guard" ]] ||
        fail "A persistent activation or recovery boundary already exists."
    [[ "$(marker_value "$active_profile_marker" profile_id)" == "$EXPECTED_PROFILE_ID" ]] ||
        fail "The accepted public game profile is not active."
    [[ "$(marker_value "$autostart_marker" policy_id)" == "$EXPECTED_AUTOSTART_POLICY_ID" ]] ||
        fail "The accepted guarded-autostart policy is not active."
    require_unit_state "$GAME_SERVICE_NAME" active enabled
    require_unit_state "$AGENT_SERVICE_NAME" inactive disabled
    "$autostart_workflow" --verify >/dev/null
}

acquire_lock() {
    exec 9>"$transition_lock"
    flock --nonblock 9 || fail "Another game-event transition is already in progress."
    chown root:"$service_group" "$transition_lock"
    chmod 0640 "$transition_lock"
}

install_persistent_policy() {
    install -m 0755 -o root -g root "$script_path" "$installed_persistent_guard"
    install -d -m 0755 -o root -g root "$game_drop_in_directory" "$agent_drop_in_directory"
    install -m 0644 -o root -g root "$staging_directory/game.conf" "$persistent_game_drop_in"
    install -m 0644 -o root -g root "$staging_directory/agent.conf" "$persistent_agent_drop_in"
    install -m 0640 -o root -g "$service_group" "$staging_directory/amxx.cfg" "$producer_configuration"
    install -m 0640 -o root -g "$service_group" "$staging_directory/agent.env" "$environment_file"
    write_persistent_marker
    systemctl daemon-reload
    systemd-analyze verify "$game_unit_file" "$agent_unit_file" >/dev/null
}

restore_guarded_backup() {
    local name target staging
    name="$(guarded_backup_name "$backup_directory/autostart-marker")" || return 1
    target="$guarded_backup_root/$name"
    validate_directory_metadata "$guarded_backup_root" root root 700 || return 1
    verify_guarded_backup "$backup_directory/guarded-autostart-backup" "$backup_directory/autostart-marker" || return 1
    if [[ -e "$target" || -L "$target" ]]; then
        verify_guarded_backup "$target" "$backup_directory/autostart-marker" || return 1
        return 0
    fi
    staging="$(mktemp -d "$guarded_backup_root/.guarded-autostart-restore.XXXXXX")" || return 1
    cp -a -- "$backup_directory/guarded-autostart-backup/." "$staging/" || return 1
    verify_guarded_backup "$staging" "$backup_directory/autostart-marker" || return 1
    mv -T -n -- "$staging" "$target" || return 1
    verify_guarded_backup "$target" "$backup_directory/autostart-marker" || return 1
    if [[ -d "$staging" ]]; then
        rm -rf -- "$staging" || return 1
    fi
}

restore_prior_policy() {
    verify_baseline_record || return 1
    restore_guarded_backup || return 1
    rm -f -- \
        "$persistent_marker" \
        "$persistent_game_drop_in" \
        "$persistent_agent_drop_in" \
        "$installed_persistent_guard" \
        "$autostart_drop_in" \
        "$autostart_marker" \
        "$runtime_enabled_marker" \
        "$active_profile_marker" || return 1
    install -d -m 0755 -o root -g root "$game_drop_in_directory" || return 1
    cp -a -- "$backup_directory/autostart-guard" "$installed_autostart_guard" || return 1
    cp -a -- "$backup_directory/autostart-drop-in.conf" "$autostart_drop_in" || return 1
    cp -a -- "$backup_directory/autostart-marker" "$autostart_marker" || return 1
    cp -a -- "$backup_directory/runtime-enabled" "$runtime_enabled_marker" || return 1
    cp -a -- "$backup_directory/managed-profile-active" "$active_profile_marker" || return 1
    systemctl daemon-reload || return 1
    systemctl disable "$AGENT_SERVICE_NAME" >/dev/null || return 1
    systemctl enable "$GAME_SERVICE_NAME" >/dev/null || return 1
    if ! systemctl is-active --quiet "$GAME_SERVICE_NAME"; then
        systemctl start "$GAME_SERVICE_NAME" || return 1
    fi
    require_unit_state "$GAME_SERVICE_NAME" active enabled || return 1
    require_unit_state "$AGENT_SERVICE_NAME" inactive disabled || return 1
    "$installed_autostart_guard" --verify >/dev/null || return 1
}

seal_local_evidence() {
    local evidence_directory="$backup_directory/evidence"
    [[ ! -e "$evidence_directory" ]] || fail "Rollback evidence was already sealed."
    install -d -m 0700 -o root -g root "$evidence_directory" ||
        fail "The rollback evidence directory could not be created."
    if [[ -f "$environment_file" && -f "$pilot_release_path/agent/GoldSrcOps.GameEventAgent" ]]; then
        if ! capture_agent_status > "$evidence_directory/status-before-rollback.json"; then
            rm -f -- "$evidence_directory/status-before-rollback.json" || return 1
            log "ROLLBACK_WARNING: aggregate status unavailable; durable queue and spool evidence will still be sealed."
        else
            chmod 0600 "$evidence_directory/status-before-rollback.json" || return 1
        fi
    fi
    [[ -d "$state_root" && ! -L "$state_root" ]] || fail "Agent state is unavailable for rollback sealing."
    [[ -d "$spool_root" && ! -L "$spool_root" ]] || fail "Producer spool is unavailable for rollback sealing."
    [[ "$(stat -c '%d' "$state_root")" == "$(stat -c '%d' "$evidence_directory")" &&
        "$(stat -c '%d' "$spool_root")" == "$(stat -c '%d' "$evidence_directory")" ]] ||
        fail "Evidence cannot be moved atomically to the rollback record."
    mv -- "$state_root" "$evidence_directory/agent-state" ||
        fail "Agent state could not be moved into the rollback record."
    if ! mv -- "$spool_root" "$evidence_directory/producer-spool"; then
        mv -- "$evidence_directory/agent-state" "$state_root" ||
            fail "Producer spool sealing failed and agent state could not be restored."
        fail "Producer spool could not be moved into the rollback record."
    fi
    install -d -m 0750 -o "$service_user" -g "$service_group" \
        "$state_root" "$state_root/queue" "$state_root/dotnet-bundle" ||
        fail "An empty pilot rollback state boundary could not be recreated."
    return 0
}

rollback_transition() {
    trap - ERR HUP INT TERM EXIT
    local rollback_failed=false

    if [[ -n "$external_gate_fifo" ]]; then
        rm -f -- "$external_gate_fifo" ||
            log "ROLLBACK_WARNING: the external gate could not be removed."
    fi

    systemctl disable "$AGENT_SERVICE_NAME" >/dev/null 2>&1 || rollback_failed=true
    systemctl stop "$AGENT_SERVICE_NAME" >/dev/null 2>&1 || rollback_failed=true
    systemctl disable "$GAME_SERVICE_NAME" >/dev/null 2>&1 || rollback_failed=true
    systemctl stop "$GAME_SERVICE_NAME" >/dev/null 2>&1 || rollback_failed=true
    [[ "$rollback_failed" == false ]] || {
        log "ROLLBACK_INCOMPLETE: services could not be stopped and disabled; recovery state was retained."
        return 1
    }

    if [[ -f "$producer_configuration" && -n "$staging_directory" ]]; then
        render_producer_configuration "$producer_configuration" "$staging_directory/amxx-disabled.cfg" 0 || return 1
        install -m 0640 -o root -g "$service_group" \
            "$staging_directory/amxx-disabled.cfg" "$producer_configuration" || return 1
    fi

    if [[ -d "$(dirname "$activation_state_file")" && -f "$activation_state_file" ]]; then
        pilot_activation_present=true
        seal_local_evidence || {
            log "ROLLBACK_INCOMPLETE: durable evidence could not be sealed; active recovery inputs were retained."
            return 1
        }
        rm -f -- "$persistent_marker" "$persistent_game_drop_in" "$persistent_agent_drop_in" "$installed_persistent_guard"
        systemctl daemon-reload >/dev/null 2>&1 || true
        if ! GOLDSRCOPS_INHERITED_ACTIVATION_LOCK_FD=9 "$pilot_activator" --rollback --apply; then
            log "ROLLBACK_INCOMPLETE: pilot rollback failed; sealed evidence and recovery inputs were retained."
            return 1
        fi
    fi

    restore_prior_policy || return 1
    printf 'schema_version=1\nrollback=complete\n' > "$backup_directory/rollback-complete" || return 1
    chmod 0600 "$backup_directory/rollback-complete" || return 1
    log "ROLLED_BACK: persistent telemetry removed and exact guarded public game policy restored."
    log "EVIDENCE_STATE: owner-only queue and spool snapshot retained with the rollback record."
    log "SERVICE_STATE: game active/enabled; agent inactive/disabled"
}

rollback_on_failure() {
    local exit_code="${1:-1}"
    if [[ "$transition_started" == true ]]; then
        log "ROLLBACK: persistent transition failed; restoring the accepted boundary."
        rollback_transition || exit 1
    fi
    exit "$exit_code"
}

arm_transition_rollback() {
    transition_started=true
    trap 'rollback_on_failure $?' ERR
    trap 'rollback_on_failure 129' HUP
    trap 'rollback_on_failure 130' INT
    trap 'rollback_on_failure 143' TERM
}

disarm_transition_rollback() {
    transition_started=false
    trap - ERR HUP INT TERM EXIT
    if [[ -n "$staging_directory" && -d "$staging_directory" ]]; then
        rm -rf -- "$staging_directory"
    fi
}

await_external_gate() {
    local expected_invocation gate_nonce receipt=""
    expected_invocation="$(systemctl show "$GAME_SERVICE_NAME" -p InvocationID --value)"
    [[ "$expected_invocation" =~ ^[0-9a-f]{32}$ ]] ||
        fail "The spool-only game invocation is invalid."
    gate_nonce="$(od -An -N16 -tx1 /dev/urandom | tr -d '[:space:]')"
    [[ "$gate_nonce" =~ ^[0-9a-f]{32}$ ]] || fail "The external gate challenge is invalid."
    external_gate_fifo="$backup_directory/external-gate.fifo"
    [[ ! -e "$external_gate_fifo" && ! -L "$external_gate_fifo" ]] ||
        fail "The external gate already exists."
    mkfifo -m 0600 -- "$external_gate_fifo"
    log "EXTERNAL_GATE_READY: $backup_name $gate_nonce"
    exec 8<> "$external_gate_fifo"
    if ! IFS= read -r -t "$external_gate_timeout" -u 8 receipt; then
        exec 8>&-
        fail "The external A2S/RCON gate did not complete in time."
    fi
    exec 8>&-
    rm -f -- "$external_gate_fifo"
    external_gate_fifo=""
    [[ "$receipt" == "$gate_nonce" ]] || fail "The external gate receipt is invalid."
    [[ "$(systemctl show "$GAME_SERVICE_NAME" -p InvocationID --value)" == "$expected_invocation" ]] ||
        fail "The spool-only game restarted during the external gate."
    require_unit_state "$GAME_SERVICE_NAME" active disabled
    require_unit_state "$AGENT_SERVICE_NAME" active disabled
    require_settled_agent_status
    log "EXTERNAL_GATE=operator-attested"
}

run_activate() {
    require_apply_environment
    acquire_lock
    require_accepted_boundary
    [[ -n "$identity_file" ]] || fail "Activation requires --identity-file."
    validate_file_metadata "$identity_file" root root 600
    prepare_staging
    capture_baseline
    arm_transition_rollback

    "$autostart_workflow" --disable --apply
    require_unit_state "$GAME_SERVICE_NAME" active disabled
    GOLDSRCOPS_INHERITED_ACTIVATION_LOCK_FD=9 "$pilot_activator" \
        --activate \
        --identity-file "$identity_file" \
        --client-secret-stdin \
        --apply
    pilot_activation_present=true
    require_unit_state "$GAME_SERVICE_NAME" active disabled
    require_unit_state "$AGENT_SERVICE_NAME" active disabled
    require_settled_agent_status
    await_external_gate

    render_producer_configuration "$producer_configuration" "$staging_directory/amxx.cfg" 1
    render_delivery_environment "$environment_file" "$staging_directory/agent.env" true
    render_game_drop_in "$staging_directory/game.conf"
    render_agent_drop_in "$staging_directory/agent.conf"

    systemctl stop "$AGENT_SERVICE_NAME"
    systemctl stop "$GAME_SERVICE_NAME"
    install_persistent_policy
    verify_persistent_files
    systemctl start "$GAME_SERVICE_NAME"
    systemctl start "$AGENT_SERVICE_NAME"
    systemctl enable "$GAME_SERVICE_NAME" >/dev/null
    systemctl enable "$AGENT_SERVICE_NAME" >/dev/null
    require_unit_state "$GAME_SERVICE_NAME" active enabled
    require_unit_state "$AGENT_SERVICE_NAME" active enabled
    verify_persistent_files
    require_settled_agent_status

    disarm_transition_rollback
    log "PERSISTENT_ACTIVATION_COMPLETED: bounded producer, spool import, and delivery are enabled."
    log "SERVICE_STATE: game active/enabled; agent active/enabled; no cross-service requirement"
    log "NEXT_GATE: recheck external A2S and authenticated RCON after the policy restart, then perform the controlled reboot gate."
}

run_verify() {
    ((EUID == 0)) || fail "--verify must run as root for complete hash verification."
    for command in awk cut env grep id jq realpath runuser sha256sum stat systemctl; do
        require_command "$command"
    done
    verify_persistent_files
    require_unit_state "$GAME_SERVICE_NAME" active enabled
    require_unit_state "$AGENT_SERVICE_NAME" active enabled
    capture_agent_status >/dev/null
    log "PERSISTENT_GAME_EVENT_GATE=passed"
}

run_rollback() {
    require_apply_environment
    acquire_lock
    verify_persistent_files
    verify_baseline_record
    prepare_staging
    arm_transition_rollback
    rollback_transition
    disarm_transition_rollback
}

print_plan() {
    case "$operation" in
        overview)
            log "PLAN: activate persistent gameplay telemetry only from accepted public-classic-v1 and guarded-autostart-v1"
            log "PLAN: reuse the reviewed pilot for identity validation, stdin-only credential transport, overlay installation, and spool-only startup"
            log "PLAN: bind both independent services to exact hashes, enable bounded producer/import/delivery, and enable only those services across boot"
            log "PLAN: preserve exact prior guard inputs and retain queue/spool evidence before any rollback cleanup"
            ;;
        activate)
            log "PLAN: verify and preserve the exact accepted game guard, profile markers, units, dormant bundle, and empty aggregate state"
            log "PLAN: disable the old guard without stopping the game, apply reviewed spool-only pilot activation, then install persistent hashes and drop-ins"
            log "PLAN: wait for a fresh root-only external A2S/RCON gate receipt while spool-only is active; timeout or interruption rolls back"
            log "PLAN: only then enable producer, spool import, delivery, game boot startup, and agent boot startup without coupling the two services"
            log "PLAN: on failure, disable intake, seal durable evidence, restore the pilot baseline, and reinstate the exact guarded public game policy"
            ;;
        rollback)
            log "PLAN: verify the persistent marker and exact owner-only baseline backup"
            log "PLAN: disable producer and both boot entries, stop the agent and game, and atomically seal local queue and spool evidence"
            log "PLAN: remove only persistent policy files, execute reviewed pilot rollback, and restore the exact pre-activation guard and marker bytes"
            ;;
        *) fail "The requested plan is unsupported." ;;
    esac
    log "PLAN_ONLY: no host state, stdin, identity file, service, or network endpoint was inspected or changed; add --apply to execute."
}

select_operation() {
    local selected="$1"
    [[ "$operation_selected" == false ]] || fail "Select exactly one operation."
    operation="$selected"
    operation_selected=true
}

validate_cli() {
    if [[ "$operation" == verify ]]; then
        [[ "$apply_changes" == false && -z "$identity_file" && "$read_secret_from_stdin" == false ]] ||
            fail "--verify cannot be combined with activation arguments or --apply."
        return
    fi
    if [[ -n "$identity_file" ]]; then
        [[ "$operation" == activate && "$identity_file" == /* ]] ||
            fail "--identity-file is valid only for activation and must be absolute."
    fi
    if [[ "$read_secret_from_stdin" == true ]]; then
        [[ "$operation" == activate && "$apply_changes" == true ]] ||
            fail "--client-secret-stdin is valid only with --activate --apply."
    fi
    if [[ "$apply_changes" == true ]]; then
        [[ "$operation" != overview ]] || fail "--apply requires an operation."
        if [[ "$operation" == activate ]]; then
            [[ -n "$identity_file" && "$read_secret_from_stdin" == true ]] ||
                fail "Activation apply requires identity and redirected client secret input."
        fi
    fi
}

main() {
    while (($# > 0)); do
        case "$1" in
            --activate) select_operation activate; shift ;;
            --verify) select_operation verify; shift ;;
            --rollback) select_operation rollback; shift ;;
            --identity-file)
                (($# >= 2)) || fail "--identity-file requires a value."
                identity_file="$2"
                shift 2
                ;;
            --client-secret-stdin) read_secret_from_stdin=true; shift ;;
            --apply) apply_changes=true; shift ;;
            -h|--help) usage; return ;;
            --guard-game|--guard-agent)
                select_operation "$1"
                shift
                ;;
            *) fail "Unknown argument." ;;
        esac
    done

    if [[ "$operation" == --guard-game || "$operation" == --guard-agent ]]; then
        [[ "$apply_changes" == false && -z "$identity_file" && "$read_secret_from_stdin" == false ]] ||
            fail "Guard checks do not accept mutating or credential arguments."
        trap 'exit "$GUARD_REJECTED_EXIT"' ERR
        prepare_persistent_verification
        if [[ "$operation" == --guard-game ]]; then
            verify_persistent_game_files
        else
            verify_persistent_agent_files
        fi
        trap - ERR
        log "PERSISTENT_GAME_EVENT_GUARD=passed"
        return
    fi

    validate_cli
    if [[ "$operation" == verify ]]; then
        run_verify
    elif [[ "$apply_changes" == false ]]; then
        print_plan
    elif [[ "$operation" == activate ]]; then
        run_activate
    elif [[ "$operation" == rollback ]]; then
        run_rollback
    else
        fail "The requested operation is unsupported."
    fi
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    main "$@"
fi
