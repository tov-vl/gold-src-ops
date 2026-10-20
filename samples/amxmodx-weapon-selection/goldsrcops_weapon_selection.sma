#pragma semicolon 1
#pragma tabsize 4

#include <amxmodx>
#include <reapi>
#include <nvault>

new const WEAPON_NAMES[][] = { "MP5", "AK-47", "M4A1" };
new const WEAPON_CLASSES[][] = { "weapon_mp5navy", "weapon_ak47", "weapon_m4a1" };
new const WeaponIdType:WEAPON_IDS[] = { WEAPON_MP5N, WEAPON_AK47, WEAPON_M4A1 };
new const BACKPACK_AMMO[] = { 120, 90, 90 };
new const PISTOL_NAMES[][] = { "USP", "Glock", "Desert Eagle" };
new const PISTOL_CLASSES[][] = { "weapon_usp", "weapon_glock18", "weapon_deagle" };
new const WeaponIdType:PISTOL_IDS[] = { WEAPON_USP, WEAPON_GLOCK18, WEAPON_DEAGLE };
new const PISTOL_AMMO[] = { 100, 120, 35 };
const WELCOME_TASK_BASE = 1000;
const HUD_TASK_ID = 2000;
const Float:HUD_REFRESH_INTERVAL = 1.0;
const Float:HUD_HOLD_TIME = 1.5;
const MAP_LEADER_LIMIT = 5;
const STATS_COUNTER_LIMIT = 1000000000;
new const STATS_VAULT_NAME[] = "goldsrcops-player-stats-v1";
new const PREFERENCES_VAULT_NAME[] = "goldsrcops-player-preferences-v1";
const PREFERENCE_LANGUAGE = 1;
const PREFERENCE_PRIMARY = 2;
const PREFERENCE_PISTOL = 4;
new const RANK_KEYS[][] = { "GS_RECRUIT", "GS_FIGHTER", "GS_VETERAN", "GS_ELITE", "GS_LEGEND" };
new const RANK_KILLS[] = { 0, 25, 100, 250, 500 };
new const PLAYER_LANGUAGES[][] = { "ru", "en" };
new const PLAYER_MENU_KEYS[][] = { "GS_GUNS", "GS_STATS", "GS_RANK", "GS_TOP", "GS_MAPS", "GS_TIMELEFT", "GS_SETTINGS", "GS_TOPALL", "GS_PROFILE" };
new const PLAYER_HELP_KEYS[][] = { "GS_HELP_MENU", "GS_HELP_GUNS", "GS_HELP_STATS", "GS_HELP_MAPS" };
new const PLAYER_HELP_PERSISTENCE_KEYS[][] = { "GS_HELP_SESSION", "GS_HELP_HUD_SAVED", "GS_HELP_SETTINGS_SAVED", "GS_HELP_SAVED" };
const Float:HELP_COOLDOWN = 5.0;

new g_enabled;
new g_kill_heal;
new g_kill_ammo;
new g_menu[2];
new g_pistol_menu[2];
new g_player_menu[2];
new g_settings_menu[MAX_PLAYERS + 1][2][2];
new g_language_menu;
new bool:g_hud_disabled[MAX_PLAYERS + 1];
new bool:g_hud_visible[MAX_PLAYERS + 1];
new g_language_override[MAX_PLAYERS + 1];
new g_choice[MAX_PLAYERS + 1];
new g_pistol_choice[MAX_PLAYERS + 1];
new bool:g_welcomed[MAX_PLAYERS + 1];
new Float:g_help_next[MAX_PLAYERS + 1];
new g_map_kills[MAX_PLAYERS + 1];
new g_map_deaths[MAX_PLAYERS + 1];
new g_kill_streak[MAX_PLAYERS + 1];
new g_best_streak[MAX_PLAYERS + 1];
new g_record_streak[MAX_PLAYERS + 1];
new g_saved_streak[MAX_PLAYERS + 1];
new bool:g_streak_ready[MAX_PLAYERS + 1];
new bool:g_streak_blocked[MAX_PLAYERS + 1];
new g_streak_errors;
new Float:g_stats_next[MAX_PLAYERS + 1][6];
const SAVED_LEADER_LIMIT = 10;
const LEADER_INDEX_LIMIT = 4096;
const LEADER_FILE_LIMIT = 4 * 1024 * 1024;
const LEADER_JOURNAL_OP_LIMIT = 65536;
const Float:LEADER_REFRESH_INTERVAL = 5.0;
enum _:LeaderRecord { LeaderKey[40], LeaderKills, LeaderDeaths, LeaderCachedKills, LeaderCachedDeaths, LeaderSearchName[32] };
new Array:g_leader_records;
new Trie:g_leader_keys;
new bool:g_leader_ready;
new bool:g_leader_dirty;
new bool:g_leader_name_recorded[MAX_PLAYERS + 1];
new Float:g_leader_refresh_after;
new g_leader_top[SAVED_LEADER_LIMIT];
new g_leader_count;
new g_leader_total;
new g_leader_errors;
new g_leader_reads;
new g_leader_rebuilds;
new g_lookup_enabled;
new Float:g_lookup_after;
const Float:PLAYER_LOOKUP_INTERVAL = 10.0;
new g_leader_names[SAVED_LEADER_LIMIT][32];
new g_leader_scores[SAVED_LEADER_LIMIT][2];
new g_stats_vault = INVALID_HANDLE;
new g_saved_key[MAX_PLAYERS + 1][40];
new g_saved_kills[MAX_PLAYERS + 1];
new g_saved_deaths[MAX_PLAYERS + 1];
new bool:g_saved_ready[MAX_PLAYERS + 1];
new bool:g_saved_blocked[MAX_PLAYERS + 1];
new g_storage_errors;
new g_preferences_vault = INVALID_HANDLE;
new g_preferences_key[MAX_PLAYERS + 1][40];
new bool:g_preferences_ready[MAX_PLAYERS + 1];
new bool:g_preferences_blocked[MAX_PLAYERS + 1];
new g_preferences_dirty[MAX_PLAYERS + 1];
new g_preferences_errors;
new bool:g_hud_preference_ready[MAX_PLAYERS + 1];
new bool:g_hud_preference_blocked[MAX_PLAYERS + 1];
new bool:g_hud_preference_dirty[MAX_PLAYERS + 1];

public plugin_natives()
{
    register_library("goldsrcops_player_menu");
    register_native("goldsrcops_player_language", "NativePlayerLanguage");
}

public NativePlayerLanguage(plugin, params)
{
    #pragma unused plugin
    if (params != 1) { return -1; }
    new id = get_param(1);
    if (id < 1 || id > MAX_PLAYERS || !is_user_connected(id) || !get_pcvar_num(g_enabled))
    {
        return -1;
    }
    return PlayerLanguage(id);
}

public plugin_init()
{
    register_plugin("GoldSrcOps Weapon Selection", "0.32.0", "GoldSrcOps");
    if (!register_dictionary("goldsrcops-player-menu.txt"))
    {
        set_fail_state("Player menu dictionary unavailable.");
        return;
    }
    PrepareLeaderIndex();
    g_stats_vault = nvault_open(STATS_VAULT_NAME);
    if (g_stats_vault == INVALID_HANDLE)
    {
        log_amx("Player stats storage unavailable; connection/map stats remain available.");
    }
    g_preferences_vault = nvault_open(PREFERENCES_VAULT_NAME);
    if (g_preferences_vault == INVALID_HANDLE)
    {
        log_amx("Player preferences storage unavailable; selections remain connection-only.");
    }
    LoadLeaderScores();
    g_enabled = register_cvar("goldsrcops_weapons_enabled", "0");
    g_lookup_enabled = register_cvar("goldsrcops_player_lookup_enabled", "0");
    g_kill_heal = register_cvar("goldsrcops_kill_heal", "15");
    g_kill_ammo = register_cvar("goldsrcops_kill_ammo", "1");
    AutoExecConfig(false, "goldsrcops-weapon-selection");
    register_clcmd("say /guns", "OpenWeapons");
    register_clcmd("say_team /guns", "OpenWeapons");
    register_clcmd("guns", "OpenWeapons");
    register_clcmd("say /menu", "OpenPlayerMenu");
    register_clcmd("say_team /menu", "OpenPlayerMenu");
    register_clcmd("menu", "OpenPlayerMenu");
    register_clcmd("say /settings", "OpenPlayerSettings");
    register_clcmd("say_team /settings", "OpenPlayerSettings");
    register_clcmd("settings", "OpenPlayerSettings");
    register_clcmd("say /help", "ShowPlayerHelp");
    register_clcmd("say_team /help", "ShowPlayerHelp");
    register_clcmd("help", "ShowPlayerHelp");
    register_srvcmd("goldsrcops_weapons_status", "OnStatusCommand");
    register_clcmd("say /stats", "ShowMapStats");
    register_clcmd("say_team /stats", "ShowMapStats");
    register_clcmd("stats", "ShowMapStats");
    register_clcmd("say /top", "ShowMapLeaders");
    register_clcmd("say_team /top", "ShowMapLeaders");
    register_clcmd("top", "ShowMapLeaders");
    register_clcmd("say /topall", "ShowSavedLeaders");
    register_clcmd("say_team /topall", "ShowSavedLeaders");
    register_clcmd("topall", "ShowSavedLeaders");
    register_clcmd("say /rank", "ShowPlayerRank");
    register_clcmd("say_team /rank", "ShowPlayerRank");
    register_clcmd("rank", "ShowPlayerRank");
    register_clcmd("say /ranks", "ShowRankGuide");
    register_clcmd("say_team /ranks", "ShowRankGuide");
    register_clcmd("ranks", "ShowRankGuide");
    register_clcmd("say /profile", "ShowPlayerProfile");
    register_clcmd("say_team /profile", "ShowPlayerProfile");
    register_clcmd("profile", "ShowPlayerProfile");
    register_srvcmd("goldsrcops_stats_status", "OnMapStatsStatus");
    register_srvcmd("goldsrcops_leaderboard_snapshot", "OnLeaderboardSnapshot");
    register_srvcmd("goldsrcops_player_lookup", "OnPlayerLookup");
    register_srvcmd("goldsrcops_player_lookup_v2", "OnPlayerLookupV2");
    register_event("DeathMsg", "OnMapStatsDeath", "a");
    RegisterHookChain(RG_CBasePlayer_Spawn, "OnSpawnPost", true);
    set_task(HUD_REFRESH_INTERVAL, "UpdatePlayerHuds", HUD_TASK_ID, _, _, "b");

    for (new id = 0; id <= MAX_PLAYERS; id++)
    {
        for (new language = 0; language < 2; language++) { arrayset(g_settings_menu[id][language], -1, 2); }
    }
    new text[128];
    for (new language = 0; language < sizeof PLAYER_LANGUAGES; language++)
    {
        formatex(text, charsmax(text), "%L", PLAYER_LANGUAGES[language], "GS_PRIMARY");
        g_menu[language] = menu_create(text, "OnWeaponSelected");
        for (new index = 0; index < sizeof WEAPON_NAMES; index++)
        {
            menu_additem(g_menu[language], WEAPON_NAMES[index]);
        }
        formatex(text, charsmax(text), "%L", PLAYER_LANGUAGES[language], "GS_PISTOL");
        g_pistol_menu[language] = menu_create(text, "OnPistolSelected");
        for (new index = 0; index < sizeof PISTOL_NAMES; index++)
        {
            menu_additem(g_pistol_menu[language], PISTOL_NAMES[index]);
        }
        formatex(text, charsmax(text), "%L", PLAYER_LANGUAGES[language], "GS_MENU");
        g_player_menu[language] = menu_create(text, "OnPlayerMenuSelected");
        for (new index = 0; index < sizeof PLAYER_MENU_KEYS; index++)
        {
            formatex(text, charsmax(text), "%L", PLAYER_LANGUAGES[language], PLAYER_MENU_KEYS[index]);
            menu_additem(g_player_menu[language], text);
        }
        menu_setprop(g_player_menu[language], MPROP_PERPAGE, 0);
        menu_setprop(g_player_menu[language], MPROP_EXIT, MEXIT_FORCE);
        formatex(text, charsmax(text), "%L", PLAYER_LANGUAGES[language], "GS_EXIT");
        menu_setprop(g_menu[language], MPROP_EXITNAME, text);
        menu_setprop(g_pistol_menu[language], MPROP_EXITNAME, text);
        menu_setprop(g_player_menu[language], MPROP_EXITNAME, text);
    }
    formatex(text, charsmax(text), "%L", "ru", "GS_LANGUAGE");
    g_language_menu = menu_create(text, "OnLanguageSelected");
    formatex(text, charsmax(text), "%L", "ru", "GS_RUSSIAN");
    menu_additem(g_language_menu, text);
    menu_additem(g_language_menu, "English / EN");
    menu_setprop(g_language_menu, MPROP_EXITNAME, "Exit / RU - EN");
}

public plugin_end()
{
    remove_task(HUD_TASK_ID);
    for (new id = 1; id <= MaxClients; id++) { HidePlayerHud(id); ClearSettingsMenus(id); }
    for (new language = 0; language < sizeof PLAYER_LANGUAGES; language++)
    {
        menu_destroy(g_menu[language]);
        menu_destroy(g_pistol_menu[language]);
        menu_destroy(g_player_menu[language]);
    }
    menu_destroy(g_language_menu);
    ArrayDestroy(g_leader_records);
    TrieDestroy(g_leader_keys);
    if (g_stats_vault != INVALID_HANDLE)
    {
        nvault_close(g_stats_vault);
        g_stats_vault = INVALID_HANDLE;
    }
    if (g_preferences_vault != INVALID_HANDLE)
    {
        nvault_close(g_preferences_vault);
        g_preferences_vault = INVALID_HANDLE;
    }
}

public OnStatusCommand()
{
    server_print("WEAPON_SELECTION_STATUS version=0.32.0 enabled=%d choices=%d pistol_choices=%d armor=100 helmet=1 first_spawn_menu=1 map_stats=1 persistent_stats=1 ranks=1",
        get_pcvar_num(g_enabled) != 0, sizeof WEAPON_NAMES, sizeof PISTOL_NAMES);
    server_print("PLAYER_MENU_STATUS version=0.32.0 languages=ru,en unset=ru unsupported=en override=steam_or_connection entries=%d map_commands=delegated language_native=1",
        sizeof PLAYER_MENU_KEYS);
    server_print("PLAYER_SETTINGS_STATUS version=0.32.0 languages=ru,en summary=private_menu opening_chat=0 change_chat=1 saved=verified_record hud_saved=separate_verified_record reset=none");
    server_print("PLAYER_HELP_STATUS version=0.32.0 languages=ru,en lines=5 cooldown=5 scope=private storage_writes=none welcome=once_per_connection");
    server_print("PLAYER_HUD_STATUS version=0.32.0 languages=ru,en interval=1 renderer=director expiry=1.5 messages=2 menus=visible scope=connection_map rank=saved_kills toggle=steam_or_connection storage_writes=explicit_toggle");
    server_print("KILL_STREAK_STATUS version=0.32.0 enabled=%d scope=connection_map reset=any_death alive_only=1 milestones=3,5,10 feedback=private languages=ru,en storage=none",
        get_pcvar_num(g_enabled) != 0);
    server_print("KILL_HEAL_STATUS version=0.32.0 enabled=%d amount=%d max_health=100 scope=enemy_human_kill alive_only=1",
        get_pcvar_num(g_enabled) != 0 && KillHealAmount() > 0, KillHealAmount());
    server_print("KILL_AMMO_STATUS version=0.32.0 enabled=%d scope=enemy_human_kill alive_only=1 weapons=loadout_owned reserve=spawn_limit clip=unchanged",
        get_pcvar_num(g_enabled) != 0 && get_pcvar_num(g_kill_ammo) > 0);
    new loaded;
    for (new id = 1; id <= MaxClients; id++)
    {
        if (IsStatsClient(id) && g_preferences_ready[id]) { loaded++; }
    }
    server_print("PLAYER_PREFERENCES_STATUS version=0.32.0 storage=%s schema=1 hud_schema=1 identity=steam loaded=%d errors=%d",
        g_preferences_vault == INVALID_HANDLE ? "unavailable" : "nvault", loaded, g_preferences_errors);
    return PLUGIN_HANDLED;
}

public client_putinserver(id)
{
    ClearSettingsMenus(id);
    remove_task(WELCOME_TASK_BASE + id);
    g_welcomed[id] = false;
    g_help_next[id] = 0.0;
    g_leader_name_recorded[id] = false;
    g_language_override[id] = 0;
    g_choice[id] = 0;
    g_pistol_choice[id] = 0;
    g_hud_disabled[id] = false;
    g_hud_visible[id] = false;
    ResetMapStats(id);
    ResetSavedStats(id);
    ResetPlayerPreferences(id);
    EnsureSavedStats(id);
    EnsurePlayerPreferences(id);
    EnsureSavedStreak(id);
    RememberLeaderName(id);
}

public client_authorized(id, const authid[])
{
    #pragma unused authid
    // Authorization can arrive before or after client_putinserver.
    EnsureSavedStats(id);
    EnsurePlayerPreferences(id);
    EnsureSavedStreak(id);
    RememberLeaderName(id);
}

public client_disconnected(id)
{
    ClearSettingsMenus(id);
    HidePlayerHud(id);
    g_hud_disabled[id] = false;
    remove_task(WELCOME_TASK_BASE + id);
    g_welcomed[id] = false;
    g_help_next[id] = 0.0;
    g_leader_name_recorded[id] = false;
    g_language_override[id] = 0;
    g_choice[id] = 0;
    g_pistol_choice[id] = 0;
    ResetMapStats(id);
    ResetSavedStats(id);
    ResetPlayerPreferences(id);
}

bool:IsPlaying(id)
{
    if (!IsStatsClient(id))
    {
        return false;
    }

    new TeamName:team = get_member(id, m_iTeam);
    return team == TEAM_TERRORIST || team == TEAM_CT;
}

PlayerLanguage(id)
{
    EnsurePlayerPreferences(id);
    return EffectivePlayerLanguage(id);
}

EffectivePlayerLanguage(id)
{
    if (g_language_override[id])
    {
        return g_language_override[id] - 1;
    }
    new language[16];
    get_user_info(id, "lang", language, charsmax(language));
    return !language[0] || equali(language, "ru") ? 0 : 1;
}

public OpenPlayerMenu(id)
{
    if (get_pcvar_num(g_enabled) && IsStatsClient(id))
    {
        remove_task(WELCOME_TASK_BASE + id);
        g_welcomed[id] = true;
        menu_display(id, g_player_menu[PlayerLanguage(id)]);
    }
    return PLUGIN_HANDLED;
}

public OnPlayerMenuSelected(id, menu, item)
{
    if (item < 0 || item >= sizeof PLAYER_MENU_KEYS || !get_pcvar_num(g_enabled) || !IsStatsClient(id))
    {
        return PLUGIN_HANDLED;
    }
    // Profile and settings dispatch must not flush pending preferences through language loading.
    new language = item == 8 || item == 6 ? EffectivePlayerLanguage(id) : PlayerLanguage(id);
    if (menu != g_player_menu[language]) { return PLUGIN_HANDLED; }
    switch (item)
    {
        case 0: OpenWeapons(id);
        case 1: { ShowMapStats(id); OpenPlayerMenu(id); }
        case 2: { ShowPlayerRank(id); OpenPlayerMenu(id); }
        case 3: { ShowMapLeaders(id); OpenPlayerMenu(id); }
        case 4: amxclient_cmd(id, "say", "/maps");
        case 5: { amxclient_cmd(id, "say", "/timeleft"); OpenPlayerMenu(id); }
        case 6: OpenPlayerSettings(id);
        case 7: ShowSavedLeaders(id);
        case 8: ShowPlayerProfile(id);
    }
    return PLUGIN_HANDLED;
}

// Each client owns up to four cached menus, preserving language/HUD stale-callback guards.
GetSettingsMenu(id, language, disabled)
{
    if (g_settings_menu[id][language][disabled] >= 0) { return g_settings_menu[id][language][disabled]; }
    new text[128];
    formatex(text, charsmax(text), "%L", PLAYER_LANGUAGES[language], "GS_SETTINGS");
    new menu = menu_create(text, "OnSettingsSelected");
    g_settings_menu[id][language][disabled] = menu;
    formatex(text, charsmax(text), "%L", PLAYER_LANGUAGES[language], "GS_GUNS");
    menu_additem(menu, text);
    formatex(text, charsmax(text), "%L", PLAYER_LANGUAGES[language], "GS_LANGUAGE");
    menu_additem(menu, text);
    formatex(text, charsmax(text), "%L", PLAYER_LANGUAGES[language], disabled ? "GS_HUD_OFF" : "GS_HUD_ON");
    menu_additem(menu, text);
    formatex(text, charsmax(text), "%L", PLAYER_LANGUAGES[language], "GS_BACK");
    menu_additem(menu, text);
    formatex(text, charsmax(text), "%L", PLAYER_LANGUAGES[language], "GS_EXIT");
    menu_setprop(menu, MPROP_EXITNAME, text);
    menu_setprop(menu, MPROP_PERPAGE, 0);
    menu_setprop(menu, MPROP_EXIT, MEXIT_FORCE);
    return menu;
}

ClearSettingsMenus(id)
{
    for (new language = 0; language < 2; language++)
    {
        for (new disabled = 0; disabled < 2; disabled++)
        {
            new menu = g_settings_menu[id][language][disabled];
            g_settings_menu[id][language][disabled] = -1;
            if (menu >= 0) { menu_destroy(menu); }
        }
    }
}

BuildPlayerSettingsTitle(id, text[], length)
{
    new language = EffectivePlayerLanguage(id);
    return formatex(text, length, "%L^n^n\w%L^n%L^n%L^n", PLAYER_LANGUAGES[language], "GS_SETTINGS",
        PLAYER_LANGUAGES[language], "GS_SETTINGS_LOADOUT", WEAPON_NAMES[g_choice[id]], PISTOL_NAMES[g_pistol_choice[id]], language == 0 ? "RU" : "EN",
        PLAYER_LANGUAGES[language], PlayerPreferencesSaved(id) ? "GS_SETTINGS_SAVED" : "GS_SETTINGS_SESSION",
        PLAYER_LANGUAGES[language], PlayerHudPreferenceSaved(id) ? "GS_HUD_SAVED" : "GS_HUD_SESSION");
}

public OpenPlayerSettings(id)
{
    if (!get_pcvar_num(g_enabled) || !IsStatsClient(id)) { return PLUGIN_HANDLED; }
    remove_task(WELCOME_TASK_BASE + id);
    g_welcomed[id] = true;
    // Inspect the loaded state without flushing pending choices through language loading.
    new menu = GetSettingsMenu(id, EffectivePlayerLanguage(id), g_hud_disabled[id]), text[384];
    BuildPlayerSettingsTitle(id, text, charsmax(text));
    menu_setprop(menu, MPROP_TITLE, text);
    menu_display(id, menu);
    return PLUGIN_HANDLED;
}

public ShowPlayerHelp(id)
{
    if (!get_pcvar_num(g_enabled) || !IsStatsClient(id)) { return PLUGIN_HANDLED; }
    new Float:now = get_gametime();
    if (now < g_help_next[id]) { return PLUGIN_HANDLED; }
    g_help_next[id] = now + HELP_COOLDOWN;
    // Reading help must not flush pending preferences or consume the welcome.
    new language = EffectivePlayerLanguage(id);
    for (new index = 0; index < sizeof PLAYER_HELP_KEYS; index++)
    {
        PlayerChat(id, "[GoldSrcOps] %L", PLAYER_LANGUAGES[language], PLAYER_HELP_KEYS[index]);
    }
    // Four command messages plus one status fit the standard five-line chat history.
    new persistence = (PlayerPreferencesSaved(id) ? 2 : 0) + (PlayerHudPreferenceSaved(id) ? 1 : 0);
    PlayerChat(id, "[GoldSrcOps] %L", PLAYER_LANGUAGES[language], PLAYER_HELP_PERSISTENCE_KEYS[persistence]);
    return PLUGIN_HANDLED;
}

public OnSettingsSelected(id, menu, item)
{
    if (menu < 0 || item < 0 || item >= 4 || !get_pcvar_num(g_enabled) || !IsStatsClient(id)
        || menu != g_settings_menu[id][EffectivePlayerLanguage(id)][g_hud_disabled[id]]) { return PLUGIN_HANDLED; }
    switch (item)
    {
        case 0: OpenWeapons(id);
        case 1: menu_display(id, g_language_menu);
        case 2:
        {
            g_hud_disabled[id] = !g_hud_disabled[id];
            g_hud_preference_dirty[id] = true;
            SaveHudPreference(id);
            HidePlayerHud(id);
            new language = EffectivePlayerLanguage(id), hudLabel[32];
            formatex(hudLabel, charsmax(hudLabel), "%L", PLAYER_LANGUAGES[language], g_hud_disabled[id] ? "GS_HUD_OFF" : "GS_HUD_ON");
            PlayerChat(id, "[GoldSrcOps] %L", PLAYER_LANGUAGES[language], "GS_HUD_SELECTED", hudLabel,
                SettingPersistenceText(id, true));
            OpenPlayerSettings(id);
        }
        case 3: OpenPlayerMenu(id);
    }
    return PLUGIN_HANDLED;
}

bool:PlayerPreferencesSaved(id)
{
    if (g_preferences_vault == INVALID_HANDLE || !g_preferences_ready[id]
        || g_preferences_blocked[id] || g_preferences_dirty[id]) { return false; }
    new value[32], timestamp, language, primary, pistol;
    return bool:(nvault_lookup(g_preferences_vault, g_preferences_key[id], value, charsmax(value), timestamp)
        && DecodePlayerPreferences(value, language, primary, pistol)
        && language == g_language_override[id] && primary == g_choice[id] && pistol == g_pistol_choice[id]);
}

SettingPersistenceText(id, bool:hud = false)
{
    new text[96];
    formatex(text, charsmax(text), "%L", PLAYER_LANGUAGES[EffectivePlayerLanguage(id)],
        (hud ? PlayerHudPreferenceSaved(id) : PlayerPreferencesSaved(id)) ? "GS_CHANGE_SAVED" : "GS_CHANGE_SESSION");
    return text;
}

public OnLanguageSelected(id, menu, item)
{
    if (!get_pcvar_num(g_enabled) || !IsStatsClient(id) || menu != g_language_menu)
    {
        return PLUGIN_HANDLED;
    }
    if (item >= 0 && item < sizeof PLAYER_LANGUAGES)
    {
        if (g_language_override[id] != item + 1)
        {
            g_language_override[id] = item + 1;
            g_preferences_dirty[id] |= PREFERENCE_LANGUAGE;
            SavePlayerPreferences(id);
            PlayerChat(id, "[GoldSrcOps] %L", PLAYER_LANGUAGES[item], "GS_LANGUAGE_SELECTED", item == 0 ? "RU" : "EN",
                SettingPersistenceText(id));
        }
        OpenPlayerMenu(id);
    }
    return PLUGIN_HANDLED;
}

public OpenWeapons(id)
{
    if (get_pcvar_num(g_enabled) && IsPlaying(id))
    {
        remove_task(WELCOME_TASK_BASE + id);
        g_welcomed[id] = true;
        menu_display(id, g_menu[PlayerLanguage(id)]);
    }
    return PLUGIN_HANDLED;
}

public OnWeaponSelected(id, menu, item)
{
    if (item < 0 || item >= sizeof WEAPON_NAMES
        || !get_pcvar_num(g_enabled) || !IsPlaying(id)
        || menu != g_menu[PlayerLanguage(id)])
    {
        return PLUGIN_HANDLED;
    }

    if (g_choice[id] != item)
    {
        g_choice[id] = item;
        g_preferences_dirty[id] |= PREFERENCE_PRIMARY;
        SavePlayerPreferences(id);
        PlayerChat(id, "[GoldSrcOps] %L", PLAYER_LANGUAGES[EffectivePlayerLanguage(id)], "GS_PRIMARY_SELECTED", WEAPON_NAMES[item],
            SettingPersistenceText(id));
    }
    menu_display(id, g_pistol_menu[PlayerLanguage(id)]);
    return PLUGIN_HANDLED;
}

public OnPistolSelected(id, menu, item)
{
    if (item < 0 || item >= sizeof PISTOL_NAMES
        || !get_pcvar_num(g_enabled) || !IsPlaying(id)
        || menu != g_pistol_menu[PlayerLanguage(id)])
    {
        return PLUGIN_HANDLED;
    }

    if (g_pistol_choice[id] != item)
    {
        g_pistol_choice[id] = item;
        g_preferences_dirty[id] |= PREFERENCE_PISTOL;
        SavePlayerPreferences(id);
        PlayerChat(id, "[GoldSrcOps] %L", PLAYER_LANGUAGES[EffectivePlayerLanguage(id)], "GS_PISTOL_SELECTED",
            WEAPON_NAMES[g_choice[id]], PISTOL_NAMES[item], SettingPersistenceText(id));
    }
    return PLUGIN_HANDLED;
}

public OnSpawnPost(id)
{
    if (!get_pcvar_num(g_enabled) || !IsPlaying(id) || !is_user_alive(id))
    {
        return HC_CONTINUE;
    }

    EnsurePlayerPreferences(id);
    // Clear both slots before granting ammo: MP5 and Glock share 9mm reserve.
    new bool:primary_ready = bool:rg_remove_items_by_slot(id, PRIMARY_WEAPON_SLOT);
    new bool:pistol_ready = bool:rg_remove_items_by_slot(id, PISTOL_SLOT);
    if (primary_ready)
    {
        GivePrimary(id);
    }
    else
    {
        log_amx("Weapon selection: primary slot removal failed.");
    }
    if (pistol_ready)
    {
        GivePistol(id);
    }
    else
    {
        log_amx("Weapon selection: pistol slot removal failed.");
    }
    rg_set_user_armor(id, 100, ARMOR_VESTHELM);
    if (!g_welcomed[id] && !task_exists(WELCOME_TASK_BASE + id))
    {
        set_task(1.0, "ShowWelcome", WELCOME_TASK_BASE + id);
    }
    return HC_CONTINUE;
}

public ShowWelcome(task_id)
{
    new id = task_id - WELCOME_TASK_BASE;
    if (id < 1 || id > MaxClients || g_welcomed[id]
        || !get_pcvar_num(g_enabled) || !IsPlaying(id) || !is_user_alive(id))
    {
        return;
    }

    g_welcomed[id] = true;
    PlayerChat(id, "[GoldSrcOps] %L", PLAYER_LANGUAGES[PlayerLanguage(id)], "GS_WELCOME");
    EnsureSavedStats(id);
    if (g_saved_ready[id])
    {
        PrintPlayerRank(id);
    }
    new menu, keys;
    // Do not replace a team/class menu or another plugin's active menu.
    if (!get_user_menu(id, menu, keys))
    {
        menu_display(id, g_menu[PlayerLanguage(id)]);
    }
}

GivePrimary(id)
{
    new choice = g_choice[id];
    if (rg_give_item(id, WEAPON_CLASSES[choice]) <= 0)
    {
        log_amx("Weapon selection: selected primary unavailable; restoring MP5.");
        choice = 0;
        if (rg_give_item(id, WEAPON_CLASSES[choice]) <= 0)
        {
            return;
        }
    }

    rg_set_user_bpammo(id, WEAPON_IDS[choice], BACKPACK_AMMO[choice]);
}

GivePistol(id)
{
    new choice = g_pistol_choice[id];
    if (rg_give_item(id, PISTOL_CLASSES[choice]) <= 0)
    {
        log_amx("Weapon selection: selected pistol unavailable; restoring USP.");
        choice = 0;
        if (rg_give_item(id, PISTOL_CLASSES[choice]) <= 0)
        {
            return;
        }
    }
    rg_set_user_bpammo(id, PISTOL_IDS[choice], PISTOL_AMMO[choice]);
}

ResetMapStats(id)
{
    g_map_kills[id] = 0;
    g_map_deaths[id] = 0;
    g_kill_streak[id] = 0;
    g_best_streak[id] = 0;
    g_record_streak[id] = 0;
    for (new command = 0; command < sizeof g_stats_next[]; command++)
    {
        g_stats_next[id][command] = 0.0;
    }
}

bool:IsStatsClient(id)
{
    return id >= 1 && id <= MaxClients && is_user_connected(id)
        && !is_user_bot(id) && !is_user_hltv(id);
}

public OnMapStatsDeath()
{
    RecordMapDeath(read_data(1), read_data(2));
    HidePlayerHud(read_data(2));
}

HidePlayerHud(id)
{
    if (id < 1 || id > MaxClients || !g_hud_visible[id]) { return; }
    // Director messages cannot be cleared individually; the last frame expires after HUD_HOLD_TIME.
    g_hud_visible[id] = false;
}

BuildPlayerHudText(id, text[], length)
{
    new language = EffectivePlayerLanguage(id), rank_text[160];
    if (!g_saved_ready[id])
    {
        formatex(rank_text, charsmax(rank_text), "%L", PLAYER_LANGUAGES[language], "GS_HUD_RANK_UNAVAILABLE");
    }
    else
    {
        new rank = RankForKills(g_saved_kills[id]), rank_name[32], next_name[32];
        formatex(rank_name, charsmax(rank_name), "%L", PLAYER_LANGUAGES[language], RANK_KEYS[rank]);
        if (rank == sizeof RANK_KILLS - 1)
        {
            formatex(rank_text, charsmax(rank_text), "%L", PLAYER_LANGUAGES[language], "GS_HUD_RANK_HIGHEST", rank_name);
        }
        else
        {
            formatex(next_name, charsmax(next_name), "%L", PLAYER_LANGUAGES[language], RANK_KEYS[rank + 1]);
            formatex(rank_text, charsmax(rank_text), "%L", PLAYER_LANGUAGES[language], "GS_HUD_RANK_PROGRESS",
                rank_name, RANK_KILLS[rank + 1] - g_saved_kills[id], next_name);
        }
    }
    return formatex(text, length, "%L^n%L^n%s", PLAYER_LANGUAGES[language], "GS_HUD_MAP",
        g_map_kills[id], g_map_deaths[id], PLAYER_LANGUAGES[language], "GS_HUD_STREAK",
        g_kill_streak[id], rank_text);
}

public UpdatePlayerHuds()
{
    new bool:enabled = bool:get_pcvar_num(g_enabled);
    for (new id = 1; id <= MaxClients; id++)
    {
        if (!enabled || g_hud_disabled[id] || !IsPlaying(id) || !is_user_alive(id))
        {
            HidePlayerHud(id);
            continue;
        }
        new text[256];
        BuildPlayerHudText(id, text, charsmax(text));
        // Split the two statistics lines from the rank to fit each director message's byte limit.
        new rank_start = strfind(text, "^n", false, strfind(text, "^n") + 1);
        text[rank_start] = 0;
        // Keep a half-second margin beyond the refresh interval for delivery/timer jitter.
        set_dhudmessage(210, 225, 210, 0.02, 0.14, 0, 0.0, HUD_HOLD_TIME, 0.0, 0.0);
        show_dhudmessage(id, "%s", text);
        set_dhudmessage(210, 225, 210, 0.02, 0.21, 0, 0.0, HUD_HOLD_TIME, 0.0, 0.0);
        show_dhudmessage(id, "%s", text[rank_start + 1]);
        g_hud_visible[id] = true;
    }
}

RecordMapDeath(killer, victim)
{
    // Any death ends the current life, even when the encounter cannot score.
    if (victim >= 1 && victim <= MaxClients)
    {
        g_kill_streak[victim] = 0;
        g_record_streak[victim] = 0;
    }
    if (!get_pcvar_num(g_enabled) || !IsStatsClient(victim) || !IsPlaying(victim))
    {
        return;
    }
    // Ignore an entire bot/HLTV encounter, not just the killer's score.
    if (killer != 0 && (!IsStatsClient(killer) || !IsPlaying(killer)))
    {
        return;
    }
    EnsureSavedStats(victim);
    g_map_deaths[victim] = min(g_map_deaths[victim], STATS_COUNTER_LIMIT - 1) + 1;
    if (g_saved_ready[victim])
    {
        g_saved_deaths[victim] = min(g_saved_deaths[victim], STATS_COUNTER_LIMIT - 1) + 1;
        SavePlayerStats(victim);
    }
    if (killer != 0 && killer != victim
        && get_member(killer, m_iTeam) != get_member(victim, m_iTeam))
    {
        RestoreKillHealth(killer);
        RefillKillAmmo(killer);
        RecordKillStreak(killer);
        EnsureSavedStats(killer);
        g_map_kills[killer] = min(g_map_kills[killer], STATS_COUNTER_LIMIT - 1) + 1;
        if (g_saved_ready[killer])
        {
            new previous_rank = RankForKills(g_saved_kills[killer]);
            g_saved_kills[killer] = min(g_saved_kills[killer], STATS_COUNTER_LIMIT - 1) + 1;
            SavePlayerStats(killer);
            // Announce only a promotion confirmed by the existing storage readback.
            new rank = RankForKills(g_saved_kills[killer]);
            if (g_saved_ready[killer] && rank > previous_rank)
            {
                new rank_name[32];
                formatex(rank_name, charsmax(rank_name), "%L", PLAYER_LANGUAGES[PlayerLanguage(killer)], RANK_KEYS[rank]);
                client_print(killer, print_chat, "[GoldSrcOps] %L", PLAYER_LANGUAGES[PlayerLanguage(killer)], "GS_PROMOTION",
                    rank_name, g_saved_kills[killer]);
            }
        }
    }
}

RecordKillStreak(id)
{
    if (!is_user_alive(id)) { return; }
    g_kill_streak[id] = min(g_kill_streak[id], STATS_COUNTER_LIMIT - 1) + 1;
    g_best_streak[id] = max(g_best_streak[id], g_kill_streak[id]);
    new bool:record = RecordSavedStreak(id);
    if (!record && (g_kill_streak[id] == 3 || g_kill_streak[id] == 5 || g_kill_streak[id] == 10))
    {
        client_print(id, print_chat, "[GoldSrcOps] %L", PLAYER_LANGUAGES[PlayerLanguage(id)],
            "GS_STREAK_MILESTONE", g_kill_streak[id]);
    }
}

KillHealAmount()
{
    new Float:amount = get_pcvar_float(g_kill_heal);
    if (amount <= 0.0) { return 0; }
    if (amount >= 100.0) { return 100; }
    return floatround(amount, floatround_floor);
}

RestoreKillHealth(id)
{
    new amount = KillHealAmount();
    if (!amount || !is_user_alive(id)) { return; }
    new Float:health = get_entvar(id, var_health);
    if (health <= 0.0 || health >= 100.0) { return; }
    set_entvar(id, var_health, floatmin(health + float(amount), 100.0));
}

RefillKillAmmo(id)
{
    if (get_pcvar_num(g_kill_ammo) <= 0 || !is_user_alive(id)) { return; }
    for (new index = 0; index < sizeof WEAPON_IDS; index++)
    {
        TopUpWeaponReserve(id, WEAPON_IDS[index], BACKPACK_AMMO[index]);
    }
    for (new index = 0; index < sizeof PISTOL_IDS; index++)
    {
        TopUpWeaponReserve(id, PISTOL_IDS[index], PISTOL_AMMO[index]);
    }
}

TopUpWeaponReserve(id, WeaponIdType:weapon, limit)
{
    if (user_has_weapon(id, _:weapon) && rg_get_user_bpammo(id, weapon) < limit)
    {
        rg_set_user_bpammo(id, weapon, limit);
    }
}

Float:MapKillDeathRatio(id)
{
    return float(g_map_kills[id]) / float(max(g_map_deaths[id], 1));
}

bool:AllowStatsCommand(id, command)
{
    if (!get_pcvar_num(g_enabled) || !IsStatsClient(id))
    {
        return false;
    }
    new Float:now = get_gametime();
    if (now < g_stats_next[id][command])
    {
        return false;
    }
    g_stats_next[id][command] = now + (command >= 3 ? 5.0 : 2.0);
    return true;
}

public ShowMapStats(id)
{
    if (AllowStatsCommand(id, 0))
    {
        EnsureSavedStreak(id);
        if (g_streak_ready[id] && g_preferences_ready[id] && !g_preferences_blocked[id])
        {
            client_print(id, print_chat, "[GoldSrcOps] %L", PLAYER_LANGUAGES[PlayerLanguage(id)],
                "GS_STREAK_SAVED", g_saved_streak[id]);
        }
        else
        {
            client_print(id, print_chat, "[GoldSrcOps] %L", PLAYER_LANGUAGES[PlayerLanguage(id)], "GS_STREAK_UNAVAILABLE");
        }
        client_print(id, print_chat, "[GoldSrcOps] %L", PLAYER_LANGUAGES[PlayerLanguage(id)],
            "GS_STREAK_STATS", g_kill_streak[id], g_best_streak[id]);
        EnsureSavedStats(id);
        if (g_saved_ready[id])
        {
            client_print(id, print_chat, "[GoldSrcOps] %L", PLAYER_LANGUAGES[PlayerLanguage(id)], "GS_SAVED_STATS",
                g_saved_kills[id], g_saved_deaths[id], float(g_saved_kills[id]) / float(max(g_saved_deaths[id], 1)));
        }
        else
        {
            client_print(id, print_chat, "[GoldSrcOps] %L", PLAYER_LANGUAGES[PlayerLanguage(id)], "GS_MAP_STATS",
                g_map_kills[id], g_map_deaths[id], MapKillDeathRatio(id));
        }
    }
    return PLUGIN_HANDLED;
}

RankForKills(kills)
{
    for (new rank = sizeof RANK_KILLS - 1; rank > 0; rank--)
    {
        if (kills >= RANK_KILLS[rank])
        {
            return rank;
        }
    }
    return 0;
}

public ShowPlayerRank(id)
{
    if (AllowStatsCommand(id, 2))
    {
        PrintPlayerRank(id);
        if (g_saved_ready[id]) { PrintPlayerStanding(id); }
    }
    return PLUGIN_HANDLED;
}

PrintPlayerRank(id)
{
    EnsureSavedStats(id);
    if (!g_saved_ready[id])
    {
        PlayerChat(id, "[GoldSrcOps] %L", PLAYER_LANGUAGES[PlayerLanguage(id)], "GS_RANK_UNAVAILABLE");
        return;
    }
    new rank = RankForKills(g_saved_kills[id]);
    new rank_name[32], next_name[32];
    new language = PlayerLanguage(id);
    formatex(rank_name, charsmax(rank_name), "%L", PLAYER_LANGUAGES[language], RANK_KEYS[rank]);
    if (rank == sizeof RANK_KILLS - 1)
    {
        PlayerChat(id, "[GoldSrcOps] %L", PLAYER_LANGUAGES[language], "GS_RANK_HIGHEST",
            rank_name, g_saved_kills[id]);
    }
    else
    {
        formatex(next_name, charsmax(next_name), "%L", PLAYER_LANGUAGES[language], RANK_KEYS[rank + 1]);
        PlayerChat(id, "[GoldSrcOps] %L", PLAYER_LANGUAGES[language], "GS_RANK_PROGRESS",
            rank_name, g_saved_kills[id], RANK_KILLS[rank + 1] - g_saved_kills[id], next_name);
    }
}

BuildMapLeaders(leaders[MAP_LEADER_LIMIT])
{
    new count;
    new bool:used[MAX_PLAYERS + 1];
    for (new rank = 0; rank < MAP_LEADER_LIMIT; rank++)
    {
        new best;
        for (new id = 1; id <= MaxClients; id++)
        {
            if (used[id] || !IsStatsClient(id) || (!g_map_kills[id] && !g_map_deaths[id]))
            {
                continue;
            }
            // Kills descending, deaths ascending; exact ties retain slot order.
            if (!best || g_map_kills[id] > g_map_kills[best]
                || (g_map_kills[id] == g_map_kills[best] && g_map_deaths[id] < g_map_deaths[best]))
            {
                best = id;
            }
        }
        if (!best)
        {
            break;
        }
        used[best] = true;
        leaders[count++] = best;
    }
    return count;
}

CleanStatsName(name[])
{
    for (new index = 0; name[index]; index++)
    {
        if (name[index] < 32 || name[index] == 127)
        {
            name[index] = ' ';
        }
    }
}

public ShowMapLeaders(id)
{
    if (!AllowStatsCommand(id, 1))
    {
        return PLUGIN_HANDLED;
    }
    new leaders[MAP_LEADER_LIMIT];
    new count = BuildMapLeaders(leaders);
    if (!count)
    {
        client_print(id, print_chat, "[GoldSrcOps] %L", PLAYER_LANGUAGES[PlayerLanguage(id)], "GS_TOP_EMPTY");
        return PLUGIN_HANDLED;
    }
    client_print(id, print_chat, "[GoldSrcOps] %L", PLAYER_LANGUAGES[PlayerLanguage(id)], "GS_TOP_HEADER");
    for (new rank = 0; rank < count; rank++)
    {
        new player = leaders[rank];
        new name[32];
        get_user_name(player, name, charsmax(name));
        CleanStatsName(name);
        client_print(id, print_chat, "[GoldSrcOps] %L", PLAYER_LANGUAGES[PlayerLanguage(id)], "GS_TOP_ROW",
            rank + 1, name, g_map_kills[player], g_map_deaths[player], MapKillDeathRatio(player));
    }
    return PLUGIN_HANDLED;
}

public OnMapStatsStatus()
{
    new leaders[MAP_LEADER_LIMIT];
    server_print("MAP_STATS_STATUS version=0.32.0 enabled=%d scope=connection_map leaders=%d limit=5 cooldown=2 bot_encounters=excluded",
        get_pcvar_num(g_enabled) != 0, BuildMapLeaders(leaders));
    new loaded;
    for (new id = 1; id <= MaxClients; id++)
    {
        if (IsStatsClient(id) && g_saved_ready[id])
        {
            loaded++;
        }
    }
    server_print("PLAYER_STATS_STATUS version=0.32.0 storage=%s schema=1 identity=steam loaded=%d errors=%d",
        g_stats_vault == INVALID_HANDLE ? "unavailable" : "nvault", loaded, g_storage_errors);
    server_print("PLAYER_RANK_STATUS version=0.32.0 enabled=%d source=saved_kills tiers=5 thresholds=0,25,100,250,500 cooldown=2 rewards=none welcome=1 promotion=private_after_readback",
        get_pcvar_num(g_enabled) != 0);
    server_print("PLAYER_LEADERBOARD_STATUS version=0.32.0 ready=%d entries=%d limit=10 capacity=4096 cache_seconds=5 errors=%d source=saved_stats names=preferences display_ids=none",
        g_leader_ready, ArraySize(g_leader_records), g_leader_errors);
    server_print("PLAYER_STANDING_STATUS version=0.32.0 source=leaderboard_cache order=kills_deaths_key rank=private_line topall=outside_top10 storage_writes=none");
    server_print("PLAYER_RANK_GUIDE_STATUS version=0.32.0 display=private_motd languages=ru_en tiers=%d cooldown=5 source=rank_constants storage_writes=none", sizeof RANK_KILLS);
    server_print("PLAYER_PROFILE_STATUS version=0.32.0 display=private_motd languages=ru_en cooldown=5 source=loaded_player_state standing=leaderboard_cache storage_writes=none");
    new streak_loaded;
    for (new id = 1; id <= MaxClients; id++)
    {
        if (IsStatsClient(id) && g_streak_ready[id] && g_preferences_ready[id] && !g_preferences_blocked[id]) { streak_loaded++; }
    }
    server_print("PLAYER_STREAK_RECORD_STATUS version=0.32.0 storage=%s schema=1 identity=steam loaded=%d errors=%d writes=new_record feedback=private_after_readback history=since_feature",
        g_preferences_vault == INVALID_HANDLE ? "unavailable" : "nvault", streak_loaded, g_streak_errors);
    return PLUGIN_HANDLED;
}

ResetSavedStats(id)
{
    g_saved_key[id][0] = 0;
    g_saved_kills[id] = 0;
    g_saved_deaths[id] = 0;
    g_saved_ready[id] = false;
    g_saved_blocked[id] = false;
}

bool:BuildStatsKey(const authid[], key[], length)
{
    key[0] = 0;
    if (strlen(authid) < 11 || !equal(authid, "STEAM_", 6)
        || (authid[6] != '0' && authid[6] != '1') || authid[7] != ':'
        || (authid[8] != '0' && authid[8] != '1') || authid[9] != ':')
    {
        return false;
    }
    new digits = strlen(authid) - 10;
    if (digits > 10 || (digits > 1 && authid[10] == '0'))
    {
        return false;
    }
    for (new index = 10; authid[index]; index++)
    {
        if (authid[index] < '0' || authid[index] > '9')
        {
            return false;
        }
    }
    if ((digits == 10 && strcmp(authid[10], "2147483647") > 0)
        || (authid[8] == '0' && equal(authid[10], "0")))
    {
        return false;
    }
    // CS 1.6 may render the same public Steam account in universe 0 or 1.
    formatex(key, length, "v1:STEAM_0:%c:%s", authid[8], authid[10]);
    return true;
}

bool:ParseStatsCounter(const text[], &value)
{
    value = 0;
    if (!text[0])
    {
        return false;
    }
    for (new index = 0; text[index]; index++)
    {
        if (text[index] < '0' || text[index] > '9'
            || value > (STATS_COUNTER_LIMIT - (text[index] - '0')) / 10)
        {
            return false;
        }
        value = value * 10 + text[index] - '0';
    }
    return true;
}

bool:DecodeSavedStats(const value[], &kills, &deaths)
{
    new schema[8], kill_text[16], death_text[16], extra[8], canonical[40];
    if (parse(value, schema, charsmax(schema), kill_text, charsmax(kill_text),
        death_text, charsmax(death_text), extra, charsmax(extra)) != 3
        || !equal(schema, "1") || !ParseStatsCounter(kill_text, kills)
        || !ParseStatsCounter(death_text, deaths))
    {
        return false;
    }
    formatex(canonical, charsmax(canonical), "1 %d %d", kills, deaths);
    return bool:equal(value, canonical);
}

EnsureSavedStats(id)
{
    if (g_stats_vault == INVALID_HANDLE || !IsStatsClient(id)
        || g_saved_ready[id] || g_saved_blocked[id])
    {
        return;
    }
    new authid[32], key[40];
    get_user_authid(id, authid, charsmax(authid));
    if (!BuildStatsKey(authid, key, charsmax(key)))
    {
        return;
    }
    for (new other = 1; other <= MaxClients; other++)
    {
        if (other != id && g_saved_ready[other] && equal(g_saved_key[other], key))
        {
            return;
        }
    }
    new value[64], timestamp, kills, deaths;
    if (nvault_lookup(g_stats_vault, key, value, charsmax(value), timestamp)
        && !DecodeSavedStats(value, kills, deaths))
    {
        g_saved_blocked[id] = true;
        g_storage_errors++;
        log_amx("Player stats record rejected; existing data retained, connection/map stats only.");
        RejectLeaderIndex();
        return;
    }
    copy(g_saved_key[id], charsmax(g_saved_key[]), key);
    g_saved_kills[id] = kills;
    g_saved_deaths[id] = deaths;
    g_saved_ready[id] = true;
    UpdateLeaderScore(g_saved_key[id], kills, deaths);
}

SavePlayerStats(id)
{
    new value[40], stored[64], timestamp;
    formatex(value, charsmax(value), "1 %d %d", g_saved_kills[id], g_saved_deaths[id]);
    nvault_set(g_stats_vault, g_saved_key[id], value);
    if (!nvault_lookup(g_stats_vault, g_saved_key[id], stored, charsmax(stored), timestamp)
        || !equal(value, stored))
    {
        g_saved_ready[id] = false;
        g_saved_blocked[id] = true;
        g_storage_errors++;
        log_amx("Player stats readback failed; connection/map stats only until reconnect.");
        RejectLeaderIndex();
        return;
    }
    UpdateLeaderScore(g_saved_key[id], g_saved_kills[id], g_saved_deaths[id]);
    RememberLeaderName(id);
}

ResetPlayerPreferences(id)
{
    g_preferences_key[id][0] = 0;
    g_preferences_ready[id] = false;
    g_preferences_blocked[id] = false;
    g_preferences_dirty[id] = 0;
    g_saved_streak[id] = 0;
    g_record_streak[id] = 0;
    g_streak_ready[id] = false;
    g_streak_blocked[id] = false;
    g_hud_preference_ready[id] = false;
    g_hud_preference_blocked[id] = false;
    g_hud_preference_dirty[id] = false;
}

bool:DecodePlayerPreferences(const value[], &language, &primary, &pistol)
{
    new schema[8], language_text[8], primary_text[8], pistol_text[8], extra[8], canonical[32];
    if (parse(value, schema, charsmax(schema), language_text, charsmax(language_text),
        primary_text, charsmax(primary_text), pistol_text, charsmax(pistol_text), extra, charsmax(extra)) != 4
        || !equal(schema, "1") || !ParseStatsCounter(language_text, language)
        || !ParseStatsCounter(primary_text, primary) || !ParseStatsCounter(pistol_text, pistol)
        || language > sizeof PLAYER_LANGUAGES || primary >= sizeof WEAPON_NAMES || pistol >= sizeof PISTOL_NAMES)
    {
        return false;
    }
    formatex(canonical, charsmax(canonical), "1 %d %d %d", language, primary, pistol);
    return bool:equal(value, canonical);
}

EnsurePlayerPreferences(id)
{
    if (g_preferences_vault == INVALID_HANDLE || !get_pcvar_num(g_enabled) || !IsStatsClient(id)
        || g_preferences_blocked[id])
    {
        return;
    }
    if (g_preferences_ready[id]) { EnsureHudPreference(id); return; }
    new authid[32], key[40];
    get_user_authid(id, authid, charsmax(authid));
    if (!BuildStatsKey(authid, key, charsmax(key))) { return; }
    for (new other = 1; other <= MaxClients; other++)
    {
        if (other != id && IsStatsClient(other) && equal(g_preferences_key[other], key)) { return; }
    }
    copy(g_preferences_key[id], charsmax(g_preferences_key[]), key);
    new value[32], timestamp, language, primary, pistol;
    if (nvault_lookup(g_preferences_vault, key, value, charsmax(value), timestamp))
    {
        if (!DecodePlayerPreferences(value, language, primary, pistol))
        {
            g_preferences_blocked[id] = true;
            g_preferences_errors++;
            log_amx("Player preferences record rejected; existing data retained, selections connection-only.");
            return;
        }
        // Late authorization must not replace choices made during this connection.
        if (!(g_preferences_dirty[id] & PREFERENCE_LANGUAGE)) { g_language_override[id] = language; }
        if (!(g_preferences_dirty[id] & PREFERENCE_PRIMARY)) { g_choice[id] = primary; }
        if (!(g_preferences_dirty[id] & PREFERENCE_PISTOL)) { g_pistol_choice[id] = pistol; }
    }
    g_preferences_ready[id] = true;
    if (g_preferences_dirty[id]) { SavePlayerPreferences(id); }
    EnsureHudPreference(id);
}

SavePlayerPreferences(id)
{
    if (!get_pcvar_num(g_enabled) || !IsStatsClient(id)) { return; }
    EnsurePlayerPreferences(id);
    if (!g_preferences_ready[id] || !g_preferences_dirty[id]) { return; }
    new value[32], stored[32], timestamp;
    formatex(value, charsmax(value), "1 %d %d %d", g_language_override[id], g_choice[id], g_pistol_choice[id]);
    nvault_set(g_preferences_vault, g_preferences_key[id], value);
    if (!nvault_lookup(g_preferences_vault, g_preferences_key[id], stored, charsmax(stored), timestamp)
        || !equal(value, stored))
    {
        g_preferences_ready[id] = false;
        g_preferences_blocked[id] = true;
        g_preferences_errors++;
        log_amx("Player preferences readback failed; selections connection-only until reconnect.");
        return;
    }
    g_preferences_dirty[id] = 0;
}

bool:DecodeSavedStreak(const value[], &best)
{
    new schema[8], counter[16], extra[8], canonical[24];
    if (parse(value, schema, charsmax(schema), counter, charsmax(counter), extra, charsmax(extra)) != 2
        || !equal(schema, "1") || !ParseStatsCounter(counter, best)) { return false; }
    formatex(canonical, charsmax(canonical), "1 %d", best);
    return bool:equal(value, canonical);
}

EnsureSavedStreak(id)
{
    if (g_preferences_vault == INVALID_HANDLE || !get_pcvar_num(g_enabled) || !IsStatsClient(id)
        || g_streak_blocked[id]) { return; }
    EnsurePlayerPreferences(id);
    if (!g_preferences_ready[id] || g_preferences_blocked[id] || g_streak_ready[id]) { return; }
    new key[48], value[64], timestamp, best;
    formatex(key, charsmax(key), "streak:%s", g_preferences_key[id]);
    if (nvault_lookup(g_preferences_vault, key, value, charsmax(value), timestamp)
        && !DecodeSavedStreak(value, best))
    {
        g_streak_blocked[id] = true;
        g_streak_errors++;
        log_amx("Player streak record rejected; existing data retained, connection/map streak only.");
        return;
    }
    g_saved_streak[id] = best;
    // Count only eligible kills after identity/storage became available; never import an unknown life.
    g_record_streak[id] = 0;
    g_streak_ready[id] = true;
}

bool:RecordSavedStreak(id)
{
    if (!get_pcvar_num(g_enabled) || !IsPlaying(id) || !is_user_alive(id)) { return false; }
    EnsureSavedStreak(id);
    if (g_preferences_vault == INVALID_HANDLE || !g_preferences_ready[id] || g_preferences_blocked[id]
        || !g_streak_ready[id] || g_streak_blocked[id]) { return false; }
    g_record_streak[id] = min(g_record_streak[id], STATS_COUNTER_LIMIT - 1) + 1;
    if (g_record_streak[id] <= g_saved_streak[id]) { return false; }
    new key[48], value[24], stored[64], timestamp;
    formatex(key, charsmax(key), "streak:%s", g_preferences_key[id]);
    formatex(value, charsmax(value), "1 %d", g_record_streak[id]);
    nvault_set(g_preferences_vault, key, value);
    if (!nvault_lookup(g_preferences_vault, key, stored, charsmax(stored), timestamp) || !equal(value, stored))
    {
        g_streak_ready[id] = false;
        g_streak_blocked[id] = true;
        g_streak_errors++;
        log_amx("Player streak readback failed; no further record writes until reconnect.");
        return false;
    }
    g_saved_streak[id] = g_record_streak[id];
    client_print(id, print_chat, "[GoldSrcOps] %L", PLAYER_LANGUAGES[PlayerLanguage(id)],
        "GS_STREAK_RECORD", g_saved_streak[id]);
    return true;
}

BuildHudPreferenceKey(id, key[], length)
{
    formatex(key, length, "hud:%s", g_preferences_key[id]);
}

bool:DecodeHudPreference(const value[], &bool:disabled)
{
    if (equal(value, "1 0")) { disabled = false; return true; }
    if (equal(value, "1 1")) { disabled = true; return true; }
    return false;
}

EnsureHudPreference(id)
{
    if (g_preferences_vault == INVALID_HANDLE || !get_pcvar_num(g_enabled) || !IsStatsClient(id)
        || !g_preferences_ready[id] || g_preferences_blocked[id]
        || g_hud_preference_ready[id] || g_hud_preference_blocked[id]) { return; }
    new key[48], value[32], timestamp, bool:disabled;
    BuildHudPreferenceKey(id, key, charsmax(key));
    if (nvault_lookup(g_preferences_vault, key, value, charsmax(value), timestamp))
    {
        if (!DecodeHudPreference(value, disabled))
        {
            g_hud_preference_blocked[id] = true;
            g_preferences_errors++;
            log_amx("HUD preference record rejected; existing data retained, HUD connection-only.");
            return;
        }
        // Preserve an explicit toggle made before authorization arrived.
        if (!g_hud_preference_dirty[id]) { g_hud_disabled[id] = disabled; }
    }
    g_hud_preference_ready[id] = true;
    if (g_hud_preference_dirty[id]) { SaveHudPreference(id); }
}

SaveHudPreference(id)
{
    if (!get_pcvar_num(g_enabled) || !IsStatsClient(id)) { return; }
    EnsurePlayerPreferences(id);
    if (g_preferences_vault == INVALID_HANDLE || !g_preferences_ready[id] || g_preferences_blocked[id]
        || !g_hud_preference_ready[id] || g_hud_preference_blocked[id] || !g_hud_preference_dirty[id]) { return; }
    new key[48], value[8], stored[32], timestamp;
    BuildHudPreferenceKey(id, key, charsmax(key));
    formatex(value, charsmax(value), "1 %d", g_hud_disabled[id]);
    nvault_set(g_preferences_vault, key, value);
    if (!nvault_lookup(g_preferences_vault, key, stored, charsmax(stored), timestamp) || !equal(value, stored))
    {
        g_hud_preference_ready[id] = false;
        g_hud_preference_blocked[id] = true;
        g_preferences_errors++;
        log_amx("HUD preference readback failed; HUD connection-only until reconnect.");
        return;
    }
    g_hud_preference_dirty[id] = false;
}

bool:PlayerHudPreferenceSaved(id)
{
    if (g_preferences_vault == INVALID_HANDLE || !g_preferences_ready[id] || g_preferences_blocked[id]
        || !g_hud_preference_ready[id] || g_hud_preference_blocked[id] || g_hud_preference_dirty[id]) { return false; }
    new key[48], value[32], timestamp, bool:disabled;
    BuildHudPreferenceKey(id, key, charsmax(key));
    return bool:(nvault_lookup(g_preferences_vault, key, value, charsmax(value), timestamp)
        && DecodeHudPreference(value, disabled) && disabled == g_hud_disabled[id]);
}


// The index is derived before nvault_open replays and erases the journal.
// Counts always come from native lookup after replay; source files are never rewritten here.
PrepareLeaderIndex()
{
    g_leader_records = ArrayCreate(LeaderRecord);
    g_leader_keys = TrieCreate();
    g_leader_ready = true;
    new directory[192], path[256];
    get_localinfo("amxx_datadir", directory, charsmax(directory));
    formatex(path, charsmax(path), "%s/vault/%s.vault", directory, STATS_VAULT_NAME);
    if (!ReadLeaderVault(path)) { RejectLeaderIndex(); }
    formatex(path, charsmax(path), "%s/vault/%s.journal", directory, STATS_VAULT_NAME);
    if (g_leader_ready && !ReadLeaderJournal(path)) { RejectLeaderIndex(); }
}

RejectLeaderIndex()
{
    if (g_leader_ready)
    {
        g_leader_errors++;
        log_amx("Saved leaderboard unavailable; player stats retained. Inspect storage before next map load.");
    }
    g_leader_ready = false;
    g_leader_count = 0;
    g_leader_total = 0;
}

bool:AddLeaderKey(const key[], bool:unique = false)
{
    new canonical[40], index;
    if (!equal(key, "v1:", 3) || !BuildStatsKey(key[3], canonical, charsmax(canonical)) || !equal(key, canonical)) { return false; }
    if (TrieGetCell(g_leader_keys, key, index)) { return !unique; }
    if (ArraySize(g_leader_records) >= LEADER_INDEX_LIMIT) { return false; }
    new record[LeaderRecord];
    copy(record[LeaderKey], charsmax(record[LeaderKey]), key);
    record[LeaderKills] = -1;
    record[LeaderCachedKills] = -1;
    TrieSetCell(g_leader_keys, key, ArrayPushArray(g_leader_records, record));
    return true;
}

bool:ReadLeaderNumber(file, &value, bytes)
{
    value = 0;
    // Pinned AMXX 1.10 fread reports bytes, including short reads.
    return bool:(fread(file, value, bytes) == bytes);
}

bool:ReadLeaderString(file, text[], length, bytes)
{
    if (bytes < 1 || bytes > length || fread_blocks(file, text, bytes, BLOCK_CHAR) != bytes) { return false; }
    text[bytes] = 0;
    return strlen(text) == bytes;
}

bool:ReadLeaderVault(const path[])
{
    if (!file_exists(path)) { return true; }
    g_leader_reads++;
    new size = file_size(path);
    if (size < 0 || size > LEADER_FILE_LIMIT) { return false; }
    // nVault creates an empty file before the first map closes.
    if (!size) { return true; }
    new file = fopen(path, "rb");
    if (!file) { return false; }
    new magic, version, count;
    new bool:valid = ReadLeaderNumber(file, magic, BLOCK_INT) && magic == 0x6E564C54
        && ReadLeaderNumber(file, version, BLOCK_SHORT) && version == 0x0200
        && ReadLeaderNumber(file, count, BLOCK_INT) && count >= 0 && count <= LEADER_INDEX_LIMIT;
    for (new index = 0; valid && index < count; index++)
    {
        new stamp, key_length, value_length, key[40], value[64];
        valid = ReadLeaderNumber(file, stamp, BLOCK_INT)
            && ReadLeaderNumber(file, key_length, BLOCK_CHAR)
            && ReadLeaderNumber(file, value_length, BLOCK_SHORT)
            && ReadLeaderString(file, key, charsmax(key), key_length)
            && ReadLeaderString(file, value, charsmax(value), value_length)
            && AddLeaderKey(key, true);
    }
    valid = valid && ftell(file) == size;
    fclose(file);
    return valid;
}

bool:ReadLeaderJournal(const path[])
{
    if (!file_exists(path)) { return true; }
    g_leader_reads++;
    new size = file_size(path);
    if (size < 0 || size > LEADER_FILE_LIMIT) { return false; }
    new file = fopen(path, "rb");
    if (!file) { return false; }
    new operations;
    new bool:valid = true;
    while (valid && ftell(file) < size)
    {
        new operation, stamp, end, key_length, value_length, key[40], value[64];
        valid = ++operations <= LEADER_JOURNAL_OP_LIMIT && ReadLeaderNumber(file, operation, BLOCK_CHAR);
        if (!valid) { break; }
        switch (operation)
        {
            case 0: { valid = ftell(file) == size; break; }
            case 1: { /* Clear is applied by native replay; a key superset remains sufficient. */ }
            case 2: { valid = ReadLeaderNumber(file, stamp, BLOCK_INT) && ReadLeaderNumber(file, end, BLOCK_INT); }
            case 3:
            {
                valid = ReadLeaderNumber(file, stamp, BLOCK_INT)
                    && ReadLeaderNumber(file, key_length, BLOCK_CHAR)
                    && ReadLeaderString(file, key, charsmax(key), key_length)
                    && ReadLeaderNumber(file, value_length, BLOCK_SHORT)
                    && ReadLeaderString(file, value, charsmax(value), value_length)
                    && AddLeaderKey(key);
            }
            case 4:
            {
                valid = ReadLeaderNumber(file, key_length, BLOCK_CHAR)
                    && ReadLeaderString(file, key, charsmax(key), key_length)
                    && AddLeaderKey(key);
            }
            default: valid = false;
        }
    }
    valid = valid && ftell(file) == size;
    fclose(file);
    return valid;
}

LoadLeaderScores()
{
    if (!g_leader_ready) { return; }
    if (g_stats_vault == INVALID_HANDLE) { RejectLeaderIndex(); return; }
    for (new index = 0; index < ArraySize(g_leader_records); index++)
    {
        new record[LeaderRecord], value[64], stamp;
        ArrayGetArray(g_leader_records, index, record);
        if (!nvault_lookup(g_stats_vault, record[LeaderKey], value, charsmax(value), stamp)) { continue; }
        if (!DecodeSavedStats(value, record[LeaderKills], record[LeaderDeaths])) { RejectLeaderIndex(); return; }
        LoadSearchName(record);
        ArraySetArray(g_leader_records, index, record);
    }
    g_leader_dirty = true;
}

UpdateLeaderScore(const key[], kills, deaths)
{
    if (!g_leader_ready) { return; }
    if (!AddLeaderKey(key)) { RejectLeaderIndex(); return; }
    new index, record[LeaderRecord];
    TrieGetCell(g_leader_keys, key, index);
    ArrayGetArray(g_leader_records, index, record);
    if (record[LeaderKills] == kills && record[LeaderDeaths] == deaths) { return; }
    record[LeaderKills] = kills;
    record[LeaderDeaths] = deaths;
    ArraySetArray(g_leader_records, index, record);
    g_leader_dirty = true;
}

RememberLeaderName(id)
{
    if (!get_pcvar_num(g_enabled) || !IsStatsClient(id) || !g_saved_ready[id]
        || g_leader_name_recorded[id] || g_preferences_vault == INVALID_HANDLE) { return; }
    g_leader_name_recorded[id] = true;
    new name[32], key[48], value[40], stored[64], stamp;
    get_user_name(id, name, charsmax(name));
    CleanStatsName(name);
    trim(name);
    if (!name[0]) { return; }
    formatex(key, charsmax(key), "name:%s", g_saved_key[id]);
    formatex(value, charsmax(value), "1 %s", name);
    if (nvault_lookup(g_preferences_vault, key, stored, charsmax(stored), stamp) && equal(value, stored))
    {
        UpdateSearchName(g_saved_key[id], name);
        return;
    }
    nvault_set(g_preferences_vault, key, value);
    if (nvault_lookup(g_preferences_vault, key, stored, charsmax(stored), stamp) && equal(value, stored))
    {
        UpdateSearchName(g_saved_key[id], name);
        g_leader_dirty = true;
    }
}

bool:LeaderBefore(const candidate[LeaderRecord], const other[LeaderRecord])
{
    return candidate[LeaderKills] > other[LeaderKills]
        || (candidate[LeaderKills] == other[LeaderKills] && (candidate[LeaderDeaths] < other[LeaderDeaths]
        || (candidate[LeaderDeaths] == other[LeaderDeaths] && strcmp(candidate[LeaderKey], other[LeaderKey]) < 0)));
}

RefreshSavedLeaders()
{
    if (!g_leader_ready || !g_leader_dirty || get_gametime() < g_leader_refresh_after) { return; }
    g_leader_count = 0;
    g_leader_total = 0;
    g_leader_rebuilds++;
    for (new index = 0; index < ArraySize(g_leader_records); index++)
    {
        new candidate[LeaderRecord], other[LeaderRecord], position;
        ArrayGetArray(g_leader_records, index, candidate);
        candidate[LeaderCachedKills] = candidate[LeaderKills];
        candidate[LeaderCachedDeaths] = candidate[LeaderDeaths];
        ArraySetArray(g_leader_records, index, candidate);
        if (candidate[LeaderKills] < 0 || (!candidate[LeaderKills] && !candidate[LeaderDeaths])) { continue; }
        g_leader_total++;
        while (position < g_leader_count)
        {
            ArrayGetArray(g_leader_records, g_leader_top[position], other);
            if (LeaderBefore(candidate, other)) { break; }
            position++;
        }
        if (position == SAVED_LEADER_LIMIT) { continue; }
        for (new move = min(g_leader_count, SAVED_LEADER_LIMIT - 1); move > position; move--) { g_leader_top[move] = g_leader_top[move - 1]; }
        g_leader_top[position] = index;
        g_leader_count = min(g_leader_count + 1, SAVED_LEADER_LIMIT);
    }
    for (new rank = 0; rank < g_leader_count; rank++)
    {
        new record[LeaderRecord], key[48], value[64], stamp;
        ArrayGetArray(g_leader_records, g_leader_top[rank], record);
        g_leader_scores[rank][0] = record[LeaderKills];
        g_leader_scores[rank][1] = record[LeaderDeaths];
        g_leader_names[rank][0] = 0;
        formatex(key, charsmax(key), "name:%s", record[LeaderKey]);
        if (g_preferences_vault != INVALID_HANDLE && nvault_lookup(g_preferences_vault, key, value, charsmax(value), stamp)
            && equal(value, "1 ", 2) && strlen(value) >= 3 && strlen(value) <= 33)
        {
            copy(g_leader_names[rank], charsmax(g_leader_names[]), value[2]);
            CleanStatsName(g_leader_names[rank]);
            trim(g_leader_names[rank]);
        }
    }
    g_leader_dirty = false;
    g_leader_refresh_after = get_gametime() + LEADER_REFRESH_INTERVAL;
}

// Use the same frozen scores and denominator as the displayed top ten.
// Positive results are positions; zero is unranked, -1 unavailable, -2 awaiting refresh.
SavedPlayerPosition(id)
{
    if (!g_leader_ready || !g_saved_ready[id]) { return -1; }
    RefreshSavedLeaders();
    new index, candidate[LeaderRecord];
    if (!TrieGetCell(g_leader_keys, g_saved_key[id], index)) { return -1; }
    ArrayGetArray(g_leader_records, index, candidate);
    candidate[LeaderKills] = candidate[LeaderCachedKills];
    candidate[LeaderDeaths] = candidate[LeaderCachedDeaths];
    if (candidate[LeaderKills] < 0 || (!candidate[LeaderKills] && !candidate[LeaderDeaths]))
    {
        return (g_saved_kills[id] || g_saved_deaths[id]) ? -2 : 0;
    }
    new position = 1;
    for (new other_index = 0; other_index < ArraySize(g_leader_records); other_index++)
    {
        new other[LeaderRecord];
        ArrayGetArray(g_leader_records, other_index, other);
        other[LeaderKills] = other[LeaderCachedKills];
        other[LeaderDeaths] = other[LeaderCachedDeaths];
        if (other[LeaderKills] >= 0 && (other[LeaderKills] || other[LeaderDeaths]) && LeaderBefore(other, candidate)) { position++; }
    }
    return position;
}

FormatPlayerStanding(id, text[], length)
{
    new position = SavedPlayerPosition(id), language = EffectivePlayerLanguage(id);
    switch (position)
    {
        case -2: formatex(text, length, "%L", PLAYER_LANGUAGES[language], "GS_STANDING_PENDING");
        case -1: formatex(text, length, "%L", PLAYER_LANGUAGES[language], "GS_STANDING_UNAVAILABLE");
        case 0: formatex(text, length, "%L", PLAYER_LANGUAGES[language], "GS_STANDING_UNRANKED");
        default: formatex(text, length, "%L", PLAYER_LANGUAGES[language], "GS_STANDING_POSITION", position, g_leader_total);
    }
    return position;
}

PrintPlayerStanding(id)
{
    new text[160];
    FormatPlayerStanding(id, text, charsmax(text));
    PlayerChat(id, "[GoldSrcOps] %s", text);
}

EscapeLeaderName(const name[], output[], length)
{
    new written;
    output[0] = 0;
    for (new index = 0; name[index]; )
    {
        new token[8], bytes = max(1, get_char_bytes(name[index]));
        switch (name[index])
        {
            case '&': copy(token, charsmax(token), "&amp;");
            case '<': copy(token, charsmax(token), "&lt;");
            case '>': copy(token, charsmax(token), "&gt;");
            case 34: copy(token, charsmax(token), "&quot;");
            case 39: copy(token, charsmax(token), "&#39;");
            default: copy(token, min(bytes, charsmax(token)), name[index]);
        }
        if (written + strlen(token) > length - 3) { add(output, length, "..."); break; }
        add(output, length, token);
        written = strlen(output);
        index += bytes;
    }
}

// Fixed read-only transport. Keys and player identifiers never enter this frame.
public OnLeaderboardSnapshot()
{
    if (!get_pcvar_num(g_enabled) || !g_leader_ready)
    {
        server_print("GSLEADER 1 unavailable");
        return PLUGIN_HANDLED;
    }
    RefreshSavedLeaders();
    new stamp = get_systime();
    server_print("GSLEADER 1 %d %d", stamp, g_leader_count);
    for (new rank = 0; rank < g_leader_count; rank++)
    {
        new encoded[63], written;
        for (new index = 0; g_leader_names[rank][index]; index++)
        {
            written += formatex(encoded[written], charsmax(encoded) - written, "%02X", g_leader_names[rank][index] & 255);
        }
        if (!written) { copy(encoded, charsmax(encoded), "-"); }
        server_print("GSLROW %d %d %d %d %s", rank + 1, RankForKills(g_leader_scores[rank][0]),
            g_leader_scores[rank][0], g_leader_scores[rank][1], encoded);
    }
    server_print("GSLEND 1 %d %d", stamp, g_leader_count);
    return PLUGIN_HANDLED;
}

// Strict UTF-8 scalar validation, excluding C0/C1 controls and private identifiers.
bool:ValidSearchName(const name[])
{
    new length = strlen(name);
    if (length < 1 || length > 31 || containi(name, "STEAM_") >= 0) { return false; }
    for (new index = 0; index < length; )
    {
        new first = name[index] & 255, bytes, codepoint, minimum;
        if (first < 128) { bytes = 1; codepoint = first; minimum = 0; }
        else if (first >= 194 && first <= 223) { bytes = 2; codepoint = first & 31; minimum = 128; }
        else if (first >= 224 && first <= 239) { bytes = 3; codepoint = first & 15; minimum = 2048; }
        else if (first >= 240 && first <= 244) { bytes = 4; codepoint = first & 7; minimum = 65536; }
        else { return false; }
        if (index + bytes > length) { return false; }
        for (new offset = 1; offset < bytes; offset++)
        {
            new continuation = name[index + offset] & 255;
            if (continuation < 128 || continuation > 191) { return false; }
            codepoint = (codepoint << 6) | (continuation & 63);
        }
        if (codepoint < minimum || codepoint > 1114111 || (codepoint >= 55296 && codepoint <= 57343)
            || codepoint < 32 || (codepoint >= 127 && codepoint <= 159)) { return false; }
        index += bytes;
    }
    return true;
}

LoadSearchName(record[LeaderRecord])
{
    record[LeaderSearchName][0] = 0;
    if (g_preferences_vault == INVALID_HANDLE) { return; }
    new key[48], value[64], stamp;
    formatex(key, charsmax(key), "name:%s", record[LeaderKey]);
    if (!nvault_lookup(g_preferences_vault, key, value, charsmax(value), stamp)
        || !equal(value, "1 ", 2) || strlen(value) > 33 || !ValidSearchName(value[2])) { return; }
    copy(record[LeaderSearchName], charsmax(record[LeaderSearchName]), value[2]);
    trim(record[LeaderSearchName]);
}

UpdateSearchName(const key[], const name[])
{
    new index, record[LeaderRecord];
    if (!g_leader_ready || !TrieGetCell(g_leader_keys, key, index)) { return; }
    ArrayGetArray(g_leader_records, index, record);
    record[LeaderSearchName][0] = 0;
    if (ValidSearchName(name))
    {
        copy(record[LeaderSearchName], charsmax(record[LeaderSearchName]), name);
        trim(record[LeaderSearchName]);
    }
    ArraySetArray(g_leader_records, index, record);
}

LookupHexDigit(character)
{
    if (character >= '0' && character <= '9') { return character - '0'; }
    if (character >= 'A' && character <= 'F') { return character - 'A' + 10; }
    if (character >= 'a' && character <= 'f') { return character - 'a' + 10; }
    return -1;
}

bool:DecodeSearchQuery(const encoded[], name[])
{
    new length = strlen(encoded);
    name[0] = 0;
    if (length < 2 || length > 62 || length % 2) { return false; }
    for (new index = 0; index < length; index += 2)
    {
        new high = LookupHexDigit(encoded[index]), low = LookupHexDigit(encoded[index + 1]);
        if (high < 0 || low < 0 || !(high | low)) { return false; }
        name[index / 2] = (high << 4) | low;
    }
    name[length / 2] = 0;
    if (!ValidSearchName(name)) { return false; }
    trim(name);
    return bool:name[0];
}

// Fixed length-preserving case map shared by the substring protocol contract.
// Input is already validated UTF-8; other alphabets and normalization stay literal.
FoldSearchName(const input[], output[32])
{
    copy(output, charsmax(output), input);
    for (new index = 0; output[index]; index++)
    {
        new first = output[index] & 255, second = output[index + 1] & 255;
        if (first >= 65 && first <= 90) { output[index] = first + 32; }
        else if (first == 208 && second >= 144 && second <= 175)
        {
            output[index] = second < 160 ? 208 : 209;
            output[index + 1] = second < 160 ? second + 32 : second - 32;
            index++;
        }
        else if (first == 208 && second == 129)
        {
            output[index] = 209;
            output[index + 1] = 145;
            index++;
        }
    }
}

// Called only after strict validation and outer ASCII-space trimming.
bool:LongEnoughSearchSubstring(const name[])
{
    new scalars;
    for (new index = 0; name[index]; index++)
    {
        if ((name[index] & 192) != 128) { scalars++; }
    }
    return scalars >= 3;
}

// Single engine callback: current saved counters and cached names cannot change mid-scan.
FindSavedPlayers(const name[], matches[SAVED_LEADER_LIMIT], &count, &total, bool:partial = false)
{
    new folded_query[32];
    if (partial) { FoldSearchName(name, folded_query); }
    count = 0;
    total = 0;
    for (new index = 0; index < ArraySize(g_leader_records); index++)
    {
        new candidate[LeaderRecord], other[LeaderRecord], position;
        ArrayGetArray(g_leader_records, index, candidate);
        if (candidate[LeaderKills] < 0 || (!candidate[LeaderKills] && !candidate[LeaderDeaths])
            || !candidate[LeaderSearchName][0]) { continue; }
        if (partial)
        {
            new folded_name[32];
            FoldSearchName(candidate[LeaderSearchName], folded_name);
            if (contain(folded_name, folded_query) < 0) { continue; }
        }
        else if (!equal(candidate[LeaderSearchName], name)) { continue; }
        total++;
        while (position < count)
        {
            ArrayGetArray(g_leader_records, matches[position], other);
            if (LeaderBefore(candidate, other)) { break; }
            position++;
        }
        if (position == SAVED_LEADER_LIMIT) { continue; }
        for (new move = min(count, SAVED_LEADER_LIMIT - 1); move > position; move--) { matches[move] = matches[move - 1]; }
        matches[position] = index;
        count = min(count + 1, SAVED_LEADER_LIMIT);
    }
}

public OnPlayerLookup()
{
    new nonce[18], encoded[64];
    read_argv(1, nonce, charsmax(nonce));
    read_argv(2, encoded, charsmax(encoded));
    if (read_argc() != 3) { server_print("GSLOOKUP 1 invalid"); return PLUGIN_HANDLED; }
    EmitPlayerLookup(nonce, encoded);
    return PLUGIN_HANDLED;
}

public OnPlayerLookupV2()
{
    new nonce[18], encoded[64];
    read_argv(1, nonce, charsmax(nonce));
    read_argv(2, encoded, charsmax(encoded));
    // A malformed v2 argument list with a valid nonce consumes shared admission.
    if (read_argc() != 3) { encoded[0] = 0; }
    EmitPlayerLookup(nonce, encoded, true);
    return PLUGIN_HANDLED;
}

EmitPlayerLookup(const nonce[], const encoded[], bool:partial = false)
{
    new version = partial ? 2 : 1;
    if (strlen(nonce) != 16) { server_print("GSLOOKUP %d invalid", version); return; }
    for (new index = 0; nonce[index]; index++)
    {
        if (LookupHexDigit(nonce[index]) < 0) { server_print("GSLOOKUP %d invalid", version); return; }
    }
    if (get_gametime() < g_lookup_after)
    {
        server_print("GSLOOKUP %d %s busy %d", version, nonce, max(1, floatround(g_lookup_after - get_gametime(), floatround_ceil)));
        return;
    }
    // Failed requests consume the slot too; toggling enable flags does not reset it.
    g_lookup_after = get_gametime() + PLAYER_LOOKUP_INTERVAL;
    new name[32];
    if (!DecodeSearchQuery(encoded, name) || (partial && !LongEnoughSearchSubstring(name))) { server_print("GSLOOKUP %d invalid", version); return; }
    if (!get_pcvar_num(g_enabled) || !get_pcvar_num(g_lookup_enabled) || !g_leader_ready
        || g_stats_vault == INVALID_HANDLE || g_preferences_vault == INVALID_HANDLE)
    {
        server_print("GSLOOKUP %d %s unavailable", version, nonce);
        return;
    }
    new matches[SAVED_LEADER_LIMIT], count, total, stamp = get_systime();
    FindSavedPlayers(name, matches, count, total, partial);
    server_print("GSLOOKUP %d %s %d %d %d", version, nonce, stamp, count, total > count);
    for (new index = 0; index < count; index++)
    {
        new record[LeaderRecord], hex[63], written;
        ArrayGetArray(g_leader_records, matches[index], record);
        for (new byte = 0; record[LeaderSearchName][byte]; byte++)
        {
            written += formatex(hex[written], charsmax(hex) - written, "%02X", record[LeaderSearchName][byte] & 255);
        }
        server_print("GSPROW %d %d %d %d %s", index + 1, RankForKills(record[LeaderKills]), record[LeaderKills], record[LeaderDeaths], hex);
    }
    server_print("GSPEND %d %s %d %d %d", version, nonce, stamp, count, total > count);
}

// Shared, local-only MOTD shell. Optional table-cell end tags keep ten rows bounded.
MotdBegin(html[], capacity, const title[])
{
    return formatex(html, capacity, "<html><meta charset=utf-8><style>body{background:#18212b;color:#eee;font:18px Arial}h2,th{color:#fc6}table{width:100%%}td,th{text-align:left;padding:4px}small{color:#abc}</style><body><h2>%s</h2>", title);
}

// Captions have a separate 31-byte UTF-8 budget checked by the dictionary gate.
ShowPlayerMotd(id, const html[], language, const caption_key[])
{
    new caption[32];
    formatex(caption, charsmax(caption), "%L", PLAYER_LANGUAGES[language], caption_key);
    show_motd(id, html, caption);
}

MotdEnd(html[], capacity)
{
    add(html, capacity, "</body></html>");
}

bool:IsPlayerChatTokenBoundary(character)
{
    return character == 0 || character == ' ' || character == '^t' || character == '(' || character == ')'
        || character == '[' || character == ']' || character == ':' || character == ';' || character == '|';
}

// Accent whole commands and integer values, preserving dictionary text and UTF-8.
PlayerChat(id, const message[], any:...)
{
    new plain[191], colored[512], written;
    vformat(plain, charsmax(plain), message, 3);
    for (new index = 0; plain[index]; )
    {
        new end = index;
        new bool:token_start = index == 0 || IsPlayerChatTokenBoundary(plain[index - 1]);
        if (index == 0 && equal(plain, "[GoldSrcOps]", 12)) { end = 12; }
        else if (token_start && plain[index] == '/' && plain[index + 1] >= 'a' && plain[index + 1] <= 'z')
        {
            end++;
            while (plain[end] >= 'a' && plain[end] <= 'z') { end++; }
        }
        else if (token_start && plain[index] >= '0' && plain[index] <= '9')
        {
            while (plain[end] >= '0' && plain[end] <= '9') { end++; }
        }
        // Sentence punctuation may follow a token, but not join it to another word or number.
        if (end > index && !IsPlayerChatTokenBoundary(plain[end])
            && !((plain[end] == '.' || plain[end] == ',' || plain[end] == '!' || plain[end] == '?')
                && IsPlayerChatTokenBoundary(plain[end + 1])))
        {
            end = index;
        }
        if (end > index)
        {
            colored[written++] = 4;
            while (index < end) { colored[written++] = plain[index++]; }
            colored[written++] = 1;
        }
        else { colored[written++] = plain[index++]; }
    }
    colored[written] = 0;
    // Preserve the complete translated message when accents would exceed the wire budget.
    client_print_color(id, print_team_default, "%s", written <= 190 ? colored : plain);
}

public ShowSavedLeaders(id)
{
    if (!AllowStatsCommand(id, 3)) { return PLUGIN_HANDLED; }
    new language = EffectivePlayerLanguage(id);
    if (!g_leader_ready)
    {
        client_print(id, print_chat, "[GoldSrcOps] %L", PLAYER_LANGUAGES[language], "GS_ALL_UNAVAILABLE");
        return PLUGIN_HANDLED;
    }
    RefreshSavedLeaders();
    if (!g_leader_count)
    {
        client_print(id, print_chat, "[GoldSrcOps] %L", PLAYER_LANGUAGES[language], "GS_ALL_EMPTY");
        PrintPlayerStanding(id);
        return PLUGIN_HANDLED;
    }
    new html[1536], title[64], name[64];
    formatex(title, charsmax(title), "%L", PLAYER_LANGUAGES[language], "GS_TOPALL");
    new length = MotdBegin(html, charsmax(html), title);
    length += formatex(html[length], charsmax(html) - length, "<small>%L</small><table><tr><th>#<th>%L<th>K<th>D",
        PLAYER_LANGUAGES[language], "GS_ALL_HEADER", PLAYER_LANGUAGES[language], "GS_ALL_UNKNOWN");
    for (new rank = 0; rank < g_leader_count; rank++)
    {
        if (g_leader_names[rank][0]) { EscapeLeaderName(g_leader_names[rank], name, charsmax(name)); }
        else { formatex(name, charsmax(name), "%L", PLAYER_LANGUAGES[language], "GS_ALL_UNKNOWN"); }
        length += formatex(html[length], charsmax(html) - length, "<tr><td>%d<td>%s<td>%d<td>%d",
            rank + 1, name, g_leader_scores[rank][0], g_leader_scores[rank][1]);
    }
    length += formatex(html[length], charsmax(html) - length, "</table>");
    new standing[160], position = FormatPlayerStanding(id, standing, charsmax(standing));
    if (position <= 0 || position > SAVED_LEADER_LIMIT)
    {
        formatex(html[length], charsmax(html) - length, "<p>%s</p>", standing);
    }
    MotdEnd(html, charsmax(html));
    ShowPlayerMotd(id, html, language, "GS_TOPALL_TITLE");
    return PLUGIN_HANDLED;
}


public ShowRankGuide(id)
{
    if (!AllowStatsCommand(id, 5)) { return PLUGIN_HANDLED; }
    // Static rules are available even when player identity or storage is unavailable.
    new language = EffectivePlayerLanguage(id), html[1536], title[64];
    formatex(title, charsmax(title), "%L", PLAYER_LANGUAGES[language], "GS_RANKS");
    new length = MotdBegin(html, charsmax(html), title);
    length += formatex(html[length], charsmax(html) - length, "<table><tr><th>%L<th>%L",
        PLAYER_LANGUAGES[language], "GS_RANK", PLAYER_LANGUAGES[language], "GS_RANKS_KILLS");
    for (new rank = 0; rank < sizeof RANK_KILLS; rank++)
    {
        length += formatex(html[length], charsmax(html) - length, "<tr><td>%L<td>%d",
            PLAYER_LANGUAGES[language], RANK_KEYS[rank], RANK_KILLS[rank]);
    }
    length += formatex(html[length], charsmax(html) - length, "</table><p>%L %L</p><p>%L</p><p>%L</p><p>%L</p>",
        PLAYER_LANGUAGES[language], "GS_RANKS_COUNT", PLAYER_LANGUAGES[language], "GS_RANKS_EXCLUDED",
        PLAYER_LANGUAGES[language], "GS_RANKS_PROGRESS", PLAYER_LANGUAGES[language], "GS_RANKS_PLACE",
        PLAYER_LANGUAGES[language], "GS_RANKS_FAIR");
    MotdEnd(html, charsmax(html));
    ShowPlayerMotd(id, html, language, "GS_RANKS_TITLE");
    return PLUGIN_HANDLED;
}

public ShowPlayerProfile(id)
{
    if (!AllowStatsCommand(id, 4)) { return PLUGIN_HANDLED; }
    // Project loaded state only. Identity callbacks own loading and pending writes.
    new language = EffectivePlayerLanguage(id), html[1536], title[64], text[192];
    formatex(title, charsmax(title), "%L", PLAYER_LANGUAGES[language], "GS_PROFILE");
    new length = MotdBegin(html, charsmax(html), title);
    length += formatex(html[length], charsmax(html) - length, "<h2>%L</h2>", PLAYER_LANGUAGES[language], "GS_PROFILE_SAVED");
    if (g_stats_vault != INVALID_HANDLE && g_saved_ready[id] && !g_saved_blocked[id])
    {
        length += formatex(html[length], charsmax(html) - length, "%L<br>", PLAYER_LANGUAGES[language], "GS_SAVED_STATS",
            g_saved_kills[id], g_saved_deaths[id], float(g_saved_kills[id]) / float(max(g_saved_deaths[id], 1)));
        new rank = RankForKills(g_saved_kills[id]), rank_name[32], next_name[32];
        formatex(rank_name, charsmax(rank_name), "%L", PLAYER_LANGUAGES[language], RANK_KEYS[rank]);
        if (rank == sizeof RANK_KILLS - 1)
        {
            length += formatex(html[length], charsmax(html) - length, "%L<br>", PLAYER_LANGUAGES[language], "GS_RANK_HIGHEST",
                rank_name, g_saved_kills[id]);
        }
        else
        {
            formatex(next_name, charsmax(next_name), "%L", PLAYER_LANGUAGES[language], RANK_KEYS[rank + 1]);
            length += formatex(html[length], charsmax(html) - length, "%L<br>", PLAYER_LANGUAGES[language], "GS_RANK_PROGRESS",
                rank_name, g_saved_kills[id], RANK_KILLS[rank + 1] - g_saved_kills[id], next_name);
        }
        FormatPlayerStanding(id, text, charsmax(text));
    }
    else
    {
        length += formatex(html[length], charsmax(html) - length, "%L<br>%L<br>", PLAYER_LANGUAGES[language], "GS_PROFILE_UNAVAILABLE",
            PLAYER_LANGUAGES[language], "GS_HUD_RANK_UNAVAILABLE");
        formatex(text, charsmax(text), "%L", PLAYER_LANGUAGES[language], "GS_STANDING_UNAVAILABLE");
    }
    length += formatex(html[length], charsmax(html) - length, "%s<br><small>%L</small><br>", text, PLAYER_LANGUAGES[language], "GS_PROFILE_CACHE");
    if (g_preferences_vault != INVALID_HANDLE && g_preferences_ready[id] && !g_preferences_blocked[id]
        && g_streak_ready[id] && !g_streak_blocked[id])
    {
        length += formatex(html[length], charsmax(html) - length, "%L<br>", PLAYER_LANGUAGES[language], "GS_STREAK_SAVED", g_saved_streak[id]);
    }
    else
    {
        length += formatex(html[length], charsmax(html) - length, "%L<br>", PLAYER_LANGUAGES[language], "GS_STREAK_UNAVAILABLE");
    }
    formatex(html[length], charsmax(html) - length, "<h2>%L</h2>%L", PLAYER_LANGUAGES[language], "GS_PROFILE_SESSION",
        PLAYER_LANGUAGES[language], "GS_STREAK_STATS", g_kill_streak[id], g_best_streak[id]);
    MotdEnd(html, charsmax(html));
    ShowPlayerMotd(id, html, language, "GS_PROFILE_TITLE");
    return PLUGIN_HANDLED;
}
