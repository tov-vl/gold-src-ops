#!/usr/bin/env bash

set -Eeuo pipefail
IFS=$'\n\t'
umask 077

readonly SERVICE_NAME="goldsrcops-gameserver.service"
readonly AGENT_SERVICE_NAME="goldsrcops-game-event-agent.service"
readonly SYSTEMD_UNIT_FILE="/etc/systemd/system/$SERVICE_NAME"
readonly DROP_IN_DIRECTORY="/etc/systemd/system/$SERVICE_NAME.d"
readonly DROP_IN_FILE="$DROP_IN_DIRECTORY/20-guarded-autostart.conf"
readonly INSTALLED_GUARD="/usr/local/libexec/goldsrcops-gameserver-boot-guard"
readonly CONFIGURATION_DIRECTORY="/etc/goldsrcops/gameserver"
readonly PREPARED_MARKER="$CONFIGURATION_DIRECTORY/host-prepared"
readonly RUNTIME_MARKER="$CONFIGURATION_DIRECTORY/runtime-installed"
readonly RUNTIME_ENABLED_MARKER="$CONFIGURATION_DIRECTORY/runtime-enabled"
readonly ACTIVE_PROFILE_MARKER="$CONFIGURATION_DIRECTORY/managed-profile-active"
readonly POLICY_MARKER="$CONFIGURATION_DIRECTORY/guarded-autostart-active"
readonly TRANSITION_LOCK="$CONFIGURATION_DIRECTORY/managed-profile.lock"
readonly PUBLIC_CONFIGURATION="$CONFIGURATION_DIRECTORY/server-public.cfg"
readonly SECRETS_DIRECTORY="$CONFIGURATION_DIRECTORY/secrets"
readonly PRIVATE_CONFIGURATION="$SECRETS_DIRECTORY/server-private.cfg"
readonly BACKUP_ROOT="/var/backups/goldsrcops/gameserver"
readonly PROFILE_ID="public-classic-v1"
readonly PROFILE_FILE_NAME="goldsrcops-managed-profile.cfg"
readonly MAPCYCLE_FILE_NAME="goldsrcops-mapcycle.txt"
readonly POLICY_ID="guarded-autostart-v1"
readonly GUARD_REJECTED_EXIT=78
readonly -a PROFILE_MAPS=(de_dust2 de_inferno de_nuke de_train cs_office)

action="enable"
apply_changes=false
verify_requested=false
prepared_operator_user=""
prepared_service_user=""
prepared_game_port=""
service_group=""
service_home=""
game_directory=""
profile_file=""
mapcycle_file=""
runtime_marker_sha256=""
service_unit_sha256=""
public_config_sha256=""
rcon_source_policy=""
rcon_secret_transport=""
service_autostart=""
profile_backup_name=""
baseline_public_sha256=""
baseline_runtime_enabled_sha256=""
active_public_sha256=""
active_runtime_enabled_sha256=""
active_profile_sha256=""
active_mapcycle_sha256=""
policy_backup_name=""
policy_guard_sha256=""
policy_drop_in_sha256=""
policy_runtime_enabled_sha256=""
policy_active_profile_sha256=""
policy_baseline_runtime_enabled_sha256=""
policy_baseline_active_profile_sha256=""
staging_directory=""
backup_directory=""

usage() {
    cat <<'EOF'
Usage:
  guarded-autostart.sh [--enable] [--apply]
  guarded-autostart.sh --disable [--apply]
  guarded-autostart.sh --verify

Without --apply, enable and disable print a sanitized plan without inspecting
or changing the host. Enable installs this exact reviewed script as a read-only
systemd ExecCondition guard, changes only the recorded boot policy, and enables
the already active public-classic-v1 game service. Disable restores the exact
pre-policy markers, removes the guard, and disables the service without stopping
the current game process.

Apply must run through sudo from the operator recorded by host bootstrap while
preserving SSH_CONNECTION. Verify is read-only and accepts no path override.
The game-event agent and automatic host reboot remain disabled.
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
    [[ "$1" =~ ^[0-9a-f]{64}$ ]] || fail "A recorded SHA-256 value is invalid."
}

validate_user_name() {
    [[ "$1" =~ ^[a-z_][a-z0-9_-]{0,31}$ && "$1" != "root" ]] ||
        fail "A prepared account name is invalid."
}

validate_port() {
    [[ "$1" =~ ^[0-9]+$ ]] || fail "The prepared port is invalid."
    ((10#$1 >= 1 && 10#$1 <= 65535)) || fail "The prepared port is invalid."
}

validate_file_metadata() {
    local path="$1"
    local owner="$2"
    local group="$3"
    local mode="$4"

    [[ -f "$path" && ! -L "$path" ]] || fail "Required file '$path' is missing or unsafe."
    [[ "$(stat -c '%U:%G:%a' -- "$path")" == "$owner:$group:$mode" ]] ||
        fail "File '$path' does not match the reviewed owner and mode."
}

validate_directory_metadata() {
    local path="$1"
    local owner="$2"
    local group="$3"
    local mode="$4"

    [[ -d "$path" && ! -L "$path" ]] || fail "Required directory '$path' is missing or unsafe."
    [[ "$(stat -c '%U:%G:%a' -- "$path")" == "$owner:$group:$mode" ]] ||
        fail "Directory '$path' does not match the reviewed owner and mode."
}

verify_file_sha256() {
    local path="$1"
    local expected="$2"

    validate_sha256 "$expected"
    [[ -f "$path" && ! -L "$path" ]] || fail "Required reviewed file '$path' is missing or unsafe."
    [[ "$(sha256sum "$path" | awk '{ print $1 }')" == "$expected" ]] ||
        fail "Reviewed file '$path' has drifted."
}

read_prepared_marker() {
    local key value schema_version="" ssh_port=""
    declare -A seen=()

    while IFS='=' read -r key value || [[ -n "${key:-}${value:-}" ]]; do
        [[ -n "${key:-}" && -z "${seen[$key]+x}" ]] || fail "The host marker is malformed."
        seen[$key]=1
        case "$key" in
            schema_version) schema_version="$value" ;;
            operator_user) prepared_operator_user="$value" ;;
            service_user) prepared_service_user="$value" ;;
            ssh_port) ssh_port="$value" ;;
            game_port) prepared_game_port="$value" ;;
            *) fail "The host marker contains an unknown key." ;;
        esac
    done < "$PREPARED_MARKER"

    [[ "$schema_version" == "1" && "${#seen[@]}" -eq 5 ]] ||
        fail "The host marker contract is invalid."
    validate_user_name "$prepared_operator_user"
    validate_user_name "$prepared_service_user"
    validate_port "$ssh_port"
    validate_port "$prepared_game_port"
}

read_runtime_enabled_marker() {
    local path="${1:-$RUNTIME_ENABLED_MARKER}"
    local expected_autostart="${2:-enabled}"
    local key value schema_version=""
    declare -A seen=()

    runtime_marker_sha256=""
    service_unit_sha256=""
    public_config_sha256=""
    rcon_source_policy=""
    rcon_secret_transport=""
    service_autostart=""
    while IFS='=' read -r key value || [[ -n "${key:-}${value:-}" ]]; do
        [[ -n "${key:-}" && -z "${seen[$key]+x}" ]] || fail "The runtime-enabled marker is malformed."
        seen[$key]=1
        case "$key" in
            schema_version) schema_version="$value" ;;
            runtime_marker_sha256) runtime_marker_sha256="$value" ;;
            service_unit_sha256) service_unit_sha256="$value" ;;
            public_config_sha256) public_config_sha256="$value" ;;
            rcon_source_policy) rcon_source_policy="$value" ;;
            rcon_secret_transport) rcon_secret_transport="$value" ;;
            service_autostart) service_autostart="$value" ;;
            *) fail "The runtime-enabled marker contains an unknown key." ;;
        esac
    done < "$path"

    [[ "$schema_version" == "1" && "${#seen[@]}" -eq 7 ]] ||
        fail "The runtime-enabled marker contract is invalid."
    validate_sha256 "$runtime_marker_sha256"
    validate_sha256 "$service_unit_sha256"
    validate_sha256 "$public_config_sha256"
    [[ "$rcon_source_policy" == "ssh-ufw-exact-ipv4-32" &&
        "$rcon_secret_transport" == "stdin" &&
        "$service_autostart" == "$expected_autostart" ]] ||
        fail "The runtime-enabled security boundary has drifted."
}

render_runtime_enabled_marker() {
    local destination="$1"
    local selected_autostart="$2"

    cat > "$destination" <<EOF
schema_version=1
runtime_marker_sha256=$runtime_marker_sha256
service_unit_sha256=$service_unit_sha256
public_config_sha256=$public_config_sha256
rcon_source_policy=$rcon_source_policy
rcon_secret_transport=$rcon_secret_transport
service_autostart=$selected_autostart
EOF
    chmod 0640 -- "$destination"
}

read_active_profile_marker() {
    local path="${1:-$ACTIVE_PROFILE_MARKER}"
    local key value schema_version="" profile_id=""
    declare -A seen=()

    profile_backup_name=""
    baseline_public_sha256=""
    baseline_runtime_enabled_sha256=""
    active_public_sha256=""
    active_runtime_enabled_sha256=""
    active_profile_sha256=""
    active_mapcycle_sha256=""
    while IFS='=' read -r key value || [[ -n "${key:-}${value:-}" ]]; do
        [[ -n "${key:-}" && -z "${seen[$key]+x}" ]] || fail "The managed-profile marker is malformed."
        seen[$key]=1
        case "$key" in
            schema_version) schema_version="$value" ;;
            profile_id) profile_id="$value" ;;
            backup_name) profile_backup_name="$value" ;;
            baseline_public_sha256) baseline_public_sha256="$value" ;;
            baseline_runtime_enabled_sha256) baseline_runtime_enabled_sha256="$value" ;;
            public_sha256) active_public_sha256="$value" ;;
            runtime_enabled_sha256) active_runtime_enabled_sha256="$value" ;;
            profile_sha256) active_profile_sha256="$value" ;;
            mapcycle_sha256) active_mapcycle_sha256="$value" ;;
            *) fail "The managed-profile marker contains an unknown key." ;;
        esac
    done < "$path"

    [[ "$schema_version" == "1" && "$profile_id" == "$PROFILE_ID" &&
        "${#seen[@]}" -eq 9 ]] || fail "The managed-profile marker contract is invalid."
    [[ "$profile_backup_name" =~ ^managed-profile-[0-9]{8}T[0-9]{6}Z-[A-Za-z0-9]{6}$ ]] ||
        fail "The managed-profile backup reference is invalid."
    validate_sha256 "$baseline_public_sha256"
    validate_sha256 "$baseline_runtime_enabled_sha256"
    validate_sha256 "$active_public_sha256"
    validate_sha256 "$active_runtime_enabled_sha256"
    validate_sha256 "$active_profile_sha256"
    validate_sha256 "$active_mapcycle_sha256"
}

render_active_profile_marker() {
    local destination="$1"
    local selected_runtime_enabled_sha256="$2"

    cat > "$destination" <<EOF
schema_version=1
profile_id=$PROFILE_ID
backup_name=$profile_backup_name
baseline_public_sha256=$baseline_public_sha256
baseline_runtime_enabled_sha256=$baseline_runtime_enabled_sha256
public_sha256=$active_public_sha256
runtime_enabled_sha256=$selected_runtime_enabled_sha256
profile_sha256=$active_profile_sha256
mapcycle_sha256=$active_mapcycle_sha256
EOF
    chmod 0640 -- "$destination"
}

read_policy_marker() {
    local path="${1:-$POLICY_MARKER}"
    local key value schema_version="" policy_id=""
    declare -A seen=()

    policy_backup_name=""
    policy_guard_sha256=""
    policy_drop_in_sha256=""
    policy_runtime_enabled_sha256=""
    policy_active_profile_sha256=""
    policy_baseline_runtime_enabled_sha256=""
    policy_baseline_active_profile_sha256=""
    while IFS='=' read -r key value || [[ -n "${key:-}${value:-}" ]]; do
        [[ -n "${key:-}" && -z "${seen[$key]+x}" ]] || fail "The guarded-autostart marker is malformed."
        seen[$key]=1
        case "$key" in
            schema_version) schema_version="$value" ;;
            policy_id) policy_id="$value" ;;
            backup_name) policy_backup_name="$value" ;;
            guard_sha256) policy_guard_sha256="$value" ;;
            drop_in_sha256) policy_drop_in_sha256="$value" ;;
            runtime_enabled_sha256) policy_runtime_enabled_sha256="$value" ;;
            active_profile_sha256) policy_active_profile_sha256="$value" ;;
            baseline_runtime_enabled_sha256) policy_baseline_runtime_enabled_sha256="$value" ;;
            baseline_active_profile_sha256) policy_baseline_active_profile_sha256="$value" ;;
            *) fail "The guarded-autostart marker contains an unknown key." ;;
        esac
    done < "$path"

    [[ "$schema_version" == "1" && "$policy_id" == "$POLICY_ID" &&
        "${#seen[@]}" -eq 9 ]] || fail "The guarded-autostart marker contract is invalid."
    [[ "$policy_backup_name" =~ ^guarded-autostart-[0-9]{8}T[0-9]{6}Z-[A-Za-z0-9]{6}$ ]] ||
        fail "The guarded-autostart backup reference is invalid."
    validate_sha256 "$policy_guard_sha256"
    validate_sha256 "$policy_drop_in_sha256"
    validate_sha256 "$policy_runtime_enabled_sha256"
    validate_sha256 "$policy_active_profile_sha256"
    validate_sha256 "$policy_baseline_runtime_enabled_sha256"
    validate_sha256 "$policy_baseline_active_profile_sha256"
}

render_drop_in() {
    local destination="$1"

    cat > "$destination" <<EOF
[Service]
ExecCondition=$INSTALLED_GUARD --verify
EOF
    chmod 0644 -- "$destination"
}

render_policy_marker() {
    local destination="$1"
    local selected_backup_name="$2"
    local guard_sha256="$3"
    local drop_in_sha256="$4"
    local runtime_enabled_sha256="$5"
    local active_profile_marker_sha256="$6"
    local baseline_runtime_sha256="$7"
    local baseline_profile_sha256="$8"

    cat > "$destination" <<EOF
schema_version=1
policy_id=$POLICY_ID
backup_name=$selected_backup_name
guard_sha256=$guard_sha256
drop_in_sha256=$drop_in_sha256
runtime_enabled_sha256=$runtime_enabled_sha256
active_profile_sha256=$active_profile_marker_sha256
baseline_runtime_enabled_sha256=$baseline_runtime_sha256
baseline_active_profile_sha256=$baseline_profile_sha256
EOF
    chmod 0640 -- "$destination"
}

initialize_runtime_paths() {
    read_prepared_marker
    service_group="$(id -gn "$prepared_service_user")"
    service_home="$(getent passwd "$prepared_service_user" | cut -d: -f6)"
    [[ "$service_home" == "/var/lib/$prepared_service_user" ]] ||
        fail "The service account has an unexpected home directory."
    game_directory="$service_home/server/cstrike"
    profile_file="$game_directory/$PROFILE_FILE_NAME"
    mapcycle_file="$game_directory/$MAPCYCLE_FILE_NAME"
}

verify_active_profile_state() {
    local expected_autostart="$1"
    local map_name

    initialize_runtime_paths
    validate_directory_metadata "$CONFIGURATION_DIRECTORY" root "$service_group" 750
    validate_directory_metadata "$SECRETS_DIRECTORY" root "$service_group" 710
    validate_directory_metadata "$service_home" "$prepared_service_user" "$service_group" 750
    validate_directory_metadata "$service_home/server" "$prepared_service_user" "$service_group" 750
    validate_file_metadata "$PREPARED_MARKER" root "$service_group" 640
    validate_file_metadata "$RUNTIME_MARKER" root "$service_group" 640
    validate_file_metadata "$RUNTIME_ENABLED_MARKER" root "$service_group" 640
    validate_file_metadata "$ACTIVE_PROFILE_MARKER" root "$service_group" 640
    validate_file_metadata "$PUBLIC_CONFIGURATION" root "$service_group" 640
    validate_file_metadata "$PRIVATE_CONFIGURATION" root root 600
    validate_file_metadata "$SYSTEMD_UNIT_FILE" root root 644
    validate_file_metadata "$profile_file" root "$service_group" 640
    validate_file_metadata "$mapcycle_file" root "$service_group" 640

    read_runtime_enabled_marker "$RUNTIME_ENABLED_MARKER" "$expected_autostart"
    verify_file_sha256 "$RUNTIME_MARKER" "$runtime_marker_sha256"
    verify_file_sha256 "$SYSTEMD_UNIT_FILE" "$service_unit_sha256"
    verify_file_sha256 "$PUBLIC_CONFIGURATION" "$public_config_sha256"
    read_active_profile_marker
    verify_file_sha256 "$PUBLIC_CONFIGURATION" "$active_public_sha256"
    verify_file_sha256 "$RUNTIME_ENABLED_MARKER" "$active_runtime_enabled_sha256"
    verify_file_sha256 "$profile_file" "$active_profile_sha256"
    verify_file_sha256 "$mapcycle_file" "$active_mapcycle_sha256"

    [[ "$(grep -Fxc "exec $PROFILE_FILE_NAME" "$PUBLIC_CONFIGURATION")" -eq 1 ]] ||
        fail "The public configuration does not load the accepted profile exactly once."
    [[ "$(grep -Fxc "mapchangecfgfile \"$PROFILE_FILE_NAME\"" "$profile_file")" -eq 1 ]] ||
        fail "The accepted map-change hook has drifted."
    [[ ! -e "$game_directory/addons/metamod" && ! -e "$game_directory/addons/amxmodx" ]] ||
        fail "The accepted plugin-free runtime boundary has drifted."
    for map_name in "${PROFILE_MAPS[@]}"; do
        [[ -f "$game_directory/maps/$map_name.bsp" && ! -L "$game_directory/maps/$map_name.bsp" ]] ||
            fail "Required managed-profile map '$map_name' is missing or unsafe."
    done
}

verify_policy_files() {
    validate_file_metadata "$POLICY_MARKER" root "$service_group" 640
    validate_file_metadata "$INSTALLED_GUARD" root root 755
    validate_file_metadata "$DROP_IN_FILE" root root 644
    read_policy_marker
    verify_file_sha256 "$INSTALLED_GUARD" "$policy_guard_sha256"
    verify_file_sha256 "$DROP_IN_FILE" "$policy_drop_in_sha256"
    verify_file_sha256 "$RUNTIME_ENABLED_MARKER" "$policy_runtime_enabled_sha256"
    verify_file_sha256 "$ACTIVE_PROFILE_MARKER" "$policy_active_profile_sha256"
}

verify_guarded_boot() {
    verify_active_profile_state enabled
    verify_policy_files
    log "GUARDED_AUTOSTART_GATE=passed"
}

require_apply_environment() {
    local invoking_user

    ((EUID == 0)) || fail "--apply must run as root."
    for command in awk basename cat chmod chown cut date flock getent grep id install \
        mktemp readlink rm runuser sha256sum stat systemctl; do
        require_command "$command"
    done
    initialize_runtime_paths
    validate_directory_metadata "$BACKUP_ROOT" root root 700
    invoking_user="${SUDO_USER:-}"
    [[ "$invoking_user" == "$prepared_operator_user" ]] ||
        fail "Run --apply through sudo from the operator recorded by host bootstrap."
    [[ -n "${SSH_CONNECTION:-}" ]] || fail "Apply requires preserved SSH_CONNECTION metadata."
    [[ "$(systemctl is-active "$SERVICE_NAME" 2>/dev/null || true)" == "active" ]] ||
        fail "The game-server service must be active before changing boot policy."
    [[ "$(systemctl is-active "$AGENT_SERVICE_NAME" 2>/dev/null || true)" == "inactive" ]] ||
        fail "The game-event agent must remain inactive."
    [[ "$(systemctl is-enabled "$AGENT_SERVICE_NAME" 2>/dev/null || true)" == "disabled" ]] ||
        fail "The game-event agent must remain disabled across boot."
}

acquire_transition_lock() {
    exec 9>"$TRANSITION_LOCK"
    flock --nonblock 9 || fail "Another game-runtime transition is already in progress."
    chown root:"$service_group" -- "$TRANSITION_LOCK"
    chmod 0640 -- "$TRANSITION_LOCK"
}

cleanup_staging() {
    if [[ -n "$staging_directory" ]] && ! rm -rf -- "$staging_directory"; then
        log "CLEANUP_PENDING: remove the retained guarded-autostart staging directory after inspection."
    fi
}

rollback_failed_enable() {
    local exit_code="${1:-1}"
    trap - ERR HUP INT TERM
    systemctl disable "$SERVICE_NAME" >/dev/null 2>&1 || true
    if [[ -n "$backup_directory" && -f "$backup_directory/runtime-enabled" ]]; then
        install -o root -g "$service_group" -m 0640 -- \
            "$backup_directory/runtime-enabled" "$RUNTIME_ENABLED_MARKER" || true
        install -o root -g "$service_group" -m 0640 -- \
            "$backup_directory/managed-profile-active" "$ACTIVE_PROFILE_MARKER" || true
    fi
    rm -f -- "$POLICY_MARKER" "$DROP_IN_FILE" "$INSTALLED_GUARD" || true
    rmdir --ignore-fail-on-non-empty "$DROP_IN_DIRECTORY" 2>/dev/null || true
    systemctl daemon-reload >/dev/null 2>&1 || true
    cleanup_staging
    log "ROLLBACK_ATTEMPTED: inspect host state before any retry."
    exit "$exit_code"
}

run_enable() {
    local source_path current_invocation current_restarts
    local staged_runtime staged_profile staged_drop_in staged_policy
    local guard_sha256 drop_in_sha256 runtime_sha256 profile_marker_sha256
    local baseline_runtime_sha256 baseline_profile_sha256

    require_apply_environment
    acquire_transition_lock
    [[ "$(systemctl is-enabled "$SERVICE_NAME" 2>/dev/null || true)" == "disabled" ]] ||
        fail "The game-server service is already enabled or has an unexpected state."
    [[ ! -e "$POLICY_MARKER" && ! -L "$POLICY_MARKER" &&
        ! -e "$DROP_IN_FILE" && ! -L "$DROP_IN_FILE" &&
        ! -e "$INSTALLED_GUARD" && ! -L "$INSTALLED_GUARD" ]] ||
        fail "Guarded-autostart state already exists."
    verify_active_profile_state disabled

    source_path="$(readlink -f -- "${BASH_SOURCE[0]}")"
    [[ -f "$source_path" && ! -L "$source_path" ]] || fail "The reviewed workflow source is unsafe."
    [[ "$(stat -c '%a' -- "$source_path")" =~ ^[0-7][0-5][0-5]$ ]] ||
        fail "The reviewed workflow source is group- or world-writable."

    staging_directory="$(mktemp -d "$CONFIGURATION_DIRECTORY/.guarded-autostart.XXXXXX")"
    backup_directory="$(mktemp -d "$BACKUP_ROOT/guarded-autostart-$(date -u '+%Y%m%dT%H%M%SZ')-XXXXXX")"
    chown root:root -- "$backup_directory"
    chmod 0700 -- "$backup_directory"
    install -o root -g root -m 0600 -- "$RUNTIME_ENABLED_MARKER" "$backup_directory/runtime-enabled"
    install -o root -g root -m 0600 -- "$ACTIVE_PROFILE_MARKER" "$backup_directory/managed-profile-active"
    baseline_runtime_sha256="$(sha256sum "$backup_directory/runtime-enabled" | awk '{ print $1 }')"
    baseline_profile_sha256="$(sha256sum "$backup_directory/managed-profile-active" | awk '{ print $1 }')"

    staged_runtime="$staging_directory/runtime-enabled"
    staged_profile="$staging_directory/managed-profile-active"
    staged_drop_in="$staging_directory/20-guarded-autostart.conf"
    staged_policy="$staging_directory/guarded-autostart-active"
    render_runtime_enabled_marker "$staged_runtime" enabled
    runtime_sha256="$(sha256sum "$staged_runtime" | awk '{ print $1 }')"
    render_active_profile_marker "$staged_profile" "$runtime_sha256"
    render_drop_in "$staged_drop_in"
    guard_sha256="$(sha256sum "$source_path" | awk '{ print $1 }')"
    drop_in_sha256="$(sha256sum "$staged_drop_in" | awk '{ print $1 }')"
    profile_marker_sha256="$(sha256sum "$staged_profile" | awk '{ print $1 }')"
    render_policy_marker "$staged_policy" "$(basename "$backup_directory")" \
        "$guard_sha256" "$drop_in_sha256" "$runtime_sha256" \
        "$profile_marker_sha256" "$baseline_runtime_sha256" "$baseline_profile_sha256"

    current_invocation="$(systemctl show "$SERVICE_NAME" -p InvocationID --value)"
    current_restarts="$(systemctl show "$SERVICE_NAME" -p NRestarts --value)"
    trap 'rollback_failed_enable $?' ERR
    trap 'rollback_failed_enable 129' HUP
    trap 'rollback_failed_enable 130' INT
    trap 'rollback_failed_enable 143' TERM
    install -d -m 0755 -o root -g root "$(dirname "$INSTALLED_GUARD")" "$DROP_IN_DIRECTORY"
    install -o root -g root -m 0755 -- "$source_path" "$INSTALLED_GUARD"
    install -o root -g root -m 0644 -- "$staged_drop_in" "$DROP_IN_FILE"
    install -o root -g "$service_group" -m 0640 -- "$staged_runtime" "$RUNTIME_ENABLED_MARKER"
    install -o root -g "$service_group" -m 0640 -- "$staged_profile" "$ACTIVE_PROFILE_MARKER"
    install -o root -g "$service_group" -m 0640 -- "$staged_policy" "$POLICY_MARKER"
    systemctl daemon-reload
    runuser -u "$prepared_service_user" -- "$INSTALLED_GUARD" --verify
    systemctl enable "$SERVICE_NAME"
    [[ "$(systemctl is-active "$SERVICE_NAME")" == "active" &&
        "$(systemctl is-enabled "$SERVICE_NAME")" == "enabled" ]] ||
        fail "The game-server service did not reach active/enabled state."
    [[ "$(systemctl show "$SERVICE_NAME" -p InvocationID --value)" == "$current_invocation" &&
        "$(systemctl show "$SERVICE_NAME" -p NRestarts --value)" == "$current_restarts" ]] ||
        fail "Changing boot policy restarted the active game process."
    trap - ERR HUP INT TERM
    cleanup_staging
    log "GUARDED_AUTOSTART_ENABLED: $PROFILE_ID"
    log "SERVICE_STATE: game active/enabled; agent inactive/disabled"
    log "NEXT_GATE: controlled reboot followed by host guard, external A2S, and authenticated RCON verification."
}

restore_enabled_policy() {
    local exit_code="${1:-1}"
    trap - ERR HUP INT TERM
    if [[ -n "$staging_directory" ]]; then
        install -d -m 0755 -o root -g root "$(dirname "$INSTALLED_GUARD")" "$DROP_IN_DIRECTORY" 2>/dev/null || true
        install -o root -g root -m 0755 -- "$staging_directory/installed-guard" "$INSTALLED_GUARD" 2>/dev/null || true
        install -o root -g root -m 0644 -- "$staging_directory/drop-in" "$DROP_IN_FILE" 2>/dev/null || true
        install -o root -g "$service_group" -m 0640 -- "$staging_directory/runtime-enabled" "$RUNTIME_ENABLED_MARKER" 2>/dev/null || true
        install -o root -g "$service_group" -m 0640 -- "$staging_directory/managed-profile-active" "$ACTIVE_PROFILE_MARKER" 2>/dev/null || true
        install -o root -g "$service_group" -m 0640 -- "$staging_directory/policy-marker" "$POLICY_MARKER" 2>/dev/null || true
    fi
    systemctl daemon-reload >/dev/null 2>&1 || true
    systemctl enable "$SERVICE_NAME" >/dev/null 2>&1 || true
    cleanup_staging
    log "ROLLBACK_ATTEMPTED: the prior enabled boot policy was restored."
    exit "$exit_code"
}

run_disable() {
    local current_invocation current_restarts restored_runtime_hash restored_profile_hash

    require_apply_environment
    acquire_transition_lock
    [[ "$(systemctl is-enabled "$SERVICE_NAME" 2>/dev/null || true)" == "enabled" ]] ||
        fail "The game-server service is not enabled."
    verify_guarded_boot
    backup_directory="$BACKUP_ROOT/$policy_backup_name"
    validate_directory_metadata "$backup_directory" root root 700
    validate_file_metadata "$backup_directory/runtime-enabled" root root 600
    validate_file_metadata "$backup_directory/managed-profile-active" root root 600
    verify_file_sha256 "$backup_directory/runtime-enabled" "$policy_baseline_runtime_enabled_sha256"
    verify_file_sha256 "$backup_directory/managed-profile-active" "$policy_baseline_active_profile_sha256"

    staging_directory="$(mktemp -d "$CONFIGURATION_DIRECTORY/.guarded-autostart-disable.XXXXXX")"
    install -o root -g root -m 0600 -- "$INSTALLED_GUARD" "$staging_directory/installed-guard"
    install -o root -g root -m 0600 -- "$DROP_IN_FILE" "$staging_directory/drop-in"
    install -o root -g root -m 0600 -- "$RUNTIME_ENABLED_MARKER" "$staging_directory/runtime-enabled"
    install -o root -g root -m 0600 -- "$ACTIVE_PROFILE_MARKER" "$staging_directory/managed-profile-active"
    install -o root -g root -m 0600 -- "$POLICY_MARKER" "$staging_directory/policy-marker"
    current_invocation="$(systemctl show "$SERVICE_NAME" -p InvocationID --value)"
    current_restarts="$(systemctl show "$SERVICE_NAME" -p NRestarts --value)"

    trap 'restore_enabled_policy $?' ERR
    trap 'restore_enabled_policy 129' HUP
    trap 'restore_enabled_policy 130' INT
    trap 'restore_enabled_policy 143' TERM
    systemctl disable "$SERVICE_NAME"
    install -o root -g "$service_group" -m 0640 -- "$backup_directory/runtime-enabled" "$RUNTIME_ENABLED_MARKER"
    install -o root -g "$service_group" -m 0640 -- "$backup_directory/managed-profile-active" "$ACTIVE_PROFILE_MARKER"
    rm -f -- "$POLICY_MARKER" "$DROP_IN_FILE" "$INSTALLED_GUARD"
    rmdir --ignore-fail-on-non-empty "$DROP_IN_DIRECTORY" 2>/dev/null || true
    systemctl daemon-reload
    restored_runtime_hash="$(sha256sum "$RUNTIME_ENABLED_MARKER" | awk '{ print $1 }')"
    restored_profile_hash="$(sha256sum "$ACTIVE_PROFILE_MARKER" | awk '{ print $1 }')"
    [[ "$restored_runtime_hash" == "$policy_baseline_runtime_enabled_sha256" &&
        "$restored_profile_hash" == "$policy_baseline_active_profile_sha256" ]] ||
        fail "The exact pre-policy markers were not restored."
    verify_active_profile_state disabled
    [[ "$(systemctl is-active "$SERVICE_NAME")" == "active" &&
        "$(systemctl is-enabled "$SERVICE_NAME" 2>/dev/null || true)" == "disabled" ]] ||
        fail "The game-server service did not reach active/disabled state."
    [[ "$(systemctl show "$SERVICE_NAME" -p InvocationID --value)" == "$current_invocation" &&
        "$(systemctl show "$SERVICE_NAME" -p NRestarts --value)" == "$current_restarts" ]] ||
        fail "Disabling boot policy restarted the active game process."
    trap - ERR HUP INT TERM
    if ! rm -rf -- "$backup_directory"; then
        log "CLEANUP_PENDING: the disabled policy backup remains owner-only."
    fi
    cleanup_staging
    log "GUARDED_AUTOSTART_DISABLED: exact pre-policy markers restored"
    log "SERVICE_STATE: game active/disabled; agent inactive/disabled"
}

print_plan() {
    if [[ "$action" == "disable" ]]; then
        log "PLAN: verify the installed guard, drop-in, active profile, and exact rollback backup"
        log "PLAN: disable boot startup without stopping or restarting the current game process"
        log "PLAN: restore the exact pre-policy runtime and profile markers, then remove only boot-policy files"
        log "PLAN_ONLY: no host state was inspected or changed; add --apply to disable guarded autostart."
    else
        log "PLAN: verify the active public-classic-v1 profile, runtime/unit hashes, map hook, maps, and plugin-free boundary"
        log "PLAN: preserve exact runtime and profile markers before changing only their recorded boot policy"
        log "PLAN: install this exact script as a fail-closed ExecCondition guard without a restart loop"
        log "PLAN: enable only the game service; keep the agent and automatic host reboot disabled"
        log "PLAN: do not stop or restart the current game process"
        log "PLAN_ONLY: no host state was inspected or changed; add --apply to enable guarded autostart."
    fi
}

main() {
    while (($# > 0)); do
        case "$1" in
            --enable)
                [[ "$action" == "enable" && "$verify_requested" == "false" ]] || fail "Conflicting actions were requested."
                action="enable"
                ;;
            --disable)
                [[ "$action" == "enable" && "$verify_requested" == "false" ]] || fail "Conflicting actions were requested."
                action="disable"
                ;;
            --verify)
                [[ "$verify_requested" == "false" && "$action" == "enable" && "$apply_changes" == "false" ]] ||
                    fail "--verify cannot be combined with another action."
                verify_requested=true
                ;;
            --apply)
                [[ "$verify_requested" == "false" ]] || fail "--verify cannot be combined with --apply."
                apply_changes=true
                ;;
            -h|--help)
                usage
                return 0
                ;;
            *) fail "Unknown argument: $1" ;;
        esac
        shift
    done

    if [[ "$verify_requested" == "true" ]]; then
        trap 'exit "$GUARD_REJECTED_EXIT"' ERR
        verify_guarded_boot
        trap - ERR
    elif [[ "$apply_changes" == "false" ]]; then
        print_plan
    elif [[ "$action" == "disable" ]]; then
        run_disable
    else
        run_enable
    fi
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    main "$@"
fi
