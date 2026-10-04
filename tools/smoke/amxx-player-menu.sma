#include <amxmodx>
#include <reapi>
#include <fakemeta>

// The product is compiled separately; only this synthetic client is human.
#define plugin_init ProductPluginInit
#define is_user_bot(%1) FixtureIsBot(%1)
#define menu_display(%1,%2) FixtureMenuDisplay(%1,%2)
#define client_print FixturePrint
#include <goldsrcops_weapon_selection.sma>
#undef plugin_init
#undef is_user_bot
#undef menu_display
#undef client_print

new g_client;
new bool:g_human = true;
new g_failures;
new g_displays;
new g_last_menu;
new g_last_text[512];
new g_prints;
new g_maps_calls;
new g_timeleft_calls;

public plugin_init()
{
    ProductPluginInit();
    register_srvcmd("goldsrcops_player_menu_smoke", "RunPlayerMenuSmoke");
    register_clcmd("say /maps", "FixtureMaps");
    register_clcmd("say /timeleft", "FixtureTimeLeft");
}

bool:FixtureIsBot(id) { return !(g_human && id == g_client) && is_user_bot(id); }
FixtureMenuDisplay(id, menu)
{
    g_displays++;
    g_last_menu = menu;
    return menu_display(id, menu);
}
FixturePrint(id, type, const message[], any:...)
{
    #pragma unused id, type
    g_prints++;
    vformat(g_last_text, charsmax(g_last_text), message, 4);
    CheckMenu(strlen(g_last_text) <= 190 && contain(g_last_text, "ML_NOTFOUND") < 0, "bounded translated chat");
    return strlen(g_last_text);
}
public FixtureMaps(id)
{
    if (id == g_client) { g_maps_calls++; }
    return PLUGIN_HANDLED;
}
public FixtureTimeLeft(id)
{
    if (id == g_client) { g_timeleft_calls++; }
    return PLUGIN_HANDLED;
}

CheckMenu(bool:passed, const label[])
{
    if (!passed) { g_failures++; server_print("PLAYER_MENU_ASSERTION_FAILED %s", label); }
}

bool:SameUtf8Bytes(const left[], const right[])
{
    // Menu info and formatting natives can extend UTF-8 bytes differently.
    for (new index = 0; ; index++)
    {
        if ((left[index] & 0xff) != (right[index] & 0xff)) { return false; }
        if (!left[index]) { return true; }
    }
    return false;
}

public RunPlayerMenuSmoke()
{
    if (g_client || get_playersnum()) { return PLUGIN_HANDLED; }
    g_client = engfunc(EngFunc_CreateFakeClient, "Player menu fixture");
    if (!g_client) { set_fail_state("Fixture slot unavailable."); return PLUGIN_HANDLED; }
    new rejected[128];
    dllfunc(DLLFunc_ClientConnect, g_client, "Player menu fixture", "127.0.0.1", rejected);
    dllfunc(DLLFunc_ClientPutInServer, g_client);
    rg_set_user_team(g_client, TEAM_CT);
    set_member(g_client, m_iJoiningState, JOINED);
    set_pcvar_num(g_enabled, 1);
    new id = g_client;
    set_user_info(id, "lang", "");
    CheckMenu(PlayerLanguage(id) == 0, "unset language defaults to ru");
    set_user_info(id, "lang", "ru");
    CheckMenu(PlayerLanguage(id) == 0, "existing ru respected");
    set_user_info(id, "lang", "en");
    CheckMenu(PlayerLanguage(id) == 1, "existing en respected");
    set_user_info(id, "lang", "de");
    CheckMenu(PlayerLanguage(id) == 1, "unsupported language uses en");
    for (new language = 0; language < 2; language++)
    {
        OnLanguageSelected(id, g_language_menu, language);
        CheckMenu(PlayerLanguage(id) == language && g_last_menu == g_player_menu[language], "manual language opens translated hub");
        new info[8], name[128], expected[128], access, callback;
        for (new item = 0; item < sizeof PLAYER_MENU_KEYS; item++)
        {
            menu_item_getinfo(g_player_menu[language], item, access, info, charsmax(info), name, charsmax(name), callback);
            formatex(expected, charsmax(expected), "%L", PLAYER_LANGUAGES[language], PLAYER_MENU_KEYS[item]);
            CheckMenu(SameUtf8Bytes(name, expected) && contain(name, "ML_NOTFOUND") < 0, "localized menu item");
        }
        rg_round_respawn(id);
        OnPlayerMenuSelected(id, g_player_menu[language], 0);
        CheckMenu(g_last_menu == g_menu[language] && !task_exists(WELCOME_TASK_BASE + id), "hub weapons and welcome suppression");
        OnWeaponSelected(id, g_menu[language], 1);
        CheckMenu(g_last_menu == g_pistol_menu[language] && g_choice[id] == 1, "localized primary callback");
        OnPistolSelected(id, g_pistol_menu[language], 2);
        CheckMenu(g_pistol_choice[id] == 2, "localized pistol callback");
        ResetMapStats(id);
        g_saved_ready[id] = false;
        g_saved_blocked[id] = true;
        OnPlayerMenuSelected(id, g_player_menu[language], 1);
        CheckMenu(g_prints > 0 && g_last_menu == g_player_menu[language], "stats action returns to hub");
        OnPlayerMenuSelected(id, g_player_menu[language], 2);
        OnPlayerMenuSelected(id, g_player_menu[language], 3);
        new prints = g_prints;
        OnPlayerMenuSelected(id, g_player_menu[language], 1);
        CheckMenu(g_prints == prints, "menu does not bypass stats cooldown");
        OnPlayerMenuSelected(id, g_player_menu[language], 4);
        OnPlayerMenuSelected(id, g_player_menu[language], 5);
        OnPlayerMenuSelected(id, g_player_menu[language], 6);
        CheckMenu(g_last_menu == g_language_menu, "language action");
        new displays = g_displays;
        OnPlayerMenuSelected(id, g_player_menu[1 - language], 0);
        OnPlayerMenuSelected(id, g_player_menu[language], MENU_EXIT);
        OnPlayerMenuSelected(id, g_player_menu[language], 7);
        OnLanguageSelected(id, g_language_menu, MENU_EXIT);
        CheckMenu(g_displays == displays, "stale invalid and cancelled callbacks do not reopen");
        g_saved_ready[id] = true;
        g_saved_kills[id] = STATS_COUNTER_LIMIT;
        g_saved_deaths[id] = 0;
        PrintPlayerRank(id);
        ResetMapStats(id);
        ShowMapStats(id);
        g_saved_ready[id] = false;
    }
    CheckMenu(g_maps_calls == 2 && g_timeleft_calls == 2, "fixed map commands reach AMXX hooks");
    new displays = g_displays;
    set_pcvar_num(g_enabled, 0);
    OpenPlayerMenu(id);
    OnPlayerMenuSelected(id, g_player_menu[1], 4);
    CheckMenu(g_displays == displays && g_maps_calls == 2, "disabled menu does not dispatch");
    set_pcvar_num(g_enabled, 1);
    g_human = false;
    OpenPlayerMenu(id);
    CheckMenu(g_displays == displays, "bots excluded");
    g_human = true;
    rg_set_user_team(id, TEAM_SPECTATOR);
    OpenWeapons(id);
    CheckMenu(g_displays == displays, "spectator cannot select weapons");
    OpenPlayerMenu(id);
    CheckMenu(g_displays == displays + 1, "spectator can use read-only hub");
    client_disconnected(id, false, rejected, charsmax(rejected));
    client_putinserver(id);
    CheckMenu(g_language_override[id] == 0 && PlayerLanguage(id) == 1, "reconnect clears override without changing client preference");
    server_print("PLAYER_MENU_SMOKE=%s failures=%d languages=2 map_hooks=delegated", g_failures ? "failed" : "passed", g_failures);
    return PLUGIN_HANDLED;
}
