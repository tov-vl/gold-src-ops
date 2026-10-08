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
new const PLAYER_MENU_KEYS[][] = { "GS_GUNS", "GS_STATS", "GS_RANK", "GS_TOP", "GS_MAPS", "GS_TIMELEFT", "GS_SETTINGS" };

new g_enabled;
new g_kill_heal;
new g_kill_ammo;
new g_menu[2];
new g_pistol_menu[2];
new g_player_menu[2];
new g_settings_menu[2][2];
new g_language_menu;
new bool:g_hud_disabled[MAX_PLAYERS + 1];
new bool:g_hud_visible[MAX_PLAYERS + 1];
new g_language_override[MAX_PLAYERS + 1];
new g_choice[MAX_PLAYERS + 1];
new g_pistol_choice[MAX_PLAYERS + 1];
new bool:g_welcomed[MAX_PLAYERS + 1];
new g_map_kills[MAX_PLAYERS + 1];
new g_map_deaths[MAX_PLAYERS + 1];
new g_kill_streak[MAX_PLAYERS + 1];
new g_best_streak[MAX_PLAYERS + 1];
new Float:g_stats_next[MAX_PLAYERS + 1][3];
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
    register_plugin("GoldSrcOps Weapon Selection", "0.16.0", "GoldSrcOps");
    if (!register_dictionary("goldsrcops-player-menu.txt"))
    {
        set_fail_state("Player menu dictionary unavailable.");
        return;
    }
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
    g_enabled = register_cvar("goldsrcops_weapons_enabled", "0");
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
    register_srvcmd("goldsrcops_weapons_status", "OnStatusCommand");
    register_clcmd("say /stats", "ShowMapStats");
    register_clcmd("say_team /stats", "ShowMapStats");
    register_clcmd("stats", "ShowMapStats");
    register_clcmd("say /top", "ShowMapLeaders");
    register_clcmd("say_team /top", "ShowMapLeaders");
    register_clcmd("top", "ShowMapLeaders");
    register_clcmd("say /rank", "ShowPlayerRank");
    register_clcmd("say_team /rank", "ShowPlayerRank");
    register_clcmd("rank", "ShowPlayerRank");
    register_srvcmd("goldsrcops_stats_status", "OnMapStatsStatus");
    register_event("DeathMsg", "OnMapStatsDeath", "a");
    RegisterHookChain(RG_CBasePlayer_Spawn, "OnSpawnPost", true);
    set_task(HUD_REFRESH_INTERVAL, "UpdatePlayerHuds", HUD_TASK_ID, _, _, "b");

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
        for (new disabled = 0; disabled < 2; disabled++)
        {
            formatex(text, charsmax(text), "%L", PLAYER_LANGUAGES[language], "GS_SETTINGS");
            g_settings_menu[language][disabled] = menu_create(text, "OnSettingsSelected");
            formatex(text, charsmax(text), "%L", PLAYER_LANGUAGES[language], "GS_GUNS");
            menu_additem(g_settings_menu[language][disabled], text);
            formatex(text, charsmax(text), "%L", PLAYER_LANGUAGES[language], "GS_LANGUAGE");
            menu_additem(g_settings_menu[language][disabled], text);
            formatex(text, charsmax(text), "%L", PLAYER_LANGUAGES[language], disabled ? "GS_HUD_OFF" : "GS_HUD_ON");
            menu_additem(g_settings_menu[language][disabled], text);
            formatex(text, charsmax(text), "%L", PLAYER_LANGUAGES[language], "GS_BACK");
            menu_additem(g_settings_menu[language][disabled], text);
            formatex(text, charsmax(text), "%L", PLAYER_LANGUAGES[language], "GS_EXIT");
            menu_setprop(g_settings_menu[language][disabled], MPROP_EXITNAME, text);
        }
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
    for (new id = 1; id <= MaxClients; id++) { HidePlayerHud(id); }
    for (new language = 0; language < sizeof PLAYER_LANGUAGES; language++)
    {
        menu_destroy(g_menu[language]);
        menu_destroy(g_pistol_menu[language]);
        menu_destroy(g_player_menu[language]);
        for (new disabled = 0; disabled < 2; disabled++) { menu_destroy(g_settings_menu[language][disabled]); }
    }
    menu_destroy(g_language_menu);
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
    server_print("WEAPON_SELECTION_STATUS version=0.16.0 enabled=%d choices=%d pistol_choices=%d armor=100 helmet=1 first_spawn_menu=1 map_stats=1 persistent_stats=1 ranks=1",
        get_pcvar_num(g_enabled) != 0, sizeof WEAPON_NAMES, sizeof PISTOL_NAMES);
    server_print("PLAYER_MENU_STATUS version=0.16.0 languages=ru,en unset=ru unsupported=en override=steam_or_connection entries=7 map_commands=delegated language_native=1");
    server_print("PLAYER_SETTINGS_STATUS version=0.16.0 languages=ru,en summary=next_spawn saved=verified_record reset=none");
    server_print("PLAYER_HUD_STATUS version=0.16.0 languages=ru,en interval=1 renderer=director expiry=1.5 messages=2 menus=visible scope=connection_map rank=saved_kills toggle=connection_only storage_writes=none");
    server_print("KILL_STREAK_STATUS version=0.16.0 enabled=%d scope=connection_map reset=any_death alive_only=1 milestones=3,5,10 feedback=private languages=ru,en storage=none",
        get_pcvar_num(g_enabled) != 0);
    server_print("KILL_HEAL_STATUS version=0.16.0 enabled=%d amount=%d max_health=100 scope=enemy_human_kill alive_only=1",
        get_pcvar_num(g_enabled) != 0 && KillHealAmount() > 0, KillHealAmount());
    server_print("KILL_AMMO_STATUS version=0.16.0 enabled=%d scope=enemy_human_kill alive_only=1 weapons=loadout_owned reserve=spawn_limit clip=unchanged",
        get_pcvar_num(g_enabled) != 0 && get_pcvar_num(g_kill_ammo) > 0);
    new loaded;
    for (new id = 1; id <= MaxClients; id++)
    {
        if (IsStatsClient(id) && g_preferences_ready[id]) { loaded++; }
    }
    server_print("PLAYER_PREFERENCES_STATUS version=0.16.0 storage=%s schema=1 identity=steam loaded=%d errors=%d",
        g_preferences_vault == INVALID_HANDLE ? "unavailable" : "nvault", loaded, g_preferences_errors);
    return PLUGIN_HANDLED;
}

public client_putinserver(id)
{
    remove_task(WELCOME_TASK_BASE + id);
    g_welcomed[id] = false;
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
}

public client_authorized(id, const authid[])
{
    #pragma unused authid
    // Authorization can arrive before or after client_putinserver.
    EnsureSavedStats(id);
    EnsurePlayerPreferences(id);
}

public client_disconnected(id)
{
    HidePlayerHud(id);
    g_hud_disabled[id] = false;
    remove_task(WELCOME_TASK_BASE + id);
    g_welcomed[id] = false;
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
    if (item < 0 || item >= sizeof PLAYER_MENU_KEYS || !get_pcvar_num(g_enabled)
        || !IsStatsClient(id) || menu != g_player_menu[PlayerLanguage(id)])
    {
        return PLUGIN_HANDLED;
    }
    switch (item)
    {
        case 0: OpenWeapons(id);
        case 1: { ShowMapStats(id); OpenPlayerMenu(id); }
        case 2: { ShowPlayerRank(id); OpenPlayerMenu(id); }
        case 3: { ShowMapLeaders(id); OpenPlayerMenu(id); }
        case 4: amxclient_cmd(id, "say", "/maps");
        case 5: { amxclient_cmd(id, "say", "/timeleft"); OpenPlayerMenu(id); }
        case 6: OpenPlayerSettings(id);
    }
    return PLUGIN_HANDLED;
}

public OpenPlayerSettings(id)
{
    if (!get_pcvar_num(g_enabled) || !IsStatsClient(id)) { return PLUGIN_HANDLED; }
    remove_task(WELCOME_TASK_BASE + id);
    g_welcomed[id] = true;
    new language = PlayerLanguage(id);
    client_print(id, print_chat, "[GoldSrcOps] %L", PLAYER_LANGUAGES[language], "GS_SETTINGS_LOADOUT",
        WEAPON_NAMES[g_choice[id]], PISTOL_NAMES[g_pistol_choice[id]], language == 0 ? "RU" : "EN");
    client_print(id, print_chat, "[GoldSrcOps] %L", PLAYER_LANGUAGES[language],
        PlayerPreferencesSaved(id) ? "GS_SETTINGS_SAVED" : "GS_SETTINGS_SESSION");
    menu_display(id, g_settings_menu[language][g_hud_disabled[id]]);
    return PLUGIN_HANDLED;
}

public OnSettingsSelected(id, menu, item)
{
    if (item < 0 || item >= 4 || !get_pcvar_num(g_enabled) || !IsStatsClient(id)
        || menu != g_settings_menu[PlayerLanguage(id)][g_hud_disabled[id]]) { return PLUGIN_HANDLED; }
    switch (item)
    {
        case 0: OpenWeapons(id);
        case 1: menu_display(id, g_language_menu);
        case 2:
        {
            g_hud_disabled[id] = !g_hud_disabled[id];
            HidePlayerHud(id);
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

public OnLanguageSelected(id, menu, item)
{
    if (!get_pcvar_num(g_enabled) || !IsStatsClient(id) || menu != g_language_menu)
    {
        return PLUGIN_HANDLED;
    }
    if (item >= 0 && item < sizeof PLAYER_LANGUAGES)
    {
        g_language_override[id] = item + 1;
        g_preferences_dirty[id] |= PREFERENCE_LANGUAGE;
        SavePlayerPreferences(id);
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

    g_choice[id] = item;
    g_preferences_dirty[id] |= PREFERENCE_PRIMARY;
    SavePlayerPreferences(id);
    client_print(id, print_chat, "[GoldSrcOps] %L", PLAYER_LANGUAGES[PlayerLanguage(id)], "GS_PRIMARY_SELECTED", WEAPON_NAMES[item]);
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

    g_pistol_choice[id] = item;
    g_preferences_dirty[id] |= PREFERENCE_PISTOL;
    SavePlayerPreferences(id);
    client_print(id, print_chat, "[GoldSrcOps] %L", PLAYER_LANGUAGES[PlayerLanguage(id)], "GS_PISTOL_SELECTED",
        WEAPON_NAMES[g_choice[id]], PISTOL_NAMES[item]);
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
    client_print(id, print_chat, "[GoldSrcOps] %L", PLAYER_LANGUAGES[PlayerLanguage(id)], "GS_WELCOME");
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
    if (victim >= 1 && victim <= MaxClients) { g_kill_streak[victim] = 0; }
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
    if (g_kill_streak[id] == 3 || g_kill_streak[id] == 5 || g_kill_streak[id] == 10)
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
    g_stats_next[id][command] = now + 2.0;
    return true;
}

public ShowMapStats(id)
{
    if (AllowStatsCommand(id, 0))
    {
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
    }
    return PLUGIN_HANDLED;
}

PrintPlayerRank(id)
{
    EnsureSavedStats(id);
    if (!g_saved_ready[id])
    {
        client_print(id, print_chat, "[GoldSrcOps] %L", PLAYER_LANGUAGES[PlayerLanguage(id)], "GS_RANK_UNAVAILABLE");
        return;
    }
    new rank = RankForKills(g_saved_kills[id]);
    new rank_name[32], next_name[32];
    new language = PlayerLanguage(id);
    formatex(rank_name, charsmax(rank_name), "%L", PLAYER_LANGUAGES[language], RANK_KEYS[rank]);
    if (rank == sizeof RANK_KILLS - 1)
    {
        client_print(id, print_chat, "[GoldSrcOps] %L", PLAYER_LANGUAGES[language], "GS_RANK_HIGHEST",
            rank_name, g_saved_kills[id]);
    }
    else
    {
        formatex(next_name, charsmax(next_name), "%L", PLAYER_LANGUAGES[language], RANK_KEYS[rank + 1]);
        client_print(id, print_chat, "[GoldSrcOps] %L", PLAYER_LANGUAGES[language], "GS_RANK_PROGRESS",
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
    server_print("MAP_STATS_STATUS version=0.16.0 enabled=%d scope=connection_map leaders=%d limit=5 cooldown=2 bot_encounters=excluded",
        get_pcvar_num(g_enabled) != 0, BuildMapLeaders(leaders));
    new loaded;
    for (new id = 1; id <= MaxClients; id++)
    {
        if (IsStatsClient(id) && g_saved_ready[id])
        {
            loaded++;
        }
    }
    server_print("PLAYER_STATS_STATUS version=0.16.0 storage=%s schema=1 identity=steam loaded=%d errors=%d",
        g_stats_vault == INVALID_HANDLE ? "unavailable" : "nvault", loaded, g_storage_errors);
    server_print("PLAYER_RANK_STATUS version=0.16.0 enabled=%d source=saved_kills tiers=5 thresholds=0,25,100,250,500 cooldown=2 rewards=none welcome=1 promotion=private_after_readback",
        get_pcvar_num(g_enabled) != 0);
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
        return;
    }
    copy(g_saved_key[id], charsmax(g_saved_key[]), key);
    g_saved_kills[id] = kills;
    g_saved_deaths[id] = deaths;
    g_saved_ready[id] = true;
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
    }
}

ResetPlayerPreferences(id)
{
    g_preferences_key[id][0] = 0;
    g_preferences_ready[id] = false;
    g_preferences_blocked[id] = false;
    g_preferences_dirty[id] = 0;
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
        || g_preferences_ready[id] || g_preferences_blocked[id])
    {
        return;
    }
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
