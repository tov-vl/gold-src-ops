# Map Menu

A small AMX Mod X 1.10.0.5481 / ReAPI 5.24.0.300 companion to the existing
managed fast-reentry profile. `/maps`, team chat `/maps`, or console `maps`
open the five-map voting menu. `/timeleft` in either chat delegates to the
game's own remaining-time response. Nothing opens automatically.

The map list is de_dust2, de_inferno, de_nuke, de_train and cs_office. The
current map is marked and disabled, avoiding a vote that would extend it.
Only connected human T/CT players can use the voting menu; spectators may
query time left. Bot and HLTV requests are ignored.

Version 0.2.0 translates the menu title, current-map marker, exit and private
request/unavailable messages through its own RU/EN dictionary. Weapon addon
0.8.1 exposes a read-only language native, so the choice in `/menu` also applies
to `/maps`. Without that provider, or while it is disabled, empty/ru client
language uses Russian and all other values use English. No client/global
language or saved player data is changed. Map names, native vote responses and
the engine's `timeleft` response remain unchanged.

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
./tools/smoke/amxx-map-menu.ps1 -CacheDirectory <verified-cache> -ProviderFixture -Offline
```

Only `goldsrcops_map_menu.amxx` is the product. Do not install
`map_menu_smoke.amxx` or `map_language_provider_smoke.amxx` on a shared or active host: they create synthetic players
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

## Bounded Host Addon

`tools/release/build-map-menu-addon.ps1 -CacheDirectory <verified-cache>
-OutputDirectory <new-directory> -Offline` produces an enabled addon with
exact manifest/payload hashes and a separate `plugins-goldsrcops-maps.ini`.
It refuses to overwrite an existing result and never includes the fixture.

The existing stopped-game installer accepts the fixed `--map-menu` selector:

```bash
bash ops/gameserver/weapon-selection-addon.sh --map-menu --install \
  --bundle /absolute/reviewed/content --manifest-sha256 <manifest-sha256>
bash ops/gameserver/weapon-selection-addon.sh --map-menu --status
bash ops/gameserver/weapon-selection-addon.sh --map-menu --remove \
  --expected-manifest-sha256 <installed-manifest-sha256>
```

Install/remove remain plan-only without `--apply`; status is read-only. Apply
requires the reviewed SSH operator, inactive game and existing persistent MP5
baseline/locks. Only the map binary, config, loader, its dictionary and receipt are owned by
this mode. Other addon paths and guards are not accepted as operation inputs.
Schema-2 packages/receipts bind `data/lang/goldsrcops-map-menu.txt`; legacy
schema-1 packages remain valid reverse inputs. The player-menu dictionary is
not owned by map operations. The ordinary weapon mode remains the default.

Removal checks the exact installed identity, retains a root-only predecessor
copy and restores it on failure. It also works while the addon is disabled.
It is an explicit recovery/removal action, not an automatic delivery retry.
No addon operation starts/stops services, reads OAuth credentials or changes
telemetry, queues, spool, weapons, mapcycle or boot policy. The outer reviewed
rollout retains unchanged baseline hashes and verifies them before restarting.
