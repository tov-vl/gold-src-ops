#!/usr/bin/env bash

set -Eeuo pipefail
IFS=$'\n\t'
umask 027

readonly CONFIGURATION_DIRECTORY="/etc/goldsrcops/gameserver"
readonly PREPARED_MARKER="$CONFIGURATION_DIRECTORY/host-prepared"
readonly RUNTIME_ENABLED_MARKER="$CONFIGURATION_DIRECTORY/runtime-enabled"
readonly ACTIVE_PROFILE_MARKER="$CONFIGURATION_DIRECTORY/managed-profile-active"
readonly AUTOSTART_MARKER="$CONFIGURATION_DIRECTORY/guarded-autostart-active"
readonly PUBLIC_FIREWALL_MARKER="$CONFIGURATION_DIRECTORY/public-game-firewall-active"
readonly PUBLIC_CONFIGURATION="$CONFIGURATION_DIRECTORY/server-public.cfg"
readonly AUTOSTART_GUARD="/usr/local/libexec/goldsrcops-gameserver-boot-guard"
readonly BACKUP_ROOT="/var/backups/goldsrcops/gameserver"
readonly TRANSITION_LOCK="$CONFIGURATION_DIRECTORY/managed-profile.lock"
readonly SERVICE_NAME="goldsrcops-gameserver.service"
readonly AGENT_SERVICE_NAME="goldsrcops-game-event-agent.service"
readonly POLICY_ID="public-game-firewall-v1"

action="enable"
apply_changes=false
verify_requested=false
prepared_operator_user=""
prepared_service_user=""
prepared_ssh_port=""
prepared_game_port=""
service_group=""
rcon_source_cidr=""
backup_directory=""
public_rule_added=false

usage() {
    cat <<'EOF'
Usage:
  public-game-firewall.sh [--enable] [--apply]
  public-game-firewall.sh --disable [--apply]
  public-game-firewall.sh --verify

Without --apply, enable and disable print a sanitized plan without inspecting
or changing the host. Enable verifies the accepted public-classic-v1 runtime,
its non-empty exact ReHLDS RCON source list, and the restricted UFW baseline.
It then opens only the existing IPv4 game UDP port to players while preserving
exact-source SSH and application-layer RCON access. Disable removes only that
public IPv4 UDP rule and restores the restricted baseline.

Apply must run through sudo from the operator recorded by host bootstrap while
preserving SSH_CONNECTION. No address, credential, or secret is accepted as an
argument, printed, or written to the public marker.
EOF
}

fail() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }
log() { printf '%s\n' "$*"; }
require_command() { command -v "$1" >/dev/null 2>&1 || fail "Required command '$1' is unavailable."; }

validate_user_name() {
    [[ "$1" =~ ^[a-z_][a-z0-9_-]{0,31}$ && "$1" != "root" ]] ||
        fail "A recorded account name is invalid."
}

validate_port() {
    [[ "$1" =~ ^[0-9]+$ ]] || fail "A recorded port is invalid."
    ((10#$1 >= 1 && 10#$1 <= 65535)) || fail "A recorded port is invalid."
}

validate_ipv4_cidr() {
    local cidr="$1" address="${1%/32}" octet
    local octets=()
    [[ "$cidr" =~ ^([0-9]{1,3}\.){3}[0-9]{1,3}/32$ ]] ||
        fail "The RCON source policy is not an exact IPv4 /32."
    IFS='.' read -r -a octets <<< "$address"
    for octet in "${octets[@]}"; do
        ((10#$octet <= 255)) || fail "The RCON source policy contains an invalid address."
    done
}

validate_sha256() { [[ "$1" =~ ^[0-9a-f]{64}$ ]] || fail "A recorded SHA-256 value is invalid."; }
sha256_text() { printf '%s' "$1" | sha256sum | awk '{ print $1 }'; }

verify_file_sha256() {
    [[ "$(sha256sum "$1" | awk '{ print $1 }')" == "$2" ]] ||
        fail "A reviewed public-game firewall input has drifted."
}

validate_file_metadata() {
    local path="$1" owner="$2" group="$3" mode="$4"
    [[ -f "$path" && ! -L "$path" ]] || fail "A required file is missing or unsafe."
    [[ "$(stat -c '%U:%G:%a' "$path")" == "$owner:$group:$mode" ]] ||
        fail "A required file owner or mode has drifted."
}

validate_directory_metadata() {
    local path="$1" owner="$2" group="$3" mode="$4"
    [[ -d "$path" && ! -L "$path" ]] || fail "A required directory is missing or unsafe."
    [[ "$(stat -c '%U:%G:%a' "$path")" == "$owner:$group:$mode" ]] ||
        fail "A required directory owner or mode has drifted."
}

read_prepared_marker() {
    local key value schema_version=""
    declare -A seen=()
    while IFS='=' read -r key value || [[ -n "${key:-}${value:-}" ]]; do
        [[ -n "${key:-}" && -z "${seen[$key]+x}" ]] || fail "The host marker is malformed."
        seen[$key]=1
        case "$key" in
            schema_version) schema_version="$value" ;;
            operator_user) prepared_operator_user="$value" ;;
            service_user) prepared_service_user="$value" ;;
            ssh_port) prepared_ssh_port="$value" ;;
            game_port) prepared_game_port="$value" ;;
            *) fail "The host marker contains an unknown key." ;;
        esac
    done < "$PREPARED_MARKER"
    [[ "$schema_version" == "1" && "${#seen[@]}" -eq 5 ]] || fail "The host marker contract is invalid."
    validate_user_name "$prepared_operator_user"
    validate_user_name "$prepared_service_user"
    validate_port "$prepared_ssh_port"
    validate_port "$prepared_game_port"
    service_group="$(id -gn "$prepared_service_user")"
}

read_rcon_source_policy() {
    local policies=()
    mapfile -t policies < <(sed -nE 's/^rcon_adduser (([0-9]{1,3}\.){3}[0-9]{1,3}\/32)$/\1/p' "$PUBLIC_CONFIGURATION")
    [[ "${#policies[@]}" -eq 1 ]] || fail "The public configuration must contain one exact RCON source policy."
    rcon_source_cidr="${policies[0]}"
    validate_ipv4_cidr "$rcon_source_cidr"
    [[ "$(grep -Ec '^rcon_adduser ' "$PUBLIC_CONFIGURATION")" -eq 1 ]] ||
        fail "The public configuration contains an unsupported RCON source command."
}

read_inbound_allow_rules() {
    LC_ALL=C ufw status verbose | awk '
        $2 == "ALLOW" && $3 == "IN" { print $1 "|" $4 }
        $3 == "ALLOW" && $4 == "IN" { print $1 " " $2 "|" $5 }
    '
}

validate_firewall_rules() {
    local expected_mode="$1" source_address="${rcon_source_cidr%/32}"
    local expected_ssh_rule="$prepared_ssh_port/tcp|$source_address"
    local expected_game_rule="$prepared_game_port/udp|$source_address"
    local expected_public_rule="$prepared_game_port/udp|Anywhere"
    local ssh_count=0 game_count=0 public_count=0 rule
    local rules=()
    [[ "$expected_mode" == "restricted" || "$expected_mode" == "public" ]] ||
        fail "The requested firewall validation mode is invalid."
    mapfile -t rules < <(read_inbound_allow_rules)
    for rule in "${rules[@]}"; do
        case "$rule" in
            "$expected_ssh_rule") ((ssh_count += 1)) ;;
            "$expected_game_rule") ((game_count += 1)) ;;
            "$expected_public_rule") ((public_count += 1)) ;;
            *) fail "UFW contains an unexpected inbound allow rule." ;;
        esac
    done
    [[ "$ssh_count" -eq 1 && "$game_count" -eq 1 ]] ||
        fail "UFW must retain the exact control-plane SSH and game rules."
    if [[ "$expected_mode" == "restricted" ]]; then
        [[ "$public_count" -eq 0 && "${#rules[@]}" -eq 2 ]] ||
            fail "The restricted UFW baseline contains a public game rule."
    else
        [[ "$public_count" -eq 1 && "${#rules[@]}" -eq 3 ]] ||
            fail "The public UFW boundary is incomplete or duplicated."
    fi
}

verify_ufw_common() {
    local status
    status="$(LC_ALL=C ufw status verbose)"
    grep -Fxq 'Status: active' <<< "$status" || fail "UFW must remain active."
    grep -Fq 'Default: deny (incoming), allow (outgoing)' <<< "$status" ||
        fail "UFW default policies do not match the reviewed boundary."
}

validate_current_ssh_source() {
    local source_address source_port server_address server_port extra
    IFS=' ' read -r source_address source_port server_address server_port extra <<< "${SSH_CONNECTION:-}"
    [[ -n "${source_address:-}" && -n "${source_port:-}" && -n "${server_address:-}" &&
        -n "${server_port:-}" && -z "${extra:-}" ]] || fail "Apply requires valid preserved SSH connection metadata."
    [[ "$source_address/32" == "$rcon_source_cidr" ]] ||
        fail "The current SSH source does not match the ReHLDS RCON allowlist."
    [[ "$server_port" == "$prepared_ssh_port" ]] || fail "The current SSH session does not use the reviewed port."
}

verify_accepted_runtime() {
    local guard_output
    validate_file_metadata "$PREPARED_MARKER" root "$service_group" 640
    validate_file_metadata "$RUNTIME_ENABLED_MARKER" root "$service_group" 640
    validate_file_metadata "$ACTIVE_PROFILE_MARKER" root "$service_group" 640
    validate_file_metadata "$AUTOSTART_MARKER" root "$service_group" 640
    validate_file_metadata "$PUBLIC_CONFIGURATION" root "$service_group" 640
    validate_file_metadata "$AUTOSTART_GUARD" root root 755
    grep -Fxq 'profile_id=public-classic-v1' "$ACTIVE_PROFILE_MARKER" || fail "The accepted public-classic-v1 profile is not active."
    grep -Fxq 'policy_id=guarded-autostart-v1' "$AUTOSTART_MARKER" || fail "The accepted guarded-autostart policy is not active."
    grep -Fxq 'service_autostart=enabled' "$RUNTIME_ENABLED_MARKER" || fail "The game runtime is not guarded across boot."
    guard_output="$("$AUTOSTART_GUARD" --verify)"
    grep -Fxq 'GUARDED_AUTOSTART_GATE=passed' <<< "$guard_output" ||
        fail "The installed guarded-autostart verification did not pass."
    read_rcon_source_policy
    [[ "$(systemctl is-active "$SERVICE_NAME")" == "active" &&
        "$(systemctl is-enabled "$SERVICE_NAME" 2>/dev/null || true)" == "enabled" ]] ||
        fail "The game service must be active and enabled."
    [[ "$(systemctl is-active "$AGENT_SERVICE_NAME" 2>/dev/null || true)" == "inactive" &&
        "$(systemctl is-enabled "$AGENT_SERVICE_NAME" 2>/dev/null || true)" == "disabled" ]] ||
        fail "The game-event agent must remain inactive and disabled."
}

render_policy_marker() {
    local destination="$1" selected_backup_name="$2" baseline_ufw_sha256="$3"
    cat > "$destination" <<EOF
schema_version=1
policy_id=$POLICY_ID
backup_name=$selected_backup_name
game_port=$prepared_game_port
rcon_source_sha256=$(sha256_text "$rcon_source_cidr")
public_config_sha256=$(sha256sum "$PUBLIC_CONFIGURATION" | awk '{ print $1 }')
runtime_enabled_sha256=$(sha256sum "$RUNTIME_ENABLED_MARKER" | awk '{ print $1 }')
active_profile_sha256=$(sha256sum "$ACTIVE_PROFILE_MARKER" | awk '{ print $1 }')
guarded_autostart_sha256=$(sha256sum "$AUTOSTART_MARKER" | awk '{ print $1 }')
baseline_ufw_sha256=$baseline_ufw_sha256
EOF
    chmod 0640 -- "$destination"
}

read_policy_marker() {
    local key value schema_version="" policy_id=""
    declare -A seen=()
    marker_backup_name=""; marker_game_port=""; marker_rcon_source_sha256=""
    marker_public_config_sha256=""; marker_runtime_enabled_sha256=""
    marker_active_profile_sha256=""; marker_autostart_sha256=""; marker_baseline_ufw_sha256=""
    while IFS='=' read -r key value || [[ -n "${key:-}${value:-}" ]]; do
        [[ -n "${key:-}" && -z "${seen[$key]+x}" ]] || fail "The public-game firewall marker is malformed."
        seen[$key]=1
        case "$key" in
            schema_version) schema_version="$value" ;;
            policy_id) policy_id="$value" ;;
            backup_name) marker_backup_name="$value" ;;
            game_port) marker_game_port="$value" ;;
            rcon_source_sha256) marker_rcon_source_sha256="$value" ;;
            public_config_sha256) marker_public_config_sha256="$value" ;;
            runtime_enabled_sha256) marker_runtime_enabled_sha256="$value" ;;
            active_profile_sha256) marker_active_profile_sha256="$value" ;;
            guarded_autostart_sha256) marker_autostart_sha256="$value" ;;
            baseline_ufw_sha256) marker_baseline_ufw_sha256="$value" ;;
            *) fail "The public-game firewall marker contains an unknown key." ;;
        esac
    done < "$PUBLIC_FIREWALL_MARKER"
    [[ "$schema_version" == "1" && "$policy_id" == "$POLICY_ID" && "${#seen[@]}" -eq 10 ]] ||
        fail "The public-game firewall marker contract is invalid."
    [[ "$marker_backup_name" =~ ^public-game-firewall-[0-9]{8}T[0-9]{6}Z-[A-Za-z0-9]{6}$ ]] ||
        fail "The public-game firewall backup reference is invalid."
    validate_port "$marker_game_port"
    [[ "$marker_game_port" == "$prepared_game_port" ]] || fail "The recorded game port has drifted."
    validate_sha256 "$marker_rcon_source_sha256"; validate_sha256 "$marker_public_config_sha256"
    validate_sha256 "$marker_runtime_enabled_sha256"; validate_sha256 "$marker_active_profile_sha256"
    validate_sha256 "$marker_autostart_sha256"; validate_sha256 "$marker_baseline_ufw_sha256"
}

verify_public_state() {
    read_prepared_marker
    verify_accepted_runtime
    validate_file_metadata "$PUBLIC_FIREWALL_MARKER" root "$service_group" 640
    read_policy_marker
    [[ "$(sha256_text "$rcon_source_cidr")" == "$marker_rcon_source_sha256" ]] || fail "The exact RCON source policy has drifted."
    verify_file_sha256 "$PUBLIC_CONFIGURATION" "$marker_public_config_sha256"
    verify_file_sha256 "$RUNTIME_ENABLED_MARKER" "$marker_runtime_enabled_sha256"
    verify_file_sha256 "$ACTIVE_PROFILE_MARKER" "$marker_active_profile_sha256"
    verify_file_sha256 "$AUTOSTART_MARKER" "$marker_autostart_sha256"
    backup_directory="$BACKUP_ROOT/$marker_backup_name"
    validate_directory_metadata "$backup_directory" root root 700
    validate_file_metadata "$backup_directory/ufw-status.before" root root 600
    verify_file_sha256 "$backup_directory/ufw-status.before" "$marker_baseline_ufw_sha256"
    verify_ufw_common
    validate_firewall_rules public
    log "PUBLIC_GAME_FIREWALL_GATE=passed"
}

remove_public_rule() {
    ufw --force delete allow proto udp from 0.0.0.0/0 to any port "$prepared_game_port"
    public_rule_added=false
}

restore_failed_enable() {
    local exit_code="${1:-1}"
    trap - ERR HUP INT TERM
    [[ "$public_rule_added" == "false" ]] || remove_public_rule >/dev/null 2>&1 || true
    rm -f -- "$PUBLIC_FIREWALL_MARKER" 2>/dev/null || true
    [[ -z "$backup_directory" ]] || rm -rf -- "$backup_directory" 2>/dev/null || true
    log "ROLLBACK_ATTEMPTED: public UDP rule and owned marker were removed."
    exit "$exit_code"
}

require_apply_environment() {
    ((EUID == 0)) || fail "--apply must run as root."
    local command
    for command in awk basename chmod chown date flock getent grep id install mktemp rm sed sha256sum stat systemctl ufw; do
        require_command "$command"
    done
    read_prepared_marker
    [[ "${SUDO_USER:-}" == "$prepared_operator_user" ]] || fail "Run --apply through sudo from the recorded operator."
    verify_accepted_runtime
    validate_current_ssh_source
    verify_ufw_common
    exec 9>"$TRANSITION_LOCK"
    flock -n 9 || fail "Another game-host policy transition is already in progress."
}

run_enable() {
    require_apply_environment
    [[ ! -e "$PUBLIC_FIREWALL_MARKER" ]] || fail "The public-game firewall is already active."
    validate_firewall_rules restricted
    local invocation_id restart_count baseline_status baseline_sha256 staged_marker
    invocation_id="$(systemctl show "$SERVICE_NAME" -p InvocationID --value)"
    restart_count="$(systemctl show "$SERVICE_NAME" -p NRestarts --value)"
    backup_directory="$(mktemp -d "$BACKUP_ROOT/public-game-firewall-$(date -u '+%Y%m%dT%H%M%SZ')-XXXXXX")"
    chmod 0700 "$backup_directory"
    baseline_status="$backup_directory/ufw-status.before"
    LC_ALL=C ufw status verbose > "$baseline_status"
    chmod 0600 "$baseline_status"
    baseline_sha256="$(sha256sum "$baseline_status" | awk '{ print $1 }')"
    staged_marker="$backup_directory/public-game-firewall-active"
    render_policy_marker "$staged_marker" "$(basename "$backup_directory")" "$baseline_sha256"
    chown root:"$service_group" "$staged_marker"

    trap 'restore_failed_enable $?' ERR
    trap 'restore_failed_enable 129' HUP
    trap 'restore_failed_enable 130' INT
    trap 'restore_failed_enable 143' TERM
    ufw allow proto udp from 0.0.0.0/0 to any port "$prepared_game_port" comment "GoldSrcOps public game endpoint"
    public_rule_added=true
    verify_ufw_common
    validate_firewall_rules public
    install -o root -g "$service_group" -m 0640 -- "$staged_marker" "$PUBLIC_FIREWALL_MARKER"
    verify_public_state >/dev/null
    [[ "$(systemctl show "$SERVICE_NAME" -p InvocationID --value)" == "$invocation_id" &&
        "$(systemctl show "$SERVICE_NAME" -p NRestarts --value)" == "$restart_count" ]] ||
        fail "Publishing the game endpoint restarted the game process."
    trap - ERR HUP INT TERM
    log "PUBLIC_GAME_FIREWALL_ENABLED: IPv4 game UDP is public; SSH and ReHLDS RCON remain exact-source."
    log "SERVICE_STATE: game active/enabled without restart; agent inactive/disabled"
    log "NEXT_GATE: external A2S, unauthorized-source RCON rejection, control-plane RCON, and one operator connection."
}

run_disable() {
    require_apply_environment
    validate_file_metadata "$PUBLIC_FIREWALL_MARKER" root "$service_group" 640
    read_policy_marker
    [[ "$(sha256_text "$rcon_source_cidr")" == "$marker_rcon_source_sha256" ]] || fail "The exact RCON source policy has drifted."
    verify_file_sha256 "$PUBLIC_CONFIGURATION" "$marker_public_config_sha256"
    verify_file_sha256 "$RUNTIME_ENABLED_MARKER" "$marker_runtime_enabled_sha256"
    verify_file_sha256 "$ACTIVE_PROFILE_MARKER" "$marker_active_profile_sha256"
    verify_file_sha256 "$AUTOSTART_MARKER" "$marker_autostart_sha256"
    backup_directory="$BACKUP_ROOT/$marker_backup_name"
    validate_directory_metadata "$backup_directory" root root 700
    validate_file_metadata "$backup_directory/ufw-status.before" root root 600
    verify_file_sha256 "$backup_directory/ufw-status.before" "$marker_baseline_ufw_sha256"
    validate_firewall_rules public
    local invocation_id restart_count
    invocation_id="$(systemctl show "$SERVICE_NAME" -p InvocationID --value)"
    restart_count="$(systemctl show "$SERVICE_NAME" -p NRestarts --value)"
    remove_public_rule
    verify_ufw_common
    validate_firewall_rules restricted
    rm -f -- "$PUBLIC_FIREWALL_MARKER"
    rm -rf -- "$backup_directory"
    [[ "$(systemctl show "$SERVICE_NAME" -p InvocationID --value)" == "$invocation_id" &&
        "$(systemctl show "$SERVICE_NAME" -p NRestarts --value)" == "$restart_count" ]] ||
        fail "Restoring the restricted endpoint restarted the game process."
    log "PUBLIC_GAME_FIREWALL_DISABLED: restricted UFW baseline restored."
    log "SERVICE_STATE: game active/enabled without restart; agent inactive/disabled"
}

print_plan() {
    if [[ "$action" == "disable" ]]; then
        log "PLAN: verify the public marker, exact retained inputs, and owner-only baseline"
        log "PLAN: remove only the public IPv4 game UDP rule"
        log "PLAN: preserve exact-source SSH, ReHLDS RCON, game invocation, and guarded autostart"
        log "PLAN_ONLY: no host state was inspected or changed; add --apply to restore the restricted boundary."
    else
        log "PLAN: verify public-classic-v1, guarded autostart, and the inactive game-event agent"
        log "PLAN: bind the current control-plane SSH source to the existing exact ReHLDS RCON /32"
        log "PLAN: preserve restricted SSH and RCON while opening only IPv4 game UDP to players"
        log "PLAN: retain an owner-only rollback input and do not restart the game process"
        log "PLAN_ONLY: no host state was inspected or changed; add --apply to publish the game endpoint."
    fi
}

main() {
    while (($# > 0)); do
        case "$1" in
            --enable) [[ "$action" == "enable" && "$verify_requested" == "false" ]] || fail "Conflicting actions were requested."; action="enable" ;;
            --disable) [[ "$action" == "enable" && "$verify_requested" == "false" ]] || fail "Conflicting actions were requested."; action="disable" ;;
            --verify) [[ "$verify_requested" == "false" && "$action" == "enable" && "$apply_changes" == "false" ]] || fail "--verify cannot be combined with another action."; verify_requested=true ;;
            --apply) [[ "$verify_requested" == "false" ]] || fail "--verify cannot be combined with --apply."; apply_changes=true ;;
            -h|--help) usage; return 0 ;;
            *) fail "Unknown argument: $1" ;;
        esac
        shift
    done
    if [[ "$verify_requested" == "true" ]]; then
        verify_public_state
    elif [[ "$apply_changes" == "false" ]]; then
        print_plan
    elif [[ "$action" == "disable" ]]; then
        run_disable
    else
        run_enable
    fi
}

if [[ "${BASH_SOURCE[0]:-$0}" == "$0" ]]; then
    main "$@"
fi
