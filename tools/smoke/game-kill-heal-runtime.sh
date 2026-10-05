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
mkfifo /tmp/kill-heal-console
chown -R steam:steam /server /tmp/kill-heal-console
chmod 0755 /server/hlds_run /server/hlds_linux
cd /server
exec 3<> /tmp/kill-heal-console
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

run_case map_stats_smoke goldsrcops_map_stats_smoke MAP_STATS_SMOKE de_dust2
grep -q '^KILL_HEAL_SMOKE=passed failures=0 native_damage=1' /tmp/goldsrcops_map_stats_smoke.log
echo KILL_HEAL_RUNTIME=passed
