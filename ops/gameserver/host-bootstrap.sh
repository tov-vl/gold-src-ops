#!/usr/bin/env bash

set -Eeuo pipefail
IFS=$'\n\t'
umask 027

readonly CONFIGURATION_DIRECTORY="/etc/goldsrcops/gameserver"
readonly PREPARED_MARKER="$CONFIGURATION_DIRECTORY/host-prepared"

control_plane_ipv4_cidr=""
operator_user="gsoadmin"
service_user="goldsrc"
ssh_port="22"
game_port="27015"
apply_changes=false

usage() {
    cat <<'EOF'
Usage:
  host-bootstrap.sh --control-plane-ipv4-cidr <IPv4/32> \
    [--operator-user <name>] [--service-user <name>] \
    [--ssh-port <port>] [--game-port <port>] [--apply]

Without --apply, the script validates inputs and prints a sanitized plan.
Apply assumes that the operator already has verified key-only SSH access. Run
it through sudo while preserving SSH_CONNECTION:

  sudo --preserve-env=SSH_CONNECTION bash ./host-bootstrap.sh ... --apply

The script prepares an Ubuntu 24.04 x86-64 host for a controlled GoldSrc
runtime. It requires an already active UFW boundary, installs security updates
and 32-bit runtime dependencies, creates a locked non-login service account,
configures owner-scoped directories, enables unattended security updates and
NTP, and preserves exact-source rules for SSH and the game UDP endpoint.

It does not install SteamCMD, HLDS, ReHLDS, ReGameDLL_CS, plugins, or secrets,
and it does not expose the game endpoint publicly.
EOF
}

fail() {
    printf 'ERROR: %s\n' "$*" >&2
    exit 1
}

log() {
    printf '%s\n' "$*"
}

require_command() {
    command -v "$1" >/dev/null 2>&1 || fail "Required command '$1' is unavailable."
}

validate_ipv4_cidr() {
    local cidr="$1"
    local address="${cidr%/32}"

    [[ "$cidr" =~ ^([0-9]{1,3}\.){3}[0-9]{1,3}/32$ ]] ||
        fail "--control-plane-ipv4-cidr must identify exactly one IPv4 address with /32."

    local octet
    IFS='.' read -r -a octets <<< "$address"
    for octet in "${octets[@]}"; do
        ((10#$octet <= 255)) ||
            fail "--control-plane-ipv4-cidr contains an invalid IPv4 address."
    done
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

validate_inputs() {
    [[ -n "$control_plane_ipv4_cidr" ]] ||
        fail "--control-plane-ipv4-cidr is required."
    validate_ipv4_cidr "$control_plane_ipv4_cidr"
    validate_user_name "--operator-user" "$operator_user"
    validate_user_name "--service-user" "$service_user"
    [[ "$operator_user" != "$service_user" ]] ||
        fail "The operator and service accounts must be different."
    validate_port "--ssh-port" "$ssh_port"
    validate_port "--game-port" "$game_port"
    [[ "$ssh_port" != "$game_port" ]] ||
        fail "The SSH and game ports must be different."
}

validate_current_ssh_source() {
    [[ -n "${SSH_CONNECTION:-}" ]] ||
        fail "Run --apply from SSH and preserve SSH_CONNECTION through sudo."

    local source_address source_port server_address server_port extra
    IFS=' ' read -r source_address source_port server_address server_port extra \
        <<< "$SSH_CONNECTION"
    [[ -n "$source_address" && -n "$source_port" && -n "$server_address" &&
        "$server_port" =~ ^[0-9]+$ && -z "${extra:-}" ]] ||
        fail "The current SSH connection metadata is invalid."

    local expected_address="${control_plane_ipv4_cidr%/32}"
    [[ "$source_address" == "$expected_address" ]] ||
        fail "The current SSH source does not match --control-plane-ipv4-cidr."
    [[ "$server_port" == "$ssh_port" ]] ||
        fail "--ssh-port must match the current SSH server port."
}

validate_effective_ssh_configuration() {
    local expected_address="${control_plane_ipv4_cidr%/32}"
    local connection_spec="user=$operator_user,host=localhost,addr=$expected_address,laddr=127.0.0.1,lport=$ssh_port"
    local effective_configuration
    effective_configuration="$(/usr/sbin/sshd -T -C "$connection_spec")" ||
        fail "Could not read the effective OpenSSH configuration."

    local expected_setting
    for expected_setting in \
        "port $ssh_port" \
        "permitrootlogin no" \
        "passwordauthentication no" \
        "kbdinteractiveauthentication no" \
        "pubkeyauthentication yes" \
        "permitemptypasswords no" \
        "allowusers $operator_user" \
        "x11forwarding no" \
        "allowagentforwarding no" \
        "allowtcpforwarding no" \
        "permittunnel no"; do
        grep -Fxq "$expected_setting" <<< "$effective_configuration" ||
            fail "Effective SSH setting '$expected_setting' is required before game-host bootstrap."
    done
}

read_inbound_allow_rules() {
    LC_ALL=C ufw status verbose |
        awk '
            $2 == "ALLOW" && $3 == "IN" { print $1 "|" $4 }
            $3 == "ALLOW" && $4 == "IN" { print $1 " " $2 "|" $5 }
        '
}

validate_inbound_rules() {
    local require_rules="$1"
    local source_address="${control_plane_ipv4_cidr%/32}"
    local expected_ssh_rule="$ssh_port/tcp|$source_address"
    local expected_game_rule="$game_port/udp|$source_address"
    local rules=()
    mapfile -t rules < <(read_inbound_allow_rules)

    local ssh_rule_count=0
    local game_rule_count=0
    local rule
    for rule in "${rules[@]}"; do
        case "$rule" in
            "$expected_ssh_rule")
                ((ssh_rule_count += 1))
                ;;
            "$expected_game_rule")
                ((game_rule_count += 1))
                ;;
            *)
                fail "UFW contains an unexpected inbound allow rule."
                ;;
        esac
    done

    ((ssh_rule_count <= 1 && game_rule_count <= 1)) ||
        fail "UFW contains duplicate inbound allow rules."
    if [[ "$require_rules" == "yes" &&
        ($ssh_rule_count -ne 1 || $game_rule_count -ne 1) ]]; then
        fail "UFW must contain exactly the restricted SSH and game allow rules."
    fi
}

ensure_endpoint_rule() {
    local port="$1"
    local protocol="$2"
    local source_address="$3"
    local comment="$4"
    local endpoint="$port/$protocol"

    if ! read_inbound_allow_rules | grep -Fxq "$endpoint|$source_address"; then
        ufw allow proto "$protocol" from "$source_address" to any port "$port" \
            comment "$comment"
    fi
}

configure_firewall() {
    local source_address="${control_plane_ipv4_cidr%/32}"
    local status
    status="$(LC_ALL=C ufw status verbose)"

    grep -Fq 'Status: active' <<< "$status" ||
        fail "UFW must already be active before game-host bootstrap."
    validate_inbound_rules "no"

    ensure_endpoint_rule "$ssh_port" tcp "$source_address" \
        "GoldSrcOps control-plane SSH"
    ensure_endpoint_rule "$game_port" udp "$source_address" \
        "GoldSrcOps control-plane game endpoint"
    ufw default deny incoming
    ufw default allow outgoing
    ufw logging low

    status="$(LC_ALL=C ufw status verbose)"
    grep -Fq 'Status: active' <<< "$status" || fail "UFW is not active."
    grep -Fq 'Default: deny (incoming), allow (outgoing)' <<< "$status" ||
        fail "UFW default policies do not match the required boundary."
    validate_inbound_rules "yes"
}

require_apply_environment() {
    ((EUID == 0)) || fail "--apply must run as root."
    [[ "${SUDO_USER:-}" == "$operator_user" ]] ||
        fail "Run --apply through sudo from the verified operator account."

    local command
    for command in \
        apt-get \
        dpkg \
        getent \
        grep \
        passwd \
        ss \
        systemctl \
        timedatectl \
        ufw \
        useradd; do
        require_command "$command"
    done
    [[ -x /usr/sbin/sshd ]] || fail "OpenSSH server is unavailable."

    [[ -r /etc/os-release ]] || fail "The operating-system identity is unavailable."
    # shellcheck disable=SC1091
    source /etc/os-release
    [[ "${ID:-}" == "ubuntu" && "${VERSION_ID:-}" == "24.04" ]] ||
        fail "The game-host bootstrap supports Ubuntu 24.04 only."
    [[ "$(uname -m)" == "x86_64" ]] ||
        fail "The game-host bootstrap requires x86-64."
    [[ "$(ps -p 1 -o comm= | tr -d '[:space:]')" == "systemd" ]] ||
        fail "The game-host bootstrap requires systemd as PID 1."

    id "$operator_user" >/dev/null 2>&1 ||
        fail "The verified operator account does not exist."
    validate_current_ssh_source
    validate_effective_ssh_configuration
}

install_runtime_dependencies() {
    export DEBIAN_FRONTEND=noninteractive
    export NEEDRESTART_MODE=a

    apt-get update
    apt-get upgrade --yes
    apt-get install --yes --no-install-recommends \
        ca-certificates \
        curl \
        file \
        gnupg \
        lib32gcc-s1 \
        lib32stdc++6 \
        lib32z1 \
        libc6-i386 \
        patchelf \
        rsync \
        unattended-upgrades \
        unzip \
        xz-utils
}

configure_service_account() {
    local service_home="/var/lib/$service_user"

    if ! id "$service_user" >/dev/null 2>&1; then
        useradd \
            --system \
            --user-group \
            --create-home \
            --home-dir "$service_home" \
            --shell /usr/sbin/nologin \
            "$service_user"
    fi

    local actual_home
    actual_home="$(getent passwd "$service_user" | cut -d: -f6)"
    [[ "$actual_home" == "$service_home" ]] ||
        fail "The service account has an unexpected home directory."
    [[ "$(getent passwd "$service_user" | cut -d: -f7)" == "/usr/sbin/nologin" ]] ||
        fail "The service account must use /usr/sbin/nologin."
    if id -nG "$service_user" | tr ' ' '\n' | grep -Fxq sudo; then
        fail "The service account must not belong to the sudo group."
    fi
    passwd --lock "$service_user" >/dev/null
}

configure_directories() {
    local service_group
    service_group="$(id -gn "$service_user")"
    local service_home="/var/lib/$service_user"

    install -d -m 0750 -o root -g "$service_group" /opt/goldsrcops/gameserver
    install -d -m 0750 -o root -g "$service_group" /opt/goldsrcops/gameserver/artifacts
    install -d -m 0750 -o "$service_user" -g "$service_group" "$service_home"
    install -d -m 0750 -o "$service_user" -g "$service_group" "$service_home/steamcmd"
    install -d -m 0750 -o "$service_user" -g "$service_group" "$service_home/server"
    install -d -m 0750 -o root -g "$service_group" "$CONFIGURATION_DIRECTORY"
    install -d -m 0710 -o root -g "$service_group" "$CONFIGURATION_DIRECTORY/secrets"
    install -d -m 0700 -o root -g root /var/backups/goldsrcops/gameserver
}

configure_automatic_updates() {
    cat > /etc/apt/apt.conf.d/20auto-upgrades <<'EOF'
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
APT::Periodic::AutocleanInterval "7";
EOF

    cat > /etc/apt/apt.conf.d/52goldsrcops-gameserver-unattended-upgrades <<'EOF'
Unattended-Upgrade::Remove-Unused-Dependencies "true";
Unattended-Upgrade::Remove-Unused-Kernel-Packages "true";
Unattended-Upgrade::Automatic-Reboot "false";
EOF
    systemctl enable --now unattended-upgrades.service
}

configure_kernel() {
    cat > /etc/sysctl.d/60-goldsrcops-gameserver-hardening.conf <<'EOF'
fs.protected_hardlinks = 1
fs.protected_symlinks = 1
kernel.dmesg_restrict = 1
kernel.kptr_restrict = 2
net.ipv4.conf.all.accept_redirects = 0
net.ipv4.conf.all.rp_filter = 1
net.ipv4.conf.all.send_redirects = 0
net.ipv4.conf.default.accept_redirects = 0
net.ipv4.conf.default.rp_filter = 1
net.ipv4.conf.default.send_redirects = 0
net.ipv4.icmp_echo_ignore_broadcasts = 1
net.ipv4.tcp_syncookies = 1
EOF
    sysctl --system >/dev/null
}

write_prepared_marker() {
    cat > "$PREPARED_MARKER" <<EOF
schema_version=1
operator_user=$operator_user
service_user=$service_user
ssh_port=$ssh_port
game_port=$game_port
EOF
    chown root:"$(id -gn "$service_user")" "$PREPARED_MARKER"
    chmod 0640 "$PREPARED_MARKER"
}

run_apply() {
    require_apply_environment
    configure_firewall
    install_runtime_dependencies
    configure_service_account
    configure_directories
    configure_automatic_updates
    configure_kernel
    timedatectl set-timezone UTC
    timedatectl set-ntp true
    validate_effective_ssh_configuration
    configure_firewall
    write_prepared_marker

    log "PREPARED: game-host dependencies, service identity, directories, updates, time, and restricted UFW are configured."
    if [[ -f /var/run/reboot-required ]]; then
        log "REBOOT_REQUIRED: yes"
    else
        log "REBOOT_REQUIRED: no"
    fi
    log "SteamCMD, HLDS, ReHLDS, ReGameDLL_CS, runtime configuration, and secrets remain uninstalled."
}

print_plan() {
    log "PLAN: validate Ubuntu 24.04 x86-64 and the current control-plane SSH source"
    log "PLAN: require effective key-only SSH hardening for the verified operator"
    log "PLAN: require active UFW and preserve only exact-source SSH and game UDP rules"
    log "PLAN: install security updates and minimal 32-bit GoldSrc runtime dependencies"
    log "PLAN: create a locked non-login service account without sudo membership"
    log "PLAN: create owner-scoped runtime, configuration, secret, artifact, and backup directories"
    log "PLAN: enable unattended updates, UTC/NTP, and baseline kernel hardening"
    log "PLAN: leave SteamCMD, server artifacts, plugins, public UDP, and secrets unchanged"
    log "PLAN_ONLY: no host changes were made; add --apply to execute this plan."
}

main() {
    while (($# > 0)); do
        case "$1" in
            --control-plane-ipv4-cidr)
                (($# >= 2)) || fail "--control-plane-ipv4-cidr requires a value."
                control_plane_ipv4_cidr="$2"
                shift 2
                ;;
            --operator-user)
                (($# >= 2)) || fail "--operator-user requires a value."
                operator_user="$2"
                shift 2
                ;;
            --service-user)
                (($# >= 2)) || fail "--service-user requires a value."
                service_user="$2"
                shift 2
                ;;
            --ssh-port)
                (($# >= 2)) || fail "--ssh-port requires a value."
                ssh_port="$2"
                shift 2
                ;;
            --game-port)
                (($# >= 2)) || fail "--game-port requires a value."
                game_port="$2"
                shift 2
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
                fail "Unknown argument '$1'."
                ;;
        esac
    done

    validate_inputs
    if [[ "$apply_changes" == "true" ]]; then
        run_apply
    else
        print_plan
    fi
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    main "$@"
fi
