#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'
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
chmod 0750 "$amxx/configs" "$amxx/configs/plugins" "$amxx/plugins"
printf 'goldsrcops_game_events.amxx\n' > "$amxx/configs/plugins.ini"
printf 'telemetry configuration\n' > "$amxx/configs/amxx.cfg"
printf 'telemetry queue\n' > "$fixture/queue"
printf 'telemetry spool\n' > "$fixture/spool"
before="$(sha256sum "$amxx/configs/plugins.ini" "$amxx/configs/amxx.cfg" "$fixture/queue" "$fixture/spool")"
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
if [[ "${!#}" == *goldsrcops-weapon-selection.cfg && -e "$FIXTURE_ROOT/install-fail" ]]; then exit 1; fi
exec /usr/bin/install "$@"
SH
chmod +x "$fixture/bin/systemctl" "$fixture/bin/install"
export PATH="$fixture/bin:$PATH"
plugin=cstrike/addons/amxmodx/plugins/goldsrcops_weapon_selection.amxx
config=cstrike/addons/amxmodx/configs/plugins/goldsrcops-weapon-selection.cfg
loader=cstrike/addons/amxmodx/configs/plugins-goldsrcops-weapons.ini
printf 'fixture plugin\n' > "$fixture/package/$plugin"
printf 'goldsrcops_weapons_enabled "1"\n' > "$fixture/package/$config"
printf 'goldsrcops_weapon_selection.amxx\n' > "$fixture/package/$loader"
jq -n --arg plugin "$plugin" --arg config "$config" --arg loader "$loader" \
    --arg p "$(sha256sum "$fixture/package/$plugin" | cut -d' ' -f1)" \
    --arg c "$(sha256sum "$fixture/package/$config" | cut -d' ' -f1)" \
    --arg l "$(sha256sum "$fixture/package/$loader" | cut -d' ' -f1)" \
    '{schemaVersion:1,purpose:"game-host-addon",productionInstallSupported:true,enabledByDefault:true,
      amxxVersion:"1.10.0.5481",reApiVersion:"5.24.0.300",sourceSha256:("a"*64),
      payload:[{path:$plugin,sha256:$p},{path:$config,sha256:$c},{path:$loader,sha256:$l}]}' > "$fixture/package/manifest.json"
digest="$(sha256sum "$fixture/package/manifest.json" | cut -d' ' -f1)"
run() { bash "$fixture/scripts/weapon-selection-addon.sh" "$@"; }
refuse() { if run "$@" > "$fixture/refusal.log" 2>&1; then echo 'Expected refusal.' >&2; exit 1; fi; }

run --install --bundle /nonexistent --manifest-sha256 "$digest" | grep -q PLAN_ONLY
[[ ! -e "$fixture/config/weapon-selection-installed" ]]
touch "$fixture/game-active"
refuse --install --bundle "$fixture/package" --manifest-sha256 "$digest" --apply
rm "$fixture/game-active"
refuse --install --bundle "$fixture/package" --manifest-sha256 "$(printf 'b%.0s' {1..64})" --apply
touch "$fixture/install-fail"
refuse --install --bundle "$fixture/package" --manifest-sha256 "$digest" --apply
[[ ! -e "$amxx/plugins/goldsrcops_weapon_selection.amxx" && ! -e "$fixture/config/weapon-selection-installed" ]]
rm "$fixture/install-fail"
touch "$fixture/fail-final-guard"
refuse --install --bundle "$fixture/package" --manifest-sha256 "$digest" --apply
[[ ! -e "$amxx/plugins/goldsrcops_weapon_selection.amxx" && ! -e "$fixture/config/weapon-selection-installed" ]]
rm "$fixture/guard-fail"
run --install --bundle "$fixture/package" --manifest-sha256 "$digest" --apply
run --status | grep -q 'WEAPON_SELECTION_ADDON=enabled'
refuse --install --bundle "$fixture/package" --manifest-sha256 "$digest" --apply
receipt_before="$(sha256sum "$fixture/config/weapon-selection-installed")"
touch "$fixture/fail-final-guard"
refuse --disable --apply
[[ -f "$amxx/configs/plugins-goldsrcops-weapons.ini" && ! -e "$fixture/config/weapon-selection-disabled-loader" ]]
[[ "$receipt_before" == "$(sha256sum "$fixture/config/weapon-selection-installed")" ]]
rm "$fixture/guard-fail"
run --disable --apply
[[ ! -e "$amxx/configs/plugins-goldsrcops-weapons.ini" ]]
[[ -f "$fixture/config/weapon-selection-disabled-loader" ]]
run --status | grep -q 'WEAPON_SELECTION_ADDON=disabled'
run --disable --apply | grep -q unchanged
run --enable --apply
run --status | grep -q 'WEAPON_SELECTION_ADDON=enabled'
printf 'drift\n' >> "$amxx/plugins/goldsrcops_weapon_selection.amxx"
refuse --disable --apply
[[ -f "$amxx/configs/plugins-goldsrcops-weapons.ini" ]]
after="$(sha256sum "$amxx/configs/plugins.ini" "$amxx/configs/amxx.cfg" "$fixture/queue" "$fixture/spool")"
[[ "$before" == "$after" ]] || { echo 'Telemetry changed.' >&2; exit 1; }
echo 'WEAPON_SELECTION_ADDON_SMOKE=passed'
