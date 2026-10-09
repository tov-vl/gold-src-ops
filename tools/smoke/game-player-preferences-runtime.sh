#!/usr/bin/env bash
set -Eeuo pipefail

# Cached disposable engine: /smoke, /assets, /compiled and /repo are read-only.
# No network or published ports; all mutable files stay inside the container.
mkdir -p /server
cp -a /opt/cs16/serverfiles-base/. /server/
cp -a /smoke/extracted/rehlds/bin/linux32/. /server/
cp -a /smoke/extracted/regamedll/bin/linux32/cstrike/. /server/cstrike/
tar -xzf /assets/amxmodx-1.10.0-git5481-base-linux.tar.gz -C /server/cstrike
tar -xzf /assets/amxmodx-1.10.0-git5481-cstrike-linux.tar.gz -C /server/cstrike
cp -a /smoke/extracted/metamod/addons/. /server/cstrike/addons/
cp -a /smoke/extracted/reapi/addons/. /server/cstrike/addons/
cp /repo/samples/amxmodx-weapon-selection/goldsrcops-player-menu.txt /server/cstrike/addons/amxmodx/data/lang/
printf 'linux addons/amxmodx/dlls/amxmodx_mm_i386.so\n' > /server/cstrike/addons/metamod/plugins.ini
printf 'reapi\nfakemeta\nhamsandwich\n' > /server/cstrike/addons/amxmodx/configs/modules.ini
mkdir -p /server/cstrike/addons/amxmodx/configs/plugins
printf 'goldsrcops_weapons_enabled "1"\n' > /server/cstrike/addons/amxmodx/configs/plugins/goldsrcops-weapon-selection.cfg
sed -i 's|^gamedll_linux .*|gamedll_linux "addons/metamod/metamod_i386.so"|' /server/cstrike/liblist.gam
printf 'sv_lan 1\nsv_password ""\nrcon_password ""\nmp_freezetime 0\nmp_startmoney 0\nmp_autokick 0\nmp_round_infinite 1\n' > /server/cstrike/server.cfg
mkfifo /tmp/preferences-console
chown -R steam:steam /server /tmp/preferences-console
chmod 0755 /server/hlds_run /server/hlds_linux
cd /server
exec 3<> /tmp/preferences-console
pid=
trap 'if [[ -n "$pid" ]]; then kill "$pid" 2>/dev/null || true; fi' EXIT

run_case() {
    local plugin="$1" command="$2" marker="$3" map="$4" log="/tmp/$2.log"
    cp "/compiled/$plugin.amxx" cstrike/addons/amxmodx/plugins/
    chown steam:steam "cstrike/addons/amxmodx/plugins/$plugin.amxx"
    printf '%s.amxx\n' "$plugin" > cstrike/addons/amxmodx/configs/plugins.ini
    gosu steam ./hlds_run -norestart -game cstrike -strictportbind +ip 0.0.0.0 -port 27025 +map "$map" +servercfgfile server.cfg -maxplayers 8 <&3 > "$log" 2>&1 &
    pid=$!
    sleep 5
    printf '%s\n' "$command" >&3
    for ((attempt=0; attempt<20; attempt++)); do
        if grep -q "$marker" "$log"; then break; fi
        sleep 1
    done
    printf 'quit\n' >&3
    wait "$pid"
    pid=
    if ! grep -Eq "^${marker}=passed( [^[:space:]]+)* failures=0( |$)" "$log" || grep -Eq 'ASSERTION_FAILED|Run time error|Load fails|bad load|ML_NOTFOUND' "$log"; then
        cat "$log"
        return 1
    fi
    grep -E 'SMOKE=|SEED=|RELOAD=' "$log"
    [[ -z "$(find cstrike/addons/amxmodx/logs -name 'error_*.log' -print -quit)" ]]
}

run_case player_preferences_smoke goldsrcops_preferences_smoke PLAYER_PREFERENCES_SMOKE de_dust2
grep -Eq '^PLAYER_HUD_PREFERENCE_SMOKE=passed failures=0 storage=real_nvault$' /tmp/goldsrcops_preferences_smoke.log
run_case player_preferences_smoke goldsrcops_preferences_seed PLAYER_PREFERENCES_SEED de_dust2
run_case player_preferences_smoke goldsrcops_preferences_verify PLAYER_PREFERENCES_RELOAD cs_office
run_case player_menu_smoke goldsrcops_player_menu_smoke PLAYER_MENU_SMOKE de_dust2
grep -Eq '^PLAYER_HELP_SMOKE=passed failures=0 languages=2 lines=6 storage_writes=none$' /tmp/goldsrcops_player_menu_smoke.log
run_case spawn_loadout_smoke goldsrcops_loadout_smoke SPAWN_LOADOUT_SMOKE de_dust2
run_case persistent_stats_smoke goldsrcops_persistent_stats_smoke PERSISTENT_STATS_SMOKE de_dust2
run_case persistent_stats_smoke goldsrcops_leaderboard_smoke PLAYER_LEADERBOARD_SMOKE de_dust2
run_case persistent_stats_smoke goldsrcops_standing_smoke PLAYER_STANDING_SMOKE de_dust2
run_case persistent_stats_smoke goldsrcops_profile_smoke PLAYER_PROFILE_SMOKE de_dust2
grep -Eq '^PLAYER_MENU_STATUS version=[^ ]+ .* entries=9 map_commands=delegated language_native=1$' /tmp/goldsrcops_profile_smoke.log
grep -Eq '^PLAYER_PROFILE_STATUS version=[^ ]+ display=private_motd languages=ru_en cooldown=5 source=loaded_player_state standing=leaderboard_cache storage_writes=none$' /tmp/goldsrcops_profile_smoke.log
run_case persistent_stats_smoke goldsrcops_saved_streak_smoke PLAYER_SAVED_STREAK_SMOKE de_dust2
run_case persistent_stats_smoke goldsrcops_saved_streak_seed PLAYER_SAVED_STREAK_SEED de_dust2
run_case persistent_stats_smoke goldsrcops_saved_streak_verify PLAYER_SAVED_STREAK_RELOAD cs_office
grep -Eq '^PLAYER_PROFILE_RELOAD=passed failures=0$' /tmp/goldsrcops_saved_streak_verify.log
run_case persistent_stats_smoke goldsrcops_leaderboard_seed PLAYER_LEADERBOARD_SEED de_dust2
run_case persistent_stats_smoke goldsrcops_leaderboard_verify PLAYER_LEADERBOARD_RELOAD cs_office

echo PLAYER_PREFERENCES_RUNTIME=passed
