#include <amxmodx>
#include <reapi>
#include <fakemeta>

// The product is compiled separately; only this synthetic client is human.
#define plugin_init ProductPluginInit
#define is_user_bot(%1) FixtureIsBot(%1)
#define menu_display(%1,%2) FixtureMenuDisplay(%1,%2)
#define client_print FixturePrint
#define show_dhudmessage FixtureHud
#define set_dhudmessage FixtureHudParameters
#include <goldsrcops_weapon_selection.sma>
#undef plugin_init
#undef is_user_bot
#undef menu_display
#undef client_print
#undef show_dhudmessage
#undef set_dhudmessage

new g_client;
new bool:g_human = true;
new g_failures;
new g_displays;
new g_last_menu;
new g_last_text[512];
new g_prints;
new g_maps_calls;
new g_timeleft_calls;
new g_hud_messages;
new g_hud_parts;
new g_last_hud[256];

FixtureHudParameters(red, green, blue, Float:x, Float:y, effects, Float:fxtime, Float:hold, Float:fadein, Float:fadeout)
{
    CheckMenu(hold == 1.0 && fadein == 0.0 && fadeout == 0.0 && effects == 0, "director frame expires within one second");
    CheckMenu(x == 0.02 && y == (g_hud_parts % 2 == 0 ? 0.22 : 0.29), "statistics and rank occupy separate positions");
    set_dhudmessage(red, green, blue, x, y, effects, fxtime, hold, fadein, fadeout);
}
FixtureHud(id, const message[], any:...)
{
    CheckMenu(id == g_client, "director HUD remains private");
    new part[256];
    vformat(part, charsmax(part), message, 3);
    CheckMenu(strlen(part) < 128 && contain(part, "ML_NOTFOUND") < 0, "each translated director message fits without truncation");
    if (g_hud_parts++ % 2 == 0)
    {
        copy(g_last_hud, charsmax(g_last_hud), part);
    }
    else
    {
        add(g_last_hud, charsmax(g_last_hud), "^n");
        add(g_last_hud, charsmax(g_last_hud), part);
        g_hud_messages++;
    }
    return show_dhudmessage(id, "%s", part);
}

public plugin_init()
{
    ProductPluginInit();
    register_srvcmd("goldsrcops_player_menu_smoke", "RunPlayerMenuSmoke");
    register_clcmd("say /maps", "FixtureMaps");
    register_clcmd("say /timeleft", "FixtureTimeLeft");
    register_menucmd(register_menuid("HUD fixture external"), MENU_KEY_1, "FixtureExternalMenuSelected");
}

public FixtureExternalMenuSelected(id, key)
{
    #pragma unused id, key
    return PLUGIN_HANDLED;
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
        CheckMenu(g_last_menu == g_settings_menu[language][0], "settings action opens translated settings");
        new saved[192];
        formatex(saved, charsmax(saved), "[GoldSrcOps] %L", PLAYER_LANGUAGES[language], "GS_SETTINGS_SESSION");
        CheckMenu(SameUtf8Bytes(g_last_text, saved), "unknown identity displays session-only preferences");
        OnSettingsSelected(id, g_settings_menu[language][0], 0);
        CheckMenu(g_last_menu == g_menu[language], "settings dispatches weapons");
        OnSettingsSelected(id, g_settings_menu[language][0], 3);
        CheckMenu(g_last_menu == g_player_menu[language], "settings back returns to hub");
        OnSettingsSelected(id, g_settings_menu[language][0], 1);
        CheckMenu(g_last_menu == g_language_menu, "language action");
        new displays = g_displays;
        OnPlayerMenuSelected(id, g_player_menu[1 - language], 0);
        OnPlayerMenuSelected(id, g_player_menu[language], MENU_EXIT);
        OnPlayerMenuSelected(id, g_player_menu[language], 7);
        OnLanguageSelected(id, g_language_menu, MENU_EXIT);
        OnSettingsSelected(id, g_settings_menu[1 - language][0], 0);
        OnSettingsSelected(id, g_settings_menu[language][0], 4);
        OnSettingsSelected(id, g_settings_menu[language][0], MENU_EXIT);
        OnSettingsSelected(id, g_settings_menu[language][0], MENU_TIMEOUT);
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
    OpenPlayerSettings(id);
    OnSettingsSelected(id, g_settings_menu[1][0], 1);
    OnPlayerMenuSelected(id, g_player_menu[1], 4);
    CheckMenu(g_displays == displays && g_maps_calls == 2, "disabled menu does not dispatch");
    set_pcvar_num(g_enabled, 1);
    g_human = false;
    OpenPlayerMenu(id);
    OpenPlayerSettings(id);
    CheckMenu(g_displays == displays, "bots excluded");
    g_human = true;
    rg_set_user_team(id, TEAM_SPECTATOR);
    OpenWeapons(id);
    CheckMenu(g_displays == displays, "spectator cannot select weapons");
    OpenPlayerMenu(id);
    CheckMenu(g_displays == displays + 1, "spectator can use read-only hub");
    OpenPlayerSettings(id);
    CheckMenu(g_last_menu == g_settings_menu[1][0], "spectator can inspect settings");
    displays = g_displays;
    OnSettingsSelected(id, g_settings_menu[1][0], 0);
    CheckMenu(g_displays == displays, "settings does not grant spectator weapons");
    RunHudChecks(id);
    g_hud_disabled[id] = true;
    client_disconnected(id, false, rejected, charsmax(rejected));
    client_putinserver(id);
    CheckMenu(g_language_override[id] == 0 && PlayerLanguage(id) == 1, "reconnect clears override without changing client preference");
    CheckMenu(!g_hud_disabled[id] && !g_hud_visible[id], "reconnect resets connection-only HUD setting");
    server_print("PLAYER_HUD_SMOKE=%s failures=%d languages=2 storage_writes=none", g_failures ? "failed" : "passed", g_failures);
    server_print("PLAYER_MENU_SMOKE=%s failures=%d languages=2 map_hooks=delegated", g_failures ? "failed" : "passed", g_failures);
    return PLUGIN_HANDLED;
}

RunHudChecks(id)
{
    CheckMenu(bool:task_exists(HUD_TASK_ID), "single periodic HUD task registered");
    rg_set_user_team(id, TEAM_CT);
    rg_round_respawn(id);
    remove_task(WELCOME_TASK_BASE + id);
    menu_cancel(id);
    show_menu(id, 0, "");
    RunMenuCloseChecks(id);
    g_map_kills[id] = STATS_COUNTER_LIMIT;
    g_map_deaths[id] = STATS_COUNTER_LIMIT;
    g_kill_streak[id] = STATS_COUNTER_LIMIT;
    for (new language = 0; language < 2; language++)
    {
        g_language_override[id] = language + 1;
        g_saved_ready[id] = true;
        g_saved_kills[id] = 24;
        new expected[256], rank_text[160], current[32], next[32];
        formatex(current, charsmax(current), "%L", PLAYER_LANGUAGES[language], RANK_KEYS[0]);
        formatex(next, charsmax(next), "%L", PLAYER_LANGUAGES[language], RANK_KEYS[1]);
        formatex(rank_text, charsmax(rank_text), "%L", PLAYER_LANGUAGES[language], "GS_HUD_RANK_PROGRESS", current, 1, next);
        formatex(expected, charsmax(expected), "%L^n%L^n%s", PLAYER_LANGUAGES[language], "GS_HUD_MAP",
            STATS_COUNTER_LIMIT, STATS_COUNTER_LIMIT, PLAYER_LANGUAGES[language], "GS_HUD_STREAK", STATS_COUNTER_LIMIT, rank_text);
        new messages = g_hud_messages;
        UpdatePlayerHuds();
        CheckMenu(g_hud_messages == messages + 1 && SameUtf8Bytes(g_last_hud, expected) && g_hud_visible[id], "bounded RU/EN map totals and saved rank progress");
        CheckMenu(strlen(g_last_hud) < 256 && contain(g_last_hud, "ML_NOTFOUND") < 0, "HUD formatting bounded");
        g_saved_kills[id] = STATS_COUNTER_LIMIT;
        UpdatePlayerHuds();
        formatex(current, charsmax(current), "%L", PLAYER_LANGUAGES[language], RANK_KEYS[4]);
        formatex(rank_text, charsmax(rank_text), "%L", PLAYER_LANGUAGES[language], "GS_HUD_RANK_HIGHEST", current);
        CheckMenu(contain(g_last_hud, rank_text) >= 0, "highest rank has no invalid next tier");
        g_saved_ready[id] = false;
        UpdatePlayerHuds();
        formatex(rank_text, charsmax(rank_text), "%L", PLAYER_LANGUAGES[language], "GS_HUD_RANK_UNAVAILABLE");
        CheckMenu(contain(g_last_hud, rank_text) >= 0, "unavailable saved rank is not invented from map score");
        OpenPlayerSettings(id);
        messages = g_hud_messages;
        UpdatePlayerHuds();
        CheckMenu(g_hud_messages == messages && !g_hud_visible[id], "open menu suppresses HUD");
        OnSettingsSelected(id, g_settings_menu[language][0], 2);
        CheckMenu(g_hud_disabled[id] && g_last_menu == g_settings_menu[language][1], "toggle opens private off-state menu");
        OnSettingsSelected(id, g_settings_menu[language][0], 2);
        CheckMenu(g_hud_disabled[id], "stale toggle cannot invert current setting");
        menu_cancel(id);
        show_menu(id, 0, "");
        UpdatePlayerHuds();
        CheckMenu(g_hud_messages == messages, "disabled HUD stays hidden without a menu");
        OnSettingsSelected(id, g_settings_menu[language][1], 2);
        CheckMenu(!g_hud_disabled[id], "toggle restores HUD");
        menu_cancel(id);
        show_menu(id, 0, "");
    }
    UpdatePlayerHuds();
    new messages = g_hud_messages;
    set_pcvar_num(g_enabled, 0);
    UpdatePlayerHuds();
    UpdatePlayerHuds();
    CheckMenu(g_hud_messages == messages && !g_hud_visible[id], "global disable stops director refresh");
    set_pcvar_num(g_enabled, 1);
    g_human = false;
    UpdatePlayerHuds();
    CheckMenu(g_hud_messages == messages, "bots receive no player HUD");
    g_human = true;
    rg_set_user_team(id, TEAM_SPECTATOR);
    UpdatePlayerHuds();
    CheckMenu(g_hud_messages == messages, "spectators receive no player HUD");
    rg_set_user_team(id, TEAM_CT);
    set_entvar(id, var_deadflag, DEAD_DEAD);
    UpdatePlayerHuds();
    CheckMenu(g_hud_messages == messages, "dead players receive no player HUD");
    rg_round_respawn(id);
    remove_task(WELCOME_TASK_BASE + id);
    UpdatePlayerHuds();
    CheckMenu(g_hud_messages == messages + 1, "HUD resumes after respawn");
    HidePlayerHud(0);
    HidePlayerHud(MAX_PLAYERS + 1);
}

RunMenuCloseChecks(id)
{
    for (new language = 0; language < 2; language++)
    {
        g_language_override[id] = language + 1;
        for (new attempt = 0; attempt < 3; attempt++)
        {
            OpenPlayerMenu(id);
            new messages = g_hud_messages;
            UpdatePlayerHuds();
            CheckMenu(g_hud_messages == messages, "hub suppresses HUD while open");
            FixtureCloseMenu(id);
            new menu, keys;
            get_user_menu(id, menu, keys);
            CheckMenu(menu == 0 && keys != 0, "native menu state retains exit key mask without an active menu");
            UpdatePlayerHuds();
            CheckMenu(g_hud_messages == messages + 1, "HUD resumes after hub exit with stale keys");
        }
        OpenPlayerSettings(id);
        FixtureCloseMenu(id);
        OnSettingsSelected(id, g_settings_menu[language][0], 2);
        FixtureCloseMenu(id);
        new messages = g_hud_messages;
        UpdatePlayerHuds();
        CheckMenu(g_hud_disabled[id] && g_hud_messages == messages, "HUD stays off after settings exit with stale keys");
        OpenPlayerSettings(id);
        FixtureCloseMenu(id);
        OnSettingsSelected(id, g_settings_menu[language][1], 2);
        FixtureCloseMenu(id);
        UpdatePlayerHuds();
        CheckMenu(!g_hud_disabled[id] && g_hud_messages == messages + 1, "HUD returns after toggle and exit with stale keys");
        OpenWeapons(id);
        FixtureCloseMenu(id);
        OnWeaponSelected(id, g_menu[language], 0);
        messages = g_hud_messages;
        UpdatePlayerHuds();
        CheckMenu(g_hud_messages == messages, "weapon submenu still suppresses HUD");
        FixtureCloseMenu(id);
        OnPistolSelected(id, g_pistol_menu[language], 0);
        UpdatePlayerHuds();
        CheckMenu(g_hud_messages == messages + 1, "HUD resumes after weapon selection with stale keys");
        OpenPlayerSettings(id);
        FixtureCloseMenu(id);
        OnSettingsSelected(id, g_settings_menu[language][0], 3);
        messages = g_hud_messages;
        UpdatePlayerHuds();
        CheckMenu(g_hud_messages == messages, "Back keeps HUD hidden under the reopened hub");
        FixtureCloseMenu(id);
        UpdatePlayerHuds();
        CheckMenu(g_hud_messages == messages + 1, "HUD returns after Back and hub exit");
        show_menu(id, MENU_KEY_1, "HUD fixture external", -1, "HUD fixture external");
        messages = g_hud_messages;
        UpdatePlayerHuds();
        CheckMenu(g_hud_messages == messages, "external legacy menu suppresses HUD");
        FixtureCloseMenu(id);
        UpdatePlayerHuds();
        CheckMenu(g_hud_messages == messages + 1, "HUD resumes after external menu selection");
    }
}

FixtureCloseMenu(id)
{
    // AMXX's menuselect clears menu ids, but leaves keys. amxclient_cmd bypasses that handler.
    // Use real menu natives to recreate that state; do not stub the product's menu query.
    new menu, keys;
    get_user_menu(id, menu, keys);
    menu_cancel(id);
    show_menu(id, keys, "");
}
