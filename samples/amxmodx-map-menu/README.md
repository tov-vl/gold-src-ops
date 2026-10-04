# Map Menu

A small AMX Mod X 1.10.0.5481 / ReAPI 5.24.0.300 companion to the existing
managed fast-reentry profile. `/maps`, team chat `/maps`, or console `maps`
open the five-map voting menu. `/timeleft` in either chat delegates to the
game's own remaining-time response. Nothing opens automatically.

The map list is de_dust2, de_inferno, de_nuke, de_train and cs_office. The
current map is marked and disabled, avoiding a vote that would extend it.
Only connected human T/CT players can use the voting menu; spectators may
query time left. Bot and HLTV requests are ignored.

Selections invoke native `votemap`, not `changelevel`. ReGameDLL retains its
elapsed-time, team/player-count, cooldown and quorum checks. A requested vote
is not necessarily accepted: the game's result is printed in the console.
No voting policy cvar is modified by the product plugin. Native voting can
change the map immediately after its quorum is reached, not only at round end.

The menu checks the exact managed mapcycle name and five installed maps in
their expected order on both open and selection. A missing, oversized,
reordered, extended or changed cycle disables requests instead of guessing
native vote IDs. Blank lines and surrounding whitespace are harmless.
This intentionally does not support arbitrary mapcycle rules or custom maps.

## Build And Local Enablement

```powershell
./tools/smoke/amxx-map-menu.ps1 -CacheDirectory <verified-cache> -Offline
./tools/smoke/amxx-map-menu.ps1 -CacheDirectory <verified-cache> -RuntimeFixture -Offline
```

Only `goldsrcops_map_menu.amxx` is the product. Do not install
`map_menu_smoke.amxx` on a shared or active host: it creates synthetic players
and changes fixture policy/files. The product defaults to disabled. On a
disposable local server, load it through AMXX and put
`goldsrcops_maps_enabled "1"` in
`addons/amxmodx/configs/plugins/goldsrcops-map-menu.cfg`.

The companion `tools/smoke/game-map-menu-runtime.sh` starts a disposable,
cached engine fixture with the mounts documented in its header. Use a new
container, `--entrypoint bash`, `--network none`, and no published ports. Send
`goldsrcops_map_menu_smoke` to `/tmp/map-menu-console`, require
`MAP_MENU_SMOKE=passed failures=0 synthetic_clients=2`, check AMXX errors,
then stop it. `product` mode loads the normal plugin without test commands.
The actual five-map cycle is rendered from the existing managed-profile code.

This sample is not an active-host installer. Exact package identity,
supplemental loader ownership and removal/rollback must be reviewed before a
host rollout; existing telemetry and weapon-addon files must remain unchanged.
