#include <amxmodx>
#include <reapi>
#include <fakemeta>

// Only this separately compiled synthetic fixture bypasses the bot exclusion.
#define plugin_init ProductMapPluginInit
#define is_user_bot(%1) FixtureMapIsBot(%1)
#define engclient_cmd FixtureMapCommand
#define client_print FixtureMapPrint
#include <goldsrcops_map_menu.sma>
#undef plugin_init
#undef is_user_bot
#undef engclient_cmd
#undef client_print

new g_fixture_map_id;
new g_map_failures;
new g_dispatch_count;
new g_last_vote;
new g_map_text[256];

FixtureMapPrint(id, type, const message[], any:...)
{
    #pragma unused id, type
    vformat(g_map_text, charsmax(g_map_text), message, 4);
    MapCheck(strlen(g_map_text) <= 190 && contain(g_map_text, "ML_NOTFOUND") < 0, "bounded translated chat");
    return strlen(g_map_text);
}

ChooseFixtureLanguage(language)
{
    if (callfunc_begin("FixtureChooseMapLanguage", "map_language_provider_smoke.amxx") != 1)
    {
        MapCheck(false, "real provider fixture available");
        return;
    }
    callfunc_push_int(g_fixture_map_id);
    callfunc_push_int(language);
    callfunc_end();
}

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
    set_user_info(g_fixture_map_id, "lang", "");
    MapCheck(MapLanguage(g_fixture_map_id) == 0, "unset uses ru");
    set_user_info(g_fixture_map_id, "lang", "en");
    MapCheck(MapLanguage(g_fixture_map_id) == 1, "en preference respected");
    set_user_info(g_fixture_map_id, "lang", "de");
    MapCheck(MapLanguage(g_fixture_map_id) == 1, "unsupported uses en");
    if (LibraryExists("goldsrcops_player_menu", LibType_Library))
    {
        MapCheck(goldsrcops_player_language(0) == -1 && goldsrcops_player_language(33) == -1, "invalid native slots refused");
        ChooseFixtureLanguage(0);
        MapCheck(MapLanguage(g_fixture_map_id) == 0, "manual ru overrides unsupported preference across plugins");
        ChooseFixtureLanguage(1);
        MapCheck(MapLanguage(g_fixture_map_id) == 1, "manual en shared across plugins");
        ChooseFixtureLanguage(-1);
        set_user_info(g_fixture_map_id, "lang", "ru");
        MapCheck(MapLanguage(g_fixture_map_id) == 0, "disabled provider falls back to client");
        ChooseFixtureLanguage(0);
    }
    set_user_info(g_fixture_map_id, "lang", "ru");
    for (new language = 0; language < 2; language++)
    {
        new info[8], text[96], expected[96], access, callback;
        menu_item_getinfo(g_maps_menu[language], 0, access, info, charsmax(info), text, charsmax(text), callback);
        formatex(expected, charsmax(expected), "%L", MAP_LANGUAGES[language], "GS_MAP_CURRENT", MAP_NAMES[0]);
        for (new index = 0; index <= strlen(expected); index++)
        {
            MapCheck((text[index] & 0xff) == (expected[index] & 0xff), "translated current-map UTF-8 bytes");
        }
        MapCheck(contain(text, "ML_NOTFOUND") < 0, "dictionary loaded");
    }
    set_cvar_string("mapcyclefile", MAP_CYCLE);
    set_cvar_string("mp_vote_flags", "km");
    WriteFixtureCycle();
    MapCheck(MapCycleReady(), "exact five-map cycle");
    MapCheck(MapVotingEnabled(), "native map policy");
    // Isolate the native player-count gate from the normal elapsed-time gate.
    set_cvar_num("mp_votemap_min_time", 0);
    OnMapSelected(g_fixture_map_id, g_maps_menu[0], 1);
    MapCheck(g_dispatch_count == 1 && g_last_vote == 2, "one-based native map ID");
    MapCheck(get_member(g_fixture_map_id, m_iMapVote) == 0, "native single-player vote refusal retained");

    OnMapSelected(g_fixture_map_id, g_maps_menu[0], MENU_EXIT);
    OnMapSelected(g_fixture_map_id, g_maps_menu[0], sizeof MAP_NAMES);
    OnMapSelected(g_fixture_map_id, g_maps_menu[1], 1);
    OnMapSelected(g_fixture_map_id, g_maps_menu[0], 0);
    MapCheck(g_dispatch_count == 1, "cancel invalid menu and current map do not vote");
    MapCheck(MapAvailable(g_fixture_map_id, g_maps_menu[0], 0) == ITEM_DISABLED, "current map disabled");
    MapCheck(MapAvailable(g_fixture_map_id, g_maps_menu[0], 1) == ITEM_ENABLED, "other map enabled");

    WriteFixtureCycle(true);
    OnMapSelected(g_fixture_map_id, g_maps_menu[0], 1);
    new expected[256];
    formatex(expected, charsmax(expected), "[GoldSrcOps] %L", "ru", "GS_MAP_UNAVAILABLE");
    MapCheck(bool:equal(g_map_text, expected), "localized unavailable message");
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
    OnMapSelected(g_fixture_map_id, g_maps_menu[0], 1);
    MapCheck(!MapVotingEnabled() && g_dispatch_count == 1, "disabled native voting refused");
    set_cvar_string("mp_vote_flags", "km");
    set_pcvar_num(g_maps_enabled, 0);
    OnMapSelected(g_fixture_map_id, g_maps_menu[0], 1);
    ShowTimeLeft(g_fixture_map_id);
    MapCheck(g_dispatch_count == 1, "disabled addon makes no engine request");
    set_pcvar_num(g_maps_enabled, 1);
    rg_set_user_team(g_fixture_map_id, TEAM_SPECTATOR);
    OnMapSelected(g_fixture_map_id, g_maps_menu[0], 1);
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
        if (LibraryExists("goldsrcops_player_menu", LibType_Library)) { ChooseFixtureLanguage(1); }
        else { set_user_info(g_fixture_map_id, "lang", "en"); }
        OnMapSelected(g_fixture_map_id, g_maps_menu[1], 1);
        new expected[256];
        formatex(expected, charsmax(expected), "[GoldSrcOps] %L", "en", "GS_MAP_REQUESTED", MAP_NAMES[1]);
        MapCheck(bool:equal(g_map_text, expected), "localized requested message");
        MapCheck(get_member(g_fixture_map_id, m_iMapVote) == 2, "native engine records requested map vote");
        new map[32];
        get_mapname(map, charsmax(map));
        MapCheck(bool:equal(map, "de_dust2"), "one of two votes does not force changelevel");
        MapCheck(!IsMapPlayer(second), "ordinary bot excluded");
    }
    server_print("MAP_MENU_SMOKE=%s failures=%d synthetic_clients=2",
        g_map_failures ? "failed" : "passed", g_map_failures);
}
