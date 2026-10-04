#!/usr/bin/env bash
set -Eeuo pipefail

# Disposable cached engine image; no published ports or network are required.
# Mount pinned engine extraction at /smoke, AMXX archives at /assets,
# compiled product/fixture plugins at /compiled, and this checkout at /repo.
mode="${1:-fixture}"
case "$mode" in fixture|product) ;; *) exit 2 ;; esac
mkdir -p /server
cp -a /opt/cs16/serverfiles-base/. /server/
cp -a /smoke/extracted/rehlds/bin/linux32/. /server/
cp -a /smoke/extracted/regamedll/bin/linux32/cstrike/. /server/cstrike/
tar -xzf /assets/amxmodx-1.10.0-git5481-base-linux.tar.gz -C /server/cstrike
tar -xzf /assets/amxmodx-1.10.0-git5481-cstrike-linux.tar.gz -C /server/cstrike
cp -a /smoke/extracted/metamod/addons/. /server/cstrike/addons/
cp -a /smoke/extracted/reapi/addons/. /server/cstrike/addons/
printf 'linux addons/amxmodx/dlls/amxmodx_mm_i386.so\n' > /server/cstrike/addons/metamod/plugins.ini
printf 'reapi\nfakemeta\n' > /server/cstrike/addons/amxmodx/configs/modules.ini
if [[ "$mode" == fixture ]]; then
    plugin=map_menu_smoke.amxx
else
    plugin=goldsrcops_map_menu.amxx
fi
cp "/compiled/$plugin" /server/cstrike/addons/amxmodx/plugins/
cp /compiled/goldsrcops_weapon_selection.amxx /server/cstrike/addons/amxmodx/plugins/
printf '%s\ngoldsrcops_weapon_selection.amxx\n' "$plugin" > /server/cstrike/addons/amxmodx/configs/plugins.ini
mkdir -p /server/cstrike/addons/amxmodx/configs/plugins
printf 'goldsrcops_maps_enabled "1"\n' > /server/cstrike/addons/amxmodx/configs/plugins/goldsrcops-map-menu.cfg
printf 'goldsrcops_weapons_enabled "1"\n' > /server/cstrike/addons/amxmodx/configs/plugins/goldsrcops-weapon-selection.cfg
bash -c 'source /repo/ops/gameserver/managed-profile.sh; render_mapcycle /server/cstrike/goldsrcops-mapcycle.txt'
sed -i 's|^gamedll_linux .*|gamedll_linux "addons/metamod/metamod_i386.so"|' /server/cstrike/liblist.gam
printf 'sv_lan 1\nsv_password ""\nrcon_password ""\nmapcyclefile "goldsrcops-mapcycle.txt"\n' > /server/cstrike/server.cfg
mkfifo /tmp/map-menu-console
chown -R steam:steam /server /tmp/map-menu-console
chmod 0755 /server/hlds_run /server/hlds_linux
cd /server
exec 3<> /tmp/map-menu-console
exec gosu steam ./hlds_run -norestart -game cstrike -strictportbind +ip 0.0.0.0 -port 27025 +clientport 27026 +map de_dust2 +servercfgfile server.cfg -maxplayers 4 <&3
