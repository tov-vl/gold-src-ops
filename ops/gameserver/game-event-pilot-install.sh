#!/usr/bin/env bash

set -Eeuo pipefail
IFS=$'\n\t'
umask 027

readonly INSTALLER_SCHEMA_VERSION="1"
readonly BUNDLE_SCHEMA_VERSION="1"
readonly EXPECTED_AMXX_VERSION="1.10.0.5481"
readonly EXPECTED_AMXX_BASE_SHA256="ee33b31ae92afd94802c43eae14ecdfa1ffa2ba0b11658e8bec98f48a5881272"
readonly EXPECTED_AMXX_CSTRIKE_SHA256="76ff2bdd39f6dc14a2088ff4af724599c69a8ae7e3526679927aaef4ca898bf2"
readonly EXPECTED_METAMOD_VERSION="1.3.0.149"
readonly EXPECTED_METAMOD_SHA256="ede7f59c4e0220afe8c02aa348a130cce527f87d36ffdb674e37a501ce57be94"
readonly EXPECTED_REAPI_VERSION="5.24.0.300"
readonly EXPECTED_REAPI_SHA256="16114cf5a782e9d3d0c9443c23cd937c17cc09dc9b16ff998ce48bda5faf0a81"
readonly SERVICE_NAME="goldsrcops-game-event-agent.service"

configuration_directory="${GOLDSRCOPS_CONFIGURATION_DIRECTORY:-/etc/goldsrcops/gameserver}"
installation_directory="${GOLDSRCOPS_INSTALLATION_DIRECTORY:-/opt/goldsrcops/gameserver}"
service_home="${GOLDSRCOPS_SERVICE_HOME:-/var/lib/goldsrc}"
systemd_unit_file="${GOLDSRCOPS_GAME_EVENT_SYSTEMD_UNIT_FILE:-/etc/systemd/system/$SERVICE_NAME}"
prepared_marker="$configuration_directory/host-prepared"
runtime_marker="$configuration_directory/runtime-installed"
runtime_enabled_marker="$configuration_directory/runtime-enabled"
pilot_marker="$configuration_directory/game-event-pilot-installed"
pilot_enabled_marker="$configuration_directory/game-event-pilot-enabled"
environment_file="$configuration_directory/game-event-agent.env"
client_secret_file="$configuration_directory/secrets/game-event-agent-client-secret"
pilot_root="$installation_directory/game-event-pilot"
pilot_releases_directory="$pilot_root/releases"
state_root="$service_home/game-event-agent"
spool_root="$service_home/server/cstrike/addons/amxmodx/data/goldsrcops-spool"

service_user="goldsrc"
bundle_path=""
bundle_sha256=""
apply_changes=false
rollback_changes=false
service_group=""
prepared_operator_user=""
prepared_service_user=""
runtime_rehlds_version=""
runtime_regamedll_version=""
pilot_bundle_version=""
pilot_source_revision=""
pilot_bundle_sha256=""
pilot_manifest_sha256=""
pilot_environment_sha256=""
pilot_unit_sha256=""
verification_root=""
release_path=""
rendered_environment=""
rendered_unit=""
created_release=false
created_state=false
installed_environment=false
installed_unit=false

usage() {
    cat <<'EOF'
Usage:
  game-event-pilot-install.sh [--bundle <absolute-path> --bundle-sha256 <sha256>] [--service-user <name>] [--apply]
  game-event-pilot-install.sh --rollback [--service-user <name>] [--apply]

Without --apply, the script validates arguments and prints a sanitized plan.
Installation requires a root-owned 0600 immutable pilot ZIP and its separately
transported SHA-256. It verifies the bundle manifest and every payload file,
installs a content-addressed release and a constrained systemd unit, and leaves
the unit disabled and inactive. It does not patch liblist.gam, copy plugins into
the live game tree, provision credentials, enable delivery, or restart services.

Rollback is pre-activation only. It refuses to proceed after an activation
marker, credential, state file, live plugin overlay, or active/enabled agent is
present.
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
    local value="$1"
    [[ "$value" =~ ^[a-z_][a-z0-9_-]{0,31}$ ]] ||
        fail "--service-user must be a valid local Linux account name."
    [[ "$value" != "root" ]] || fail "--service-user must not be root."
}

validate_sha256() {
    local name="$1"
    local value="$2"
    [[ "$value" =~ ^[0-9a-f]{64}$ ]] || fail "$name must be a lowercase SHA-256."
}

validate_inputs() {
    validate_user_name "$service_user"
    if [[ "$rollback_changes" == "true" ]]; then
        [[ -z "$bundle_path" && -z "$bundle_sha256" ]] ||
            fail "--rollback cannot be combined with bundle arguments."
        return
    fi

    if [[ -n "$bundle_path" || -n "$bundle_sha256" ]]; then
        [[ -n "$bundle_path" && -n "$bundle_sha256" ]] ||
            fail "--bundle and --bundle-sha256 must be supplied together."
        [[ "$bundle_path" == /* ]] || fail "--bundle must be an absolute path."
        [[ "$bundle_path" != *$'\n'* && "$bundle_path" != *$'\r'* ]] ||
            fail "--bundle contains an unsupported character."
        validate_sha256 "--bundle-sha256" "$bundle_sha256"
    elif [[ "$apply_changes" == "true" ]]; then
        fail "--apply installation requires --bundle and --bundle-sha256."
    fi
}

read_prepared_marker() {
    local marker_path="${1:-$prepared_marker}"
    local key value prepared_schema_version="" prepared_game_port="" prepared_ssh_port=""
    declare -A seen=()

    [[ -f "$marker_path" && ! -L "$marker_path" ]] ||
        fail "The reviewed game-host readiness marker is missing or unsafe."
    prepared_operator_user=""
    prepared_service_user=""

    while IFS='=' read -r key value || [[ -n "${key:-}${value:-}" ]]; do
        [[ -n "${key:-}" ]] || fail "The game-host readiness marker contains an empty key."
        [[ -z "${seen[$key]+x}" ]] ||
            fail "The game-host readiness marker contains a duplicate key."
        seen[$key]=1

        case "$key" in
            schema_version) prepared_schema_version="$value" ;;
            operator_user) prepared_operator_user="$value" ;;
            service_user) prepared_service_user="$value" ;;
            ssh_port) prepared_ssh_port="$value" ;;
            game_port) prepared_game_port="$value" ;;
            *) fail "The game-host readiness marker contains an unknown key." ;;
        esac
    done < "$marker_path"

    [[ "$prepared_schema_version" == "1" ]] ||
        fail "The game-host readiness marker schema is unsupported."
    [[ "$prepared_operator_user" =~ ^[a-z_][a-z0-9_-]{0,31}$ ]] ||
        fail "The prepared operator account is invalid."
    [[ "$prepared_service_user" == "$service_user" ]] ||
        fail "--service-user does not match the reviewed game-host foundation."
    [[ "$prepared_ssh_port" =~ ^[0-9]+$ && "$prepared_game_port" =~ ^[0-9]+$ ]] ||
        fail "The game-host readiness marker contains an invalid port."
}

read_runtime_marker() {
    local marker_path="${1:-$runtime_marker}"
    local key value runtime_schema_version="" required_key
    declare -A seen=()
    declare -A values=()

    [[ -f "$marker_path" && ! -L "$marker_path" ]] ||
        fail "The reviewed game-runtime marker is missing or unsafe."
    runtime_rehlds_version=""
    runtime_regamedll_version=""

    while IFS='=' read -r key value || [[ -n "${key:-}${value:-}" ]]; do
        [[ -n "${key:-}" ]] || fail "The game-runtime marker contains an empty key."
        [[ -z "${seen[$key]+x}" ]] || fail "The game-runtime marker contains a duplicate key."
        seen[$key]=1
        values[$key]="$value"

        case "$key" in
            schema_version) runtime_schema_version="$value" ;;
            rehlds_version) runtime_rehlds_version="$value" ;;
            regamedll_version) runtime_regamedll_version="$value" ;;
            steam_app_id|steam_branch|steamcmd_bootstrap_sha256|steamcmd_script_sha256|\
            steamcmd_binary_sha256|steamclient_binary_sha256|hlds_build_id|\
            hlds_app_manifest_sha256|base_hlds_linux_sha256|rehlds_archive_sha256|\
            rehlds_hlds_linux_sha256|rehlds_engine_sha256|regamedll_archive_sha256|\
            regamedll_binary_sha256|service_unit_sha256) ;;
            *) fail "The game-runtime marker contains an unknown key." ;;
        esac
    done < "$marker_path"

    for required_key in \
        schema_version \
        steam_app_id \
        steam_branch \
        steamcmd_bootstrap_sha256 \
        steamcmd_script_sha256 \
        steamcmd_binary_sha256 \
        steamclient_binary_sha256 \
        hlds_build_id \
        hlds_app_manifest_sha256 \
        base_hlds_linux_sha256 \
        rehlds_version \
        rehlds_archive_sha256 \
        rehlds_hlds_linux_sha256 \
        rehlds_engine_sha256 \
        regamedll_version \
        regamedll_archive_sha256 \
        regamedll_binary_sha256 \
        service_unit_sha256; do
        [[ -n "${seen[$required_key]+x}" ]] || fail "The game-runtime marker is incomplete."
    done
    [[ "$runtime_schema_version" == "1" ]] || fail "The game-runtime marker schema is unsupported."
    [[ "${values[steam_app_id]}" == "90" && "${values[steam_branch]}" == "steam_legacy" ]] ||
        fail "The installed Steam runtime identity is not the reviewed pilot baseline."
    [[ "${values[hlds_build_id]}" =~ ^[0-9]+$ ]] || fail "The game-runtime marker build identity is invalid."
    [[ "$runtime_rehlds_version" == "3.15.0.896" ]] ||
        fail "The installed ReHLDS version is not the reviewed pilot baseline."
    [[ "$runtime_regamedll_version" == "5.30.0.814" ]] ||
        fail "The installed ReGameDLL_CS version is not the reviewed pilot baseline."
    for key in "${!values[@]}"; do
        if [[ "$key" == *_sha256 ]]; then
            validate_sha256 "game-runtime marker hash" "${values[$key]}"
        fi
    done
}

read_pilot_marker() {
    local marker_path="${1:-$pilot_marker}"
    local key value pilot_schema_version=""
    declare -A seen=()

    [[ -f "$marker_path" && ! -L "$marker_path" ]] ||
        fail "The pilot installation marker is missing or unsafe."
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
            schema_version) pilot_schema_version="$value" ;;
            bundle_version) pilot_bundle_version="$value" ;;
            source_revision) pilot_source_revision="$value" ;;
            bundle_sha256) pilot_bundle_sha256="$value" ;;
            manifest_sha256) pilot_manifest_sha256="$value" ;;
            environment_file_sha256) pilot_environment_sha256="$value" ;;
            service_unit_sha256) pilot_unit_sha256="$value" ;;
            *) fail "The pilot marker contains an unknown key." ;;
        esac
    done < "$marker_path"

    [[ "$pilot_schema_version" == "$INSTALLER_SCHEMA_VERSION" ]] ||
        fail "The pilot marker schema is unsupported."
    [[ "$pilot_bundle_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z]+(\.[0-9A-Za-z]+)*)?$ ]] ||
        fail "The pilot marker bundle version is invalid."
    [[ "$pilot_source_revision" =~ ^[0-9a-f]{40}$ ]] ||
        fail "The pilot marker source revision is invalid."
    validate_sha256 "pilot marker bundle_sha256" "$pilot_bundle_sha256"
    validate_sha256 "pilot marker manifest_sha256" "$pilot_manifest_sha256"
    validate_sha256 "pilot marker environment_file_sha256" "$pilot_environment_sha256"
    validate_sha256 "pilot marker service_unit_sha256" "$pilot_unit_sha256"
}

validate_foundation_directory() {
    local path="$1"
    local expected_owner="$2"
    local expected_group="$3"
    local expected_mode="$4"
    local actual

    [[ -d "$path" && ! -L "$path" ]] || fail "A required foundation directory is missing or unsafe."
    actual="$(stat -c '%U:%G:%a' "$path")"
    [[ "$actual" == "$expected_owner:$expected_group:$expected_mode" ]] ||
        fail "A foundation directory owner or mode has drifted."
}

validate_pristine_game_runtime() {
    local server_root="$service_home/server"
    [[ -f "$server_root/cstrike/liblist.gam" && ! -L "$server_root/cstrike/liblist.gam" ]] ||
        fail "The game runtime liblist is missing or unsafe."
    grep -Eq '^[[:space:]]*gamedll_linux[[:space:]]+"?dlls/cs\.so"?' \
        "$server_root/cstrike/liblist.gam" ||
        fail "The game runtime no longer loads ReGameDLL_CS directly."
    ! grep -Eiq 'metamod|amxmodx|reapi|yapb|reunion' "$server_root/cstrike/liblist.gam" ||
        fail "The game runtime already references a plugin loader."

    local excluded_path
    for excluded_path in \
        cstrike/addons/metamod \
        cstrike/addons/amxmodx \
        cstrike/addons/yapb \
        cstrike/addons/reapi \
        cstrike/addons/reunion; do
        [[ ! -e "$server_root/$excluded_path" ]] ||
            fail "The game runtime already contains a deferred plugin component."
    done
}

require_base_environment() {
    ((EUID == 0)) || fail "--apply must run as root."

    local command
    for command in \
        awk \
        cat \
        chmod \
        chown \
        cmp \
        cut \
        dirname \
        find \
        file \
        flock \
        getent \
        grep \
        id \
        install \
        jq \
        mkdir \
        mv \
        ps \
        realpath \
        rm \
        rmdir \
        sha256sum \
        sort \
        stat \
        systemctl \
        systemd-analyze \
        tr \
        uname \
        uniq \
        unzip \
        wc \
        zipinfo; do
        require_command "$command"
    done

    [[ -r /etc/os-release ]] || fail "The operating-system identity is unavailable."
    # shellcheck disable=SC1091
    source /etc/os-release
    [[ "${ID:-}" == "ubuntu" && "${VERSION_ID:-}" == "24.04" ]] ||
        fail "The pilot installer supports Ubuntu 24.04 only."
    [[ "$(uname -m)" == "x86_64" ]] || fail "The pilot installer requires x86-64."
    [[ "$(ps -p 1 -o comm= | tr -d '[:space:]')" == "systemd" ]] ||
        fail "The pilot installer requires systemd as PID 1."

    read_prepared_marker
    read_runtime_marker
    service_group="$(id -gn "$service_user")"
    [[ "$(getent passwd "$service_user" | cut -d: -f6)" == "$service_home" ]] ||
        fail "The service account home directory has drifted."
    [[ "$(getent passwd "$service_user" | cut -d: -f7)" == "/usr/sbin/nologin" ]] ||
        fail "The service account must remain non-interactive."
    [[ "${SUDO_USER:-}" == "$prepared_operator_user" ]] ||
        fail "Run --apply through sudo from the reviewed operator account."

    [[ "$(stat -c '%U:%G:%a' "$prepared_marker")" == "root:$service_group:640" ]] ||
        fail "The game-host readiness marker owner or mode has drifted."
    [[ "$(stat -c '%U:%G:%a' "$runtime_marker")" == "root:$service_group:640" ]] ||
        fail "The game-runtime marker owner or mode has drifted."
    [[ -f "$runtime_enabled_marker" && ! -L "$runtime_enabled_marker" ]] ||
        fail "The reviewed game runtime is not activated."
    validate_foundation_directory "$configuration_directory" root "$service_group" 750
    validate_foundation_directory "$configuration_directory/secrets" root "$service_group" 710
    validate_foundation_directory "$installation_directory" root "$service_group" 750
    validate_foundation_directory "$service_home" "$service_user" "$service_group" 750
    [[ "$(stat -c '%U:%G:%a' "$runtime_enabled_marker")" == "root:$service_group:640" ]] ||
        fail "The game-runtime activation marker owner or mode has drifted."
}

require_install_environment() {
    require_base_environment
    validate_pristine_game_runtime

    [[ -f "$bundle_path" && ! -L "$bundle_path" ]] || fail "The pilot bundle is missing or unsafe."
    [[ "$(realpath -e -- "$bundle_path")" == "$bundle_path" ]] ||
        fail "The pilot bundle path must be canonical and contain no symbolic-link component."
    [[ "$(stat -c '%U:%G:%a' "$bundle_path")" == "root:root:600" ]] ||
        fail "The pilot bundle must be owned by root:root with mode 0600."
    local bundle_parent bundle_parent_mode
    bundle_parent="$(dirname "$bundle_path")"
    [[ "$(stat -c '%U' "$bundle_parent")" == "root" ]] ||
        fail "The pilot bundle parent must be owned by root."
    bundle_parent_mode="$(stat -c '%a' "$bundle_parent")"
    (( (8#$bundle_parent_mode & 8#022) == 0 )) ||
        fail "The pilot bundle parent must not be writable by group or others."
    [[ ! -e "$pilot_marker" && ! -e "$pilot_enabled_marker" ]] ||
        fail "A pilot marker already exists."
    [[ ! -e "$environment_file" && ! -e "$client_secret_file" ]] ||
        fail "Pilot configuration or credentials already exist."
    [[ ! -e "$systemd_unit_file" ]] || fail "The pilot systemd unit already exists."
    [[ ! -e "$state_root" ]] || fail "The pilot state directory already exists."
    ! systemctl is-active --quiet "$SERVICE_NAME" || fail "The pilot service is already active."
    ! systemctl is-enabled --quiet "$SERVICE_NAME" || fail "The pilot service is already enabled."
}

verify_sha256() {
    local path="$1"
    local expected="$2"
    local actual
    validate_sha256 "expected SHA-256" "$expected"
    [[ -f "$path" && ! -L "$path" ]] || fail "A hashed file is missing or unsafe."
    actual="$(sha256sum "$path" | awk '{ print $1 }')"
    [[ "$actual" == "$expected" ]] || fail "SHA-256 verification failed."
}

reject_unsafe_archive_entry() {
    local entry="$1"
    [[ -n "$entry" && "$entry" != /* && "$entry" != *\\* ]] ||
        fail "The bundle contains an unsafe path."
    case "$entry" in
        ../*|*/../*|*/..)
            fail "The bundle contains an unsafe path."
            ;;
    esac
}

validate_manifest_contract() {
    local manifest="$1"
    local required_path

    [[ -f "$manifest" && ! -L "$manifest" ]] || fail "The pilot manifest is missing or unsafe."
    jq -e \
        --argjson schema "$BUNDLE_SCHEMA_VERSION" \
        --arg amxxVersion "$EXPECTED_AMXX_VERSION" \
        --arg amxxBaseSha "$EXPECTED_AMXX_BASE_SHA256" \
        --arg amxxCstrikeSha "$EXPECTED_AMXX_CSTRIKE_SHA256" \
        --arg metamodVersion "$EXPECTED_METAMOD_VERSION" \
        --arg metamodSha "$EXPECTED_METAMOD_SHA256" \
        --arg reapiVersion "$EXPECTED_REAPI_VERSION" \
        --arg reapiSha "$EXPECTED_REAPI_SHA256" '
        .schemaVersion == $schema and
        (.bundleVersion | type == "string" and test("^[0-9]+\\.[0-9]+\\.[0-9]+(-[0-9A-Za-z]+(\\.[0-9A-Za-z]+)*)?$")) and
        (.sourceRevision | type == "string" and test("^[0-9a-f]{40}$")) and
        .sourceDirty == false and
        .productionEligible == true and
        .targetRuntime == "linux-x64" and
        .activation == {
            changesGameServerRuntime: false,
            producerEnabled: false,
            spoolImportEnabled: false,
            deliveryEnabled: false
        } and
        (.components | type == "array" and length == 4) and
        ([.components[] | select(
            .name == "AMX Mod X base" and
            .version == $amxxVersion and
            .archiveSha256 == $amxxBaseSha)] | length) == 1 and
        ([.components[] | select(
            .name == "AMX Mod X Counter-Strike" and
            .version == $amxxVersion and
            .archiveSha256 == $amxxCstrikeSha)] | length) == 1 and
        ([.components[] | select(
            .name == "Metamod-R" and
            .version == $metamodVersion and
            .archiveSha256 == $metamodSha)] | length) == 1 and
        ([.components[] | select(
            .name == "ReAPI" and
            .version == $reapiVersion and
            .archiveSha256 == $reapiSha)] | length) == 1 and
        (.payload | type == "array" and length > 0) and
        all(.payload[];
            (.path | type == "string" and
                test("^[A-Za-z0-9][A-Za-z0-9._-]*(/[A-Za-z0-9][A-Za-z0-9._-]*)*$")) and
            (.length | type == "number" and . >= 0 and floor == .) and
            (.sha256 | type == "string" and test("^[0-9a-f]{64}$")) and
            (if .path == "agent/GoldSrcOps.GameEventAgent" or .path == "agent/run.sh"
                then .mode == "0750"
                else .mode == "0640"
             end)) and
        .producer.sourcePath == "samples/amxmodx-game-event-producer/goldsrcops_game_events.sma" and
        (.producer.sourceSha256 | type == "string" and test("^[0-9a-f]{64}$")) and
        (.producer.compiledSha256 | type == "string" and test("^[0-9a-f]{64}$")) and
        .producer.compiledSha256 ==
            ([.payload[] | select(.path == "gameserver/cstrike/addons/amxmodx/plugins/goldsrcops_game_events.amxx")][0].sha256)
        ' "$manifest" >/dev/null || fail "The pilot manifest contract is invalid."

    for required_path in \
        agent/GoldSrcOps.GameEventAgent \
        agent/appsettings.json \
        agent/run.sh \
        gameserver/cstrike/addons/metamod/metamod_i386.so \
        gameserver/cstrike/addons/metamod/plugins.ini \
        gameserver/cstrike/addons/amxmodx/dlls/amxmodx_mm_i386.so \
        gameserver/cstrike/addons/amxmodx/modules/reapi_amxx_i386.so \
        gameserver/cstrike/addons/amxmodx/plugins/goldsrcops_game_events.amxx \
        gameserver/cstrike/addons/amxmodx/configs/plugins.ini \
        gameserver/cstrike/addons/amxmodx/configs/modules.ini \
        gameserver/cstrike/addons/amxmodx/configs/amxx.cfg; do
        [[ "$(jq --arg path "$required_path" '[.payload[] | select(.path == $path)] | length' "$manifest")" == "1" ]] ||
            fail "The pilot manifest is missing a required payload entry."
    done
}

verify_manifest_payload() {
    local content_root="$1"
    local manifest="$content_root/manifest.json"
    local manifest_files actual_files duplicate_paths
    local payload_path payload_length payload_sha payload_mode actual_length

    manifest_files="$(jq -r '.payload[].path' "$manifest" | sort)"
    duplicate_paths="$(printf '%s\n' "$manifest_files" | uniq -d)"
    [[ -z "$duplicate_paths" ]] ||
        fail "The pilot manifest contains duplicate payload paths."
    actual_files="$({
        cd "$content_root"
        find . -type f -printf '%P\n' | grep -vFx 'manifest.json' | sort
    })"
    [[ "$manifest_files" == "$actual_files" ]] ||
        fail "The bundle files do not exactly match the pilot manifest."

    while IFS=$'\t' read -r payload_path payload_length payload_sha payload_mode; do
        local full_path="$content_root/$payload_path"
        [[ -f "$full_path" && ! -L "$full_path" ]] || fail "A manifest payload file is missing or unsafe."
        actual_length="$(stat -c '%s' "$full_path")"
        [[ "$actual_length" == "$payload_length" ]] || fail "A payload file length does not match the manifest."
        verify_sha256 "$full_path" "$payload_sha"
    done < <(jq -r '.payload[] | [.path, .length, .sha256, .mode] | @tsv' "$manifest")

}

validate_runtime_payload_contract() {
    local content_root="$1"
    local linux32_binary

    file -b "$content_root/agent/GoldSrcOps.GameEventAgent" | grep -Fq 'ELF 64-bit LSB' ||
        fail "The pilot agent is not a 64-bit Linux ELF executable."
    for linux32_binary in \
        gameserver/cstrike/addons/metamod/metamod_i386.so \
        gameserver/cstrike/addons/amxmodx/dlls/amxmodx_mm_i386.so \
        gameserver/cstrike/addons/amxmodx/modules/reapi_amxx_i386.so; do
        file -b "$content_root/$linux32_binary" | grep -Fq 'ELF 32-bit LSB' ||
            fail "A pilot game component is not a 32-bit Linux ELF binary."
    done
    [[ -z "$(find "$content_root/gameserver" -type f \( -iname '*.dll' -o -iname '*.exe' \) -print -quit)" ]] ||
        fail "The Linux pilot payload contains a Windows binary."
    [[ ! -e "$content_root/gameserver/cstrike/addons/amxmodx/scripting" ]] ||
        fail "The pilot payload unexpectedly contains the compiler source tree."
    [[ "$(find "$content_root/gameserver/cstrike/addons/amxmodx/plugins" -maxdepth 1 -type f -name '*.amxx' | wc -l)" == "1" ]] ||
        fail "The pilot payload must contain exactly one AMX Mod X plugin."
    [[ -f "$content_root/gameserver/cstrike/addons/amxmodx/plugins/goldsrcops_game_events.amxx" ]] ||
        fail "The pilot payload producer is missing."
    [[ "$(cat "$content_root/gameserver/cstrike/addons/metamod/plugins.ini")" == \
        "linux addons/amxmodx/dlls/amxmodx_mm_i386.so" ]] ||
        fail "The pilot Metamod loader configuration is not minimal."
    [[ "$(cat "$content_root/gameserver/cstrike/addons/amxmodx/configs/plugins.ini")" == \
        "goldsrcops_game_events.amxx" ]] ||
        fail "The pilot AMX Mod X plugin configuration is not minimal."
    [[ "$(cat "$content_root/gameserver/cstrike/addons/amxmodx/configs/modules.ini")" == "reapi" ]] ||
        fail "The pilot AMX Mod X module configuration is not minimal."
    grep -Fxq 'goldsrcops_events_enabled 0' \
        "$content_root/gameserver/cstrike/addons/amxmodx/configs/amxx.cfg" ||
        fail "The pilot producer is not disabled in its runtime configuration."
    ! grep -Eq '^[[:space:]]*goldsrcops_events_enabled[[:space:]]+1([[:space:]]|$)' \
        "$content_root/gameserver/cstrike/addons/amxmodx/configs/amxx.cfg" ||
        fail "The pilot producer is enabled unexpectedly."
    jq -e '
        .GameEventAgent.Spool.Enabled == false and
        .GameEventAgent.Delivery.Enabled == false
        ' "$content_root/agent/appsettings.json" >/dev/null ||
        fail "The pilot agent settings do not preserve both default-off gates."
}

normalize_payload_permissions() {
    local content_root="$1"
    local manifest="$content_root/manifest.json"
    local payload_path payload_mode

    while IFS=$'\t' read -r payload_path payload_mode; do
        chown root:"$service_group" "$content_root/$payload_path"
        chmod "$payload_mode" "$content_root/$payload_path"
    done < <(jq -r '.payload[] | [.path, .mode] | @tsv' "$manifest")

    find "$content_root" -type d -exec chown root:"$service_group" {} +
    find "$content_root" -type d -exec chmod 0750 {} +
    chown root:"$service_group" "$manifest"
    chmod 0640 "$manifest"
}

prepare_and_verify_bundle() {
    local archive_entries payload_path

    verify_sha256 "$bundle_path" "$bundle_sha256"
    if [[ -e "$pilot_root" ]]; then
        validate_foundation_directory "$pilot_root" root "$service_group" 750
    else
        install -d -m 0750 -o root -g "$service_group" "$pilot_root"
    fi
    if [[ -e "$pilot_releases_directory" ]]; then
        validate_foundation_directory "$pilot_releases_directory" root "$service_group" 750
    else
        install -d -m 0750 -o root -g "$service_group" "$pilot_releases_directory"
    fi

    verification_root="$pilot_releases_directory/.verify.$$"
    [[ ! -e "$verification_root" ]] || fail "A pilot verification directory already exists."
    install -d -m 0700 -o root -g root "$verification_root"
    archive_entries="$verification_root/archive-entries"

    unzip -Z1 "$bundle_path" > "$archive_entries" || fail "The pilot bundle cannot be listed."
    [[ -s "$archive_entries" ]] || fail "The pilot bundle is empty."
    while IFS= read -r payload_path; do
        reject_unsafe_archive_entry "$payload_path"
    done < "$archive_entries"
    [[ "$(sort "$archive_entries" | uniq -d | wc -l)" == "0" ]] ||
        fail "The pilot bundle contains duplicate paths."
    if zipinfo -l "$bundle_path" |
        awk '$1 ~ /^l/ && length($1) == 10 { found = 1 } END { exit(found ? 0 : 1) }'; then
        fail "The pilot bundle contains a symbolic-link entry."
    fi

    install -d -m 0700 -o root -g root "$verification_root/content"
    unzip -q "$bundle_path" -d "$verification_root/content" ||
        fail "The pilot bundle could not be extracted."
    [[ -z "$(find "$verification_root/content" -type l -print -quit)" ]] ||
        fail "The pilot bundle contains a symbolic link."
    [[ -z "$(find "$verification_root/content" ! -type d ! -type f -print -quit)" ]] ||
        fail "The pilot bundle contains an unsupported filesystem entry."

    local manifest="$verification_root/content/manifest.json"
    validate_manifest_contract "$manifest"
    verify_manifest_payload "$verification_root/content"
    validate_runtime_payload_contract "$verification_root/content"
    normalize_payload_permissions "$verification_root/content"

    pilot_bundle_version="$(jq -r '.bundleVersion' "$manifest")"
    pilot_source_revision="$(jq -r '.sourceRevision' "$manifest")"
    pilot_manifest_sha256="$(sha256sum "$manifest" | awk '{ print $1 }')"
    release_path="$pilot_releases_directory/$bundle_sha256"
    [[ ! -e "$release_path" ]] || fail "The content-addressed pilot release already exists."
}

render_environment_file() {
    local destination="$1"
    cat > "$destination" <<EOF
GameEventAgent__QueuePath=$state_root/queue/game-event-agent.db
GameEventAgent__Spool__Enabled=false
GameEventAgent__Spool__RootPath=$spool_root
GameEventAgent__Delivery__Enabled=false
EOF
    chmod 0640 "$destination"
}

render_service_unit() {
    local destination="$1"
    local selected_release="${2:-$release_path}"

    cat > "$destination" <<EOF
[Unit]
Description=GoldSrcOps game-event pilot agent
Documentation=https://github.com/tov-vl/gold-src-ops/blob/main/docs/v2.11-game-event-pilot.md
Wants=network-online.target
After=network-online.target
StartLimitIntervalSec=300
StartLimitBurst=5
ConditionPathExists=$pilot_enabled_marker
ConditionPathExists=$client_secret_file

[Service]
Type=simple
User=$service_user
Group=$service_group
WorkingDirectory=$state_root
EnvironmentFile=$environment_file
Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR=$state_root/dotnet-bundle
LoadCredential=oauth-client-secret:$client_secret_file
ExecStart=$selected_release/agent/run.sh
Restart=on-failure
RestartSec=5s
TimeoutStopSec=30s
KillSignal=SIGINT
UMask=0077
LimitNOFILE=4096
TasksMax=64
MemoryMax=256M
NoNewPrivileges=true
CapabilityBoundingSet=
AmbientCapabilities=
LockPersonality=true
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
ProcSubset=all
RemoveIPC=true
RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6
RestrictNamespaces=true
RestrictRealtime=true
RestrictSUIDSGID=true
ReadOnlyPaths=$configuration_directory
ReadWritePaths=$state_root -$spool_root
InaccessiblePaths=$installation_directory/artifacts /var/backups/goldsrcops/gameserver
StandardOutput=journal
StandardError=journal
SyslogIdentifier=goldsrcops-game-event-agent
LogRateLimitIntervalSec=30s
LogRateLimitBurst=200

[Install]
WantedBy=multi-user.target
EOF
    chmod 0644 "$destination"
}

prepare_rendered_files() {
    rendered_environment="$verification_root/game-event-agent.env"
    rendered_unit="$verification_root/$SERVICE_NAME"
    render_environment_file "$rendered_environment"
    render_service_unit "$rendered_unit" "$release_path"

    grep -Fxq 'GameEventAgent__Spool__Enabled=false' "$rendered_environment" ||
        fail "The rendered pilot environment does not keep spool import disabled."
    grep -Fxq 'GameEventAgent__Delivery__Enabled=false' "$rendered_environment" ||
        fail "The rendered pilot environment does not keep delivery disabled."
    grep -Fxq "ConditionPathExists=$pilot_enabled_marker" "$rendered_unit" ||
        fail "The rendered pilot unit is missing its activation gate."
    grep -Fxq "LoadCredential=oauth-client-secret:$client_secret_file" "$rendered_unit" ||
        fail "The rendered pilot unit is missing systemd credential transport."
    ! grep -Eiq 'Environment=.*(secret|password|token)=' "$rendered_unit" ||
        fail "The rendered pilot unit contains unsafe secret transport."
}

promote_release() {
    mv -- "$verification_root/content" "$release_path"
    created_release=true
}

install_state_directories() {
    install -d -m 0750 -o "$service_user" -g "$service_group" \
        "$state_root" \
        "$state_root/queue" \
        "$state_root/dotnet-bundle"
    created_state=true
}

install_configuration() {
    systemd-analyze verify "$rendered_unit" >/dev/null || fail "The pilot systemd unit is invalid."
    install -m 0640 -o root -g "$service_group" "$rendered_environment" "$environment_file"
    installed_environment=true
    pilot_environment_sha256="$(sha256sum "$environment_file" | awk '{ print $1 }')"

    install -m 0644 -o root -g root "$rendered_unit" "$systemd_unit_file"
    installed_unit=true
    pilot_unit_sha256="$(sha256sum "$systemd_unit_file" | awk '{ print $1 }')"
    systemctl daemon-reload
}

write_pilot_marker() {
    local temporary_marker="$configuration_directory/.game-event-pilot-installed.$$"
    cat > "$temporary_marker" <<EOF
schema_version=$INSTALLER_SCHEMA_VERSION
bundle_version=$pilot_bundle_version
source_revision=$pilot_source_revision
bundle_sha256=$bundle_sha256
manifest_sha256=$pilot_manifest_sha256
environment_file_sha256=$pilot_environment_sha256
service_unit_sha256=$pilot_unit_sha256
EOF
    chown root:"$service_group" "$temporary_marker"
    chmod 0640 "$temporary_marker"
    mv -- "$temporary_marker" "$pilot_marker"
}

verify_installed_state() {
    [[ "$(systemctl is-enabled "$SERVICE_NAME" 2>/dev/null || true)" == "disabled" ]] ||
        fail "The pilot service did not remain disabled."
    [[ "$(systemctl is-active "$SERVICE_NAME" 2>/dev/null || true)" == "inactive" ]] ||
        fail "The pilot service did not remain inactive."
    [[ ! -e "$pilot_enabled_marker" && ! -e "$client_secret_file" ]] ||
        fail "A pilot activation gate or credential appeared unexpectedly."
    validate_pristine_game_runtime
}

cleanup_install() {
    local exit_code=$?
    if ((exit_code != 0)); then
        rm -f -- "$pilot_marker"
        if [[ "$installed_unit" == "true" ]]; then
            rm -f -- "$systemd_unit_file"
            systemctl daemon-reload >/dev/null 2>&1 || true
        fi
        if [[ "$installed_environment" == "true" ]]; then
            rm -f -- "$environment_file"
        fi
        if [[ "$created_state" == "true" && -d "$state_root" &&
            -z "$(find "$state_root" -mindepth 1 ! -type d -print -quit)" ]]; then
            rm -rf -- "$state_root"
        fi
        if [[ "$created_release" == "true" && -n "$release_path" &&
            "$release_path" == "$pilot_releases_directory/"[0-9a-f]* ]]; then
            rm -rf -- "$release_path"
        fi
    fi

    if [[ -n "$verification_root" &&
        "$verification_root" == "$pilot_releases_directory/.verify."* ]]; then
        rm -rf -- "$verification_root"
    fi
    return "$exit_code"
}

run_install() {
    require_install_environment
    exec 9>"$configuration_directory/game-event-pilot-install.lock"
    flock --nonblock 9 || fail "Another pilot installation is already in progress."
    chown root:"$service_group" "$configuration_directory/game-event-pilot-install.lock"
    chmod 0640 "$configuration_directory/game-event-pilot-install.lock"

    trap cleanup_install EXIT
    prepare_and_verify_bundle
    prepare_rendered_files
    promote_release
    install_state_directories
    install_configuration
    write_pilot_marker
    verify_installed_state

    log "INSTALLED: game-event pilot bundle $pilot_bundle_version at verified source revision."
    log "SERVICE_STATE: disabled and inactive"
    log "GAME_RUNTIME_STATE: unchanged; no plugin loader or producer is active"
    log "NEXT_GATE: review identity, activation, one bounded event, and rollback separately"
}

require_rollback_environment() {
    require_base_environment
    read_pilot_marker
    release_path="$pilot_releases_directory/$pilot_bundle_sha256"

    [[ ! -e "$pilot_enabled_marker" ]] || fail "Pre-activation rollback is unavailable after activation."
    [[ ! -e "$client_secret_file" ]] || fail "Pre-activation rollback refuses to remove a credential."
    [[ "$(systemctl is-enabled "$SERVICE_NAME" 2>/dev/null || true)" == "disabled" ]] ||
        fail "Pre-activation rollback requires a disabled pilot service."
    [[ "$(systemctl is-active "$SERVICE_NAME" 2>/dev/null || true)" == "inactive" ]] ||
        fail "Pre-activation rollback requires an inactive pilot service."
    [[ -f "$systemd_unit_file" && ! -L "$systemd_unit_file" ]] ||
        fail "The installed pilot unit is missing or unsafe."
    [[ -f "$environment_file" && ! -L "$environment_file" ]] ||
        fail "The installed pilot environment is missing or unsafe."
    [[ -d "$release_path" && ! -L "$release_path" ]] ||
        fail "The content-addressed pilot release is missing or unsafe."
    [[ -d "$state_root" && ! -L "$state_root" ]] ||
        fail "The pilot state root is missing or unsafe."
    [[ -z "$(find "$state_root" -mindepth 1 ! -type d -print -quit)" ]] ||
        fail "Pre-activation rollback refuses to remove pilot state."

    verify_sha256 "$release_path/manifest.json" "$pilot_manifest_sha256"
    verify_sha256 "$environment_file" "$pilot_environment_sha256"
    verify_sha256 "$systemd_unit_file" "$pilot_unit_sha256"
    validate_manifest_contract "$release_path/manifest.json"
    verify_manifest_payload "$release_path"
    validate_runtime_payload_contract "$release_path"
    validate_pristine_game_runtime
}

run_rollback() {
    require_rollback_environment
    exec 9>"$configuration_directory/game-event-pilot-install.lock"
    flock --nonblock 9 || fail "Another pilot installation operation is in progress."

    rm -f -- "$systemd_unit_file"
    systemctl daemon-reload
    rm -f -- "$environment_file" "$pilot_marker"
    rm -rf -- "$release_path" "$state_root"
    rmdir --ignore-fail-on-non-empty "$pilot_releases_directory" 2>/dev/null || true
    rmdir --ignore-fail-on-non-empty "$(dirname "$pilot_releases_directory")" 2>/dev/null || true

    validate_pristine_game_runtime
    log "ROLLED_BACK: unactivated game-event pilot installation removed"
    log "GAME_RUNTIME_STATE: unchanged"
}

print_install_plan() {
    log "PLAN: require the reviewed active plugin-free game runtime and service account"
    log "PLAN: verify a root-owned 0600 bundle against its separately supplied SHA-256"
    log "PLAN: verify manifest schema $BUNDLE_SCHEMA_VERSION, linux-x64 target, source identity, pins, and every payload hash"
    log "PLAN: install one content-addressed pilot release and owner-only agent state"
    log "PLAN: install default-off environment and constrained $SERVICE_NAME"
    log "PLAN: leave liblist.gam, live plugin files, credentials, delivery, and both running services unchanged"
    log "PLAN_ONLY: no host changes were made; add --apply with reviewed bundle inputs to execute this plan."
}

print_rollback_plan() {
    log "PLAN: require an inactive and disabled pilot with no activation marker, credential, or state files"
    log "PLAN: verify installed manifest, environment, unit hashes, and the unchanged plugin-free game runtime"
    log "PLAN: remove only the unactivated pilot release, unit, environment, marker, and empty state directories"
    log "PLAN_ONLY: no host changes were made; add --apply to execute this pre-activation rollback."
}

main() {
    while (($# > 0)); do
        case "$1" in
            --bundle)
                (($# >= 2)) || fail "--bundle requires a value."
                bundle_path="$2"
                shift 2
                ;;
            --bundle-sha256)
                (($# >= 2)) || fail "--bundle-sha256 requires a value."
                bundle_sha256="$2"
                shift 2
                ;;
            --service-user)
                (($# >= 2)) || fail "--service-user requires a value."
                service_user="$2"
                shift 2
                ;;
            --apply)
                apply_changes=true
                shift
                ;;
            --rollback)
                rollback_changes=true
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
        if [[ "$rollback_changes" == "true" ]]; then
            run_rollback
        else
            run_install
        fi
    elif [[ "$rollback_changes" == "true" ]]; then
        print_rollback_plan
    else
        print_install_plan
    fi
}

if [[ "${BASH_SOURCE[0]:-$0}" == "$0" ]]; then
    main "$@"
fi
