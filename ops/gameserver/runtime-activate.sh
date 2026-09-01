#!/usr/bin/env bash

set -Eeuo pipefail
IFS=$'\n\t'
umask 077

readonly SERVICE_NAME="goldsrcops-gameserver.service"
readonly CONFIGURATION_DIRECTORY="/etc/goldsrcops/gameserver"
readonly PREPARED_MARKER="$CONFIGURATION_DIRECTORY/host-prepared"
readonly RUNTIME_MARKER="$CONFIGURATION_DIRECTORY/runtime-installed"
readonly RUNTIME_ENABLED_MARKER="$CONFIGURATION_DIRECTORY/runtime-enabled"
readonly PUBLIC_CONFIGURATION="$CONFIGURATION_DIRECTORY/server-public.cfg"
readonly PRIVATE_CONFIGURATION="$CONFIGURATION_DIRECTORY/secrets/server-private.cfg"
readonly SYSTEMD_UNIT_FILE="/etc/systemd/system/$SERVICE_NAME"
readonly SERVER_NAME="GoldSrcOps Controlled Baseline"
readonly PUBLIC_CONFIGURATION_MARKER="GoldSrcOps public runtime configuration loaded"
readonly PRIVATE_CONFIGURATION_MARKER="GoldSrcOps private runtime configuration loaded"
readonly EXPECTED_REHLDS_VERSION="3.15.0.896"
readonly EXPECTED_REGAMEDLL_VERSION="5.30.0.814"

apply_changes=false
read_secret_from_stdin=false
prepared_operator_user=""
prepared_service_user=""
prepared_ssh_port=""
prepared_game_port=""
service_group=""
service_home=""
approved_rcon_cidr=""
rcon_secret=""
staging_directory=""
activation_started=false

usage() {
    cat <<'EOF'
Usage:
  runtime-activate.sh [--apply --rcon-secret-stdin]

Without --apply, the script prints a sanitized plan and does not read stdin or
change the host. Apply must run through sudo from the operator recorded by the
game-host foundation while preserving SSH_CONNECTION:

  <secret-producer> | sudo --preserve-env=SSH_CONNECTION \
    bash ./runtime-activate.sh --apply --rcon-secret-stdin

The secret producer must emit one 32-128 character Base64-safe RCON password.
Apply derives the approved exact IPv4 /32 from the current SSH source and the
existing UFW boundary. It creates the public/private configuration and activation
marker, starts the reviewed unit, and leaves the unit disabled across reboot.

The script never accepts the RCON password as an argument or environment value,
never prints it, and removes all activation files if first start fails.
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
    local option_name="$1"
    local value="$2"

    [[ "$value" =~ ^[a-z_][a-z0-9_-]{0,31}$ ]] ||
        fail "$option_name must be a valid local Linux account name."
    [[ "$value" != "root" ]] || fail "$option_name must not be root."
}

validate_port() {
    local option_name="$1"
    local value="$2"

    [[ "$value" =~ ^[0-9]+$ ]] || fail "$option_name must be numeric."
    ((10#$value >= 1 && 10#$value <= 65535)) ||
        fail "$option_name must be between 1 and 65535."
}

validate_ipv4_address() {
    local value="$1"
    local octets=()
    local octet
    local IFS='.'

    read -r -a octets <<< "$value"
    [[ "${#octets[@]}" -eq 4 ]] || fail "The SSH source must be an IPv4 address."
    for octet in "${octets[@]}"; do
        [[ "$octet" =~ ^(0|[1-9][0-9]{0,2})$ ]] ||
            fail "The SSH source must be an unambiguous IPv4 address."
        ((10#$octet <= 255)) || fail "The SSH source contains an invalid IPv4 octet."
    done
}

validate_rcon_secret() {
    local value="$1"

    ((${#value} >= 32 && ${#value} <= 128)) ||
        fail "The RCON secret must contain between 32 and 128 characters."
    [[ "$value" =~ ^[A-Za-z0-9+/=_-]+$ ]] ||
        fail "The RCON secret must use only the reviewed Base64-safe alphabet."
}

read_prepared_marker() {
    local marker_path="${1:-$PREPARED_MARKER}"
    local key value
    local prepared_schema_version=""
    declare -A seen=()

    [[ -f "$marker_path" && ! -L "$marker_path" ]] ||
        fail "The reviewed game-host readiness marker is missing or unsafe."

    prepared_operator_user=""
    prepared_service_user=""
    prepared_ssh_port=""
    prepared_game_port=""

    while IFS='=' read -r key value || [[ -n "${key:-}${value:-}" ]]; do
        [[ -n "${key:-}" ]] || fail "The game-host readiness marker contains an empty key."
        [[ -z "${seen[$key]+x}" ]] ||
            fail "The game-host readiness marker contains a duplicate key."
        seen[$key]=1

        case "$key" in
            schema_version)
                prepared_schema_version="$value"
                ;;
            operator_user)
                prepared_operator_user="$value"
                ;;
            service_user)
                prepared_service_user="$value"
                ;;
            ssh_port)
                prepared_ssh_port="$value"
                ;;
            game_port)
                prepared_game_port="$value"
                ;;
            *)
                fail "The game-host readiness marker contains an unknown key."
                ;;
        esac
    done < "$marker_path"

    [[ "$prepared_schema_version" == "1" ]] ||
        fail "The game-host readiness marker schema is unsupported."
    validate_user_name "host-prepared operator_user" "$prepared_operator_user"
    validate_user_name "host-prepared service_user" "$prepared_service_user"
    validate_port "host-prepared ssh_port" "$prepared_ssh_port"
    validate_port "host-prepared game_port" "$prepared_game_port"
    [[ "${#seen[@]}" -eq 5 ]] || fail "The game-host readiness marker is incomplete."
}

read_runtime_marker() {
    local marker_path="${1:-$RUNTIME_MARKER}"
    local key value
    declare -gA runtime_values=()
    declare -A seen=()
    local expected_keys=(
        schema_version
        steam_app_id
        steam_branch
        steamcmd_bootstrap_sha256
        steamcmd_script_sha256
        steamcmd_binary_sha256
        steamclient_binary_sha256
        hlds_build_id
        hlds_app_manifest_sha256
        base_hlds_linux_sha256
        rehlds_version
        rehlds_archive_sha256
        rehlds_hlds_linux_sha256
        rehlds_engine_sha256
        regamedll_version
        regamedll_archive_sha256
        regamedll_binary_sha256
        service_unit_sha256
    )

    [[ -f "$marker_path" && ! -L "$marker_path" ]] ||
        fail "The reviewed runtime marker is missing or unsafe."

    while IFS='=' read -r key value || [[ -n "${key:-}${value:-}" ]]; do
        [[ -n "${key:-}" ]] || fail "The runtime marker contains an empty key."
        [[ -z "${seen[$key]+x}" ]] || fail "The runtime marker contains a duplicate key."
        case "$key" in
            schema_version|steam_app_id|steam_branch|steamcmd_bootstrap_sha256|\
                steamcmd_script_sha256|steamcmd_binary_sha256|steamclient_binary_sha256|\
                hlds_build_id|hlds_app_manifest_sha256|base_hlds_linux_sha256|\
                rehlds_version|rehlds_archive_sha256|rehlds_hlds_linux_sha256|\
                rehlds_engine_sha256|regamedll_version|regamedll_archive_sha256|\
                regamedll_binary_sha256|service_unit_sha256)
                runtime_values[$key]="$value"
                seen[$key]=1
                ;;
            *)
                fail "The runtime marker contains an unknown key."
                ;;
        esac
    done < "$marker_path"

    for key in "${expected_keys[@]}"; do
        [[ -n "${seen[$key]+x}" ]] || fail "The runtime marker is incomplete."
    done
    [[ "${#seen[@]}" -eq "${#expected_keys[@]}" ]] || fail "The runtime marker is invalid."

    [[ "${runtime_values[schema_version]}" == "1" ]] || fail "The runtime marker schema is unsupported."
    [[ "${runtime_values[steam_app_id]}" == "90" ]] || fail "The Steam app identity changed."
    [[ "${runtime_values[steam_branch]}" == "steam_legacy" ]] || fail "The Steam branch changed."
    [[ "${runtime_values[hlds_build_id]}" =~ ^[0-9]+$ ]] || fail "The HLDS build identity is invalid."
    [[ "${runtime_values[rehlds_version]}" == "$EXPECTED_REHLDS_VERSION" ]] ||
        fail "The ReHLDS version changed after review."
    [[ "${runtime_values[regamedll_version]}" == "$EXPECTED_REGAMEDLL_VERSION" ]] ||
        fail "The ReGameDLL_CS version changed after review."

    for key in \
        steamcmd_bootstrap_sha256 \
        steamcmd_script_sha256 \
        steamcmd_binary_sha256 \
        steamclient_binary_sha256 \
        hlds_app_manifest_sha256 \
        base_hlds_linux_sha256 \
        rehlds_archive_sha256 \
        rehlds_hlds_linux_sha256 \
        rehlds_engine_sha256 \
        regamedll_archive_sha256 \
        regamedll_binary_sha256 \
        service_unit_sha256; do
        [[ "${runtime_values[$key]}" =~ ^[0-9a-f]{64}$ ]] ||
            fail "The runtime marker contains an invalid SHA-256 value."
    done
}

validate_file_metadata() {
    local path="$1"
    local expected_owner="$2"
    local expected_group="$3"
    local expected_mode="$4"

    [[ -f "$path" && ! -L "$path" ]] || fail "Required file '$path' is missing or unsafe."
    [[ "$(stat -c '%U:%G:%a' "$path")" == "$expected_owner:$expected_group:$expected_mode" ]] ||
        fail "File '$path' does not match the reviewed owner and mode."
}

validate_directory_metadata() {
    local path="$1"
    local expected_owner="$2"
    local expected_group="$3"
    local expected_mode="$4"

    [[ -d "$path" && ! -L "$path" ]] || fail "Required directory '$path' is missing or unsafe."
    [[ "$(stat -c '%U:%G:%a' "$path")" == "$expected_owner:$expected_group:$expected_mode" ]] ||
        fail "Directory '$path' does not match the reviewed owner and mode."
}

verify_file_sha256() {
    local path="$1"
    local expected_sha256="$2"

    [[ -f "$path" && ! -L "$path" ]] || fail "Required runtime file '$path' is missing or unsafe."
    [[ "$(sha256sum "$path" | awk '{ print $1 }')" == "$expected_sha256" ]] ||
        fail "A reviewed runtime file changed after installation."
}

validate_runtime_identity() {
    validate_file_metadata "$PREPARED_MARKER" root "$service_group" 640
    validate_file_metadata "$RUNTIME_MARKER" root "$service_group" 640
    validate_file_metadata "$SYSTEMD_UNIT_FILE" root root 644
    validate_directory_metadata "$CONFIGURATION_DIRECTORY" root "$service_group" 750
    validate_directory_metadata "$CONFIGURATION_DIRECTORY/secrets" root "$service_group" 710
    validate_directory_metadata "$service_home" "$prepared_service_user" "$service_group" 750
    validate_directory_metadata "$service_home/steamcmd" "$prepared_service_user" "$service_group" 750
    validate_directory_metadata "$service_home/server" "$prepared_service_user" "$service_group" 750

    verify_file_sha256 "$service_home/steamcmd/steamcmd.sh" "${runtime_values[steamcmd_script_sha256]}"
    verify_file_sha256 "$service_home/steamcmd/linux32/steamcmd" "${runtime_values[steamcmd_binary_sha256]}"
    verify_file_sha256 "$service_home/steamcmd/linux32/steamclient.so" "${runtime_values[steamclient_binary_sha256]}"
    verify_file_sha256 "$service_home/server/hlds_linux" "${runtime_values[rehlds_hlds_linux_sha256]}"
    verify_file_sha256 "$service_home/server/engine_i486.so" "${runtime_values[rehlds_engine_sha256]}"
    verify_file_sha256 "$service_home/server/cstrike/dlls/cs.so" "${runtime_values[regamedll_binary_sha256]}"
    verify_file_sha256 "$SYSTEMD_UNIT_FILE" "${runtime_values[service_unit_sha256]}"
    [[ -x "$service_home/server/hlds_run" ]] || fail "The HLDS launcher is missing or not executable."

    local excluded_path
    for excluded_path in \
        addons/metamod \
        addons/amxmodx \
        addons/yapb \
        addons/reapi \
        addons/reunion; do
        [[ ! -e "$service_home/server/cstrike/$excluded_path" ]] ||
            fail "The minimal runtime contains an excluded extension."
    done
    ! grep -Eiq 'metamod|amxmodx|reapi|yapb|reunion' "$service_home/server/cstrike/liblist.gam" ||
        fail "The minimal runtime references an excluded loader."
}

read_firewall_status() {
    LC_ALL=C ufw status verbose
}

read_inbound_allow_rules() {
    awk '
        $2 == "ALLOW" && $3 == "IN" { print $1 "|" $4 }
        $3 == "ALLOW" && $4 == "IN" { print $1 " " $2 "|" $5 }
    '
}

derive_and_validate_rcon_source() {
    local source_address source_port server_address server_port extra
    IFS=' ' read -r source_address source_port server_address server_port extra \
        <<< "${SSH_CONNECTION:-}"
    [[ -n "${source_address:-}" && -n "${source_port:-}" && -n "${server_address:-}" &&
        -n "${server_port:-}" && -z "${extra:-}" ]] ||
        fail "Apply requires valid preserved SSH connection metadata."
    validate_ipv4_address "$source_address"
    [[ "$server_port" == "$prepared_ssh_port" ]] ||
        fail "The current SSH session does not use the reviewed port."

    local firewall_status rules=()
    firewall_status="$(read_firewall_status)"
    grep -Fxq 'Status: active' <<< "$firewall_status" || fail "UFW must remain active."
    grep -Fq 'Default: deny (incoming), allow (outgoing)' <<< "$firewall_status" ||
        fail "UFW default policies changed after host preparation."
    mapfile -t rules < <(read_inbound_allow_rules <<< "$firewall_status")
    [[ "${#rules[@]}" -eq 2 ]] || fail "UFW must contain exactly two inbound allow rules."
    printf '%s\n' "${rules[@]}" | grep -Fxq "$prepared_ssh_port/tcp|$source_address" ||
        fail "The SSH source does not match the exact UFW boundary."
    printf '%s\n' "${rules[@]}" | grep -Fxq "$prepared_game_port/udp|$source_address" ||
        fail "The game endpoint does not match the exact UFW boundary."

    approved_rcon_cidr="$source_address/32"
}

validate_service_contract() {
    local expected
    for expected in \
        "ConditionPathExists=$RUNTIME_ENABLED_MARKER" \
        "ConditionPathExists=$PUBLIC_CONFIGURATION" \
        "ConditionPathExists=$PRIVATE_CONFIGURATION" \
        "User=$prepared_service_user" \
        "Group=$service_group" \
        "LoadCredential=server-public.cfg:$PUBLIC_CONFIGURATION" \
        "LoadCredential=server-private.cfg:$PRIVATE_CONFIGURATION" \
        "ExecStart=$service_home/server/hlds_run -game cstrike -console -strictportbind -ip 0.0.0.0 -port $prepared_game_port +servercfgfile goldsrcops-public.cfg +maxplayers 4 +map de_dust2" \
        'NoNewPrivileges=true' \
        'CapabilityBoundingSet=' \
        'ProtectProc=invisible' \
        'ProcSubset=all' \
        'ProtectSystem=strict'; do
        grep -Fxq "$expected" "$SYSTEMD_UNIT_FILE" || fail "The game-server unit contract changed."
    done
    systemd-analyze verify "$SYSTEMD_UNIT_FILE"
}

validate_inactive_boundary() {
    [[ ! -e "$RUNTIME_ENABLED_MARKER" && ! -L "$RUNTIME_ENABLED_MARKER" ]] ||
        fail "The runtime is already activated."
    [[ ! -e "$PUBLIC_CONFIGURATION" && ! -L "$PUBLIC_CONFIGURATION" ]] ||
        fail "The public runtime configuration already exists."
    [[ ! -e "$PRIVATE_CONFIGURATION" && ! -L "$PRIVATE_CONFIGURATION" ]] ||
        fail "The private runtime configuration already exists."
    [[ ! -e "$service_home/server/cstrike/goldsrcops-public.cfg" &&
        ! -L "$service_home/server/cstrike/goldsrcops-public.cfg" ]] ||
        fail "A stale public credential link exists."
    [[ ! -e "$service_home/server/cstrike/goldsrcops-private.cfg" &&
        ! -L "$service_home/server/cstrike/goldsrcops-private.cfg" ]] ||
        fail "A stale private credential link exists."
    [[ "$(systemctl is-enabled "$SERVICE_NAME" 2>/dev/null || true)" == "disabled" ]] ||
        fail "The game-server unit must remain disabled before first start."
    [[ "$(systemctl is-active "$SERVICE_NAME" 2>/dev/null || true)" == "inactive" ]] ||
        fail "The game-server unit must be inactive before first start."
    ! pgrep -u "$prepared_service_user" -x hlds_linux >/dev/null ||
        fail "A game-server process is already running."
    ! ss -H -lun "sport = :$prepared_game_port" | grep -q . ||
        fail "The reviewed game UDP port is already listening."
}

read_rcon_secret() {
    [[ "$read_secret_from_stdin" == "true" ]] ||
        fail "Apply requires --rcon-secret-stdin."
    [[ ! -t 0 ]] || fail "The RCON secret must arrive through redirected stdin."

    rcon_secret="$(cat)"
    validate_rcon_secret "$rcon_secret"
}

render_public_configuration() {
    local destination="$1"

    cat > "$destination" <<EOF
hostname "$SERVER_NAME"
sv_lan "0"
sv_password ""
sv_rcon_condebug "0"
rcon_adduser $approved_rcon_cidr
exec goldsrcops-private.cfg
echo "$PUBLIC_CONFIGURATION_MARKER"
EOF
    chmod 0640 "$destination"
}

render_private_configuration() {
    local destination="$1"

    printf 'rcon_password "%s"\necho "%s"\n' \
        "$rcon_secret" \
        "$PRIVATE_CONFIGURATION_MARKER" > "$destination"
    chmod 0600 "$destination"
}

render_activation_marker() {
    local destination="$1"
    local public_sha256
    public_sha256="$(sha256sum "$staging_directory/server-public.cfg" | awk '{ print $1 }')"

    cat > "$destination" <<EOF
schema_version=1
runtime_marker_sha256=$(sha256sum "$RUNTIME_MARKER" | awk '{ print $1 }')
service_unit_sha256=${runtime_values[service_unit_sha256]}
public_config_sha256=$public_sha256
rcon_source_policy=ssh-ufw-exact-ipv4-32
rcon_secret_transport=stdin
service_autostart=disabled
EOF
    chmod 0640 "$destination"
}

prepare_activation_files() {
    staging_directory="$(mktemp -d "$CONFIGURATION_DIRECTORY/.runtime-activate.XXXXXX")"
    chmod 0700 "$staging_directory"
    activation_started=true

    render_public_configuration "$staging_directory/server-public.cfg"
    render_private_configuration "$staging_directory/server-private.cfg"
    rcon_secret=""
    unset rcon_secret
    render_activation_marker "$staging_directory/runtime-enabled"
}

install_activation_files() {
    local private_sha256 public_sha256 marker_sha256
    private_sha256="$(sha256sum "$staging_directory/server-private.cfg" | awk '{ print $1 }')"
    public_sha256="$(sha256sum "$staging_directory/server-public.cfg" | awk '{ print $1 }')"
    marker_sha256="$(sha256sum "$staging_directory/runtime-enabled" | awk '{ print $1 }')"

    chown root:root "$staging_directory/server-private.cfg"
    chmod 0600 "$staging_directory/server-private.cfg"
    chown root:"$service_group" \
        "$staging_directory/server-public.cfg" \
        "$staging_directory/runtime-enabled"
    chmod 0640 \
        "$staging_directory/server-public.cfg" \
        "$staging_directory/runtime-enabled"

    mv -- "$staging_directory/server-private.cfg" "$PRIVATE_CONFIGURATION"
    mv -- "$staging_directory/server-public.cfg" "$PUBLIC_CONFIGURATION"
    mv -- "$staging_directory/runtime-enabled" "$RUNTIME_ENABLED_MARKER"

    validate_file_metadata "$PRIVATE_CONFIGURATION" root root 600
    validate_file_metadata "$PUBLIC_CONFIGURATION" root "$service_group" 640
    validate_file_metadata "$RUNTIME_ENABLED_MARKER" root "$service_group" 640
    [[ "$(sha256sum "$PRIVATE_CONFIGURATION" | awk '{ print $1 }')" == "$private_sha256" ]] ||
        fail "The installed private configuration changed during activation."
    [[ "$(sha256sum "$PUBLIC_CONFIGURATION" | awk '{ print $1 }')" == "$public_sha256" ]] ||
        fail "The installed public configuration changed during activation."
    [[ "$(sha256sum "$RUNTIME_ENABLED_MARKER" | awk '{ print $1 }')" == "$marker_sha256" ]] ||
        fail "The installed activation marker changed during activation."
}

verify_first_start() {
    local _
    for _ in {1..30}; do
        if systemctl is-active --quiet "$SERVICE_NAME" &&
            ss -H -lun "sport = :$prepared_game_port" | grep -q .; then
            break
        fi
        sleep 1
    done

    systemctl is-active --quiet "$SERVICE_NAME" || fail "The game-server service did not become active."
    ss -H -lun "sport = :$prepared_game_port" | grep -q . ||
        fail "The game-server UDP listener did not become active."
    sleep 5
    systemctl is-active --quiet "$SERVICE_NAME" || fail "The game-server service did not remain active."
    ss -H -lun "sport = :$prepared_game_port" | grep -q . ||
        fail "The game-server UDP listener did not remain active."
    [[ "$(systemctl is-enabled "$SERVICE_NAME" 2>/dev/null || true)" == "disabled" ]] ||
        fail "First start unexpectedly enabled the game-server unit."
    [[ "$(systemctl show "$SERVICE_NAME" --property=NRestarts --value)" == "0" ]] ||
        fail "The game-server process restarted during first-start verification."

    local main_pid process_user
    main_pid="$(systemctl show "$SERVICE_NAME" --property=MainPID --value)"
    [[ "$main_pid" =~ ^[1-9][0-9]*$ ]] || fail "The game-server main process is unavailable."
    process_user="$(ps -o user= -p "$main_pid" | tr -d '[:space:]')"
    [[ "$process_user" == "$prepared_service_user" ]] ||
        fail "The game-server process owner changed."

    local invocation_id journal_output
    invocation_id="$(systemctl show "$SERVICE_NAME" --property=InvocationID --value)"
    [[ "$invocation_id" =~ ^[0-9a-f]{32}$ ]] ||
        fail "The game-server invocation identity is unavailable."
    journal_output="$(journalctl \
        --quiet \
        --no-pager \
        --output=cat \
        "_SYSTEMD_INVOCATION_ID=$invocation_id")"
    grep -Fq "$PUBLIC_CONFIGURATION_MARKER" <<< "$journal_output" ||
        fail "The public runtime configuration was not loaded."
    grep -Fq "$PRIVATE_CONFIGURATION_MARKER" <<< "$journal_output" ||
        fail "The private runtime configuration was not loaded."
}

cleanup_staging() {
    rcon_secret=""
    unset rcon_secret 2>/dev/null || true
    if [[ -n "$staging_directory" && -d "$staging_directory" ]]; then
        rm -rf -- "$staging_directory"
    fi
}

rollback_activation() {
    local status="$1"
    trap - ERR HUP INT TERM

    if [[ "$activation_started" == "true" ]]; then
        systemctl stop "$SERVICE_NAME" >/dev/null 2>&1 || true
        rm -f -- \
            "$RUNTIME_ENABLED_MARKER" \
            "$PUBLIC_CONFIGURATION" \
            "$PRIVATE_CONFIGURATION" \
            "$service_home/server/cstrike/goldsrcops-public.cfg" \
            "$service_home/server/cstrike/goldsrcops-private.cfg"
    fi
    cleanup_staging
    exit "$status"
}

require_apply_environment() {
    ((EUID == 0)) || fail "--apply must run as root."

    local command
    for command in \
        awk \
        cat \
        chmod \
        chown \
        getent \
        grep \
        id \
        journalctl \
        mktemp \
        mv \
        pgrep \
        ps \
        rm \
        sha256sum \
        sleep \
        ss \
        stat \
        systemctl \
        systemd-analyze \
        tr \
        ufw \
        uname; do
        require_command "$command"
    done

    [[ -r /etc/os-release ]] || fail "The operating-system identity is unavailable."
    # shellcheck disable=SC1091
    source /etc/os-release
    [[ "${ID:-}" == "ubuntu" && "${VERSION_ID:-}" == "24.04" ]] ||
        fail "Runtime activation supports Ubuntu 24.04 only."
    [[ "$(uname -m)" == "x86_64" ]] || fail "Runtime activation requires x86-64."
    [[ "$(ps -p 1 -o comm= | tr -d '[:space:]')" == "systemd" ]] ||
        fail "Runtime activation requires systemd as PID 1."

    read_prepared_marker
    read_runtime_marker
    [[ "${SUDO_USER:-}" == "$prepared_operator_user" ]] ||
        fail "Run --apply through sudo from the operator recorded by host bootstrap."

    service_group="$(id -gn "$prepared_service_user")"
    service_home="$(getent passwd "$prepared_service_user" | cut -d: -f6)"
    [[ "$service_home" == "/var/lib/$prepared_service_user" ]] ||
        fail "The service account has an unexpected home directory."
    [[ "$(getent passwd "$prepared_service_user" | cut -d: -f7)" == "/usr/sbin/nologin" ]] ||
        fail "The service account must remain non-interactive."

    validate_runtime_identity
    derive_and_validate_rcon_source
    validate_service_contract
    validate_inactive_boundary
}

run_apply() {
    require_apply_environment
    read_rcon_secret

    trap 'rollback_activation $?' ERR
    trap 'rollback_activation 129' HUP
    trap 'rollback_activation 130' INT
    trap 'rollback_activation 143' TERM

    prepare_activation_files
    install_activation_files
    systemctl start "$SERVICE_NAME"
    verify_first_start

    activation_started=false
    trap - ERR HUP INT TERM
    cleanup_staging

    log "ACTIVATED: reviewed runtime configuration and exact-source RCON policy are installed."
    log "SERVICE_STATE: active and disabled"
    log "SECRET_TRANSPORT: stdin only; value omitted"
    log "NEXT_GATE: verify external A2S and authenticated rcon_users from the approved control-plane source."
}

print_plan() {
    log "PLAN: require the reviewed host foundation, installed runtime identity, and unchanged systemd unit"
    log "PLAN: derive one exact IPv4 /32 from preserved SSH metadata and the two-rule UFW boundary"
    log "PLAN: accept one bounded Base64-safe RCON secret through stdin only and never print it"
    log "PLAN: atomically install root-controlled public/private configuration and runtime-enabled"
    log "PLAN: start but do not enable $SERVICE_NAME, with rollback on any first-start failure"
    log "PLAN: verify both config-load markers, stable active state, process ownership, zero restarts, and one UDP listener"
    log "PLAN_ONLY: no stdin was read and no host changes were made; add --apply --rcon-secret-stdin to execute this plan."
}

main() {
    while (($# > 0)); do
        case "$1" in
            --apply)
                apply_changes=true
                shift
                ;;
            --rcon-secret-stdin)
                read_secret_from_stdin=true
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

    if [[ "$apply_changes" == "true" ]]; then
        run_apply
    else
        [[ "$read_secret_from_stdin" == "false" ]] ||
            fail "--rcon-secret-stdin is valid only with --apply."
        print_plan
    fi
}

if [[ "${BASH_SOURCE[0]:-$0}" == "$0" ]]; then
    main "$@"
fi
