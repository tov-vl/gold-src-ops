#include <amxmodx>
#include <reapi>
#include <fakemeta>

// Only this separately compiled synthetic fixture bypasses the bot exclusion.
#define plugin_init ProductMapPluginInit
#define is_user_bot(%1) FixtureMapIsBot(%1)
#define engclient_cmd FixtureMapCommand
#include <goldsrcops_map_menu.sma>
#undef plugin_init
#undef is_user_bot
#undef engclient_cmd

new g_fixture_map_id;
new g_map_failures;
new g_dispatch_count;
new g_last_vote;

public plugin_init()
{
    ProductMapPluginInit();
    register_srvcmd("goldsrcops_map_menu_smoke", "RunMapMenuSmoke");
}

bool:FixtureMapIsBot(id)
{
    return id != g_fixture_map_id && is_user_bot(id);
}

FixtureMapCommand(id, const command[], const argument[] = "")
{
    g_dispatch_count++;
    g_last_vote = str_to_num(argument);
    engclient_cmd(id, command, argument);
}

MapCheck(bool:passed, const label[])
{
    if (!passed)
    {
        g_map_failures++;
        server_print("MAP_MENU_ASSERTION_FAILED %s", label);
    }
}

WriteFixtureCycle(bool:reverse = false, count = -1)
{
    if (count < 0) count = sizeof MAP_NAMES;
    new file = fopen(MAP_CYCLE, "wt");
    for (new index = 0; index < count; index++)
    {
        fprintf(file, "%s^n", MAP_NAMES[reverse ? sizeof MAP_NAMES - index - 1 : index]);
    }
    fclose(file);
}

public RunMapMenuSmoke()
{
    if (g_fixture_map_id || get_playersnum())
    {
        server_print("MAP_MENU_SMOKE=refused; requires a fresh empty fixture");
        return PLUGIN_HANDLED;
    }
    g_fixture_map_id = engfunc(EngFunc_CreateFakeClient, "Map menu fixture");
    if (!g_fixture_map_id)
    {
        server_print("MAP_MENU_SMOKE=failed; no fixture slot");
        return PLUGIN_HANDLED;
    }
    new rejected[128];
    dllfunc(DLLFunc_ClientConnect, g_fixture_map_id, "Map menu fixture", "127.0.0.1", rejected);
    dllfunc(DLLFunc_ClientPutInServer, g_fixture_map_id);
    rg_set_user_team(g_fixture_map_id, TEAM_CT);
    set_member(g_fixture_map_id, m_iJoiningState, JOINED);
    set_pcvar_num(g_maps_enabled, 1);
    set_cvar_string("mapcyclefile", MAP_CYCLE);
    set_cvar_string("mp_vote_flags", "km");
    WriteFixtureCycle();
    MapCheck(MapCycleReady(), "exact five-map cycle");
    MapCheck(MapVotingEnabled(), "native map policy");
    // Isolate the native player-count gate from the normal elapsed-time gate.
    set_cvar_num("mp_votemap_min_time", 0);
    OnMapSelected(g_fixture_map_id, g_maps_menu, 1);
    MapCheck(g_dispatch_count == 1 && g_last_vote == 2, "one-based native map ID");
    MapCheck(get_member(g_fixture_map_id, m_iMapVote) == 0, "native single-player vote refusal retained");

    OnMapSelected(g_fixture_map_id, g_maps_menu, MENU_EXIT);
    OnMapSelected(g_fixture_map_id, g_maps_menu, sizeof MAP_NAMES);
    OnMapSelected(g_fixture_map_id, g_maps_menu + 1, 1);
    OnMapSelected(g_fixture_map_id, g_maps_menu, 0);
    MapCheck(g_dispatch_count == 1, "cancel invalid menu and current map do not vote");
    MapCheck(MapAvailable(g_fixture_map_id, g_maps_menu, 0) == ITEM_DISABLED, "current map disabled");
    MapCheck(MapAvailable(g_fixture_map_id, g_maps_menu, 1) == ITEM_ENABLED, "other map enabled");

    WriteFixtureCycle(true);
    OnMapSelected(g_fixture_map_id, g_maps_menu, 1);
    MapCheck(!MapCycleReady() && g_dispatch_count == 1, "stale reordered cycle refused");
    WriteFixtureCycle(false, 4);
    MapCheck(!MapCycleReady(), "truncated cycle refused");
    WriteFixtureCycle();
    write_file(MAP_CYCLE, "de_dust2");
    MapCheck(!MapCycleReady(), "extra map refused");
    WriteFixtureCycle();
    set_cvar_string("mapcyclefile", "mapcycle.txt");
    MapCheck(!MapCycleReady(), "other cycle refused");
    set_cvar_string("mapcyclefile", MAP_CYCLE);
    set_cvar_string("mp_vote_flags", "k");
    OnMapSelected(g_fixture_map_id, g_maps_menu, 1);
    MapCheck(!MapVotingEnabled() && g_dispatch_count == 1, "disabled native voting refused");
    set_cvar_string("mp_vote_flags", "km");
    set_pcvar_num(g_maps_enabled, 0);
    OnMapSelected(g_fixture_map_id, g_maps_menu, 1);
    ShowTimeLeft(g_fixture_map_id);
    MapCheck(g_dispatch_count == 1, "disabled addon makes no engine request");
    set_pcvar_num(g_maps_enabled, 1);
    rg_set_user_team(g_fixture_map_id, TEAM_SPECTATOR);
    OnMapSelected(g_fixture_map_id, g_maps_menu, 1);
    MapCheck(g_dispatch_count == 1, "spectator cannot vote through menu");
    ShowTimeLeft(g_fixture_map_id);
    MapCheck(g_dispatch_count == 2 && g_last_vote == 0, "spectator timeleft forwarded");
    MapCheck(!IsMapPlayer(0), "disconnected slot refused");
    set_task(1.0, "FinishMapMenuSmoke");
    return PLUGIN_HANDLED;
}

public FinishMapMenuSmoke()
{
    new second = engfunc(EngFunc_CreateFakeClient, "Second map fixture");
    if (!second)
    {
        MapCheck(false, "second fixture slot");
    }
    else
    {
        new rejected[128];
        dllfunc(DLLFunc_ClientConnect, second, "Second map fixture", "127.0.0.1", rejected);
        dllfunc(DLLFunc_ClientPutInServer, second);
        rg_set_user_team(second, TEAM_CT);
        set_member(second, m_iJoiningState, JOINED);
        rg_set_user_team(g_fixture_map_id, TEAM_CT);
        set_member(g_fixture_map_id, m_flNextVoteTime, 0.0);
        OnMapSelected(g_fixture_map_id, g_maps_menu, 1);
        MapCheck(get_member(g_fixture_map_id, m_iMapVote) == 2, "native engine records requested map vote");
        new map[32];
        get_mapname(map, charsmax(map));
        MapCheck(bool:equal(map, "de_dust2"), "one of two votes does not force changelevel");
        MapCheck(!IsMapPlayer(second), "ordinary bot excluded");
    }
    server_print("MAP_MENU_SMOKE=%s failures=%d synthetic_clients=2",
        g_map_failures ? "failed" : "passed", g_map_failures);
}
