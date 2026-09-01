#!/usr/bin/env bash

set -Eeuo pipefail
IFS=$'\n\t'
umask 027

readonly INSTALLER_SCHEMA_VERSION="1"
readonly STEAM_APP_ID="90"
readonly STEAM_BRANCH="steam_legacy"
readonly STEAMCMD_ARCHIVE_NAME="steamcmd_linux.tar.gz"
readonly STEAMCMD_ARCHIVE_URL="https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz"
readonly STEAMCMD_ARCHIVE_SHA256="cebf0046bfd08cf45da6bc094ae47aa39ebf4155e5ede41373b579b8f1071e7c"
readonly REHLDS_VERSION="3.15.0.896"
readonly REHLDS_ARCHIVE_NAME="rehlds-bin-${REHLDS_VERSION}.zip"
readonly REHLDS_ARCHIVE_URL="https://github.com/rehlds/ReHLDS/releases/download/${REHLDS_VERSION}/${REHLDS_ARCHIVE_NAME}"
readonly REHLDS_ARCHIVE_SHA256="997baeb7ef3842dab3e034d82dc651ebfe560c23b158adce660a6b97976b4e2b"
readonly REHLDS_SIGNATURE_NAME="${REHLDS_ARCHIVE_NAME}.asc"
readonly REHLDS_SIGNATURE_URL="${REHLDS_ARCHIVE_URL}.asc"
readonly REGAMEDLL_VERSION="5.30.0.814"
readonly REGAMEDLL_ARCHIVE_NAME="regamedll-bin-${REGAMEDLL_VERSION}.zip"
readonly REGAMEDLL_ARCHIVE_URL="https://github.com/rehlds/ReGameDLL_CS/releases/download/${REGAMEDLL_VERSION}/${REGAMEDLL_ARCHIVE_NAME}"
readonly REGAMEDLL_ARCHIVE_SHA256="457f5c96a4d10280fcad47f106cbd5be86363a47df1ccab91c514e9d1bc6fd18"
readonly REGAMEDLL_SIGNATURE_NAME="${REGAMEDLL_ARCHIVE_NAME}.asc"
readonly REGAMEDLL_SIGNATURE_URL="${REGAMEDLL_ARCHIVE_URL}.asc"
readonly REHLDS_SIGNING_KEY_FINGERPRINT="63547829004F07716F7BE4856C32C4282E60FB67"
readonly SERVICE_NAME="goldsrcops-gameserver.service"

configuration_directory="${GOLDSRCOPS_CONFIGURATION_DIRECTORY:-/etc/goldsrcops/gameserver}"
installation_directory="${GOLDSRCOPS_INSTALLATION_DIRECTORY:-/opt/goldsrcops/gameserver}"
systemd_unit_file="${GOLDSRCOPS_SYSTEMD_UNIT_FILE:-/etc/systemd/system/$SERVICE_NAME}"
prepared_marker="$configuration_directory/host-prepared"
runtime_marker="$configuration_directory/runtime-installed"
runtime_enabled_marker="$configuration_directory/runtime-enabled"
artifact_directory="$installation_directory/artifacts"

service_user="goldsrc"
game_port="27015"
apply_changes=false
staging_root=""
verification_root=""
service_group=""
service_home=""
prepared_operator_user=""
prepared_service_user=""
prepared_ssh_port=""
prepared_game_port=""
hlds_build_id=""
hlds_manifest_sha256=""
steamcmd_script_sha256=""
steamcmd_binary_sha256=""
steamclient_binary_sha256=""
base_hlds_linux_sha256=""
final_hlds_linux_sha256=""
final_engine_sha256=""
final_regamedll_sha256=""

usage() {
    cat <<'EOF'
Usage:
  runtime-install.sh [--service-user <name>] [--game-port <port>] [--apply]

Without --apply, the script validates arguments and prints a sanitized plan.
Apply must run through sudo from the operator recorded by the reviewed game-host
bootstrap. It requires /etc/goldsrcops/gameserver/host-prepared and an empty
runtime target.

The installer downloads and verifies one pinned SteamCMD bootstrap, installs
HLDS app 90 from the steam_legacy branch, overlays pinned ReHLDS and
ReGameDLL_CS release artifacts, and installs a constrained systemd unit.

It does not create an RCON password, enable or start the service, publish the
game endpoint, install plugins or bots, or update an existing runtime.
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
    validate_user_name "--service-user" "$service_user"
    validate_port "--game-port" "$game_port"
}

read_prepared_marker() {
    local marker_path="${1:-$prepared_marker}"
    local key value
    declare -A seen=()

    [[ -f "$marker_path" && ! -L "$marker_path" ]] ||
        fail "The reviewed game-host readiness marker is missing or unsafe."

    prepared_operator_user=""
    prepared_service_user=""
    prepared_ssh_port=""
    prepared_game_port=""
    local prepared_schema_version=""

    while IFS='=' read -r key value || [[ -n "${key:-}${value:-}" ]]; do
        [[ -n "${key:-}" ]] || fail "The game-host readiness marker contains an empty key."
        [[ -z "${seen[$key]+x}" ]] ||
            fail "The game-host readiness marker contains duplicate key '$key'."
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
                fail "The game-host readiness marker contains unknown key '$key'."
                ;;
        esac
    done < "$marker_path"

    [[ "$prepared_schema_version" == "1" ]] ||
        fail "The game-host readiness marker schema is unsupported."
    validate_user_name "host-prepared operator_user" "$prepared_operator_user"
    validate_user_name "host-prepared service_user" "$prepared_service_user"
    validate_port "host-prepared ssh_port" "$prepared_ssh_port"
    validate_port "host-prepared game_port" "$prepared_game_port"
    [[ "$prepared_service_user" == "$service_user" ]] ||
        fail "--service-user does not match the reviewed game-host foundation."
    [[ "$prepared_game_port" == "$game_port" ]] ||
        fail "--game-port does not match the reviewed game-host foundation."
}

directory_is_empty() {
    local path="$1"
    [[ -d "$path" && -z "$(find "$path" -mindepth 1 -maxdepth 1 -print -quit)" ]]
}

validate_foundation_directory() {
    local path="$1"
    local expected_owner="$2"
    local expected_group="$3"
    local expected_mode="$4"
    local actual

    [[ -d "$path" && ! -L "$path" ]] || fail "Required directory '$path' is missing or unsafe."
    actual="$(stat -c '%U:%G:%a' "$path")"
    [[ "$actual" == "$expected_owner:$expected_group:$expected_mode" ]] ||
        fail "Directory '$path' does not match the reviewed owner and mode."
}

require_apply_environment() {
    ((EUID == 0)) || fail "--apply must run as root."

    local command
    for command in \
        curl \
        file \
        find \
        flock \
        getent \
        gpg \
        install \
        runuser \
        sha256sum \
        stat \
        systemctl \
        tar \
        unzip; do
        require_command "$command"
    done

    [[ -r /etc/os-release ]] || fail "The operating-system identity is unavailable."
    # shellcheck disable=SC1091
    source /etc/os-release
    [[ "${ID:-}" == "ubuntu" && "${VERSION_ID:-}" == "24.04" ]] ||
        fail "The runtime installer supports Ubuntu 24.04 only."
    [[ "$(uname -m)" == "x86_64" ]] || fail "The runtime installer requires x86-64."
    [[ "$(ps -p 1 -o comm= | tr -d '[:space:]')" == "systemd" ]] ||
        fail "The runtime installer requires systemd as PID 1."

    read_prepared_marker
    service_group="$(id -gn "$service_user")"
    service_home="$(getent passwd "$service_user" | cut -d: -f6)"
    [[ "$service_home" == "/var/lib/$service_user" ]] ||
        fail "The service account has an unexpected home directory."
    [[ "$(getent passwd "$service_user" | cut -d: -f7)" == "/usr/sbin/nologin" ]] ||
        fail "The service account must remain non-interactive."
    [[ "${SUDO_USER:-}" == "$prepared_operator_user" ]] ||
        fail "Run --apply through sudo from the operator recorded by host bootstrap."

    [[ "$(stat -c '%U:%G:%a' "$prepared_marker")" == "root:$service_group:640" ]] ||
        fail "The game-host readiness marker owner or mode has drifted."
    validate_foundation_directory "$installation_directory" root "$service_group" 750
    validate_foundation_directory "$artifact_directory" root "$service_group" 750
    validate_foundation_directory "$service_home" "$service_user" "$service_group" 750
    validate_foundation_directory "$service_home/steamcmd" "$service_user" "$service_group" 750
    validate_foundation_directory "$service_home/server" "$service_user" "$service_group" 750
    validate_foundation_directory "$configuration_directory" root "$service_group" 750
    validate_foundation_directory "$configuration_directory/secrets" root "$service_group" 710

    directory_is_empty "$service_home/steamcmd" ||
        fail "The SteamCMD target is not empty; upgrades require a separate reviewed workflow."
    directory_is_empty "$service_home/server" ||
        fail "The server target is not empty; upgrades require a separate reviewed workflow."
    [[ ! -e "$runtime_marker" && ! -e "$runtime_enabled_marker" ]] ||
        fail "A runtime marker already exists; this installer only supports the first installation."
    [[ ! -e "$configuration_directory/server-public.cfg" &&
        ! -e "$configuration_directory/secrets/server-private.cfg" ]] ||
        fail "Runtime configuration or secrets already exist; configure them in the activation workflow."
    [[ ! -e "$systemd_unit_file" ]] ||
        fail "The game-server systemd unit already exists."
    ! systemctl is-active --quiet "$SERVICE_NAME" ||
        fail "The game-server service must not be active during installation."
    ! systemctl is-enabled --quiet "$SERVICE_NAME" ||
        fail "The game-server service must not be enabled during installation."
}

verify_sha256() {
    local path="$1"
    local expected_sha256="$2"
    local actual_sha256

    [[ "$expected_sha256" =~ ^[0-9a-f]{64}$ ]] || fail "The expected SHA-256 is invalid."
    [[ -f "$path" && ! -L "$path" ]] || fail "Artifact '$path' is missing or unsafe."
    actual_sha256="$(sha256sum "$path" | awk '{ print $1 }')"
    [[ "$actual_sha256" == "$expected_sha256" ]] ||
        fail "SHA-256 verification failed for '$(basename "$path")'."
}

download_artifact() {
    local name="$1"
    local url="$2"
    local expected_sha256="${3:-}"
    local destination="$artifact_directory/$name"
    local partial="$artifact_directory/.${name}.partial.$$"

    if [[ -e "$destination" ]]; then
        [[ -f "$destination" && ! -L "$destination" ]] ||
            fail "Cached artifact '$name' is unsafe."
        if [[ -n "$expected_sha256" ]]; then
            verify_sha256 "$destination" "$expected_sha256"
        fi
        log "VERIFIED: reused cached artifact $name"
        return
    fi

    rm -f -- "$partial"
    curl \
        --fail \
        --silent \
        --show-error \
        --location \
        --proto '=https' \
        --tlsv1.2 \
        --retry 4 \
        --retry-delay 3 \
        --connect-timeout 30 \
        --output "$partial" \
        "$url"
    if [[ -n "$expected_sha256" ]]; then
        verify_sha256 "$partial" "$expected_sha256"
    fi
    chown root:"$service_group" "$partial"
    chmod 0640 "$partial"
    mv -- "$partial" "$destination"
    log "VERIFIED: downloaded artifact $name"
}

write_rehlds_signing_key() {
    local destination="$1"

    cat > "$destination" <<'EOF'
-----BEGIN PGP PUBLIC KEY BLOCK-----

mDMEZzzorxYJKwYBBAHaRw8BAQdAz2dQ+9M7Vp2yShgoKwIe2c6r4I2mBlCTq1gh
snmAMO60HVJlSExEUyBUZWFtIDx0ZWFtQHJlaGxkcy5kZXY+iJMEExYKADsWIQRj
VHgpAE8HcW975IVsMsQoLmD7ZwUCZzzorwIbIwULCQgHAgIiAgYVCgkICwIEFgID
AQIeBwIXgAAKCRBsMsQoLmD7Z7qVAP4ov8vzunq2HPyLUuAen4JVG/s8LE73l0zW
91b3HwtwkgEAjl2wo/1NIHm4u9+1JA0efg3dJbJUcas6qO3+pE9QJAG4OARnPOiv
EgorBgEEAZdVAQUBAQdAnEbH4Ch9kQQcmOT03TPyRONJestjn9F0/CnHtlTdNFkD
AQgHiHgEGBYKACAWIQRjVHgpAE8HcW975IVsMsQoLmD7ZwUCZzzorwIbDAAKCRBs
MsQoLmD7Zx17AQCcr6RTQzyyl33yUeTTr1rkyhY8Zrcv2e7lZZ7CQzhJuQD/XWww
51g/zuvcY8hCsrO65B8yWhYCUdJ4r01vNKIxIgE=
=7IRq
-----END PGP PUBLIC KEY BLOCK-----
EOF
    chmod 0600 "$destination"
}

verify_release_signatures() {
    local key_file="$verification_root/rehlds-signing-key.asc"
    local gnupg_home="$verification_root/gnupg"
    local fingerprint

    install -d -m 0700 "$gnupg_home"
    write_rehlds_signing_key "$key_file"
    fingerprint="$(GNUPGHOME="$gnupg_home" gpg \
        --batch \
        --with-colons \
        --import-options show-only \
        --import "$key_file" 2>/dev/null |
        awk -F: '$1 == "fpr" { print $10; exit }')"
    [[ "$fingerprint" == "$REHLDS_SIGNING_KEY_FINGERPRINT" ]] ||
        fail "The embedded ReHLDS signing key fingerprint is invalid."

    GNUPGHOME="$gnupg_home" gpg --batch --quiet --import "$key_file"
    GNUPGHOME="$gnupg_home" gpg --batch --verify \
        "$artifact_directory/$REHLDS_SIGNATURE_NAME" \
        "$artifact_directory/$REHLDS_ARCHIVE_NAME"
    GNUPGHOME="$gnupg_home" gpg --batch --verify \
        "$artifact_directory/$REGAMEDLL_SIGNATURE_NAME" \
        "$artifact_directory/$REGAMEDLL_ARCHIVE_NAME"
    log "VERIFIED: ReHLDS and ReGameDLL_CS detached signatures"
}

reject_unsafe_archive_entry() {
    local entry="$1"
    case "$entry" in
        /*|../*|*/../*|*/..)
            fail "An archive contains an unsafe path."
            ;;
    esac
}

verify_archive_layouts() {
    local entry steamcmd_entries rehlds_entries regamedll_entries

    steamcmd_entries="$(tar -tzf "$artifact_directory/$STEAMCMD_ARCHIVE_NAME")" ||
        fail "The pinned SteamCMD archive cannot be listed."
    while IFS= read -r entry; do
        reject_unsafe_archive_entry "$entry"
    done <<< "$steamcmd_entries"
    grep -Fxq 'steamcmd.sh' <<< "$steamcmd_entries" ||
        fail "The pinned SteamCMD archive is missing steamcmd.sh."
    grep -Fxq 'linux32/steamcmd' <<< "$steamcmd_entries" ||
        fail "The pinned SteamCMD archive is missing its Linux bootstrap."

    rehlds_entries="$(unzip -Z1 "$artifact_directory/$REHLDS_ARCHIVE_NAME")" ||
        fail "The pinned ReHLDS archive cannot be listed."
    while IFS= read -r entry; do
        reject_unsafe_archive_entry "$entry"
    done <<< "$rehlds_entries"
    grep -Fxq 'bin/linux32/hlds_linux' <<< "$rehlds_entries" ||
        fail "The pinned ReHLDS archive has an unexpected layout."
    grep -Fxq 'bin/linux32/engine_i486.so' <<< "$rehlds_entries" ||
        fail "The pinned ReHLDS archive is missing engine_i486.so."

    regamedll_entries="$(unzip -Z1 "$artifact_directory/$REGAMEDLL_ARCHIVE_NAME")" ||
        fail "The pinned ReGameDLL_CS archive cannot be listed."
    while IFS= read -r entry; do
        reject_unsafe_archive_entry "$entry"
    done <<< "$regamedll_entries"
    grep -Fxq 'bin/linux32/cstrike/dlls/cs.so' <<< "$regamedll_entries" ||
        fail "The pinned ReGameDLL_CS archive has an unexpected layout."
}

acquire_and_verify_artifacts() {
    download_artifact "$STEAMCMD_ARCHIVE_NAME" \
        "$STEAMCMD_ARCHIVE_URL" "$STEAMCMD_ARCHIVE_SHA256"
    download_artifact "$REHLDS_ARCHIVE_NAME" \
        "$REHLDS_ARCHIVE_URL" "$REHLDS_ARCHIVE_SHA256"
    download_artifact "$REHLDS_SIGNATURE_NAME" "$REHLDS_SIGNATURE_URL"
    download_artifact "$REGAMEDLL_ARCHIVE_NAME" \
        "$REGAMEDLL_ARCHIVE_URL" "$REGAMEDLL_ARCHIVE_SHA256"
    download_artifact "$REGAMEDLL_SIGNATURE_NAME" "$REGAMEDLL_SIGNATURE_URL"
    verify_release_signatures
    verify_archive_layouts
}

prepare_staging() {
    staging_root="$service_home/.runtime-install.$$"
    verification_root="$installation_directory/.runtime-verification.$$"
    [[ ! -e "$staging_root" && ! -e "$verification_root" ]] ||
        fail "A runtime installation staging path already exists."
    install -d -m 0750 -o "$service_user" -g "$service_group" "$staging_root"
    install -d -m 0700 -o root -g root "$verification_root"
}

run_as_service() {
    runuser --user "$service_user" -- "$@"
}

install_steamcmd() {
    install -d -m 0750 -o "$service_user" -g "$service_group" \
        "$staging_root/steamcmd" \
        "$staging_root/server" \
        "$staging_root/home"
    run_as_service tar -xzf "$artifact_directory/$STEAMCMD_ARCHIVE_NAME" \
        -C "$staging_root/steamcmd"
    [[ -f "$staging_root/steamcmd/steamcmd.sh" ]] ||
        fail "SteamCMD extraction did not produce steamcmd.sh."
    chmod 0750 "$staging_root/steamcmd/steamcmd.sh"

    run_as_service env \
        HOME="$staging_root/home" \
        STEAMCMDDIR="$staging_root/steamcmd" \
        "$staging_root/steamcmd/steamcmd.sh" +quit
    [[ -f "$staging_root/steamcmd/linux32/steamclient.so" ]] ||
        fail "SteamCMD bootstrap did not produce steamclient.so."
    steamcmd_script_sha256="$(sha256sum "$staging_root/steamcmd/steamcmd.sh" | awk '{ print $1 }')"
    steamcmd_binary_sha256="$(sha256sum "$staging_root/steamcmd/linux32/steamcmd" | awk '{ print $1 }')"
    steamclient_binary_sha256="$(sha256sum "$staging_root/steamcmd/linux32/steamclient.so" | awk '{ print $1 }')"
}

hlds_sentinels_exist() {
    local server_root="$staging_root/server"
    local relative_path
    for relative_path in \
        hlds_run \
        hlds_linux \
        engine_i486.so \
        cstrike/dlls/cs.so \
        cstrike/liblist.gam \
        cstrike/maps/de_dust2.bsp; do
        [[ -f "$server_root/$relative_path" ]] || return 1
    done
    [[ -n "$(find "$server_root" -name gfx.wad -type f -print -quit)" ]]
}

install_hlds() {
    local previous_size="-1"
    local stable_passes=0
    local current_size run

    for run in $(seq 1 30); do
        log "SteamCMD app $STEAM_APP_ID pass $run"
        if ! run_as_service env \
            HOME="$staging_root/home" \
            STEAMCMDDIR="$staging_root/steamcmd" \
            "$staging_root/steamcmd/steamcmd.sh" \
            +force_install_dir "$staging_root/server" \
            +login anonymous \
            +app_update "$STEAM_APP_ID" -beta "$STEAM_BRANCH" validate \
            +quit; then
            log "SteamCMD pass $run returned non-zero; completeness checks will decide whether to retry."
        fi

        current_size="$(du -sb "$staging_root/server" | cut -f1)"
        if [[ "$current_size" == "$previous_size" && "$current_size" != "0" ]]; then
            ((stable_passes += 1))
        else
            stable_passes=0
        fi
        previous_size="$current_size"

        if ((stable_passes >= 2)) && hlds_sentinels_exist; then
            log "VERIFIED: complete HLDS app $STEAM_APP_ID after $run passes"
            break
        fi
    done

    if ((stable_passes < 2)) || ! hlds_sentinels_exist; then
        fail "SteamCMD did not produce a complete stable Counter-Strike 1.6 server tree."
    fi
    rm -rf -- "$staging_root/server/linux64"

    local manifests=()
    mapfile -t manifests < <(find "$staging_root" -name "appmanifest_${STEAM_APP_ID}.acf" -type f -print)
    ((${#manifests[@]} == 1)) ||
        fail "Expected exactly one Steam app manifest for app $STEAM_APP_ID."
    hlds_build_id="$(awk -F'"' '$2 == "buildid" { print $4; exit }' "${manifests[0]}")"
    [[ "$hlds_build_id" =~ ^[0-9]+$ ]] || fail "The HLDS Steam build id is unavailable."
    hlds_manifest_sha256="$(sha256sum "${manifests[0]}" | awk '{ print $1 }')"
    base_hlds_linux_sha256="$(sha256sum "$staging_root/server/hlds_linux" | awk '{ print $1 }')"
}

apply_runtime_overlays() {
    local rehlds_extract="$verification_root/rehlds"
    local regamedll_extract="$verification_root/regamedll"
    install -d -m 0700 "$rehlds_extract" "$regamedll_extract"

    unzip -q "$artifact_directory/$REHLDS_ARCHIVE_NAME" 'bin/linux32/*' \
        -d "$rehlds_extract"
    unzip -q "$artifact_directory/$REGAMEDLL_ARCHIVE_NAME" 'bin/linux32/cstrike/*' \
        -d "$regamedll_extract"
    cp -a "$rehlds_extract/bin/linux32/." "$staging_root/server/"
    cp -a "$regamedll_extract/bin/linux32/cstrike/." "$staging_root/server/cstrike/"

    chmod 0750 \
        "$staging_root/server/hlds_run" \
        "$staging_root/server/hlds_linux" \
        "$staging_root/server/hltv"
    chown -R "$service_user:$service_group" \
        "$staging_root/steamcmd" \
        "$staging_root/server"

    final_hlds_linux_sha256="$(sha256sum "$staging_root/server/hlds_linux" | awk '{ print $1 }')"
    final_engine_sha256="$(sha256sum "$staging_root/server/engine_i486.so" | awk '{ print $1 }')"
    final_regamedll_sha256="$(sha256sum "$staging_root/server/cstrike/dlls/cs.so" | awk '{ print $1 }')"

    [[ "$final_hlds_linux_sha256" == \
        "$(sha256sum "$rehlds_extract/bin/linux32/hlds_linux" | awk '{ print $1 }')" ]] ||
        fail "The installed ReHLDS launcher does not match the verified archive."
    [[ "$final_engine_sha256" == \
        "$(sha256sum "$rehlds_extract/bin/linux32/engine_i486.so" | awk '{ print $1 }')" ]] ||
        fail "The installed ReHLDS engine does not match the verified archive."
    [[ "$final_regamedll_sha256" == \
        "$(sha256sum "$regamedll_extract/bin/linux32/cstrike/dlls/cs.so" | awk '{ print $1 }')" ]] ||
        fail "The installed ReGameDLL_CS binary does not match the verified archive."
}

validate_runtime_tree() {
    local server_root="$staging_root/server"
    hlds_sentinels_exist || fail "The assembled runtime is missing a required server file."
    [[ -f "$server_root/libsteam_api.so" ]] ||
        fail "The assembled runtime is missing libsteam_api.so."

    file -b "$server_root/hlds_linux" | grep -Fq 'ELF 32-bit LSB' ||
        fail "The ReHLDS launcher is not a 32-bit Linux ELF binary."
    file -b "$server_root/engine_i486.so" | grep -Fq 'ELF 32-bit LSB' ||
        fail "The ReHLDS engine is not a 32-bit Linux ELF binary."
    file -b "$server_root/cstrike/dlls/cs.so" | grep -Fq 'ELF 32-bit LSB' ||
        fail "ReGameDLL_CS is not a 32-bit Linux ELF binary."

    grep -Eq '^[[:space:]]*gamedll_linux[[:space:]]+"?dlls/cs\.so"?' \
        "$server_root/cstrike/liblist.gam" ||
        fail "The minimal baseline must load ReGameDLL_CS directly."
    if grep -Eiq 'metamod|amxmodx|reapi|yapb|reunion' "$server_root/cstrike/liblist.gam"; then
        fail "The minimal baseline unexpectedly references a plugin loader."
    fi

    local excluded_path
    for excluded_path in \
        cstrike/addons/metamod \
        cstrike/addons/amxmodx \
        cstrike/addons/yapb \
        cstrike/addons/reapi \
        cstrike/addons/reunion; do
        [[ ! -e "$server_root/$excluded_path" ]] ||
            fail "The minimal baseline unexpectedly contains '$excluded_path'."
    done
}

render_service_unit() {
    local destination="$1"
    # The value must remain literal for systemd to expand at service start.
    # shellcheck disable=SC2016
    local credentials_directory_reference='${CREDENTIALS_DIRECTORY}'

    cat > "$destination" <<EOF
[Unit]
Description=GoldSrcOps controlled Counter-Strike 1.6 server
Documentation=https://github.com/tov-vl/gold-src-ops/blob/main/ops/gameserver/README.md
Wants=network-online.target
After=network-online.target
StartLimitIntervalSec=300
StartLimitBurst=5
ConditionPathExists=$runtime_enabled_marker
ConditionPathExists=$configuration_directory/server-public.cfg
ConditionPathExists=$configuration_directory/secrets/server-private.cfg

[Service]
Type=simple
User=$service_user
Group=$service_group
WorkingDirectory=$service_home/server
LoadCredential=server-public.cfg:$configuration_directory/server-public.cfg
LoadCredential=server-private.cfg:$configuration_directory/secrets/server-private.cfg
ExecStartPre=/usr/bin/ln --symbolic --force $credentials_directory_reference/server-public.cfg $service_home/server/cstrike/goldsrcops-public.cfg
ExecStartPre=/usr/bin/ln --symbolic --force $credentials_directory_reference/server-private.cfg $service_home/server/cstrike/goldsrcops-private.cfg
ExecStart=$service_home/server/hlds_run -game cstrike -console -strictportbind -ip 0.0.0.0 -port $game_port +map de_dust2 +maxplayers 4 +exec goldsrcops-public.cfg +exec goldsrcops-private.cfg
ExecStopPost=-/usr/bin/rm --force $service_home/server/cstrike/goldsrcops-public.cfg $service_home/server/cstrike/goldsrcops-private.cfg
Restart=on-failure
RestartSec=5s
TimeoutStopSec=30s
KillSignal=SIGINT
UMask=0027
LimitNOFILE=8192
TasksMax=128
MemoryMax=1G
NoNewPrivileges=true
CapabilityBoundingSet=
AmbientCapabilities=
PrivateDevices=true
PrivateTmp=true
ProtectClock=true
ProtectControlGroups=true
ProtectHome=true
ProtectHostname=true
ProtectKernelLogs=true
ProtectKernelModules=true
ProtectKernelTunables=true
ProtectProc=invisible
ProtectSystem=strict
ProcSubset=pid
RemoveIPC=true
RestrictNamespaces=true
RestrictRealtime=true
RestrictSUIDSGID=true
ReadOnlyPaths=$configuration_directory
ReadWritePaths=$service_home/server
InaccessiblePaths=$artifact_directory /var/backups/goldsrcops/gameserver
StandardOutput=journal
StandardError=journal
SyslogIdentifier=goldsrcops-gameserver
LogRateLimitIntervalSec=30s
LogRateLimitBurst=1000

[Install]
WantedBy=multi-user.target
EOF
    chmod 0644 "$destination"
}

promote_runtime() {
    directory_is_empty "$service_home/steamcmd" ||
        fail "The SteamCMD target changed during installation."
    directory_is_empty "$service_home/server" ||
        fail "The server target changed during installation."

    rmdir -- "$service_home/steamcmd" "$service_home/server"
    mv -- "$staging_root/steamcmd" "$service_home/steamcmd"
    mv -- "$staging_root/server" "$service_home/server"
    install -d -m 0750 -o "$service_user" -g "$service_group" "$service_home/.steam/sdk32"
    ln -s "$service_home/steamcmd/linux32/steamclient.so" \
        "$service_home/.steam/sdk32/steamclient.so"
    chown -h "$service_user:$service_group" "$service_home/.steam/sdk32/steamclient.so"
}

install_service_unit() {
    local rendered_unit="$verification_root/$SERVICE_NAME"
    render_service_unit "$rendered_unit"
    grep -Fq "User=$service_user" "$rendered_unit" || fail "The rendered unit user is invalid."
    grep -Fq "ConditionPathExists=$runtime_enabled_marker" "$rendered_unit" ||
        fail "The rendered unit is missing its activation gate."
    install -m 0644 -o root -g root "$rendered_unit" "$systemd_unit_file"
    systemctl daemon-reload
    ! systemctl is-enabled --quiet "$SERVICE_NAME" ||
        fail "The game-server service became enabled unexpectedly."
    ! systemctl is-active --quiet "$SERVICE_NAME" ||
        fail "The game-server service became active unexpectedly."
}

write_runtime_marker() {
    local temporary_marker="$configuration_directory/.runtime-installed.$$"
    local service_unit_sha256
    service_unit_sha256="$(sha256sum "$systemd_unit_file" | awk '{ print $1 }')"
    cat > "$temporary_marker" <<EOF
schema_version=$INSTALLER_SCHEMA_VERSION
steam_app_id=$STEAM_APP_ID
steam_branch=$STEAM_BRANCH
steamcmd_bootstrap_sha256=$STEAMCMD_ARCHIVE_SHA256
steamcmd_script_sha256=$steamcmd_script_sha256
steamcmd_binary_sha256=$steamcmd_binary_sha256
steamclient_binary_sha256=$steamclient_binary_sha256
hlds_build_id=$hlds_build_id
hlds_app_manifest_sha256=$hlds_manifest_sha256
base_hlds_linux_sha256=$base_hlds_linux_sha256
rehlds_version=$REHLDS_VERSION
rehlds_archive_sha256=$REHLDS_ARCHIVE_SHA256
rehlds_hlds_linux_sha256=$final_hlds_linux_sha256
rehlds_engine_sha256=$final_engine_sha256
regamedll_version=$REGAMEDLL_VERSION
regamedll_archive_sha256=$REGAMEDLL_ARCHIVE_SHA256
regamedll_binary_sha256=$final_regamedll_sha256
service_unit_sha256=$service_unit_sha256
EOF
    chown root:"$service_group" "$temporary_marker"
    chmod 0640 "$temporary_marker"
    mv -- "$temporary_marker" "$runtime_marker"
}

cleanup() {
    local exit_code=$?
    if [[ -n "$staging_root" && "$staging_root" == "$service_home/.runtime-install."* ]]; then
        rm -rf -- "$staging_root"
    fi
    if [[ -n "$verification_root" &&
        "$verification_root" == "$installation_directory/.runtime-verification."* ]]; then
        rm -rf -- "$verification_root"
    fi
    find "$artifact_directory" -maxdepth 1 -type f -name '.*.partial.*' -delete 2>/dev/null || true
    return "$exit_code"
}

run_apply() {
    require_apply_environment
    exec 9>"$configuration_directory/runtime-install.lock"
    flock --nonblock 9 || fail "Another runtime installation is already in progress."
    chown root:"$service_group" "$configuration_directory/runtime-install.lock"
    chmod 0640 "$configuration_directory/runtime-install.lock"

    prepare_staging
    trap cleanup EXIT
    acquire_and_verify_artifacts
    install_steamcmd
    install_hlds
    apply_runtime_overlays
    validate_runtime_tree
    promote_runtime
    install_service_unit
    write_runtime_marker

    log "INSTALLED: pinned SteamCMD, HLDS, ReHLDS $REHLDS_VERSION, and ReGameDLL_CS $REGAMEDLL_VERSION."
    log "SERVICE_STATE: disabled and inactive"
    log "RUNTIME_GATE: create reviewed public/private configuration and runtime-enabled separately before start."
}

print_plan() {
    log "PLAN: require the reviewed game-host foundation and an empty runtime target"
    log "PLAN: verify SteamCMD bootstrap SHA-256 $STEAMCMD_ARCHIVE_SHA256"
    log "PLAN: install HLDS app $STEAM_APP_ID from pinned branch $STEAM_BRANCH until completeness stabilizes"
    log "PLAN: verify ReHLDS $REHLDS_VERSION SHA-256 and detached signature"
    log "PLAN: verify ReGameDLL_CS $REGAMEDLL_VERSION SHA-256 and detached signature"
    log "PLAN: reject plugin or bot files and record the installed Steam build identity"
    log "PLAN: install a constrained, disabled-by-default $SERVICE_NAME"
    log "PLAN: leave RCON secrets, runtime activation, service start, and public UDP unchanged"
    log "PLAN_ONLY: no host changes were made; add --apply to execute this plan."
}

main() {
    while (($# > 0)); do
        case "$1" in
            --service-user)
                (($# >= 2)) || fail "--service-user requires a value."
                service_user="$2"
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

if [[ "${BASH_SOURCE[0]:-$0}" == "$0" ]]; then
    main "$@"
fi
