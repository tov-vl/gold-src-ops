#include <amxmodx>
#include <reapi>
#include <fakemeta>
#include <hamsandwich>
#include <nvault>

// Exercise the included observer with real inventory/ammo natives and entities.
// Only entry points and diagnostic capture are replaced in this test binary.
#define plugin_init FixturePluginInit
#define plugin_precache FixturePluginPrecache
#define plugin_natives FixturePluginNatives
#define client_disconnected FixtureDisconnect
#define log_amx CaptureAmmoLog
#define client_cmd CaptureFixtureClientCommand
#include <fixture_vgui_guard.sma>
#undef plugin_init
#undef plugin_precache
#undef plugin_natives
#undef client_disconnected
#undef log_amx
#undef client_cmd

new g_player, g_ak, g_deagle, g_failures, g_checks;
new g_direct_logs, g_cache_logs, g_result_logs;
new g_direct_report[256], g_cache_report[160], g_result_report[80];
new bool:g_started, bool:g_delayed_case;
new g_deaths, g_native_spawns, g_snapshot_callbacks;
new Float:g_last_selection_at, Float:g_movement_delay;

public plugin_init()
{
    register_plugin("Fixture ammo observer checks", "1.0.0", "");
    g_readiness = register_cvar("gso_lab_readiness", "0");
    register_srvcmd("gso_fixture_ammo_smoke", "StartAmmoSmoke");
    RegisterHookChain(RG_CBasePlayer_Killed, "ObserveTestDeath", true);
    RegisterHookChain(RG_CBasePlayer_Spawn, "ObserveTestSpawn", true);
}
public plugin_natives() { FixturePluginNatives(); }

Check(bool:valid, const label[])
{
    g_checks++;
    if (!valid) { g_failures++; server_print("FIXTURE_AMMO_ASSERTION_FAILED %s", label); }
}

CaptureAmmoLog(const format[], any:...)
{
    new text[256]; vformat(text, charsmax(text), format, 2);
    if (containi(text, "FIXTURE_GUNS_LOADOUT ") == 0)
    {
        Check(!g_direct_logs && !g_cache_logs && !g_result_logs, "one direct report first");
        copy(g_direct_report, charsmax(g_direct_report), text); g_direct_logs++;
    }
    if (containi(text, "FIXTURE_GUNS_CACHE ") == 0)
    {
        Check(g_direct_logs == 1 && !g_cache_logs && !g_result_logs, "one cache report second");
        copy(g_cache_report, charsmax(g_cache_report), text); g_cache_logs++;
    }
    if (containi(text, "FIXTURE_GUNS_RESPAWN ") == 0)
    {
        Check(g_direct_logs == 1 && g_cache_logs == 1 && !g_result_logs, "one strict result last");
        copy(g_result_report, charsmax(g_result_report), text); g_result_logs++;
    }
    log_amx("%s", text);
    return 1;
}

public StartAmmoSmoke()
{
    if (g_started || get_playersnum())
    {
        server_print("FIXTURE_AMMO_SMOKE=refused requires=fresh_empty_fixture");
        return PLUGIN_HANDLED;
    }
    g_started = true;
    g_player = engfunc(EngFunc_CreateFakeClient, "Ammo observer fixture");
    if (!g_player)
    {
        server_print("FIXTURE_AMMO_SMOKE=failed failures=1 reason=no_synthetic_slot");
        return PLUGIN_HANDLED;
    }
    new rejected[128];
    dllfunc(DLLFunc_ClientConnect, g_player, "Ammo observer fixture", "127.0.0.1", rejected);
    dllfunc(DLLFunc_ClientPutInServer, g_player);
    rg_set_user_team(g_player, TEAM_CT);
    set_member(g_player, m_iJoiningState, JOINED);
    set_member(g_player, m_bJustConnected, false);
    set_member(g_player, m_iMenu, Menu_OFF);
    set_member(g_player, m_bMissionBriefing, false);
    rg_round_respawn(g_player);
    Check(bool:is_user_connected(g_player) && bool:is_user_alive(g_player), "connected living synthetic player");
    Check(bool:rg_remove_all_items(g_player), "synthetic initial inventory setup");
    g_ak = rg_give_item(g_player, "weapon_ak47");
    g_deagle = rg_give_item(g_player, "weapon_deagle");
    Check(g_ak > 0 && g_deagle > 0, "real carried weapons created");
    rg_set_user_bpammo(g_player, WEAPON_AK47, 90);
    rg_set_user_bpammo(g_player, WEAPON_DEAGLE, 35);
    Check(bool:rg_switch_weapon(g_player, g_ak), "primary active before observation");
    set_task(0.2, "RunAmmoObservations");
    return PLUGIN_HANDLED;
}

ReadAmmoState(values[8])
{
    values[0] = get_member(g_player, m_pActiveItem);
    values[1] = get_entvar(g_player, var_weapons);
    values[2] = get_member(g_ak, m_Weapon_iClip);
    values[3] = rg_get_user_bpammo(g_player, WEAPON_AK47);
    values[4] = get_member(g_deagle, m_Weapon_iClip);
    values[5] = rg_get_user_bpammo(g_player, WEAPON_DEAGLE);
    values[6] = get_member(g_ak, m_pPlayer);
    values[7] = get_member(g_deagle, m_pPlayer);
}

RunAmmoObservation(const name[], expected_clip, expected_valid)
{
    g_direct_logs = 0; g_cache_logs = 0; g_result_logs = 0;
    new before[8], after[8], ak_clip, ak_reserve, de_clip, de_reserve;
    new ak_ok = get_user_ammo(g_player, CSW_AK47, ak_clip, ak_reserve);
    new de_ok = get_user_ammo(g_player, CSW_DEAGLE, de_clip, de_reserve);
    Check(ak_ok == 1 && de_ok == 1, "real AMXX diagnostic natives succeed");
    ReadAmmoState(before);
    g_guns_respawn[g_player] = GunsRespawnSpawned;
    ObserveGunsLoadout(g_player);
    ReadAmmoState(after);
    Check(before[0] == g_ak && before[6] == g_player && before[7] == g_player, "inactive pistol remains owned");
    Check(before[2] == 30 && before[3] == 90 && before[4] == expected_clip && before[5] == 35, "real entity and reserve state");
    for (new index = 0; index < sizeof before; index++) { Check(before[index] == after[index], "observer never changes inventory or ammo"); }
    new expected[256];
    formatex(expected, charsmax(expected), "FIXTURE_GUNS_LOADOUT ak47=1 deagle=1 mp5=0 usp=0 ak_clip=30 ak_reserve=90 de_clip=%d de_reserve=35", expected_clip);
    Check(equal(g_direct_report, expected) != 0, "reported authoritative values match actual entities");
    formatex(expected, charsmax(expected), "FIXTURE_GUNS_CACHE ak_ok=%d ak_clip=%d ak_reserve=%d de_ok=%d de_clip=%d de_reserve=%d",
        ak_ok, ak_clip, ak_reserve, de_ok, de_clip, de_reserve);
    Check(equal(g_cache_report, expected) != 0, "raw cache diagnostics retained exactly");
    formatex(expected, charsmax(expected), "FIXTURE_GUNS_RESPAWN selected_loadout=%d", expected_valid);
    Check(equal(g_result_report, expected) != 0, "strict actual observer result");
    Check(g_direct_logs == 1 && g_cache_logs == 1 && g_result_logs == 1, "one observation without retry");
    if (expected_valid) { Check(de_clip == 0 && before[4] == 7, "full inactive pistol accepted despite zero cache"); }
    server_print("FIXTURE_AMMO_CASE name=%s actual_de_clip=%d cached_de_clip=%d selected_loadout=%d unchanged=1",
        name, before[4], de_clip, expected_valid);
}

public RunAmmoObservations()
{
    RunAmmoObservation("full", 7, 1);
    // Negative-control setup only, between cases. The observer must not refill it.
    rg_set_user_ammo(g_player, WEAPON_DEAGLE, 0);
    Check(get_member(g_deagle, m_Weapon_iClip) == 0, "genuinely empty magazine before negative observation");
    RunAmmoObservation("empty", 0, 0);
    StartDelayedMovementCase();
}

CaptureFixtureClientCommand(player, const command[], any:...)
{
    Check(g_delayed_case && player == g_player && equal(command, "gso_lab_respawn"), "only the fixed respawn snapshot callback");
    Check(g_deaths == 1 && g_native_spawns == 1 && g_guns_respawn[player] == GunsRespawnChecked
        && is_user_alive(player) && equal(g_result_report, "FIXTURE_GUNS_RESPAWN selected_loadout=1"),
        "snapshot requested only after confirmed native spawn and loadout");
    g_snapshot_callbacks++;
    return 1;
}

StartDelayedMovementCase()
{
    g_direct_logs = 0; g_cache_logs = 0; g_result_logs = 0;
    g_delayed_case = true;
    set_cvar_num("mp_forcerespawn", 1);
    set_cvar_float("mp_roundrespawn_time", -1.0);
    set_cvar_num("mp_round_infinite", 1);
    set_pcvar_num(g_readiness, 1);
    client_putinserver(g_player);
    ObserveGunsSpawn(g_player);
    Check(!task_exists(g_player + 100) && !task_exists(g_player + 200),
        "bots do not emit real-client readiness callbacks");
    // Enter the post-setup state explicitly for this synthetic observer test.
    g_spawnReady[g_player] = true;
    g_setupAck[g_player] = true;
    g_guns[g_player] = 1;
    g_guns_step[g_player] = 5;
    g_guns_respawn[g_player] = GunsRespawnIdle;
    GunsInventory(g_player, g_guns_inventory[g_player]);
    GunsMovementStart(g_player);
    g_last_selection_at = get_gametime();
    ObserveGunsInventory(g_player);
    set_task(7.0, "ApplyDelayedMovement");
}

public ApplyDelayedMovement()
{
    g_movement_delay = get_gametime() - g_last_selection_at;
    Check(g_movement_delay >= 6.95, "at least seven seconds elapsed before movement");
    Check(g_guns_respawn[g_player] == GunsRespawnIdle && is_user_alive(g_player) && !g_deaths
        && !g_native_spawns && !g_snapshot_callbacks, "no death, respawn or snapshot during the movement delay");
    ObserveGunsLoadout(g_player);
    Check(!g_result_logs && !g_snapshot_callbacks, "unconfirmed spawn cannot request a snapshot");

    // Feed the two movement buttons and displacement into the actual observer.
    new Float:origin[3];
    get_entvar(g_player, var_origin, origin);
    origin[0] = g_guns_origin[g_player][0] + 12.0;
    set_entvar(g_player, var_origin, origin);
    set_entvar(g_player, var_button, IN_BACK | IN_MOVELEFT);
    ObserveGunsMovement(g_player);
    ObserveGunsMovement(g_player);
    Check(g_guns_respawn[g_player] == GunsRespawnQueued && !g_snapshot_callbacks,
        "observed movement queues one delayed death outside PostThink");
    set_task(0.1, "PollDelayedRespawn");
}

public ObserveTestDeath(player)
{
    if (!g_delayed_case || player != g_player) { return HC_CONTINUE; }
    g_deaths++;
    Check(g_guns_respawn[player] == GunsRespawnRequested && !g_snapshot_callbacks,
        "death follows the helper request and precedes the snapshot");
    return HC_CONTINUE;
}

public ObserveTestSpawn(player)
{
    if (!g_delayed_case || player != g_player || g_guns_respawn[player] != GunsRespawnRequested
        || !is_user_alive(player)) { return HC_CONTINUE; }
    g_native_spawns++;
    Check(g_deaths == 1 && !g_snapshot_callbacks, "native scheduler spawned the player after death");

    // This isolated observer regression supplies a known inventory at native spawn.
    Check(bool:rg_remove_all_items(player), "clear base inventory on test spawn");
    Check(rg_give_item(player, "weapon_ak47") > 0 && rg_give_item(player, "weapon_deagle") > 0, "seed expected loadout");
    rg_set_user_bpammo(player, WEAPON_AK47, 90);
    rg_set_user_bpammo(player, WEAPON_DEAGLE, 35);
    ObserveGunsSpawn(player);
    return HC_CONTINUE;
}

public PollDelayedRespawn()
{
    // Fake clients need their native PreThink path pumped for the scheduler.
    if (!is_user_alive(g_player)) { ExecuteHamB(Ham_Player_PreThink, g_player); }
    if (g_snapshot_callbacks || get_gametime() - g_last_selection_at >= 13.0)
    {
        FinishDelayedMovementCase();
        return;
    }
    set_task(0.1, "PollDelayedRespawn");
}

FinishDelayedMovementCase()
{
    Check(g_deaths == 1 && g_native_spawns == 1 && g_snapshot_callbacks == 1 && is_user_alive(g_player),
        "one real death, native respawn and snapshot callback");
    ObserveGunsLoadout(g_player);
    Check(g_snapshot_callbacks == 1, "duplicate loadout observation cannot request another snapshot");
    server_print("FIXTURE_DELAYED_RESPAWN delay=%.2f deaths=%d native_spawns=%d snapshots=%d",
        g_movement_delay, g_deaths, g_native_spawns, g_snapshot_callbacks);
    server_print("FIXTURE_AMMO_SMOKE=%s failures=%d checks=%d synthetic=1 cases=3",
        g_failures ? "failed" : "passed", g_failures, g_checks);
}
