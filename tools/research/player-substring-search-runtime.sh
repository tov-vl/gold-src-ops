#!/usr/bin/env bash
set -Eeuo pipefail
# Disposable research container, no network or published ports; inputs read-only.
# This runner expects a base image containing /hlds and runuser.
check() { echo "$2  $1" | sha256sum -c -; }
check /inputs/assets/rehlds-bin-3.15.0.896.zip 997baeb7ef3842dab3e034d82dc651ebfe560c23b158adce660a6b97976b4e2b
check /inputs/assets/regamedll-bin-5.30.0.814.zip 457f5c96a4d10280fcad47f106cbd5be86363a47df1ccab91c514e9d1bc6fd18
check /inputs/assets/amxmodx-1.10.0-git5481-base-linux.tar.gz ee33b31ae92afd94802c43eae14ecdfa1ffa2ba0b11658e8bec98f48a5881272
check /inputs/assets/amxmodx-1.10.0-git5481-cstrike-linux.tar.gz 76ff2bdd39f6dc14a2088ff4af724599c69a8ae7e3526679927aaef4ca898bf2
check /inputs/assets/metamod-bin-1.3.0.149.zip ede7f59c4e0220afe8c02aa348a130cce527f87d36ffdb674e37a501ce57be94
check /inputs/assets/reapi-bin-5.24.0.300.zip 16114cf5a782e9d3d0c9443c23cd937c17cc09dc9b16ff998ce48bda5faf0a81
check /inputs/assets/steamclient.so 6d8e7e938ff44527a78e2550525c16a9f4605a88d5b20f4466aff311cc00a103
mkdir /server
cp -a /hlds/. /server/
cp -a /inputs/extracted/rehlds/bin/linux32/. /server/
cp -a /inputs/extracted/regamedll/bin/linux32/cstrike/. /server/cstrike/
tar -xzf /inputs/assets/amxmodx-1.10.0-git5481-base-linux.tar.gz -C /server/cstrike
tar -xzf /inputs/assets/amxmodx-1.10.0-git5481-cstrike-linux.tar.gz -C /server/cstrike
cp -a /inputs/extracted/metamod/addons/. /server/cstrike/addons/
cp -a /inputs/extracted/reapi/addons/. /server/cstrike/addons/
cp /repo/samples/amxmodx-weapon-selection/goldsrcops-player-menu.txt /server/cstrike/addons/amxmodx/data/lang/
cp /inputs/output/player_substring_research.amxx /server/cstrike/addons/amxmodx/plugins/
printf 'linux addons/amxmodx/dlls/amxmodx_mm_i386.so\n' > /server/cstrike/addons/metamod/plugins.ini
printf 'player_substring_research.amxx\n' > /server/cstrike/addons/amxmodx/configs/plugins.ini
printf 'reapi\nfakemeta\n' > /server/cstrike/addons/amxmodx/configs/modules.ini
sed -i 's|^gamedll_linux .*|gamedll_linux "addons/metamod/metamod_i386.so"|' /server/cstrike/liblist.gam
printf 'sv_lan 1\nsv_password ""\nrcon_password ""\nmp_autokick 0\n' > /server/cstrike/server.cfg
useradd --create-home --shell /bin/bash steam
mkdir -p /home/steam/.steam/sdk32
cp /inputs/assets/steamclient.so /server/steamclient.so
cp /inputs/assets/steamclient.so /home/steam/.steam/sdk32/steamclient.so
chown -R steam:steam /home/steam
mkfifo /tmp/research-console
chown -R steam:steam /server /tmp/research-console
chmod 0755 /server/hlds_run /server/hlds_linux
cd /server
exec 3<> /tmp/research-console
runuser -u steam -- ./hlds_run -norestart -insecure -game cstrike -strictportbind +ip 127.0.0.1 -port 27025 +map de_dust2 +servercfgfile server.cfg -maxplayers 4 <&3 > /tmp/research.log 2>&1 &
pid=$!
trap 'kill "$pid" 2>/dev/null || true' EXIT
sleep 5
printf 'goldsrcops_substring_research\n' >&3
for ((attempt=0; attempt<45; attempt++)); do
    if grep -q '^SUBSTRING_RESEARCH=' /tmp/research.log; then break; fi
    if ! kill -0 "$pid" 2>/dev/null; then break; fi
    sleep 1
done
printf 'quit\n' >&3
if ! wait "$pid"; then cat /tmp/research.log; exit 1; fi
trap - EXIT
if ! grep -q '^SUBSTRING_RESEARCH=passed failures=0 ' /tmp/research.log || grep -Eq 'ASSERTION_FAILED|Run time error|Load fails|bad load|ML_NOTFOUND' /tmp/research.log; then
    cat /tmp/research.log
    exit 1
fi
if [[ -n "$(find cstrike/addons/amxmodx/logs -name 'error_*.log' -print -quit)" ]]; then
    find cstrike/addons/amxmodx/logs -name 'error_*.log' -exec cat {} +
    exit 1
fi
grep '^SUBSTRING_' /tmp/research.log
