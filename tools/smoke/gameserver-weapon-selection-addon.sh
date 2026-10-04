#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'
kind=weapon-selection
label=WEAPON_SELECTION
plugin_name=goldsrcops_weapon_selection.amxx
config_name=goldsrcops-weapon-selection.cfg
loader_name=plugins-goldsrcops-weapons.ini
config_command='goldsrcops_weapons_enabled "1"'
purpose=game-host-addon
dictionary_name=goldsrcops-player-menu.txt
addon_arguments=()
if [[ "${1:-}" == --map-menu && "$#" == 1 ]]; then
    kind=map-menu
    label=MAP_MENU
    plugin_name=goldsrcops_map_menu.amxx
    config_name=goldsrcops-map-menu.cfg
    loader_name=plugins-goldsrcops-maps.ini
    config_command='goldsrcops_maps_enabled "1"'
    purpose=map-menu-addon
    dictionary_name=goldsrcops-map-menu.txt
    addon_arguments=(--map-menu)
elif (($# > 0)); then
    exit 2
fi
export TARGET_PLUGIN="$plugin_name" TARGET_CONFIG="$config_name" TARGET_RECEIPT="$kind-installed" TARGET_DICTIONARY="$dictionary_name"
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
fixture="$(mktemp -d)"
trap 'rm -rf -- "$fixture"' EXIT
[[ "$EUID" == 0 ]] || { echo 'Run the addon fixture as root.' >&2; exit 1; }
mkdir -p "$fixture/scripts" "$fixture/bin" "$fixture/config" "$fixture/service/server/cstrike/addons/amxmodx/configs/plugins" \
    "$fixture/service/server/cstrike/addons/amxmodx/plugins" "$fixture/package/cstrike/addons/amxmodx/configs/plugins" \
    "$fixture/package/cstrike/addons/amxmodx/plugins"
export FIXTURE_ROOT="$fixture" REPO_ROOT="$repo" SUDO_USER=operator SSH_CONNECTION=fixture
export GOLDSRCOPS_CONFIGURATION_DIRECTORY="$fixture/config" GOLDSRCOPS_SERVICE_HOME="$fixture/service"
amxx="$fixture/service/server/cstrike/addons/amxmodx"
mkdir -p "$amxx/data/lang" "$amxx/data/vault"
chmod 0750 "$amxx/data" "$amxx/data/lang"
printf 'saved player stats\n' > "$amxx/data/vault/fixture.vault"
chmod 0750 "$amxx/configs" "$amxx/configs/plugins" "$amxx/plugins"
printf 'goldsrcops_game_events.amxx\n' > "$amxx/configs/plugins.ini"
printf 'telemetry configuration\n' > "$amxx/configs/amxx.cfg"
printf 'telemetry queue\n' > "$fixture/queue"
printf 'telemetry spool\n' > "$fixture/spool"
sentinels=("$amxx/configs/plugins.ini" "$amxx/configs/amxx.cfg" "$fixture/queue" "$fixture/spool" "$amxx/data/vault/fixture.vault")
if [[ "$kind" == map-menu ]]; then
    for file in "$amxx/plugins/goldsrcops_weapon_selection.amxx" "$amxx/configs/plugins/goldsrcops-weapon-selection.cfg" \
        "$amxx/configs/plugins-goldsrcops-weapons.ini" "$fixture/config/weapon-selection-installed"; do
        printf 'accepted weapon addon sentinel\n' > "$file"
        sentinels+=("$file")
    done
    printf 'accepted player dictionary sentinel\n' > "$amxx/data/lang/goldsrcops-player-menu.txt"
    sentinels+=("$amxx/data/lang/goldsrcops-player-menu.txt")
fi
before="$(sha256sum "${sentinels[@]}")"
cp "$repo/ops/gameserver/weapon-selection-addon.sh" "$fixture/scripts/"
# Mock host readiness only; file checks and addon changes use the real workflow.
cat > "$fixture/scripts/game-event-persistent.sh" <<'SH'
source "$REPO_ROOT/ops/gameserver/game-event-persistent.sh"
read_prepared_marker() { prepared_operator_user=operator; service_group=root; }
verify_persistent_files() {
    [[ ! -e "$FIXTURE_ROOT/guard-fail" ]] || return 1
    marker_schema_version=2
    marker_profile_sha256="$FAST_LOADOUT_PROFILE_SHA256"
    if [[ -e "$FIXTURE_ROOT/fail-final-guard" ]]; then
        rm "$FIXTURE_ROOT/fail-final-guard"
        touch "$FIXTURE_ROOT/guard-fail"
    fi
}
acquire_lock() { exec 6>"$configuration_directory/game-event-pilot-install.lock"; flock --nonblock 6; }
SH
cat > "$fixture/bin/systemctl" <<'SH'
#!/usr/bin/env bash
[[ "$*" == 'show --property=ActiveState --value goldsrcops-gameserver.service' ]] || exit 1
if [[ -e "$FIXTURE_ROOT/game-active" ]]; then echo active; else echo inactive; fi
SH
cat > "$fixture/bin/install" <<'SH'
#!/usr/bin/env bash
if [[ "${!#}" == *"$TARGET_DICTIONARY" && -e "$FIXTURE_ROOT/dictionary-fail" ]]; then
    printf 'partial dictionary\n' > "${!#}"
    exit 1
fi
if [[ "${!#}" == *"$TARGET_CONFIG" && -e "$FIXTURE_ROOT/install-fail" ]]; then exit 1; fi
if [[ "${!#}" == *"$TARGET_PLUGIN" && -e "$FIXTURE_ROOT/upgrade-fail" ]]; then
    printf 'partial binary\n' > "${!#}"
    exit 1
fi
exec /usr/bin/install "$@"
SH
cat > "$fixture/bin/rm" <<'SH'
#!/usr/bin/env bash
if [[ "${!#}" == *"$TARGET_RECEIPT" && -e "$FIXTURE_ROOT/remove-fail" ]]; then exit 1; fi
exec /usr/bin/rm "$@"
SH
chmod +x "$fixture/bin/systemctl" "$fixture/bin/install" "$fixture/bin/rm"
export PATH="$fixture/bin:$PATH"
plugin="cstrike/addons/amxmodx/plugins/$plugin_name"
config="cstrike/addons/amxmodx/configs/plugins/$config_name"
loader="cstrike/addons/amxmodx/configs/$loader_name"
printf 'fixture plugin\n' > "$fixture/package/$plugin"
printf '%s\n' "$config_command" > "$fixture/package/$config"
printf '%s\n' "$plugin_name" > "$fixture/package/$loader"
jq -n --arg plugin "$plugin" --arg config "$config" --arg loader "$loader" --arg purpose "$purpose" \
    --arg p "$(sha256sum "$fixture/package/$plugin" | cut -d' ' -f1)" \
    --arg c "$(sha256sum "$fixture/package/$config" | cut -d' ' -f1)" \
    --arg l "$(sha256sum "$fixture/package/$loader" | cut -d' ' -f1)" \
    '{schemaVersion:1,purpose:$purpose,productionInstallSupported:true,enabledByDefault:true,
      amxxVersion:"1.10.0.5481",reApiVersion:"5.24.0.300",sourceSha256:("a"*64),
      payload:[{path:$plugin,sha256:$p},{path:$config,sha256:$c},{path:$loader,sha256:$l}]}' > "$fixture/package/manifest.json"
digest="$(sha256sum "$fixture/package/manifest.json" | cut -d' ' -f1)"
run() { bash "$fixture/scripts/weapon-selection-addon.sh" "${addon_arguments[@]}" "$@"; }
refuse() { if run "$@" > "$fixture/refusal.log" 2>&1; then echo 'Expected refusal.' >&2; exit 1; fi; }

run --install --bundle /nonexistent --manifest-sha256 "$digest" | grep -q PLAN_ONLY
[[ ! -e "$fixture/config/$kind-installed" ]]
touch "$fixture/game-active"
refuse --install --bundle "$fixture/package" --manifest-sha256 "$digest" --apply
rm "$fixture/game-active"
refuse --install --bundle "$fixture/package" --manifest-sha256 "$(printf 'b%.0s' {1..64})" --apply
touch "$fixture/install-fail"
refuse --install --bundle "$fixture/package" --manifest-sha256 "$digest" --apply
[[ ! -e "$amxx/plugins/$plugin_name" && ! -e "$fixture/config/$kind-installed" ]]
rm "$fixture/install-fail"
touch "$fixture/fail-final-guard"
refuse --install --bundle "$fixture/package" --manifest-sha256 "$digest" --apply
[[ ! -e "$amxx/plugins/$plugin_name" && ! -e "$fixture/config/$kind-installed" ]]
rm "$fixture/guard-fail"
run --install --bundle "$fixture/package" --manifest-sha256 "$digest" --apply
run --status | grep -q "${label}_ADDON=enabled"
refuse --install --bundle "$fixture/package" --manifest-sha256 "$digest" --apply
receipt_before="$(sha256sum "$fixture/config/$kind-installed")"
touch "$fixture/fail-final-guard"
refuse --disable --apply
[[ -f "$amxx/configs/$loader_name" && ! -e "$fixture/config/$kind-disabled-loader" ]]
[[ "$receipt_before" == "$(sha256sum "$fixture/config/$kind-installed")" ]]
rm "$fixture/guard-fail"
run --disable --apply
[[ ! -e "$amxx/configs/$loader_name" ]]
[[ -f "$fixture/config/$kind-disabled-loader" ]]
run --status | grep -q "${label}_ADDON=disabled"
run --disable --apply | grep -q unchanged
run --enable --apply
run --status | grep -q "${label}_ADDON=enabled"

# Upgrade retains the loader/config, rejects a stale predecessor and restores
# the exact binary/receipt on partial-copy or final-guard failure.
cp -a "$fixture/package" "$fixture/next-package"
printf 'next fixture plugin\n' > "$fixture/next-package/$plugin"
jq --arg hash "$(sha256sum "$fixture/next-package/$plugin" | cut -d' ' -f1)" \
    '(.payload[] | select(.path | endswith(".amxx")) | .sha256) = $hash' \
    "$fixture/package/manifest.json" > "$fixture/next-package/manifest.json"
next_digest="$(sha256sum "$fixture/next-package/manifest.json" | cut -d' ' -f1)"
upgrade() { run --upgrade --bundle "$fixture/next-package" --manifest-sha256 "$next_digest" --expected-manifest-sha256 "$digest" "$@"; }
upgrade | grep -q PLAN_ONLY
refuse --upgrade --bundle "$fixture/next-package" --manifest-sha256 "$next_digest" --apply
refuse --upgrade --bundle "$fixture/next-package" --manifest-sha256 "$next_digest" --expected-manifest-sha256 "$(printf 'b%.0s' {1..64})" --apply
original_binary="$(sha256sum "$amxx/plugins/$plugin_name")"
original_receipt="$(sha256sum "$fixture/config/$kind-installed")"
touch "$fixture/upgrade-fail"
if upgrade --apply > "$fixture/refusal.log" 2>&1; then exit 1; fi
[[ "$original_binary" == "$(sha256sum "$amxx/plugins/$plugin_name")" ]]
[[ "$original_receipt" == "$(sha256sum "$fixture/config/$kind-installed")" ]]
rm "$fixture/upgrade-fail"
touch "$fixture/fail-final-guard"
if upgrade --apply > "$fixture/refusal.log" 2>&1; then exit 1; fi
[[ "$original_binary" == "$(sha256sum "$amxx/plugins/$plugin_name")" ]]
[[ "$original_receipt" == "$(sha256sum "$fixture/config/$kind-installed")" ]]
rm "$fixture/guard-fail"
upgrade --apply
run --status | grep -q "${label}_ADDON=enabled"
[[ "$(sed -n 's/^manifest_sha256=//p' "$fixture/config/$kind-installed")" == "$next_digest" ]]
[[ "$(sha256sum "$amxx/plugins/$plugin_name" | cut -d' ' -f1)" == "$(sha256sum "$fixture/next-package/$plugin" | cut -d' ' -f1)" ]]
refuse --upgrade --bundle "$fixture/next-package" --manifest-sha256 "$next_digest" --expected-manifest-sha256 "$digest" --apply
run --disable --apply
# The previous trusted package is also the explicit reverse-upgrade input.
run --upgrade --bundle "$fixture/package" --manifest-sha256 "$digest" --expected-manifest-sha256 "$next_digest" --apply
run --status | grep -q "${label}_ADDON=disabled"
[[ ! -e "$amxx/configs/$loader_name" ]]
[[ "$original_binary" == "$(sha256sum "$amxx/plugins/$plugin_name")" ]]
run --enable --apply
if [[ "$kind" == map-menu ]]; then
    removal_before="$(sha256sum "$amxx/plugins/$plugin_name" "$amxx/configs/plugins/$config_name" \
        "$amxx/configs/$loader_name" "$fixture/config/$kind-installed")"
    refuse --remove --expected-manifest-sha256 "$(printf 'b%.0s' {1..64})" --apply
    touch "$fixture/remove-fail"
    refuse --remove --expected-manifest-sha256 "$digest" --apply
    rm "$fixture/remove-fail"
    [[ "$removal_before" == "$(sha256sum "$amxx/plugins/$plugin_name" "$amxx/configs/plugins/$config_name" \
        "$amxx/configs/$loader_name" "$fixture/config/$kind-installed")" ]]
    touch "$fixture/fail-final-guard"
    refuse --remove --expected-manifest-sha256 "$digest" --apply
    rm "$fixture/guard-fail"
    [[ "$removal_before" == "$(sha256sum "$amxx/plugins/$plugin_name" "$amxx/configs/plugins/$config_name" \
        "$amxx/configs/$loader_name" "$fixture/config/$kind-installed")" ]]
    run --remove --expected-manifest-sha256 "$digest" --apply
    [[ ! -e "$amxx/plugins/$plugin_name" && ! -e "$amxx/configs/plugins/$config_name" \
        && ! -e "$amxx/configs/$loader_name" && ! -e "$fixture/config/$kind-installed" ]]
    run --install --bundle "$fixture/package" --manifest-sha256 "$digest" --apply
    run --disable --apply
    run --remove --expected-manifest-sha256 "$digest" --apply
    [[ ! -e "$fixture/config/$kind-disabled-loader" && ! -e "$fixture/config/$kind-installed" ]]
    run --install --bundle "$fixture/package" --manifest-sha256 "$digest" --apply
fi
if [[ "$kind" == weapon-selection || "$kind" == map-menu ]]; then
    dictionary="cstrike/addons/amxmodx/data/lang/$dictionary_name"
    live_dictionary="$amxx/data/lang/$dictionary_name"
    cp -a "$fixture/next-package" "$fixture/localized-package"
    mkdir -p "$fixture/localized-package/$(dirname "$dictionary")"
    printf '[en]\nGS_MENU = Fixture\n[ru]\nGS_MENU = Fixture\n' > "$fixture/localized-package/$dictionary"
    jq --arg path "$dictionary" --arg hash "$(sha256sum "$fixture/localized-package/$dictionary" | cut -d' ' -f1)" \
        '.schemaVersion = 2 | .payload += [{path:$path,sha256:$hash}]' \
        "$fixture/next-package/manifest.json" > "$fixture/localized-package/manifest.json"
    localized_digest="$(sha256sum "$fixture/localized-package/manifest.json" | cut -d' ' -f1)"
    localized() { run --upgrade --bundle "$fixture/localized-package" --manifest-sha256 "$localized_digest" --expected-manifest-sha256 "$digest" --apply; }
    original_receipt="$(sha256sum "$fixture/config/$kind-installed")"
    ln -s "$fixture/queue" "$live_dictionary"
    if localized > "$fixture/refusal.log" 2>&1; then exit 1; fi
    [[ -L "$live_dictionary" ]]
    rm "$live_dictionary"
    for failure in dictionary-fail upgrade-fail fail-final-guard; do
        touch "$fixture/$failure"
        if localized > "$fixture/refusal.log" 2>&1; then exit 1; fi
        [[ ! -e "$live_dictionary" && ! -L "$live_dictionary" ]]
        [[ "$original_binary" == "$(sha256sum "$amxx/plugins/$plugin_name")" ]]
        [[ "$original_receipt" == "$(sha256sum "$fixture/config/$kind-installed")" ]]
        rm -f "$fixture/$failure" "$fixture/guard-fail"
    done
    localized
    run --status | grep -q "${label}_ADDON=enabled"
    grep -q '^schema_version=2$' "$fixture/config/$kind-installed"
    dictionary_before="$(sha256sum "$live_dictionary")"
    localized_receipt="$(sha256sum "$fixture/config/$kind-installed")"
    cp -a "$fixture/localized-package" "$fixture/translated-package"
    printf 'updated localized binary\n' > "$fixture/translated-package/$plugin"
    printf '[en]\nGS_MENU = Updated\n[ru]\nGS_MENU = Updated\n' > "$fixture/translated-package/$dictionary"
    jq --arg plugin "$plugin" --arg dictionary "$dictionary" \
        --arg p "$(sha256sum "$fixture/translated-package/$plugin" | cut -d' ' -f1)" \
        --arg d "$(sha256sum "$fixture/translated-package/$dictionary" | cut -d' ' -f1)" \
        '(.payload[] | select(.path == $plugin) | .sha256) = $p |
         (.payload[] | select(.path == $dictionary) | .sha256) = $d' \
        "$fixture/localized-package/manifest.json" > "$fixture/translated-package/manifest.json"
    translated_digest="$(sha256sum "$fixture/translated-package/manifest.json" | cut -d' ' -f1)"
    localized_binary="$(sha256sum "$amxx/plugins/$plugin_name")"
    for failure in dictionary-fail upgrade-fail fail-final-guard; do
        touch "$fixture/$failure"
        refuse --upgrade --bundle "$fixture/translated-package" --manifest-sha256 "$translated_digest" --expected-manifest-sha256 "$localized_digest" --apply
        [[ "$dictionary_before" == "$(sha256sum "$live_dictionary")" ]]
        [[ "$localized_binary" == "$(sha256sum "$amxx/plugins/$plugin_name")" ]]
        [[ "$localized_receipt" == "$(sha256sum "$fixture/config/$kind-installed")" ]]
        rm -f "$fixture/$failure" "$fixture/guard-fail"
    done
    run --upgrade --bundle "$fixture/translated-package" --manifest-sha256 "$translated_digest" --expected-manifest-sha256 "$localized_digest" --apply
    localized_digest="$translated_digest"
    dictionary_before="$(sha256sum "$live_dictionary")"
    localized_receipt="$(sha256sum "$fixture/config/$kind-installed")"
    # Reverse upgrade restores a removed dictionary if the final guard fails.
    touch "$fixture/fail-final-guard"
    refuse --upgrade --bundle "$fixture/package" --manifest-sha256 "$digest" --expected-manifest-sha256 "$localized_digest" --apply
    [[ "$dictionary_before" == "$(sha256sum "$live_dictionary")" ]]
    [[ "$localized_receipt" == "$(sha256sum "$fixture/config/$kind-installed")" ]]
    rm "$fixture/guard-fail"
    cp "$live_dictionary" "$fixture/dictionary-original"
    printf 'drift\n' >> "$live_dictionary"
    refuse --status
    cp "$fixture/dictionary-original" "$live_dictionary"
    run --disable --apply
    run --upgrade --bundle "$fixture/package" --manifest-sha256 "$digest" --expected-manifest-sha256 "$localized_digest" --apply
    [[ ! -e "$live_dictionary" && ! -L "$live_dictionary" ]]
    run --status | grep -q "${label}_ADDON=disabled"
    run --enable --apply
    if [[ "$kind" == map-menu ]]; then
        localized_digest="$(sha256sum "$fixture/localized-package/manifest.json" | cut -d' ' -f1)"
        localized
        removal_before="$(sha256sum "$live_dictionary")"
        touch "$fixture/fail-final-guard"
        refuse --remove --expected-manifest-sha256 "$localized_digest" --apply
        [[ "$removal_before" == "$(sha256sum "$live_dictionary")" ]]
        rm "$fixture/guard-fail"
        run --remove --expected-manifest-sha256 "$localized_digest" --apply
        [[ ! -e "$live_dictionary" && ! -L "$live_dictionary" ]]
        run --install --bundle "$fixture/package" --manifest-sha256 "$digest" --apply
    fi
fi
printf 'drift\n' >> "$amxx/plugins/$plugin_name"
refuse --disable --apply
[[ -f "$amxx/configs/$loader_name" ]]
after="$(sha256sum "${sentinels[@]}")"
[[ "$before" == "$after" ]] || { echo 'Telemetry changed.' >&2; exit 1; }
echo "${label}_ADDON_SMOKE=passed"
