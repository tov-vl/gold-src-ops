#!/usr/bin/env bash
set -Eeuo pipefail

# Verified archives are extracted by engine-ci.py. Only synthetic evidence is exported.
# The host enforces no network, no published ports, two CPUs, 1 GiB and a 600s deadline.
ulimit -c 0
mkdir /server
cp -a /hlds/. /server/
cp -a /inputs/rehlds/bin/linux32/. /server/
cp -a /inputs/regamedll/bin/linux32/cstrike/. /server/cstrike/
cp -a /inputs/amxx/. /server/cstrike/
cp -a /inputs/cstrike/. /server/cstrike/
cp -a /inputs/metamod/addons/. /server/cstrike/addons/
cp -a /inputs/reapi/addons/. /server/cstrike/addons/
cp /repo/samples/amxmodx-weapon-selection/goldsrcops-player-menu.txt /server/cstrike/addons/amxmodx/data/lang/
printf 'linux addons/amxmodx/dlls/amxmodx_mm_i386.so\n' > /server/cstrike/addons/metamod/plugins.ini
printf 'reapi\nfakemeta\nhamsandwich\n' > /server/cstrike/addons/amxmodx/configs/modules.ini
mkdir -p /server/cstrike/addons/amxmodx/configs/plugins
printf 'goldsrcops_weapons_enabled "1"\n' > /server/cstrike/addons/amxmodx/configs/plugins/goldsrcops-weapon-selection.cfg
sed -i 's|^gamedll_linux .*|gamedll_linux "addons/metamod/metamod_i386.so"|' /server/cstrike/liblist.gam
printf 'sv_lan 1\nsv_password ""\nrcon_password ""\nmp_freezetime 0\nmp_startmoney 0\nmp_autokick 0\nmp_round_infinite 1\n' > /server/cstrike/server.cfg
useradd --create-home --shell /bin/bash steam
mkdir -p /home/steam/.steam/sdk32
cp /inputs/steamclient.so /server/steamclient.so
cp /inputs/steamclient.so /home/steam/.steam/sdk32/steamclient.so
chown -R steam:steam /home/steam
chmod 0755 /server/hlds_run /server/hlds_linux
ulimit -f 16384
mkfifo /tmp/preferences-console
chown -R steam:steam /server /tmp/preferences-console
cd /server
exec 3<> /tmp/preferences-console
pid=
finish() {
    local status=$?
    trap - EXIT
    if [[ -n "$pid" ]]; then kill "$pid" 2>/dev/null || true; fi
    find cstrike/addons/amxmodx/logs -name 'error_*.log' -type f -exec cp {} /evidence/ \;
    exit "$status"
}
trap finish EXIT

run_case() {
    local plugin="$1" command="$2" marker="$3" map="$4" log="/evidence/$2.log"
    cp "/compiled/$plugin.amxx" cstrike/addons/amxmodx/plugins/
    chown steam:steam "cstrike/addons/amxmodx/plugins/$plugin.amxx"
    printf '%s.amxx\n' "$plugin" > cstrike/addons/amxmodx/configs/plugins.ini
    runuser -u steam -- timeout --signal=TERM --kill-after=3 45 ./hlds_run -norestart -insecure -game cstrike -strictportbind +ip 127.0.0.1 -port 27025 +map "$map" +servercfgfile server.cfg -maxplayers 8 <&3 > "$log" 2>&1 &
    pid=$!
    sleep 5
    printf '%s\n' "$command" >&3
    for ((attempt=0; attempt<25; attempt++)); do
        if grep -q "$marker" "$log"; then break; fi
        kill -0 "$pid" 2>/dev/null || break
        sleep 1
    done
    printf 'quit\n' >&3
    wait "$pid"
    pid=
    grep -Eq "^${marker}=passed( [^[:space:]]+)* failures=0( |$)" "$log"
    if grep -Eq 'ASSERTION_FAILED|Run time error|Load fails|bad load|ML_NOTFOUND|failures=[1-9][0-9]*' "$log"; then return 1; fi
    [[ -z "$(find cstrike/addons/amxmodx/logs -name 'error_*.log' -type f -size +0c -print -quit)" ]]
    grep -E 'SMOKE=|SEED=|RELOAD=|PLAYER_LOOKUP_(LOAD|TIMING)[= ]' "$log"
}

while IFS=$'\t' read -r plugin command marker map; do
    run_case "$plugin" "$command" "$marker" "$map"
done < /inputs/cases.tsv
echo PLAYER_PREFERENCES_RUNTIME=passed
