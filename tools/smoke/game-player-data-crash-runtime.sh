#!/usr/bin/env bash
set -Eeuo pipefail

# Same pinned inputs and disposable /server as the existing engine smoke.
# The only writable host mount is synthetic /evidence; no live game data is used.
ulimit -c 0
mkdir /server
cp -a /hlds/. /server/
cp -a /inputs/rehlds/bin/linux32/. /server/
cp -a /inputs/regamedll/bin/linux32/cstrike/. /server/cstrike/
cp -a /inputs/amxx/. /server/cstrike/
if [[ -f /inputs/nvault_amxx_i386.so ]]; then
    cp /inputs/nvault_amxx_i386.so /server/cstrike/addons/amxmodx/modules/nvault_amxx_i386.so
fi
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
cp /compiled/player_data_crash_smoke.amxx /server/cstrike/addons/amxmodx/plugins/
printf 'player_data_crash_smoke.amxx\n' > /server/cstrike/addons/amxmodx/configs/plugins.ini
chmod 0755 /server/hlds_linux
ulimit -f 16384
mkfifo /server/crash-console
chown -R steam:steam /server /home/steam
cd /server
exec 3<> /server/crash-console
pid=
finish() {
    local status=$?
    trap - EXIT
    if [[ -n "$pid" ]]; then kill "$pid" 2>/dev/null || true; fi
    find cstrike/addons/amxmodx/logs -name 'error_*.log' -type f -exec cp {} /evidence/ \;
    exit "$status"
}
trap finish EXIT

vault=cstrike/addons/amxmodx/data/vault
names=(goldsrcops-player-stats-v1 goldsrcops-player-preferences-v1)
run_case() {
    local command="$1" marker="$2" mode="$3" log="/evidence/$1.log" engine_pid status name
    local -a children
    # Launch the engine directly: no hlds_run supervisor can restart it after KILL.
    runuser -u steam -- env LD_LIBRARY_PATH=/server ./hlds_linux -insecure -game cstrike -strictportbind +ip 127.0.0.1 -port 27025 +map de_dust2 +servercfgfile server.cfg -maxplayers 8 <&3 > "$log" 2>&1 &
    pid=$!
    sleep 5
    printf '%s\n' "$command" >&3
    for ((attempt=0; attempt<30; attempt++)); do
        if grep -q "^${marker}=" "$log"; then break; fi
        kill -0 "$pid" 2>/dev/null || break
        sleep 1
    done
    grep -Eq "^${marker}=passed failures=0$" "$log"
    ! grep -Eq 'ASSERTION_FAILED|Run time error|Load fails|bad load|ML_NOTFOUND' "$log"
    if [[ "$mode" == kill ]]; then
        read -r -a children <<< "$(cat "/proc/$pid/task/$pid/children")"
        [[ "${#children[@]}" == 1 ]] || { echo 'Expected one direct engine child' >&2; return 1; }
        engine_pid="${children[0]}"
        [[ "$engine_pid" =~ ^[0-9]+$ ]]
        # Container root has no SYS_PTRACE. Inspect the same-UID process as steam.
        [[ "$(runuser -u steam -- readlink -f "/proc/$engine_pid/exe")" == /server/hlds_linux ]] || { echo 'Unexpected child executable' >&2; return 1; }
        kill -KILL "$engine_pid"
        status=0
        wait "$pid" || status=$?
        pid=
        [[ "$status" == 137 ]]
        ! grep -q '^PLAYER_DATA_PLUGIN_END=' "$log"
        printf '9\t%s\n' "$status" > /evidence/process-kill.tsv
        mkdir /evidence/post-crash
        for name in "${names[@]}"; do
            cp "$vault/$name.vault" /evidence/post-crash/
            if [[ -f "$vault/$name.journal" ]]; then cp "$vault/$name.journal" /evidence/post-crash/; fi
            # A journal is essential: the old checkpoint must not contain updates.
            cmp "/evidence/baseline/$name.vault" "$vault/$name.vault"
            [[ -f "$vault/$name.journal" && ! -L "$vault/$name.journal" ]] || { echo "Journal missing: $name" >&2; return 1; }
            printf 'CRASH_JOURNAL name=%s bytes=%s\n' "$name" "$(stat -c %s "$vault/$name.journal")"
        done
        if [[ "${1}" == goldsrcops_crash_write && "${DISCARD_CRASH_JOURNALS:-0}" == 1 ]]; then
            # Deliberately broken synthetic control; baseline vaults are retained.
            rm -- "$vault/${names[0]}.journal" "$vault/${names[1]}.journal"
        fi
    else
        printf 'quit\n' >&3
        wait "$pid"
        pid=
        [[ "$(grep -c '^PLAYER_DATA_PLUGIN_END=called$' "$log")" == 1 ]]
    fi
    grep -E "^${marker}=" "$log"
}

run_case goldsrcops_crash_baseline PLAYER_DATA_BASELINE clean
mkdir /evidence/baseline
for name in "${names[@]}"; do cp "$vault/$name.vault" /evidence/baseline/; done
run_case goldsrcops_crash_write PLAYER_DATA_BEFORE_KILL kill
run_case goldsrcops_crash_recover PLAYER_DATA_RECOVERED clean
run_case goldsrcops_crash_reopen PLAYER_DATA_REOPENED clean
echo PLAYER_DATA_CRASH_RUNTIME=passed
