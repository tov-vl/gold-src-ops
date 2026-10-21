#!/usr/bin/env bash
set -Eeuo pipefail

# Map inputs for the shared engine-ci.py runner; no standalone server startup.
# The caller has already installed the pinned engine and modules into /server.
[[ $# -eq 0 && -d /server/cstrike/addons/amxmodx/data/lang ]]
cp /repo/samples/amxmodx-map-menu/goldsrcops-map-menu.txt /server/cstrike/addons/amxmodx/data/lang/
printf 'goldsrcops_maps_enabled "1"\n' > /server/cstrike/addons/amxmodx/configs/plugins/goldsrcops-map-menu.cfg
bash -c 'source /repo/ops/gameserver/managed-profile.sh; render_mapcycle /server/cstrike/goldsrcops-mapcycle.txt'
printf 'mapcyclefile "goldsrcops-mapcycle.txt"\n' >> /server/cstrike/server.cfg
