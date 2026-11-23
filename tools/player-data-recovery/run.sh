#!/usr/bin/env bash
set -Eeuo pipefail
umask 077
ulimit -c 0

digest=$1
mode=$2
phase=input
trap 'printf "RECOVERY_PHASE_FAILED=%s\n" "$phase"' ERR
if ! python3 -B /kit/load.py receive "$digest" "$mode" 2>/dev/null; then
    exit 1
fi

phase=setup
cp -a /opt/cs16/serverfiles-base/. /server/
cp -a /kit/rehlds/bin/linux32/. /server/
cp -a /kit/regamedll/bin/linux32/cstrike/. /server/cstrike/
cp -a /kit/amxx/. /server/cstrike/
cp -a /kit/cstrike/. /server/cstrike/
cp -a /kit/metamod/addons/. /server/cstrike/addons/
cp -a /kit/reapi/addons/. /server/cstrike/addons/
cd /server
amxx=cstrike/addons/amxmodx
cp /kit/goldsrcops-player-menu.txt "$amxx/data/lang/"
cp /kit/nvault_amxx_i386.so "$amxx/modules/"
cp /kit/goldsrcops_weapon_selection.amxx "$amxx/plugins/"
printf 'linux addons/amxmodx/dlls/amxmodx_mm_i386.so\n' > cstrike/addons/metamod/plugins.ini
printf 'reapi\nfakemeta\nhamsandwich\n' > "$amxx/configs/modules.ini"
mkdir -p "$amxx/configs/plugins"
cp /kit/goldsrcops-weapon-selection.cfg "$amxx/configs/plugins/"
sed -i 's|^gamedll_linux .*|gamedll_linux "addons/metamod/metamod_i386.so"|' cstrike/liblist.gam
printf 'sv_lan 1\nsv_password ""\nrcon_password ""\nmp_freezetime 0\nmp_autokick 0\nmp_round_infinite 1\n' > cstrike/server.cfg
mkdir -p .steam/sdk32
cp /kit/steamclient.so .steam/sdk32/
cp /kit/steamclient.so steamclient.so
chmod 0755 hlds_run hlds_linux /server
chown -R steam:steam /server
mkfifo /private/console
exec 3<> /private/console

phase=compile
(
    cd "$amxx/scripting"
    chmod u+x amxxpc
    cp /kit/recovery.sma /kit/goldsrcops_weapon_selection.sma /kit/goldsrcops-process-storage.inc .
    ./amxxpc recovery.sma "-i$PWD/include" "-i$PWD" -o"/server/$amxx/plugins/recovery.amxx" -E
) > /private/compile.log 2>&1
test -s "$amxx/plugins/recovery.amxx"
chown steam:steam "$amxx/plugins/recovery.amxx"

restore_vault() {
    local vault="$amxx/data/vault"
    if [[ -d "$vault" ]]; then
        rmdir -- "$vault"
    fi
    python3 -B /kit/load.py restore "$digest" "$mode" 2>/dev/null
    chown -R steam:steam "$vault"
}

run_instance() {
    local log=$1 commands=$2 expected=$3 pid
    HOME=/server runuser -u steam -m -- timeout --signal=TERM --kill-after=3 40 \
        ./hlds_run -norestart -insecure -game cstrike -strictportbind \
        +ip 127.0.0.1 -port 27025 +map de_dust2 +servercfgfile server.cfg -maxplayers 8 \
        <&3 > "$log" 2>&1 &
    pid=$!
    sleep 5
    printf '%s\n' "$commands" >&3
    for ((attempt=0;attempt<25;attempt++)); do
        if grep -q "^$expected" "$log"; then break; fi
        kill -0 "$pid" 2>/dev/null || break
        sleep 1
    done
    printf 'quit\n' >&3
    wait "$pid"
    if grep -Eq 'Run time error|Load fails|bad load|ML_NOTFOUND|Host_Error|FATAL ERROR' "$log"; then
        return 1
    fi
    [[ -z "$(find "$amxx/logs" -name 'error_*.log' -type f -size +0c -print -quit)" ]]
}

phase=stock
restore_vault
printf 'goldsrcops_weapon_selection.amxx\n' > "$amxx/configs/plugins.ini"
log=/private/stock.log
run_instance "$log" $'goldsrcops_weapons_status\ngoldsrcops_stats_status' PLAYER_LEADERBOARD_STATUS
grep -Eq '^WEAPON_SELECTION_STATUS version=0\.40\.0 enabled=1 ' "$log"
grep -Eq '^PLAYER_PREFERENCES_STATUS version=0\.40\.0 storage=nvault .* errors=0$' "$log"
grep -Eq '^PLAYER_STATS_STATUS version=0\.40\.0 storage=nvault .* errors=0$' "$log"
grep -Eq '^PLAYER_LEADERBOARD_STATUS version=0\.40\.0 ready=1 entries=[0-9]+ .* errors=0 ' "$log"
echo RECOVERY_STABLE_PAIR=passed

phase=engine
mv -- "$amxx/data/vault" "$amxx/data/stock-vault"
restore_vault
install -m 0400 -o steam -g steam /private/expectations.tsv "$amxx/data/recovery-expectations.tsv"
printf 'recovery.amxx\n' > "$amxx/configs/plugins.ini"
for epoch in 1 2; do
    log="/private/engine-$epoch.log"
    run_instance "$log" goldsrcops_actual_copy_check ACTUAL_COPY_ENGINE=
    marker=$(grep '^ACTUAL_COPY_ENGINE=' "$log")
    [[ "$marker" =~ ^ACTUAL_COPY_ENGINE=(passed|failed)\ rows=[0-9]+\ stats=[0-9]+\ preferences=[0-9]+\ hud=[0-9]+\ streak=[0-9]+\ names=[0-9]+\ writes=[0-9]+\ failures=[0-9]+$ ]]
    printf 'RECOVERY_EPOCH=%d %s\n' "$epoch" "${marker#ACTUAL_COPY_ENGINE=}"
    if [[ "$marker" == ACTUAL_COPY_ENGINE=failed* ]]; then
        echo RECOVERY_CHECK=mismatch
        exit 1
    fi
    python3 -B /kit/load.py compare "$digest" "$mode" 2>/dev/null
done
echo RECOVERY_CHECK=passed
