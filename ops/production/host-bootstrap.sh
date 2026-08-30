#!/usr/bin/env bash

set -Eeuo pipefail
IFS=$'\n\t'

readonly STATE_DIRECTORY="/etc/goldsrcops/host"
readonly PREPARED_MARKER="$STATE_DIRECTORY/prepared"
readonly FINALIZED_MARKER="$STATE_DIRECTORY/finalized"
readonly SSH_HARDENING_FILE="/etc/ssh/sshd_config.d/00-goldsrcops-hardening.conf"

phase=""
admin_ipv4_cidr=""
operator_user="gsoadmin"
operator_public_key_file=""
ssh_port="22"
apply_changes=false

usage() {
    cat <<'EOF'
Usage:
  host-bootstrap.sh --phase prepare --admin-ipv4-cidr <IPv4/32> \
    --operator-public-key-file <path> [--operator-user <name>] \
    [--ssh-port <port>] [--apply]

  host-bootstrap.sh --phase finalize --admin-ipv4-cidr <IPv4/32> \
    [--operator-user <name>] [--ssh-port <port>] [--apply]

Without --apply, the script validates inputs and prints a sanitized plan.
When elevating an SSH session, use sudo --preserve-env=SSH_CONNECTION.

The prepare phase configures a fresh Ubuntu 24.04 x86-64 host, creates the
operator account, installs Docker Engine, Docker Compose, PowerShell, UFW, and
security-update tooling, and applies the reference firewall policy. It does not
disable the provider-created SSH login.

The finalize phase must be run through sudo from a verified SSH session owned
by the prepared operator. It then disables root, password, and keyboard-
interactive SSH authentication. The requested SSH port must match the current
session; this script does not migrate a live SSH service between ports.
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
        fail "--admin-ipv4-cidr must identify exactly one IPv4 address with /32."

    local octet
    IFS='.' read -r -a octets <<< "$address"
    for octet in "${octets[@]}"; do
        ((10#$octet <= 255)) || fail "--admin-ipv4-cidr contains an invalid IPv4 address."
    done
}

validate_operator_user() {
    [[ "$operator_user" =~ ^[a-z_][a-z0-9_-]{0,31}$ ]] ||
        fail "--operator-user must be a valid local Linux account name."
    [[ "$operator_user" != "root" ]] || fail "The operator account must not be root."
}

validate_ssh_port() {
    [[ "$ssh_port" =~ ^[0-9]+$ ]] || fail "--ssh-port must be numeric."
    ((10#$ssh_port >= 1 && 10#$ssh_port <= 65535)) ||
        fail "--ssh-port must be between 1 and 65535."
}

read_operator_public_key() {
    [[ -n "$operator_public_key_file" ]] ||
        fail "--operator-public-key-file is required for the prepare phase."
    [[ -f "$operator_public_key_file" ]] ||
        fail "The operator public-key file does not exist."

    mapfile -t key_lines < <(grep -Ev '^[[:space:]]*$' "$operator_public_key_file")
    ((${#key_lines[@]} == 1)) || fail "The operator public-key file must contain exactly one key."
    operator_public_key="${key_lines[0]%$'\r'}"
    [[ "$operator_public_key" =~ ^ssh-ed25519[[:space:]]+[A-Za-z0-9+/]+={0,3}([[:space:]].*)?$ ]] ||
        fail "The operator public key must use OpenSSH Ed25519 format."
}

validate_current_ssh_source() {
    [[ -n "${SSH_CONNECTION:-}" ]] ||
        fail "Run apply from SSH and preserve SSH_CONNECTION through sudo."

    local source_address source_port server_address server_port extra
    IFS=' ' read -r source_address source_port server_address server_port extra \
        <<< "$SSH_CONNECTION"
    [[ -n "$source_address" && -n "$source_port" && -n "$server_address" &&
        "$server_port" =~ ^[0-9]+$ && -z "${extra:-}" ]] ||
        fail "The current SSH connection metadata is invalid."
    local expected_address="${admin_ipv4_cidr%/32}"
    [[ "$source_address" == "$expected_address" ]] ||
        fail "The current SSH source does not match --admin-ipv4-cidr."
    [[ "$server_port" == "$ssh_port" ]] ||
        fail "--ssh-port must match the current SSH server port; port migration is not automated."
}

require_apply_environment() {
    ((EUID == 0)) || fail "--apply must run as root."
    require_command apt-get
    require_command dpkg
    require_command systemctl

    [[ -r /etc/os-release ]] || fail "The operating-system identity is unavailable."
    # shellcheck disable=SC1091
    source /etc/os-release
    [[ "${ID:-}" == "ubuntu" && "${VERSION_ID:-}" == "24.04" ]] ||
        fail "The reference bootstrap supports Ubuntu 24.04 only."
    [[ "$(uname -m)" == "x86_64" ]] || fail "The reference bootstrap requires x86-64."
    [[ "$(ps -p 1 -o comm= | tr -d '[:space:]')" == "systemd" ]] ||
        fail "The reference bootstrap requires systemd as PID 1."

    validate_current_ssh_source
}

install_base_packages() {
    export DEBIAN_FRONTEND=noninteractive
    export NEEDRESTART_MODE=a

    apt-get update
    apt-get upgrade --yes
    apt-get install --yes \
        apparmor \
        ca-certificates \
        curl \
        git \
        gnupg \
        jq \
        openssh-server \
        sudo \
        ufw \
        unattended-upgrades
}

install_docker() {
    local conflicting_packages=()
    local package
    for package in \
        docker.io \
        docker-compose \
        docker-compose-v2 \
        docker-doc \
        docker-buildx \
        podman-docker \
        containerd \
        runc; do
        if dpkg-query --show --showformat='${db:Status-Abbrev}' "$package" 2>/dev/null |
            grep -q '^ii '; then
            conflicting_packages+=("$package")
        fi
    done
    if ((${#conflicting_packages[@]} > 0)); then
        apt-get remove --yes "${conflicting_packages[@]}"
    fi

    install -m 0755 -d /etc/apt/keyrings
    curl --fail --silent --show-error --location \
        https://download.docker.com/linux/ubuntu/gpg \
        --output /etc/apt/keyrings/docker.asc
    chmod a+r /etc/apt/keyrings/docker.asc

    local architecture
    architecture="$(dpkg --print-architecture)"
    cat > /etc/apt/sources.list.d/docker.sources <<EOF
Types: deb
URIs: https://download.docker.com/linux/ubuntu
Suites: noble
Components: stable
Architectures: $architecture
Signed-By: /etc/apt/keyrings/docker.asc
EOF

    apt-get update
    apt-get install --yes \
        containerd.io \
        docker-buildx-plugin \
        docker-ce \
        docker-ce-cli \
        docker-compose-plugin
    systemctl enable --now docker.service
}

install_powershell() {
    (
        local package_file
        package_file="$(mktemp --suffix=.deb)"
        trap 'rm -f -- "$package_file"' EXIT

        curl --fail --silent --show-error --location \
            https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb \
            --output "$package_file"
        dpkg --install "$package_file"
        apt-get update
        apt-get install --yes powershell
    )
}

configure_operator() {
    if ! id "$operator_user" >/dev/null 2>&1; then
        useradd --create-home --user-group --shell /bin/bash "$operator_user"
    fi
    passwd --lock "$operator_user" >/dev/null
    usermod --append --groups sudo "$operator_user"

    local operator_home
    operator_home="$(getent passwd "$operator_user" | cut -d: -f6)"
    [[ -n "$operator_home" && -d "$operator_home" ]] ||
        fail "Could not resolve the operator home directory."
    local operator_group
    operator_group="$(id -gn "$operator_user")"

    install -d -m 0700 -o "$operator_user" -g "$operator_group" "$operator_home/.ssh"
    local authorized_keys="$operator_home/.ssh/authorized_keys"
    install -m 0600 -o "$operator_user" -g "$operator_group" \
        /dev/null "$authorized_keys"
    printf '%s\n' "$operator_public_key" > "$authorized_keys"

    local sudoers_file="/etc/sudoers.d/90-goldsrcops-operator"
    install -m 0440 -o root -g root /dev/null "$sudoers_file"
    printf '%s ALL=(ALL:ALL) NOPASSWD: ALL\n' "$operator_user" > "$sudoers_file"
    visudo --check --file "$sudoers_file" >/dev/null
}

configure_automatic_updates() {
    cat > /etc/apt/apt.conf.d/20auto-upgrades <<'EOF'
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
APT::Periodic::AutocleanInterval "7";
EOF

    cat > /etc/apt/apt.conf.d/52goldsrcops-unattended-upgrades <<'EOF'
Unattended-Upgrade::Remove-Unused-Dependencies "true";
Unattended-Upgrade::Remove-Unused-Kernel-Packages "true";
Unattended-Upgrade::Automatic-Reboot "false";
EOF
    systemctl enable --now unattended-upgrades.service
}

configure_kernel() {
    cat > /etc/sysctl.d/60-goldsrcops-hardening.conf <<'EOF'
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

configure_firewall() {
    ufw --force reset >/dev/null
    ufw default deny incoming
    ufw default allow outgoing
    ufw allow proto tcp from "${admin_ipv4_cidr%/32}" to any port "$ssh_port" \
        comment 'GoldSrcOps restricted SSH'
    ufw allow 80/tcp comment 'GoldSrcOps HTTP redirect and ACME'
    ufw allow 443/tcp comment 'GoldSrcOps HTTPS'
    ufw allow 443/udp comment 'GoldSrcOps HTTP3'
    ufw logging low
    ufw --force enable
}

configure_directories() {
    local operator_group
    operator_group="$(id -gn "$operator_user")"
    install -d -m 0750 -o "$operator_user" -g "$operator_group" /opt/goldsrcops
    install -d -m 0755 -o root -g root /etc/goldsrcops
    install -d -m 0711 -o root -g root /etc/goldsrcops/secrets
    install -d -m 0700 -o root -g root /var/lib/goldsrcops/evidence
    install -d -m 0700 -o root -g root "$STATE_DIRECTORY"
}

run_prepare() {
    require_apply_environment
    install_base_packages
    install_docker
    install_powershell
    configure_operator
    configure_automatic_updates
    configure_kernel
    configure_firewall
    configure_directories

    timedatectl set-timezone UTC
    timedatectl set-ntp true
    /usr/sbin/sshd -t

    cat > "$PREPARED_MARKER" <<EOF
schema_version=1
operator_user=$operator_user
ssh_port=$ssh_port
EOF
    chmod 0600 "$PREPARED_MARKER"

    log "PREPARED: base packages, Docker, PowerShell, operator access, updates, time, and UFW are configured."
    if [[ -f /var/run/reboot-required ]]; then
        log "REBOOT_REQUIRED: yes"
    else
        log "REBOOT_REQUIRED: no"
    fi
    log "Verify a new SSH session as '$operator_user', then run the finalize phase through sudo."
}

validate_effective_ssh_configuration() {
    local expected_admin_address="${admin_ipv4_cidr%/32}"
    local connection_spec="user=$operator_user,host=localhost,addr=$expected_admin_address,laddr=127.0.0.1,lport=$ssh_port"
    local effective_configuration
    effective_configuration="$(/usr/sbin/sshd -T -C "$connection_spec")" || return 1

    local expected_setting
    for expected_setting in \
        "port $ssh_port" \
        "permitrootlogin no" \
        "passwordauthentication no" \
        "kbdinteractiveauthentication no" \
        "pubkeyauthentication yes" \
        "authenticationmethods publickey" \
        "permitemptypasswords no" \
        "allowusers $operator_user" \
        "x11forwarding no" \
        "allowagentforwarding no" \
        "allowtcpforwarding no" \
        "permittunnel no"; do
        grep -Fxq "$expected_setting" <<< "$effective_configuration" || return 1
    done
}

restore_ssh_configuration() {
    local backup_configuration="$1"
    if [[ -n "$backup_configuration" ]]; then
        install -m 0644 -o root -g root "$backup_configuration" "$SSH_HARDENING_FILE"
    else
        rm -f "$SSH_HARDENING_FILE"
    fi
}

run_finalize() {
    require_apply_environment
    [[ -f "$PREPARED_MARKER" ]] || fail "The prepare phase marker is missing."
    [[ "${SUDO_USER:-}" == "$operator_user" ]] ||
        fail "Run finalize through sudo from the prepared operator account."
    id "$operator_user" >/dev/null 2>&1 || fail "The prepared operator account is missing."

    local operator_home
    operator_home="$(getent passwd "$operator_user" | cut -d: -f6)"
    [[ -s "$operator_home/.ssh/authorized_keys" ]] ||
        fail "The prepared operator authorized_keys file is missing or empty."

    local temporary_configuration
    temporary_configuration="$(mktemp)"
    cat > "$temporary_configuration" <<EOF
Port $ssh_port
PermitRootLogin no
PasswordAuthentication no
KbdInteractiveAuthentication no
PubkeyAuthentication yes
AuthenticationMethods publickey
PermitEmptyPasswords no
AllowUsers $operator_user
X11Forwarding no
AllowAgentForwarding no
AllowTcpForwarding no
PermitTunnel no
LoginGraceTime 30
MaxAuthTries 3
MaxSessions 4
EOF

    local backup_configuration=""
    if [[ -f "$SSH_HARDENING_FILE" ]]; then
        backup_configuration="$(mktemp)"
        cp --preserve=mode,ownership,timestamps "$SSH_HARDENING_FILE" "$backup_configuration"
    fi

    install -m 0644 -o root -g root "$temporary_configuration" "$SSH_HARDENING_FILE"
    rm -f "$temporary_configuration"

    if ! /usr/sbin/sshd -t || ! validate_effective_ssh_configuration; then
        restore_ssh_configuration "$backup_configuration"
        if [[ -n "$backup_configuration" ]]; then
            rm -f "$backup_configuration"
        fi
        fail "The hardened SSH configuration is invalid or ineffective; the previous configuration was restored."
    fi

    if ! systemctl reload ssh.service; then
        restore_ssh_configuration "$backup_configuration"
        /usr/sbin/sshd -t && systemctl reload ssh.service || true
        if [[ -n "$backup_configuration" ]]; then
            rm -f "$backup_configuration"
        fi
        fail "OpenSSH rejected the reload; the previous configuration was restored."
    fi

    if [[ -n "$backup_configuration" ]]; then
        rm -f "$backup_configuration"
    fi
    cat > "$FINALIZED_MARKER" <<EOF
schema_version=1
operator_user=$operator_user
ssh_port=$ssh_port
EOF
    chmod 0600 "$FINALIZED_MARKER"

    log "FINALIZED: root and interactive SSH authentication are disabled."
    log "Keep this session open until a second key-only operator login succeeds."
}

print_plan() {
    log "PLAN: phase=$phase"
    log "PLAN: operator-user=$operator_user"
    log "PLAN: ssh-port=$ssh_port"
    if [[ "$phase" == "prepare" ]]; then
        log "PLAN: validate Ubuntu 24.04 x86-64 and the current restricted SSH source"
        log "PLAN: install security updates, Docker Engine with Compose, and PowerShell"
        log "PLAN: create a key-only sudo operator without Docker-group membership"
        log "PLAN: enable unattended updates, UTC/NTP, kernel hardening, and exact UFW rules"
        log "PLAN: keep the provider-created SSH login available for operator-login verification"
    else
        log "PLAN: require sudo from a verified operator SSH session"
        log "PLAN: validate and reload key-only SSH configuration"
        log "PLAN: disable root, password, and keyboard-interactive SSH authentication"
    fi
    log "PLAN_ONLY: no host changes were made; add --apply to execute this phase."
}

while (($# > 0)); do
    case "$1" in
        --phase)
            (($# >= 2)) || fail "--phase requires a value."
            phase="$2"
            shift 2
            ;;
        --admin-ipv4-cidr)
            (($# >= 2)) || fail "--admin-ipv4-cidr requires a value."
            admin_ipv4_cidr="$2"
            shift 2
            ;;
        --operator-user)
            (($# >= 2)) || fail "--operator-user requires a value."
            operator_user="$2"
            shift 2
            ;;
        --operator-public-key-file)
            (($# >= 2)) || fail "--operator-public-key-file requires a value."
            operator_public_key_file="$2"
            shift 2
            ;;
        --ssh-port)
            (($# >= 2)) || fail "--ssh-port requires a value."
            ssh_port="$2"
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

[[ "$phase" == "prepare" || "$phase" == "finalize" ]] ||
    fail "--phase must be 'prepare' or 'finalize'."
[[ -n "$admin_ipv4_cidr" ]] || fail "--admin-ipv4-cidr is required."
validate_ipv4_cidr "$admin_ipv4_cidr"
validate_operator_user
validate_ssh_port

if [[ "$phase" == "prepare" ]]; then
    read_operator_public_key
fi

if [[ "$apply_changes" != true ]]; then
    print_plan
    exit 0
fi

if [[ "$phase" == "prepare" ]]; then
    run_prepare
else
    run_finalize
fi
