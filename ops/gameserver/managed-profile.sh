#!/usr/bin/env bash

set -Eeuo pipefail
IFS=$'\n\t'
umask 077

readonly SERVICE_NAME="goldsrcops-gameserver.service"
readonly CONFIGURATION_DIRECTORY="/etc/goldsrcops/gameserver"
readonly PREPARED_MARKER="$CONFIGURATION_DIRECTORY/host-prepared"
readonly RUNTIME_ENABLED_MARKER="$CONFIGURATION_DIRECTORY/runtime-enabled"
readonly ACTIVE_PROFILE_MARKER="$CONFIGURATION_DIRECTORY/managed-profile-active"
readonly PUBLIC_CONFIGURATION="$CONFIGURATION_DIRECTORY/server-public.cfg"
readonly BACKUP_ROOT="/var/backups/goldsrcops/gameserver"
readonly PROFILE_ID="public-classic-v1"
readonly PROFILE_MARKER="GoldSrcOps managed profile public-classic-v1 loaded"
readonly PUBLIC_CONFIGURATION_MARKER="GoldSrcOps public runtime configuration loaded"
readonly PROFILE_FILE_NAME="goldsrcops-managed-profile.cfg"
readonly MAPCYCLE_FILE_NAME="goldsrcops-mapcycle.txt"
readonly -a PROFILE_MAPS=(de_dust2 de_inferno de_nuke de_train cs_office)

apply_changes=false
rollback_requested=false
prepared_operator_user=""
prepared_service_user=""
prepared_game_port=""
runtime_marker_sha256=""
service_unit_sha256=""
service_group=""
service_home=""
game_directory=""
profile_file=""
mapcycle_file=""
staging_directory=""
backup_directory=""

usage() {
    cat <<'EOF'
Usage:
  managed-profile.sh [--apply]
  managed-profile.sh --rollback [--apply]

Without --apply, the script prints a sanitized plan and does not inspect or
change the host. Apply installs the fixed public-classic-v1 server profile and
map cycle into the reviewed active ReHLDS runtime. Rollback restores the exact
pre-profile public configuration and removes only files owned by this workflow.

Apply and rollback must run through sudo from the operator recorded by host
bootstrap while preserving SSH_CONNECTION. The game service remains disabled
across boot. No credential is accepted, read, copied, or printed.
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
    [[ "$1" =~ ^[a-z_][a-z0-9_-]{0,31}$ && "$1" != "root" ]] ||
        fail "The prepared account name is invalid."
}

validate_port() {
    [[ "$1" =~ ^[0-9]+$ ]] || fail "The prepared port is invalid."
    ((10#$1 >= 1 && 10#$1 <= 65535)) || fail "The prepared port is invalid."
}

validate_sha256() {
    [[ "$1" =~ ^[0-9a-f]{64}$ ]] || fail "A recorded SHA-256 value is invalid."
}

read_runtime_enabled_marker() {
    local marker_path="${1:-$RUNTIME_ENABLED_MARKER}"
    local public_path="${2:-$PUBLIC_CONFIGURATION}"
    local key value schema_version="" public_config_sha256=""
    local rcon_source_policy="" rcon_secret_transport="" service_autostart=""
    declare -A seen=()

    [[ -f "$marker_path" && ! -L "$marker_path" ]] ||
        fail "The reviewed runtime-enabled marker is missing or unsafe."
    [[ -f "$public_path" && ! -L "$public_path" ]] ||
        fail "The public runtime configuration is missing or unsafe."
    runtime_marker_sha256=""
    service_unit_sha256=""

    while IFS='=' read -r key value || [[ -n "${key:-}${value:-}" ]]; do
        [[ -n "${key:-}" && -z "${seen[$key]+x}" ]] ||
            fail "The runtime-enabled marker is malformed."
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
    done < "$marker_path"

    [[ "$schema_version" == "1" && "${#seen[@]}" -eq 7 ]] ||
        fail "The runtime-enabled marker contract is invalid."
    validate_sha256 "$runtime_marker_sha256"
    validate_sha256 "$service_unit_sha256"
    validate_sha256 "$public_config_sha256"
    [[ "$public_config_sha256" == \
        "$(sha256sum "$public_path" | awk '{ print $1 }')" ]] ||
        fail "The public runtime configuration has drifted from runtime-enabled."
    [[ "$rcon_source_policy" == "ssh-ufw-exact-ipv4-32" &&
        "$rcon_secret_transport" == "stdin" &&
        "$service_autostart" == "disabled" ]] ||
        fail "The runtime-enabled security boundary has drifted."
}

render_runtime_enabled_marker() {
    local destination="$1"
    local public_path="$2"

    cat > "$destination" <<EOF
schema_version=1
runtime_marker_sha256=$runtime_marker_sha256
service_unit_sha256=$service_unit_sha256
public_config_sha256=$(sha256sum "$public_path" | awk '{ print $1 }')
rcon_source_policy=ssh-ufw-exact-ipv4-32
rcon_secret_transport=stdin
service_autostart=disabled
EOF
    chmod 0640 -- "$destination"
}

read_prepared_marker() {
    local marker_path="${1:-$PREPARED_MARKER}"
    local key value schema_version="" ssh_port=""
    declare -A seen=()

    [[ -f "$marker_path" && ! -L "$marker_path" ]] ||
        fail "The reviewed game-host readiness marker is missing or unsafe."
    prepared_operator_user=""
    prepared_service_user=""
    prepared_game_port=""

    while IFS='=' read -r key value || [[ -n "${key:-}${value:-}" ]]; do
        [[ -n "${key:-}" && -z "${seen[$key]+x}" ]] ||
            fail "The game-host readiness marker is malformed."
        seen[$key]=1
        case "$key" in
            schema_version) schema_version="$value" ;;
            operator_user) prepared_operator_user="$value" ;;
            service_user) prepared_service_user="$value" ;;
            ssh_port) ssh_port="$value" ;;
            game_port) prepared_game_port="$value" ;;
            *) fail "The game-host readiness marker contains an unknown key." ;;
        esac
    done < "$marker_path"

    [[ "$schema_version" == "1" && "${#seen[@]}" -eq 5 ]] ||
        fail "The game-host readiness marker contract is invalid."
    validate_user_name "$prepared_operator_user"
    validate_user_name "$prepared_service_user"
    validate_port "$ssh_port"
    validate_port "$prepared_game_port"
}

render_profile() {
    local destination="$1"

    cat > "$destination" <<EOF
hostname "GoldSrcOps Public Classic"
mp_timelimit "25"
mp_roundtime "2.5"
mp_freezetime "5"
mp_startmoney "800"
mp_friendlyfire "0"
mp_autoteambalance "1"
mp_limitteams "2"
mp_c4timer "35"
mp_buytime "0.5"
mapcyclefile "$MAPCYCLE_FILE_NAME"
echo "$PROFILE_MARKER"
EOF
    chmod 0640 -- "$destination"
}

render_mapcycle() {
    local destination="$1"
    local map_name

    for map_name in "${PROFILE_MAPS[@]}"; do
        printf '%s\n' "$map_name"
    done > "$destination"
    chmod 0640 -- "$destination"
}

render_managed_public_configuration() {
    local source="$1"
    local destination="$2"

    awk -v marker="$PUBLIC_CONFIGURATION_MARKER" -v profile="$PROFILE_FILE_NAME" '
        $0 == "echo \"" marker "\"" {
            print "exec " profile
            print
            inserted = 1
            next
        }
        { print }
        END { if (!inserted) exit 42 }
    ' "$source" > "$destination" ||
        fail "The baseline public configuration marker is missing."
    chmod 0640 -- "$destination"
}

validate_baseline_public_configuration() {
    local path="${1:-$PUBLIC_CONFIGURATION}"

    [[ -f "$path" && ! -L "$path" ]] ||
        fail "The public runtime configuration is missing or unsafe."
    [[ "$(stat -c '%U:%G:%a' -- "$path")" == "root:$service_group:640" ]] ||
        fail "The public runtime configuration owner or mode has drifted."
    [[ "$(grep -Fxc 'exec goldsrcops-private.cfg' "$path")" -eq 1 ]] ||
        fail "The private configuration load contract has drifted."
    [[ "$(grep -Fxc "echo \"$PUBLIC_CONFIGURATION_MARKER\"" "$path")" -eq 1 ]] ||
        fail "The public configuration marker contract has drifted."
    [[ "$(grep -Ec '^rcon_adduser ([0-9]{1,3}\.){3}[0-9]{1,3}/32$' "$path")" -eq 1 ]] ||
        fail "The exact-source RCON contract has drifted."
    ! grep -Fq 'rcon_password' "$path" ||
        fail "The public runtime configuration contains a credential command."
    ! grep -Fq "exec $PROFILE_FILE_NAME" "$path" ||
        fail "The baseline public configuration still loads the managed profile."
}

read_active_profile_marker() {
    local marker_path="${1:-$ACTIVE_PROFILE_MARKER}"
    local key value schema_version="" profile_id="" backup_name=""
    local baseline_public_sha256="" baseline_runtime_enabled_sha256=""
    local public_sha256="" runtime_enabled_sha256="" profile_sha256=""
    local mapcycle_sha256="" active_path backup_public_configuration
    local backup_runtime_enabled_marker
    declare -A seen=()

    [[ -f "$marker_path" && ! -L "$marker_path" ]] ||
        fail "The managed-profile marker is missing or unsafe."
    while IFS='=' read -r key value || [[ -n "${key:-}${value:-}" ]]; do
        [[ -n "${key:-}" && -z "${seen[$key]+x}" ]] ||
            fail "The managed-profile marker is malformed."
        seen[$key]=1
        case "$key" in
            schema_version) schema_version="$value" ;;
            profile_id) profile_id="$value" ;;
            backup_name) backup_name="$value" ;;
            baseline_public_sha256) baseline_public_sha256="$value" ;;
            baseline_runtime_enabled_sha256) baseline_runtime_enabled_sha256="$value" ;;
            public_sha256) public_sha256="$value" ;;
            runtime_enabled_sha256) runtime_enabled_sha256="$value" ;;
            profile_sha256) profile_sha256="$value" ;;
            mapcycle_sha256) mapcycle_sha256="$value" ;;
            *) fail "The managed-profile marker contains an unknown key." ;;
        esac
    done < "$marker_path"

    [[ "$schema_version" == "1" && "$profile_id" == "$PROFILE_ID" &&
        "${#seen[@]}" -eq 9 ]] || fail "The managed-profile marker contract is invalid."
    [[ "$backup_name" =~ ^managed-profile-[0-9]{8}T[0-9]{6}Z-[A-Za-z0-9]{6}$ ]] ||
        fail "The managed-profile backup reference is invalid."
    validate_sha256 "$baseline_public_sha256"
    validate_sha256 "$baseline_runtime_enabled_sha256"
    validate_sha256 "$public_sha256"
    validate_sha256 "$runtime_enabled_sha256"
    validate_sha256 "$profile_sha256"
    validate_sha256 "$mapcycle_sha256"

    backup_directory="$BACKUP_ROOT/$backup_name"
    [[ -d "$backup_directory" && ! -L "$backup_directory" ]] ||
        fail "The managed-profile rollback backup is missing or unsafe."
    [[ "$(stat -c '%U:%G:%a' -- "$backup_directory")" == "root:root:700" ]] ||
        fail "The managed-profile rollback backup owner or mode has drifted."
    backup_public_configuration="$backup_directory/server-public.cfg"
    backup_runtime_enabled_marker="$backup_directory/runtime-enabled"
    [[ -f "$backup_public_configuration" && ! -L "$backup_public_configuration" ]] ||
        fail "The managed-profile rollback configuration is missing or unsafe."
    [[ -f "$backup_runtime_enabled_marker" && ! -L "$backup_runtime_enabled_marker" ]] ||
        fail "The managed-profile rollback runtime marker is missing or unsafe."
    [[ "$(stat -c '%U:%G:%a' -- "$backup_public_configuration")" == "root:root:600" ]] ||
        fail "The managed-profile rollback configuration owner or mode has drifted."
    [[ "$(stat -c '%U:%G:%a' -- "$backup_runtime_enabled_marker")" == "root:root:600" ]] ||
        fail "The managed-profile rollback runtime marker owner or mode has drifted."
    [[ "$(sha256sum "$backup_public_configuration" | awk '{ print $1 }')" == \
        "$baseline_public_sha256" ]] ||
        fail "The managed-profile rollback configuration has drifted."
    [[ "$(sha256sum "$backup_runtime_enabled_marker" | awk '{ print $1 }')" == \
        "$baseline_runtime_enabled_sha256" ]] ||
        fail "The managed-profile rollback runtime marker has drifted."
    for active_path in "$PUBLIC_CONFIGURATION" "$RUNTIME_ENABLED_MARKER" \
        "$profile_file" "$mapcycle_file"; do
        [[ -f "$active_path" && ! -L "$active_path" ]] ||
            fail "An active managed-profile file is missing or unsafe."
    done
    [[ "$(sha256sum "$PUBLIC_CONFIGURATION" | awk '{ print $1 }')" == "$public_sha256" ]] ||
        fail "The active public configuration has drifted."
    [[ "$(sha256sum "$RUNTIME_ENABLED_MARKER" | awk '{ print $1 }')" == \
        "$runtime_enabled_sha256" ]] ||
        fail "The active runtime-enabled marker has drifted."
    [[ "$(sha256sum "$profile_file" | awk '{ print $1 }')" == "$profile_sha256" ]] ||
        fail "The active managed profile has drifted."
    [[ "$(sha256sum "$mapcycle_file" | awk '{ print $1 }')" == "$mapcycle_sha256" ]] ||
        fail "The active managed map cycle has drifted."
}

require_apply_environment() {
    local invoking_user

    ((EUID == 0)) || fail "--apply must run as root."
    for command in awk basename chmod chown date getent grep id install journalctl \
        mktemp pgrep rm sha256sum ss stat systemctl wc; do
        require_command "$command"
    done
    read_prepared_marker
    invoking_user="${SUDO_USER:-}"
    [[ "$invoking_user" == "$prepared_operator_user" ]] ||
        fail "Run --apply through sudo from the operator recorded by host bootstrap."
    [[ -n "${SSH_CONNECTION:-}" ]] ||
        fail "Apply requires preserved SSH_CONNECTION metadata."

    service_group="$(id -gn "$prepared_service_user")"
    service_home="$(getent passwd "$prepared_service_user" | cut -d: -f6)"
    [[ "$service_home" == "/var/lib/$prepared_service_user" ]] ||
        fail "The service account has an unexpected home directory."
    game_directory="$service_home/server/cstrike"
    profile_file="$game_directory/$PROFILE_FILE_NAME"
    mapcycle_file="$game_directory/$MAPCYCLE_FILE_NAME"

    [[ -f "$RUNTIME_ENABLED_MARKER" && ! -L "$RUNTIME_ENABLED_MARKER" ]] ||
        fail "The reviewed game runtime is not active."
    [[ -d "$game_directory" && ! -L "$game_directory" ]] ||
        fail "The reviewed game directory is missing or unsafe."
    [[ ! -e "$CONFIGURATION_DIRECTORY/game-event-pilot-enabled" ]] ||
        fail "The gameplay pilot must be rolled back before changing the managed profile."
    [[ ! -e "$game_directory/addons/metamod" &&
        ! -e "$game_directory/addons/amxmodx" ]] ||
        fail "The active game runtime is not plugin-free."
    [[ "$(systemctl is-active "$SERVICE_NAME" 2>/dev/null || true)" == "active" ]] ||
        fail "The game-server service must be active before this transition."
    [[ "$(systemctl is-enabled "$SERVICE_NAME" 2>/dev/null || true)" == "disabled" ]] ||
        fail "The game-server service must remain disabled across boot."
}

prepare_apply() {
    local map_name

    [[ ! -e "$ACTIVE_PROFILE_MARKER" && ! -L "$ACTIVE_PROFILE_MARKER" ]] ||
        fail "A managed profile is already active."
    [[ ! -e "$profile_file" && ! -L "$profile_file" ]] ||
        fail "The managed profile path is already occupied."
    [[ ! -e "$mapcycle_file" && ! -L "$mapcycle_file" ]] ||
        fail "The managed map-cycle path is already occupied."
    for map_name in "${PROFILE_MAPS[@]}"; do
        [[ -f "$game_directory/maps/$map_name.bsp" &&
            ! -L "$game_directory/maps/$map_name.bsp" ]] ||
            fail "Required managed-profile map '$map_name' is missing or unsafe."
    done
    validate_baseline_public_configuration
    read_runtime_enabled_marker

    staging_directory="$(mktemp -d "$CONFIGURATION_DIRECTORY/.managed-profile.XXXXXX")"
    render_profile "$staging_directory/$PROFILE_FILE_NAME"
    render_mapcycle "$staging_directory/$MAPCYCLE_FILE_NAME"
    render_managed_public_configuration "$PUBLIC_CONFIGURATION" \
        "$staging_directory/server-public.cfg"
    render_runtime_enabled_marker "$staging_directory/runtime-enabled" \
        "$staging_directory/server-public.cfg"

    backup_directory="$(mktemp -d \
        "$BACKUP_ROOT/managed-profile-$(date -u '+%Y%m%dT%H%M%SZ')-XXXXXX")"
    chown root:root -- "$backup_directory"
    chmod 0700 -- "$backup_directory"
    install -o root -g root -m 0600 -- "$PUBLIC_CONFIGURATION" \
        "$backup_directory/server-public.cfg"
    install -o root -g root -m 0600 -- "$RUNTIME_ENABLED_MARKER" \
        "$backup_directory/runtime-enabled"
}

install_profile_files() {
    local marker_staging="$staging_directory/managed-profile-active"
    local baseline_public_sha256 baseline_runtime_enabled_sha256
    local public_sha256 runtime_enabled_sha256 profile_sha256 mapcycle_sha256

    systemctl stop "$SERVICE_NAME"
    install -o root -g "$service_group" -m 0640 -- \
        "$staging_directory/$PROFILE_FILE_NAME" "$profile_file"
    install -o root -g "$service_group" -m 0640 -- \
        "$staging_directory/$MAPCYCLE_FILE_NAME" "$mapcycle_file"
    install -o root -g "$service_group" -m 0640 -- \
        "$staging_directory/server-public.cfg" "$PUBLIC_CONFIGURATION"
    install -o root -g "$service_group" -m 0640 -- \
        "$staging_directory/runtime-enabled" "$RUNTIME_ENABLED_MARKER"

    baseline_public_sha256="$(sha256sum \
        "$backup_directory/server-public.cfg" | awk '{ print $1 }')"
    baseline_runtime_enabled_sha256="$(sha256sum \
        "$backup_directory/runtime-enabled" | awk '{ print $1 }')"
    public_sha256="$(sha256sum "$PUBLIC_CONFIGURATION" | awk '{ print $1 }')"
    runtime_enabled_sha256="$(sha256sum "$RUNTIME_ENABLED_MARKER" | awk '{ print $1 }')"
    profile_sha256="$(sha256sum "$profile_file" | awk '{ print $1 }')"
    mapcycle_sha256="$(sha256sum "$mapcycle_file" | awk '{ print $1 }')"
    cat > "$marker_staging" <<EOF
schema_version=1
profile_id=$PROFILE_ID
backup_name=$(basename "$backup_directory")
baseline_public_sha256=$baseline_public_sha256
baseline_runtime_enabled_sha256=$baseline_runtime_enabled_sha256
public_sha256=$public_sha256
runtime_enabled_sha256=$runtime_enabled_sha256
profile_sha256=$profile_sha256
mapcycle_sha256=$mapcycle_sha256
EOF
    install -o root -g "$service_group" -m 0640 -- "$marker_staging" \
        "$ACTIVE_PROFILE_MARKER"

    systemctl start "$SERVICE_NAME"
    verify_profile_start
}

verify_profile_start() {
    local invocation_id journal_output listener_count process_count

    read_runtime_enabled_marker
    [[ "$(systemctl is-active "$SERVICE_NAME")" == "active" ]] ||
        fail "The game-server service did not become active."
    [[ "$(systemctl is-enabled "$SERVICE_NAME" 2>/dev/null || true)" == "disabled" ]] ||
        fail "The game-server service became enabled across boot."
    [[ "$(systemctl show "$SERVICE_NAME" -p NRestarts --value)" == "0" ]] ||
        fail "The managed profile start triggered an automatic restart."
    invocation_id="$(systemctl show "$SERVICE_NAME" -p InvocationID --value)"
    [[ "$invocation_id" =~ ^[0-9A-Fa-f]{32}$ ]] ||
        fail "The current service invocation identifier is invalid."
    journal_output="$(journalctl -u "$SERVICE_NAME" \
        _SYSTEMD_INVOCATION_ID="$invocation_id" --no-pager -o cat)"
    grep -Fq "$PUBLIC_CONFIGURATION_MARKER" <<< "$journal_output" ||
        fail "The public runtime configuration was not loaded by the current process."
    grep -Fq "$PROFILE_MARKER" <<< "$journal_output" ||
        fail "The managed profile was not loaded by the current process."
    process_count="$(pgrep -u "$prepared_service_user" -x hlds_linux | wc -l)"
    [[ "$process_count" -eq 1 ]] ||
        fail "The reviewed game process count is not exactly one."
    listener_count="$(ss -H -lun "sport = :$prepared_game_port" | \
        awk 'NF { count++ } END { print count + 0 }')"
    [[ "$listener_count" -eq 1 ]] ||
        fail "The reviewed game UDP listener count is not exactly one."
}

restore_baseline() {
    local exit_code="${1:-1}"
    local rollback_failed=false

    trap - ERR HUP INT TERM
    if ! systemctl stop "$SERVICE_NAME" >/dev/null 2>&1; then
        rollback_failed=true
    fi
    if [[ -n "$backup_directory" && -f "$backup_directory/server-public.cfg" ]]; then
        if ! install -o root -g "$service_group" -m 0640 -- \
            "$backup_directory/server-public.cfg" "$PUBLIC_CONFIGURATION"; then
            rollback_failed=true
        fi
    else
        rollback_failed=true
    fi
    if [[ -n "$backup_directory" && -f "$backup_directory/runtime-enabled" ]]; then
        if ! install -o root -g "$service_group" -m 0640 -- \
            "$backup_directory/runtime-enabled" "$RUNTIME_ENABLED_MARKER"; then
            rollback_failed=true
        fi
    else
        rollback_failed=true
    fi
    if ! rm -f -- "$ACTIVE_PROFILE_MARKER" "$profile_file" "$mapcycle_file"; then
        rollback_failed=true
    fi
    if ! systemctl start "$SERVICE_NAME" >/dev/null 2>&1; then
        rollback_failed=true
    fi
    [[ -z "$staging_directory" ]] || rm -rf -- "$staging_directory"

    if [[ "$rollback_failed" == "true" ]]; then
        log "ROLLBACK_FAILED: manual recovery from the owner-only backup is required."
        exit 1
    fi

    if ((exit_code != 0)); then
        log "ROLLBACK_ATTEMPTED: restored the pre-profile public configuration."
        exit "$exit_code"
    fi
}

run_apply() {
    require_apply_environment
    prepare_apply
    trap 'restore_baseline $?' ERR
    trap 'restore_baseline 129' HUP
    trap 'restore_baseline 130' INT
    trap 'restore_baseline 143' TERM
    install_profile_files
    trap - ERR HUP INT TERM
    rm -rf -- "$staging_directory"
    log "PROFILE_ACTIVATED: $PROFILE_ID"
    log "SERVICE_STATE: active and disabled"
    log "NEXT_GATE: verify external A2S, active rules, map rotation, and zero bots."
}

run_rollback() {
    require_apply_environment
    read_active_profile_marker
    restore_baseline 0
    validate_baseline_public_configuration
    read_runtime_enabled_marker
    [[ "$(systemctl is-active "$SERVICE_NAME")" == "active" ]] ||
        fail "The game-server service did not recover after profile rollback."
    log "PROFILE_ROLLED_BACK: $PROFILE_ID"
    log "SERVICE_STATE: active and disabled"
}

print_plan() {
    if [[ "$rollback_requested" == "true" ]]; then
        log "PLAN: verify the active managed-profile marker and exact installed hashes"
        log "PLAN: stop the game, restore the exact pre-profile public configuration, and remove only profile-owned files"
        log "PLAN: start the plugin-free baseline and leave the service disabled across boot"
        log "PLAN_ONLY: no host state was inspected or changed; add --apply to execute rollback."
    else
        log "PLAN: verify the active plugin-free runtime and exact baseline public configuration"
        log "PLAN: preserve an owner-only exact rollback copy before changing the game tree"
        log "PLAN: install the fixed public-classic-v1 rules and five-map rotation without credentials or plugins"
        log "PLAN: restart once, require current-invocation load markers, and leave the service disabled across boot"
        log "PLAN: on any failed transition, restore the same pre-profile baseline before returning failure"
        log "PLAN_ONLY: no host state was inspected or changed; add --apply to activate the profile."
    fi
}

main() {
    while (($# > 0)); do
        case "$1" in
            --apply) apply_changes=true ;;
            --rollback) rollback_requested=true ;;
            --help|-h) usage; exit 0 ;;
            *) fail "Unknown argument." ;;
        esac
        shift
    done

    if [[ "$apply_changes" == "true" ]]; then
        if [[ "$rollback_requested" == "true" ]]; then
            run_rollback
        else
            run_apply
        fi
    else
        print_plan
    fi
}

if [[ "${BASH_SOURCE[0]:-$0}" == "$0" ]]; then
    main "$@"
fi
