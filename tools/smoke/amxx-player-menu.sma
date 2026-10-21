#include <amxmodx>
#include <reapi>
#include <fakemeta>

// The product is compiled separately; only this synthetic client is human.
#define plugin_init ProductPluginInit
#define menu_setprop FixtureMenuSetProp
#define is_user_bot(%1) FixtureIsBot(%1)
#define menu_display(%1,%2) FixtureMenuDisplay(%1,%2)
#define client_print FixturePrint
#define client_print_color FixtureColorPrint
#define show_motd FixtureMotd
#define show_dhudmessage FixtureHud
#define set_dhudmessage FixtureHudParameters
#include <goldsrcops_weapon_selection.sma>
#undef plugin_init
#undef menu_setprop
#undef is_user_bot
#undef menu_display
#undef client_print
#undef client_print_color
#undef show_motd
#undef show_dhudmessage
#undef set_dhudmessage

new g_client;
new bool:g_human = true;
new g_failures;
new g_menu_text[MAX_PLAYERS + 1][768];
new Trie:g_menu_titles;
new g_displays;
new g_last_menu;
new g_last_text[512];
new g_previous_text[512];
new g_prints;
new g_motd_count;
new g_motd_text[1536];
new g_motd_title[64];
new g_color_text[192];
new bool:g_collect_help;
new g_help_lines[5][192];
new g_help_line_count;
new g_maps_calls;
new g_timeleft_calls;
new g_hud_messages;
new g_hud_parts;
new g_last_hud[256];
new Float:g_last_hold;
new bool:g_periodic_checks;
new g_periodic_samples[2];
new Float:g_previous_frame_time;
new Float:g_previous_frame_hold;

FixtureHudParameters(red, green, blue, Float:x, Float:y, effects, Float:fxtime, Float:hold, Float:fadein, Float:fadeout)
{
    CheckMenu(hold == 1.5 && fadein == 0.0 && fadeout == 0.0 && effects == 0, "director frame has bounded expiry with no fades");
    CheckMenu(x == 0.02 && y == (g_hud_parts % 2 == 0 ? 0.14 : 0.21), "statistics and rank occupy separate positions above the menu");
    g_last_hold = hold;
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
        if (g_periodic_checks)
        {
            new Float:now = get_gametime();
            if (g_periodic_samples[0] + g_periodic_samples[1] > 0)
            {
                CheckMenu(now + 0.25 < g_previous_frame_time + g_previous_frame_hold, "periodic frame arrives with a quarter-second delivery margin");
            }
            g_previous_frame_time = now;
            g_previous_frame_hold = g_last_hold;
            g_periodic_samples[PlayerLanguage(id)]++;
        }
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


// Synthetic clients have no observable ShowMenu transport; capture the exact native title input.
FixtureMenuSetProp(menu, property, any:...)
{
    if (property == MPROP_TITLE || property == MPROP_EXITNAME)
    {
        new text[384], index;
        while (index < charsmax(text) && (text[index] = getarg(2, index))) { index++; }
        text[index] = 0;
        if (property == MPROP_TITLE)
        {
            new key[16];
            num_to_str(menu, key, charsmax(key));
            TrieSetString(g_menu_titles, key, text);
        }
        return menu_setprop(menu, property, text);
    }
    return menu_setprop(menu, property, getarg(2));
}

FindUtf8Bytes(const text[], const fragment[])
{
    // Native menu/Trie strings and formatted translations may extend UTF-8 bytes differently.
    for (new start = 0; text[start]; start++)
    {
        new offset;
        while (fragment[offset] && text[start + offset]
            && (text[start + offset] & 255) == (fragment[offset] & 255)) { offset++; }
        if (!fragment[offset]) { return start; }
    }
    return -1;
}

CaptureMenuTitle(id, menu)
{
    new key[16];
    num_to_str(menu, key, charsmax(key));
    g_menu_text[id][0] = 0;
    TrieGetString(g_menu_titles, key, g_menu_text[id], charsmax(g_menu_text[]));
}

public plugin_init()
{
    g_menu_titles = TrieCreate();
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
    CaptureMenuTitle(id, menu);
    return menu_display(id, menu);
}
FixtureMotd(id, const text[], const title[])
{
    CheckMenu(id == g_client, "MOTD remains private");
    CheckMenu(strlen(text) < charsmax(g_motd_text) && contain(text, "ML_NOTFOUND") < 0
        && contain(text, "</body></html>") == strlen(text) - 14, "complete translated MOTD fits the byte budget");
    CheckMenu(strlen(title) > 0 && strlen(title) <= 31, "MOTD caption fits the client byte budget");
    g_motd_count++;
    copy(g_motd_text, charsmax(g_motd_text), text);
    copy(g_motd_title, charsmax(g_motd_title), title);
    return show_motd(id, text, title);
}

FixtureColorPrint(id, sender, const message[], any:...)
{
    #pragma unused sender
    new colored[512], plain[192], written;
    vformat(colored, charsmax(colored), message, 4);
    CheckMenu(strlen(colored) <= 190 && contain(colored, "ML_NOTFOUND") < 0, "colored transport respects chat byte budget");
    copy(g_color_text, charsmax(g_color_text), colored);
    for (new index = 0; colored[index] && written < charsmax(plain); index++)
    {
        if (colored[index] != 1 && colored[index] != 4) { plain[written++] = colored[index]; }
    }
    plain[written] = 0;
    return FixturePrint(id, print_chat, "%s", plain);
}

FixturePrint(id, type, const message[], any:...)
{
    #pragma unused id, type
    g_prints++;
    copy(g_previous_text, charsmax(g_previous_text), g_last_text);
    vformat(g_last_text, charsmax(g_last_text), message, 4);
    if (g_collect_help)
    {
        CheckMenu(id == g_client && type == print_chat, "help remains private chat");
        if (g_help_line_count < sizeof g_help_lines)
        {
            copy(g_help_lines[g_help_line_count], charsmax(g_help_lines[]), g_last_text);
        }
        g_help_line_count++;
    }
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

RunChatTokenChecks(id)
{
    new expected[192];
    PlayerChat(id, "[GoldSrcOps] (/help), /rank: 25 kills | 3.");
    formatex(expected, charsmax(expected), "^4[GoldSrcOps]^1 (^4/help^1), ^4/rank^1: %c25%c kills | %c3%c.", 4, 1, 4, 1);
    CheckMenu(equal(g_color_text, expected) != 0, "standalone commands and rank values retain accents beside punctuation");
    PlayerChat(id, "[GoldSrcOps] /rank2 /rank_extra 25kills kills25 1.25");
    CheckMenu(equal(g_color_text, "^4[GoldSrcOps]^1 /rank2 /rank_extra 25kills kills25 1.25") != 0,
        "command and number fragments inside larger tokens remain plain");
    for (new language = 0; language < 2; language++)
    {
        for (new persistence = 0; persistence < sizeof PLAYER_HELP_PERSISTENCE_KEYS; persistence++)
        {
            PlayerChat(id, "[GoldSrcOps] %L", PLAYER_LANGUAGES[language], PLAYER_HELP_PERSISTENCE_KEYS[persistence]);
            formatex(expected, charsmax(expected), "^4[GoldSrcOps]^1 %L", PLAYER_LANGUAGES[language], PLAYER_HELP_PERSISTENCE_KEYS[persistence]);
            CheckMenu(SameUtf8Bytes(g_color_text, expected), "RU/EN persistence sentences never turn slash-separated prose into commands");
        }
        for (new weapon = 0; weapon < sizeof WEAPON_NAMES; weapon++)
        {
            PlayerChat(id, "[GoldSrcOps] %L", PLAYER_LANGUAGES[language], "GS_PISTOL_SELECTED", WEAPON_NAMES[weapon], "Glock",
                PLAYER_LANGUAGES[language], "GS_CHANGE_SAVED");
            formatex(expected, charsmax(expected), "^4[GoldSrcOps]^1 %L", PLAYER_LANGUAGES[language], "GS_PISTOL_SELECTED", WEAPON_NAMES[weapon], "Glock",
                PLAYER_LANGUAGES[language], "GS_CHANGE_SAVED");
            CheckMenu(SameUtf8Bytes(g_color_text, expected), "MP5 AK-47 and M4A1 stay plain and intact in RU/EN weapon confirmations");
        }
    }
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
    PlayerChat(id, "[GoldSrcOps] /rank: 25 kills.");
    new number_offset = contain(g_color_text, "25");
    CheckMenu(contain(g_color_text, "^4/rank^1") >= 0 && number_offset > 0
        && g_color_text[number_offset - 1] == 4 && g_color_text[number_offset + 2] == 1,
        "commands and numeric values have explicit accents and reset colors");
    RunChatTokenChecks(id);
    new boundary[191];
    for (new n = 0; n < charsmax(boundary); n++) { boundary[n] = n % 2 == 0 ? '1' : ' '; }
    boundary[charsmax(boundary)] = 0;
    PlayerChat(id, "%s", boundary);
    CheckMenu(equal(g_color_text, boundary) != 0, "over-budget accents fall back without truncating text");
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
        CheckMenu(menu_items(g_player_menu[language]) == 9 && menu_pages(g_player_menu[language]) == 1,
            "nine hub items including profile fit one native menu page");
        CheckMenu(menu_find_id(g_player_menu[language], 0, 10) == MENU_EXIT,
            "zero key maps to exit on the nine-item native menu");
        new exit_displays = g_displays, original_choice = g_choice[id];
        OnPlayerMenuSelected(id, g_player_menu[language], MENU_EXIT);
        CheckMenu(g_displays == exit_displays && g_choice[id] == original_choice,
            "exit neither reopens the menu nor changes the loadout");
        new info[8], name[128], expected[128], access, callback;
        for (new item = 0; item < sizeof PLAYER_MENU_KEYS; item++)
        {
            menu_item_getinfo(g_player_menu[language], item, access, info, charsmax(info), name, charsmax(name), callback);
            formatex(expected, charsmax(expected), "%L", PLAYER_LANGUAGES[language], PLAYER_MENU_KEYS[item]);
            CheckMenu(SameUtf8Bytes(name, expected) && contain(name, "ML_NOTFOUND") < 0, "localized menu item");
        }
        rg_round_respawn(id);
        OnPlayerMenuSelected(id, g_player_menu[language], 0);
        CheckMenu(g_last_menu == g_menu[language][g_choice[id]] && !task_exists(WELCOME_TASK_BASE + id), "hub weapons and welcome suppression");
        OnWeaponSelected(id, g_menu[language][g_choice[id]], 1);
        CheckMenu(g_last_menu == g_pistol_menu[language][g_pistol_choice[id]] && g_choice[id] == 1, "localized primary callback");
        OnPistolSelected(id, g_pistol_menu[language][g_pistol_choice[id]], 2);
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
        prints = g_prints;
        OnPlayerMenuSelected(id, g_player_menu[language], 6);
        OpenPlayerSettings(id);
        CheckMenu(g_prints == prints, "opening and reopening settings is silent");
        CheckMenu(g_last_menu == GetSettingsMenu(id, language, 0), "settings action opens translated settings");
        new saved[192];
        formatex(saved, charsmax(saved), "%L", PLAYER_LANGUAGES[language], "GS_HUD_SESSION");
        CheckMenu(FindUtf8Bytes(g_menu_text[id], saved) >= 0, "settings menu shows session-only HUD preference");
        formatex(saved, charsmax(saved), "%L", PLAYER_LANGUAGES[language], "GS_SETTINGS_SESSION");
        CheckMenu(FindUtf8Bytes(g_menu_text[id], saved) >= 0, "settings menu shows session-only language and weapon preferences");
        formatex(saved, charsmax(saved), "%L", PLAYER_LANGUAGES[language], "GS_SETTINGS_LOADOUT", "AK-47", "Desert Eagle", language == 0 ? "RU" : "EN");
        CheckMenu(FindUtf8Bytes(g_menu_text[id], saved) >= 0 && strlen(g_menu_text[id]) < 384 && menu_items(g_last_menu) == 4 && menu_pages(g_last_menu) == 1
            && FindUtf8Bytes(g_menu_text[id], "ML_NOTFOUND") < 0, "settings title includes current loadout and four items on one page");
        OnSettingsSelected(id, GetSettingsMenu(id, language, 0), 0);
        CheckMenu(g_last_menu == g_menu[language][g_choice[id]], "settings dispatches weapons");
        OnSettingsSelected(id, GetSettingsMenu(id, language, 0), 3);
        CheckMenu(g_last_menu == g_player_menu[language], "settings back returns to hub");
        OnSettingsSelected(id, GetSettingsMenu(id, language, 0), 1);
        CheckMenu(g_last_menu == g_language_menu, "language action");
        new displays = g_displays;
        OnPlayerMenuSelected(id, g_player_menu[1 - language], 0);
        OnPlayerMenuSelected(id, g_player_menu[language], MENU_EXIT);
        OnPlayerMenuSelected(id, g_player_menu[language], sizeof PLAYER_MENU_KEYS);
        OnLanguageSelected(id, g_language_menu, MENU_EXIT);
        OnSettingsSelected(id, GetSettingsMenu(id, 1 - language, 0), 0);
        OnSettingsSelected(id, GetSettingsMenu(id, language, 0), 4);
        OnSettingsSelected(id, GetSettingsMenu(id, language, 0), MENU_EXIT);
        OnSettingsSelected(id, GetSettingsMenu(id, language, 0), MENU_TIMEOUT);
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
    OnSettingsSelected(id, GetSettingsMenu(id, 1, 0), 1);
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
    CheckMenu(g_last_menu == GetSettingsMenu(id, 1, 0), "spectator can inspect settings");
    displays = g_displays;
    OnSettingsSelected(id, GetSettingsMenu(id, 1, 0), 0);
    CheckMenu(g_displays == displays, "settings does not grant spectator weapons");
    RunHelpChecks(id);
    RunRankGuideChecks(id);
    RunHudChecks(id);
    g_hud_disabled[id] = true;
    client_disconnected(id, false, rejected, charsmax(rejected));
    client_putinserver(id);
    CheckMenu(g_help_next[id] == 0.0, "reconnect resets help cooldown");
    CheckMenu(g_stats_next[id][5] == 0.0, "reconnect resets rank guide cooldown");
    CheckMenu(g_language_override[id] == 0 && PlayerLanguage(id) == 1, "reconnect clears override without changing client preference");
    CheckMenu(!g_hud_disabled[id] && !g_hud_visible[id], "reconnect resets HUD without a persistent identity");
    rg_round_respawn(id);
    remove_task(WELCOME_TASK_BASE + id);
    g_language_override[id] = 1;
    OpenPlayerMenu(id);
    g_periodic_checks = true;
    set_task(2.2, "SwitchHudContinuityLanguage");
    set_task(4.3, "FinishHudContinuity");
    return PLUGIN_HANDLED;
}

public SwitchHudContinuityLanguage()
{
    g_language_override[g_client] = 2;
    OpenPlayerMenu(g_client);
}

public FinishHudContinuity()
{
    g_periodic_checks = false;
    CheckMenu(g_periodic_samples[0] >= 2 && g_periodic_samples[1] >= 2, "real periodic task refreshes HUD under RU/EN menus");
    new old_menu, new_menu;
    player_menu_info(g_client, old_menu, new_menu);
    CheckMenu(new_menu == g_player_menu[1] && g_hud_visible[g_client], "HUD refresh preserves the active menu");
    server_print("PLAYER_HUD_CONTINUITY_SMOKE=%s failures=%d ru=%d en=%d", g_failures ? "failed" : "passed", g_failures,
        g_periodic_samples[0], g_periodic_samples[1]);
    server_print("PLAYER_HUD_SMOKE=%s failures=%d languages=2 storage_writes=none", g_failures ? "failed" : "passed", g_failures);
    server_print("PLAYER_MENU_SMOKE=%s failures=%d languages=2 map_hooks=delegated", g_failures ? "failed" : "passed", g_failures);
    server_print("PLAYER_RANK_GUIDE_SMOKE=%s failures=%d languages=2 tiers=5 storage_writes=none", g_failures ? "failed" : "passed", g_failures);
    server_print("PLAYER_HELP_SMOKE=%s failures=%d languages=2 lines=5 storage_writes=none", g_failures ? "failed" : "passed", g_failures);
}

RunRankGuideChecks(id)
{
    new expected[192], motds = g_motd_count, displays = g_displays;
    new reads = g_leader_reads, rebuilds = g_leader_rebuilds;
    new saved_kills = g_saved_kills[id], map_kills = g_map_kills[id];
    new bool:welcomed = g_welcomed[id];
    new dirty = g_preferences_dirty[id], bool:hud_dirty = g_hud_preference_dirty[id];
    g_preferences_dirty[id] = PREFERENCE_LANGUAGE | PREFERENCE_PRIMARY | PREFERENCE_PISTOL;
    g_hud_preference_dirty[id] = true;
    g_stats_next[id][2] = get_gametime() + 20.0;
    g_stats_next[id][4] = get_gametime() + 20.0;
    g_help_next[id] = get_gametime() + 20.0;
    for (new language = 0; language < 2; language++)
    {
        g_language_override[id] = language + 1;
        g_stats_next[id][5] = 0.0;
        ShowRankGuide(id);
        CheckMenu(g_motd_count == ++motds, "rank guide has an independent cooldown");
        CheckMenu(SameUtf8Bytes(g_motd_title, language == 0 ? "Ранги" : "Ranks"),
            "rank guide caption preserves every RU/EN UTF-8 byte");
        formatex(expected, charsmax(expected), "<h2>%L</h2>", PLAYER_LANGUAGES[language], "GS_RANKS");
        CheckMenu(FindUtf8Bytes(g_motd_text, expected) >= 0, "rank guide keeps its complete inner heading");
        new expected_kills[] = { 0, 25, 100, 250, 500 };
        new previous = -1;
        for (new rank = 0; rank < sizeof expected_kills; rank++)
        {
            formatex(expected, charsmax(expected), "<tr><td>%L<td>%d",
                PLAYER_LANGUAGES[language], RANK_KEYS[rank], expected_kills[rank]);
            new position = FindUtf8Bytes(g_motd_text, expected);
            CheckMenu(position > previous && position >= 0, "all five rank names and exact thresholds are in ascending order");
            previous = position;
        }
        new const rule_keys[][] = { "GS_RANKS_COUNT", "GS_RANKS_EXCLUDED", "GS_RANKS_PROGRESS", "GS_RANKS_PLACE", "GS_RANKS_FAIR" };
        for (new rule = 0; rule < sizeof rule_keys; rule++)
        {
            formatex(expected, charsmax(expected), "%L", PLAYER_LANGUAGES[language], rule_keys[rule]);
            CheckMenu(FindUtf8Bytes(g_motd_text, expected) >= 0, "every complete translated rank rule is present");
        }
        ShowRankGuide(id);
        CheckMenu(g_motd_count == motds && g_stats_next[id][5] == get_gametime() + 5.0, "rank guide suppresses immediate repeats");
        g_stats_next[id][5] = get_gametime();
        ShowRankGuide(id);
        CheckMenu(g_motd_count == ++motds, "rank guide admits the cooldown boundary");
    }
    CheckMenu(g_preferences_dirty[id] == (PREFERENCE_LANGUAGE | PREFERENCE_PRIMARY | PREFERENCE_PISTOL)
        && g_hud_preference_dirty[id] && g_saved_kills[id] == saved_kills && g_map_kills[id] == map_kills
        && g_welcomed[id] == welcomed && g_displays == displays && g_leader_reads == reads && g_leader_rebuilds == rebuilds,
        "reading ranks preserves pending choices, scores, welcome, menu and leaderboard work");
    g_preferences_dirty[id] = dirty;
    g_hud_preference_dirty[id] = hud_dirty;
    g_stats_next[id][5] = 0.0;
    ShowRankGuide(0);
    ShowRankGuide(MAX_PLAYERS + 1);
    ShowRankGuide(id == MaxClients ? 1 : MaxClients);
    set_pcvar_num(g_enabled, 0);
    ShowRankGuide(id);
    set_pcvar_num(g_enabled, 1);
    g_human = false;
    ShowRankGuide(id);
    g_human = true;
    CheckMenu(g_motd_count == motds && g_stats_next[id][5] == 0.0, "invalid disconnected disabled and bot rank queries do nothing");
    new vault = g_stats_vault, preference_vault = g_preferences_vault;
    g_stats_vault = INVALID_HANDLE;
    g_preferences_vault = INVALID_HANDLE;
    rg_set_user_team(id, TEAM_SPECTATOR);
    ShowRankGuide(id);
    CheckMenu(g_motd_count == ++motds, "spectator reads static rules without identity or storage");
    g_stats_vault = vault;
    g_preferences_vault = preference_vault;
    rg_set_user_team(id, TEAM_CT);
    rg_round_respawn(id);
    set_entvar(id, var_deadflag, DEAD_DEAD);
    g_stats_next[id][5] = 0.0;
    ShowRankGuide(id);
    CheckMenu(g_motd_count == ++motds, "dead players can read rank rules");
    rg_round_respawn(id);
}

RunHelpChecks(id)
{
    new displays = g_displays, expected[192];
    for (new language = 0; language < 2; language++)
    {
        g_language_override[id] = language + 1;
        g_help_next[id] = 0.0;
        g_help_line_count = 0;
        g_collect_help = true;
        ShowPlayerHelp(id);
        g_collect_help = false;
        CheckMenu(g_help_line_count == 5 && g_displays == displays, "help fits the five-line chat viewport without replacing a menu");
        CheckMenu(contain(g_help_lines[0], "/menu") >= 0 && contain(g_help_lines[0], "/profile") >= 0
            && contain(g_help_lines[0], "/settings") >= 0, "first help line retains the entry commands");
        for (new line = 0; line < 4; line++)
        {
            formatex(expected, charsmax(expected), "[GoldSrcOps] %L", PLAYER_LANGUAGES[language], PLAYER_HELP_KEYS[line]);
            CheckMenu(SameUtf8Bytes(g_help_lines[line], expected), "RU/EN help command translation");
        }
        CheckMenu(contain(g_help_lines[2], "/ranks") >= 0 && contain(g_help_lines[2], "/rank:") >= 0,
            "five-line help distinguishes personal rank from the rank guide");
        formatex(expected, charsmax(expected), "[GoldSrcOps] %L", PLAYER_LANGUAGES[language], "GS_HELP_SESSION");
        CheckMenu(SameUtf8Bytes(g_help_lines[4], expected), "help combines both unsaved states in the fifth line");
        new prints = g_prints;
        ShowPlayerHelp(id);
        CheckMenu(g_prints == prints && g_help_next[id] == get_gametime() + 5.0, "repeated help is rate limited");
        g_help_next[id] = get_gametime();
        ShowPlayerHelp(id);
        CheckMenu(g_prints == prints + 5, "help becomes available at cooldown boundary");
    }
    new prints = g_prints;
    ShowPlayerHelp(0);
    ShowPlayerHelp(MAX_PLAYERS + 1);
    g_help_next[id] = 0.0;
    set_pcvar_num(g_enabled, 0);
    ShowPlayerHelp(id);
    set_pcvar_num(g_enabled, 1);
    g_human = false;
    ShowPlayerHelp(id);
    g_human = true;
    CheckMenu(g_prints == prints && g_help_next[id] == 0.0, "disabled bot and invalid help has no output or cooldown");
    rg_set_user_team(id, TEAM_CT);
    rg_round_respawn(id);
    set_entvar(id, var_deadflag, DEAD_DEAD);
    ShowPlayerHelp(id);
    CheckMenu(g_prints == prints + 5, "dead players can read help");
    g_welcomed[id] = false;
    rg_round_respawn(id);
    g_help_next[id] = 0.0;
    CheckMenu(bool:task_exists(WELCOME_TASK_BASE + id), "welcome scheduled for eligible spawn");
    ShowPlayerHelp(id);
    CheckMenu(!g_welcomed[id] && bool:task_exists(WELCOME_TASK_BASE + id), "help does not consume or cancel pending welcome");
    g_help_line_count = 0;
    g_collect_help = true;
    ShowWelcome(WELCOME_TASK_BASE + id);
    g_collect_help = false;
    CheckMenu(contain(g_help_lines[0], "/help") >= 0 && g_welcomed[id], "welcome includes help hint");
    prints = g_prints;
    ShowWelcome(WELCOME_TASK_BASE + id);
    CheckMenu(g_prints == prints, "welcome hint remains once-only");
    remove_task(WELCOME_TASK_BASE + id);
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
        CheckMenu(g_hud_visible[id], "opening settings preserves the current HUD frame");
        messages = g_hud_messages;
        UpdatePlayerHuds();
        CheckMenu(g_hud_messages == messages + 1 && g_hud_visible[id], "open settings keeps HUD updating");
        messages = g_hud_messages;
        OnSettingsSelected(id, GetSettingsMenu(id, language, 0), 2);
        CheckMenu(g_hud_disabled[id] && g_last_menu == GetSettingsMenu(id, language, 1), "toggle opens private off-state menu");
        OnSettingsSelected(id, GetSettingsMenu(id, language, 0), 2);
        CheckMenu(g_hud_disabled[id], "stale toggle cannot invert current setting");
        menu_cancel(id);
        show_menu(id, 0, "");
        UpdatePlayerHuds();
        CheckMenu(g_hud_messages == messages, "disabled HUD stays hidden without a menu");
        OnSettingsSelected(id, GetSettingsMenu(id, language, 1), 2);
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
            CheckMenu(g_hud_messages == messages + 1 && g_hud_visible[id], "hub keeps HUD updating while open");
            OpenPlayerMenu(id);
            CheckMenu(g_hud_visible[id], "reopening hub preserves the current HUD frame");
            messages = g_hud_messages;
            FixtureCloseMenu(id);
            new menu, keys;
            get_user_menu(id, menu, keys);
            CheckMenu(menu == 0 && keys != 0, "native menu state retains exit key mask without an active menu");
            UpdatePlayerHuds();
            CheckMenu(g_hud_messages == messages + 1, "HUD resumes after hub exit with stale keys");
        }
        OpenPlayerSettings(id);
        FixtureCloseMenu(id);
        OnSettingsSelected(id, GetSettingsMenu(id, language, 0), 2);
        FixtureCloseMenu(id);
        new messages = g_hud_messages;
        UpdatePlayerHuds();
        CheckMenu(g_hud_disabled[id] && g_hud_messages == messages, "HUD stays off after settings exit with stale keys");
        OpenPlayerSettings(id);
        FixtureCloseMenu(id);
        OnSettingsSelected(id, GetSettingsMenu(id, language, 1), 2);
        FixtureCloseMenu(id);
        UpdatePlayerHuds();
        CheckMenu(!g_hud_disabled[id] && g_hud_messages == messages + 1, "HUD returns after toggle and exit with stale keys");
        OpenWeapons(id);
        FixtureCloseMenu(id);
        OnWeaponSelected(id, g_menu[language][g_choice[id]], 0);
        messages = g_hud_messages;
        UpdatePlayerHuds();
        CheckMenu(g_hud_messages == messages + 1 && g_hud_visible[id], "weapon submenu keeps HUD updating");
        OpenWeapons(id);
        CheckMenu(g_hud_visible[id], "opening weapons preserves the current HUD frame");
        FixtureCloseMenu(id);
        OnWeaponSelected(id, g_menu[language][g_choice[id]], 0);
        messages = g_hud_messages;
        FixtureCloseMenu(id);
        OnPistolSelected(id, g_pistol_menu[language][g_pistol_choice[id]], 0);
        UpdatePlayerHuds();
        CheckMenu(g_hud_messages == messages + 1, "HUD resumes after weapon selection with stale keys");
        OpenPlayerSettings(id);
        FixtureCloseMenu(id);
        OnSettingsSelected(id, GetSettingsMenu(id, language, 0), 3);
        messages = g_hud_messages;
        UpdatePlayerHuds();
        CheckMenu(g_hud_messages == messages + 1 && g_hud_visible[id], "Back keeps HUD updating under the reopened hub");
        messages = g_hud_messages;
        FixtureCloseMenu(id);
        UpdatePlayerHuds();
        CheckMenu(g_hud_messages == messages + 1, "HUD returns after Back and hub exit");
        show_menu(id, MENU_KEY_1, "HUD fixture external", -1, "HUD fixture external");
        messages = g_hud_messages;
        UpdatePlayerHuds();
        CheckMenu(g_hud_messages == messages + 1 && g_hud_visible[id], "external legacy menu keeps HUD updating");
        messages = g_hud_messages;
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
