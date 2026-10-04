#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'
umask 077

ws_directory="$(dirname "$(realpath -e -- "${BASH_SOURCE[0]}")")"
# shellcheck source=ops/gameserver/game-event-persistent.sh
source "$ws_directory/game-event-persistent.sh"

ws_operation=""
ws_apply=false
ws_bundle=""
ws_manifest_sha=""
ws_expected_manifest_sha=""
ws_map_menu=false
ws_kind=weapon-selection
ws_label=WEAPON_SELECTION
ws_plugin_name=goldsrcops_weapon_selection.amxx
ws_configuration_name=goldsrcops-weapon-selection.cfg
ws_loader_name=plugins-goldsrcops-weapons.ini
ws_configuration_command='goldsrcops_weapons_enabled "1"'
ws_purpose=game-host-addon
ws_state=""
ws_old_state=""
ws_mutating=false
ws_receipt_backup=""
ws_created=()
ws_upgrade_backup=""
ws_remove_paths=()

ws_write_receipt() {
    local temporary
    temporary="$(mktemp "$configuration_directory/.$ws_kind.XXXXXX")"
    cat > "$temporary" <<EOF
schema_version=1
state=$ws_state
manifest_sha256=$ws_manifest_sha
plugin_sha256=$(sha256_file "$ws_plugin")
config_sha256=$(sha256_file "$ws_config")
loader_sha256=$(sha256_file "$(ws_loader_path)")
EOF
    chown root:"$service_group" "$temporary"
    chmod 0640 "$temporary"
    mv -f -- "$temporary" "$ws_receipt"
}

ws_loader_path() {
    if [[ "$ws_state" == enabled ]]; then printf '%s\n' "$ws_loader";
    else printf '%s\n' "$ws_disabled_loader"; fi
}

ws_verify() {
    validate_file_metadata "$ws_receipt" root "$service_group" 640
    [[ "$(marker_value "$ws_receipt" schema_version)" == 1 ]] || fail "Unsupported addon receipt."
    ws_state="$(marker_value "$ws_receipt" state)"
    [[ "$ws_state" == enabled || "$ws_state" == disabled ]] || fail "Unknown addon state."
    ws_manifest_sha="$(marker_value "$ws_receipt" manifest_sha256)"
    validate_sha256 "$ws_manifest_sha"
    verify_sha256 "$ws_plugin" "$(marker_value "$ws_receipt" plugin_sha256)"
    verify_sha256 "$ws_config" "$(marker_value "$ws_receipt" config_sha256)"
    verify_sha256 "$(ws_loader_path)" "$(marker_value "$ws_receipt" loader_sha256)"
    validate_file_metadata "$ws_plugin" root "$service_group" 640
    validate_file_metadata "$ws_config" root "$service_group" 640
    validate_file_metadata "$(ws_loader_path)" root "$service_group" 640
    [[ "$(cat "$(ws_loader_path)")" == "$ws_plugin_name" ]] || fail "Unexpected addon loader."
    if [[ "$ws_state" == enabled ]]; then
        [[ ! -e "$ws_disabled_loader" && ! -L "$ws_disabled_loader" ]] || fail "Duplicate addon loader."
    else
        [[ ! -e "$ws_loader" && ! -L "$ws_loader" ]] || fail "Disabled addon still has a loader."
    fi
}

ws_rollback() {
    local file index
    if [[ "$ws_operation" == install ]]; then
        for file in "${ws_created[@]}"; do rm -f -- "$file" || return 1; done
    elif [[ "$ws_operation" == upgrade ]]; then
        cp -p -- "$ws_upgrade_backup/plugin" "$ws_plugin" || return 1
        cp -p -- "$ws_upgrade_backup/receipt" "$ws_receipt" || return 1
    elif [[ "$ws_operation" == remove ]]; then
        for index in "${!ws_remove_paths[@]}"; do
            cp -p -- "$ws_upgrade_backup/$index" "${ws_remove_paths[index]}" || return 1
        done
    elif [[ "$ws_old_state" == enabled && -f "$ws_disabled_loader" && ! -e "$ws_loader" ]]; then
        mv -- "$ws_disabled_loader" "$ws_loader" || return 1
    elif [[ "$ws_old_state" == disabled && -f "$ws_loader" && ! -e "$ws_disabled_loader" ]]; then
        mv -- "$ws_loader" "$ws_disabled_loader" || return 1
    fi
    if [[ -n "$ws_receipt_backup" ]]; then
        install -o root -g "$service_group" -m 0640 "$ws_receipt_backup" "$ws_receipt" || return 1
    fi
    log "${ws_label}_ROLLBACK=restored; game remains stopped"
}

ws_on_error() {
    local status="$?"
    trap - ERR
    if [[ "$ws_mutating" == true ]]; then
        ws_rollback || log "${ws_label}_ROLLBACK=failed; inspect before any restart"
    fi
    [[ -z "$ws_receipt_backup" ]] || rm -f -- "$ws_receipt_backup"
    exit "$status"
}

ws_validate_bundle() {
    local manifest="$ws_bundle/manifest.json" relative
    local -a relatives=("$ws_plugin_relative" "$ws_config_relative" "$ws_loader_relative")
    verify_sha256 "$manifest" "$ws_manifest_sha"
    jq -e --arg plugin "${relatives[0]}" --arg config "${relatives[1]}" --arg loader "${relatives[2]}" --arg purpose "$ws_purpose" '
        .schemaVersion == 1 and .purpose == $purpose and
        .productionInstallSupported == true and .enabledByDefault == true and
        .amxxVersion == "1.10.0.5481" and .reApiVersion == "5.24.0.300" and
        (.sourceSha256 | test("^[0-9a-f]{64}$")) and
        (.payload | length) == 3 and
        ([.payload[].path] | sort) == ([$plugin, $config, $loader] | sort) and
        all(.payload[]; .sha256 | test("^[0-9a-f]{64}$"))' "$manifest" >/dev/null || fail "Unsupported addon package."
    for relative in "${relatives[@]}"; do
        verify_sha256 "$ws_bundle/$relative" "$(jq -er --arg path "$relative" '.payload[] | select(.path == $path) | .sha256' "$manifest")"
    done
    [[ "$(cat "$ws_bundle/${relatives[1]}")" == "$ws_configuration_command" ]] || fail "Unexpected addon configuration."
    [[ "$(cat "$ws_bundle/${relatives[2]}")" == "$ws_plugin_name" ]] || fail "Unexpected addon loader."
}

ws_install() {
    local destination index
    local -a destinations=("$ws_plugin" "$ws_config" "$ws_loader")
    local -a relatives=("$ws_plugin_relative" "$ws_config_relative" "$ws_loader_relative")
    ws_validate_bundle
    for destination in "${destinations[@]}" "$ws_disabled_loader" "$ws_receipt"; do
        [[ ! -e "$destination" && ! -L "$destination" ]] || fail "Addon path already exists; refusing overwrite."
    done
    ws_mutating=true
    # Install the supplemental loader last without changing telemetry files.
    for index in 0 1 2; do
        destination="${destinations[index]}"
        ws_created+=("$destination")
        install -o root -g "$service_group" -m 0640 "$ws_bundle/${relatives[index]}" "$destination"
    done
    ws_state=enabled
    ws_created+=("$ws_receipt")
    ws_write_receipt
}

ws_upgrade() {
    local next_manifest_sha="$ws_manifest_sha" next_plugin_sha
    ws_verify
    [[ "$ws_manifest_sha" == "$ws_expected_manifest_sha" ]] || fail "Installed addon does not match the reviewed predecessor."
    ws_manifest_sha="$next_manifest_sha"
    ws_validate_bundle
    # Upgrade only the binary and receipt; retain configuration and loader state.
    verify_sha256 "$ws_bundle/$ws_config_relative" "$(sha256_file "$ws_config")"
    verify_sha256 "$ws_bundle/$ws_loader_relative" "$(sha256_file "$(ws_loader_path)")"
    next_plugin_sha="$(jq -er --arg path "$ws_plugin_relative" '.payload[] | select(.path == $path) | .sha256' "$ws_bundle/manifest.json")"
    [[ "$next_plugin_sha" != "$(sha256_file "$ws_plugin")" ]] || fail "Upgrade must contain a changed plugin."
    ws_upgrade_backup="$(mktemp -d "$configuration_directory/$ws_kind-backup.XXXXXX")"
    cp -p -- "$ws_plugin" "$ws_upgrade_backup/plugin"
    cp -p -- "$ws_receipt" "$ws_upgrade_backup/receipt"
    verify_sha256 "$ws_upgrade_backup/plugin" "$(sha256_file "$ws_plugin")"
    verify_sha256 "$ws_upgrade_backup/receipt" "$(sha256_file "$ws_receipt")"
    ws_mutating=true
    install -o root -g "$service_group" -m 0640 "$ws_bundle/$ws_plugin_relative" "$ws_plugin"
    ws_write_receipt
}

ws_remove() {
    local index
    ws_verify
    [[ "$ws_manifest_sha" == "$ws_expected_manifest_sha" ]] || fail "Installed addon does not match the reviewed removal."
    ws_remove_paths=("$ws_plugin" "$ws_config" "$(ws_loader_path)" "$ws_receipt")
    ws_upgrade_backup="$(mktemp -d "$configuration_directory/$ws_kind-removal.XXXXXX")"
    for index in "${!ws_remove_paths[@]}"; do
        cp -p -- "${ws_remove_paths[index]}" "$ws_upgrade_backup/$index"
        verify_sha256 "$ws_upgrade_backup/$index" "$(sha256_file "${ws_remove_paths[index]}")"
    done
    ws_mutating=true
    for index in "${!ws_remove_paths[@]}"; do rm -- "${ws_remove_paths[index]}"; done
    verify_persistent_files
    ws_mutating=false
    log "${ws_label}_ADDON=removed; exact predecessor retained root-only; game remains stopped"
}

while (($# > 0)); do
    case "$1" in
        --install|--upgrade|--enable|--disable|--status|--remove)
            [[ -z "$ws_operation" ]] || fail "Select exactly one addon operation."
            ws_operation="${1#--}"; shift ;;
        --bundle) (($# >= 2)) || fail "Missing bundle path."; ws_bundle="$2"; shift 2 ;;
        --manifest-sha256) (($# >= 2)) || fail "Missing manifest digest."; ws_manifest_sha="$2"; shift 2 ;;
        --expected-manifest-sha256) (($# >= 2)) || fail "Missing predecessor digest."; ws_expected_manifest_sha="$2"; shift 2 ;;
        --apply) ws_apply=true; shift ;;
        --map-menu) ws_map_menu=true; shift ;;
        *) fail "Unknown addon argument." ;;
    esac
done
if [[ "$ws_map_menu" == true ]]; then
    ws_kind=map-menu
    ws_label=MAP_MENU
    ws_plugin_name=goldsrcops_map_menu.amxx
    ws_configuration_name=goldsrcops-map-menu.cfg
    ws_loader_name=plugins-goldsrcops-maps.ini
    ws_configuration_command='goldsrcops_maps_enabled "1"'
    ws_purpose=map-menu-addon
fi
ws_receipt="$configuration_directory/$ws_kind-installed"
ws_plugin="$live_amxx_root/plugins/$ws_plugin_name"
ws_config="$live_amxx_root/configs/plugins/$ws_configuration_name"
ws_loader="$live_amxx_root/configs/$ws_loader_name"
ws_disabled_loader="$configuration_directory/$ws_kind-disabled-loader"
ws_plugin_relative="cstrike/addons/amxmodx/plugins/$ws_plugin_name"
ws_config_relative="cstrike/addons/amxmodx/configs/plugins/$ws_configuration_name"
ws_loader_relative="cstrike/addons/amxmodx/configs/$ws_loader_name"
[[ -n "$ws_operation" ]] || fail "Select one addon operation."
[[ "$ws_operation" != remove || "$ws_map_menu" == true ]] || fail "Remove is supported only for the map-menu addon."
if [[ "$ws_operation" == install || "$ws_operation" == upgrade ]]; then
    [[ "$ws_bundle" == /* ]] || fail "Install/upgrade requires an absolute bundle directory."
    validate_sha256 "$ws_manifest_sha"
else
    [[ -z "$ws_bundle" && -z "$ws_manifest_sha" ]] || fail "Bundle inputs are install/upgrade-only."
fi
if [[ "$ws_operation" == upgrade || "$ws_operation" == remove ]]; then
    validate_sha256 "$ws_expected_manifest_sha"
else
    [[ -z "$ws_expected_manifest_sha" ]] || fail "Predecessor digest is upgrade/remove-only."
fi
if [[ "$ws_operation" != status && "$ws_apply" == false ]]; then
    log "PLAN: $ws_operation only the $ws_kind addon; game must already be stopped"
    log "PLAN: preserve telemetry, guards, identity, queue, spool, and boot policy; do not restart services"
    log "PLAN_ONLY: no host files, services, or endpoints inspected; add --apply to execute."
    exit 0
fi
[[ "$ws_operation" != status || "$ws_apply" == false ]] || fail "Status does not accept --apply."
[[ "$EUID" == 0 ]] || fail "Addon operation requires root."
read_prepared_marker
if [[ "$ws_operation" == status ]]; then ws_verify; log "${ws_label}_ADDON=$ws_state"; exit 0; fi
[[ "${SUDO_USER:-}" == "$prepared_operator_user" && -n "${SSH_CONNECTION:-}" ]] || fail "Apply requires the reviewed SSH operator."
acquire_lock
exec 7>"$configuration_directory/managed-profile.lock"
flock --nonblock 7 || fail "A managed-profile transition is in progress."
verify_persistent_files
[[ "$marker_schema_version" == 2 && "$marker_profile_sha256" == "$FAST_LOADOUT_PROFILE_SHA256" ]] || fail "Addon requires the accepted MP5 loadout."
[[ "$(systemctl show --property=ActiveState --value "$GAME_SERVICE_NAME")" == inactive ]] || fail "Stop the game before modifying the addon."
for directory in "$live_amxx_root/plugins" "$live_amxx_root/configs"; do
    validate_directory_metadata "$directory" root "$service_group" 750
done
if [[ ! -d "$live_amxx_root/configs/plugins" || -L "$live_amxx_root/configs/plugins" ]]; then
    fail "Prepare the root-owned configs/plugins directory before apply."
fi
validate_directory_metadata "$live_amxx_root/configs/plugins" root "$service_group" 750
trap ws_on_error ERR
if [[ "$ws_operation" == install ]]; then
    ws_install
elif [[ "$ws_operation" == upgrade ]]; then
    ws_upgrade
elif [[ "$ws_operation" == remove ]]; then
    ws_remove
    trap - ERR
    exit 0
else
    ws_verify
    ws_old_state="$ws_state"
    if [[ "$ws_operation" == enable ]]; then ws_state=enabled; else ws_state=disabled; fi
    if [[ "$ws_state" == "$ws_old_state" ]]; then log "${ws_label}_ADDON=$ws_state (unchanged)"; exit 0; fi
    ws_receipt_backup="$(mktemp "$configuration_directory/.$ws_kind-backup.XXXXXX")"
    cp -- "$ws_receipt" "$ws_receipt_backup"
    ws_mutating=true
    if [[ "$ws_state" == enabled ]]; then mv -- "$ws_disabled_loader" "$ws_loader";
    else mv -- "$ws_loader" "$ws_disabled_loader"; fi
    ws_write_receipt
fi
ws_verify
verify_persistent_files
ws_mutating=false
trap - ERR
[[ -z "$ws_receipt_backup" ]] || rm -f -- "$ws_receipt_backup"
log "${ws_label}_ADDON=$ws_state; game remains stopped"
if [[ "$ws_operation" == upgrade ]]; then
    log "${ws_label}_UPGRADE=completed; predecessor binary and receipt retained root-only"
fi
