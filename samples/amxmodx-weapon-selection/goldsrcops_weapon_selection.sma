#pragma semicolon 1
#pragma tabsize 4

#include <amxmodx>
#include <reapi>

new const WEAPON_NAMES[][] = { "MP5", "AK-47", "M4A1" };
new const WEAPON_CLASSES[][] = { "weapon_mp5navy", "weapon_ak47", "weapon_m4a1" };
new const WeaponIdType:WEAPON_IDS[] = { WEAPON_MP5N, WEAPON_AK47, WEAPON_M4A1 };
new const BACKPACK_AMMO[] = { 120, 90, 90 };
new const PISTOL_NAMES[][] = { "USP", "Glock", "Desert Eagle" };
new const PISTOL_CLASSES[][] = { "weapon_usp", "weapon_glock18", "weapon_deagle" };
new const WeaponIdType:PISTOL_IDS[] = { WEAPON_USP, WEAPON_GLOCK18, WEAPON_DEAGLE };
new const PISTOL_AMMO[] = { 100, 120, 35 };
const WELCOME_TASK_BASE = 1000;
const MAP_LEADER_LIMIT = 5;

new g_enabled;
new g_menu;
new g_pistol_menu;
new g_choice[MAX_PLAYERS + 1];
new g_pistol_choice[MAX_PLAYERS + 1];
new bool:g_welcomed[MAX_PLAYERS + 1];
new g_map_kills[MAX_PLAYERS + 1];
new g_map_deaths[MAX_PLAYERS + 1];
new Float:g_stats_next[MAX_PLAYERS + 1][2];

public plugin_init()
{
    register_plugin("GoldSrcOps Weapon Selection", "0.4.0", "GoldSrcOps");
    g_enabled = register_cvar("goldsrcops_weapons_enabled", "0");
    AutoExecConfig(false, "goldsrcops-weapon-selection");
    register_clcmd("say /guns", "OpenWeapons");
    register_clcmd("say_team /guns", "OpenWeapons");
    register_clcmd("guns", "OpenWeapons");
    register_srvcmd("goldsrcops_weapons_status", "OnStatusCommand");
    register_clcmd("say /stats", "ShowMapStats");
    register_clcmd("say_team /stats", "ShowMapStats");
    register_clcmd("stats", "ShowMapStats");
    register_clcmd("say /top", "ShowMapLeaders");
    register_clcmd("say_team /top", "ShowMapLeaders");
    register_clcmd("top", "ShowMapLeaders");
    register_srvcmd("goldsrcops_stats_status", "OnMapStatsStatus");
    register_event("DeathMsg", "OnMapStatsDeath", "a");
    RegisterHookChain(RG_CBasePlayer_Spawn, "OnSpawnPost", true);

    g_menu = menu_create("Primary weapon (next spawn)", "OnWeaponSelected");
    for (new index = 0; index < sizeof WEAPON_NAMES; index++)
    {
        menu_additem(g_menu, WEAPON_NAMES[index]);
    }
    g_pistol_menu = menu_create("Pistol (next spawn)", "OnPistolSelected");
    for (new index = 0; index < sizeof PISTOL_NAMES; index++)
    {
        menu_additem(g_pistol_menu, PISTOL_NAMES[index]);
    }
}

public plugin_end()
{
    menu_destroy(g_menu);
    menu_destroy(g_pistol_menu);
}

public OnStatusCommand()
{
    server_print("WEAPON_SELECTION_STATUS version=0.4.0 enabled=%d choices=%d pistol_choices=%d armor=100 helmet=1 first_spawn_menu=1 map_stats=1",
        get_pcvar_num(g_enabled) != 0, sizeof WEAPON_NAMES, sizeof PISTOL_NAMES);
    return PLUGIN_HANDLED;
}

public client_putinserver(id)
{
    remove_task(WELCOME_TASK_BASE + id);
    g_welcomed[id] = false;
    g_choice[id] = 0;
    g_pistol_choice[id] = 0;
    ResetMapStats(id);
}

public client_disconnected(id)
{
    remove_task(WELCOME_TASK_BASE + id);
    g_welcomed[id] = false;
    g_choice[id] = 0;
    g_pistol_choice[id] = 0;
    ResetMapStats(id);
}

bool:IsPlaying(id)
{
    if (!is_user_connected(id) || is_user_bot(id) || is_user_hltv(id))
    {
        return false;
    }

    new TeamName:team = get_member(id, m_iTeam);
    return team == TEAM_TERRORIST || team == TEAM_CT;
}

public OpenWeapons(id)
{
    if (get_pcvar_num(g_enabled) && IsPlaying(id))
    {
        remove_task(WELCOME_TASK_BASE + id);
        g_welcomed[id] = true;
        menu_display(id, g_menu);
    }
    return PLUGIN_HANDLED;
}

public OnWeaponSelected(id, menu, item)
{
    #pragma unused menu

    if (item < 0 || item >= sizeof WEAPON_NAMES
        || !get_pcvar_num(g_enabled) || !IsPlaying(id))
    {
        return PLUGIN_HANDLED;
    }

    g_choice[id] = item;
    client_print(id, print_chat, "[GoldSrcOps] %s selected for your next spawn.", WEAPON_NAMES[item]);
    menu_display(id, g_pistol_menu);
    return PLUGIN_HANDLED;
}

public OnPistolSelected(id, menu, item)
{
    #pragma unused menu

    if (item < 0 || item >= sizeof PISTOL_NAMES
        || !get_pcvar_num(g_enabled) || !IsPlaying(id))
    {
        return PLUGIN_HANDLED;
    }

    g_pistol_choice[id] = item;
    client_print(id, print_chat, "[GoldSrcOps] %s + %s selected for your next spawn.",
        WEAPON_NAMES[g_choice[id]], PISTOL_NAMES[item]);
    return PLUGIN_HANDLED;
}

public OnSpawnPost(id)
{
    if (!get_pcvar_num(g_enabled) || !IsPlaying(id) || !is_user_alive(id))
    {
        return HC_CONTINUE;
    }

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
    client_print(id, print_chat, "[GoldSrcOps] /guns: next-spawn weapons | /stats: your score | /top: map leaders | /maps | /timeleft");
    new menu, keys;
    // Do not replace a team/class menu or another plugin's active menu.
    if (!get_user_menu(id, menu, keys))
    {
        menu_display(id, g_menu);
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
    g_stats_next[id][0] = 0.0;
    g_stats_next[id][1] = 0.0;
}

bool:IsStatsClient(id)
{
    return id >= 1 && id <= MaxClients && is_user_connected(id)
        && !is_user_bot(id) && !is_user_hltv(id);
}

public OnMapStatsDeath()
{
    RecordMapDeath(read_data(1), read_data(2));
}

RecordMapDeath(killer, victim)
{
    if (!get_pcvar_num(g_enabled) || !IsStatsClient(victim) || !IsPlaying(victim))
    {
        return;
    }
    // Ignore an entire bot/HLTV encounter, not just the killer's score.
    if (killer != 0 && (!IsStatsClient(killer) || !IsPlaying(killer)))
    {
        return;
    }
    g_map_deaths[victim]++;
    if (killer != 0 && killer != victim
        && get_member(killer, m_iTeam) != get_member(victim, m_iTeam))
    {
        g_map_kills[killer]++;
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
        client_print(id, print_chat, "[GoldSrcOps] This connection/map: K %d | D %d | K/D %.2f (minimum 1 death).",
            g_map_kills[id], g_map_deaths[id], MapKillDeathRatio(id));
    }
    return PLUGIN_HANDLED;
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
        client_print(id, print_chat, "[GoldSrcOps] No human kills or deaths recorded yet on this map.");
        return PLUGIN_HANDLED;
    }
    client_print(id, print_chat, "[GoldSrcOps] Map leaders (connected players; reconnect resets your score):");
    for (new rank = 0; rank < count; rank++)
    {
        new player = leaders[rank];
        new name[32];
        get_user_name(player, name, charsmax(name));
        CleanStatsName(name);
        client_print(id, print_chat, "[GoldSrcOps] #%d %s | K %d D %d | K/D %.2f",
            rank + 1, name, g_map_kills[player], g_map_deaths[player], MapKillDeathRatio(player));
    }
    return PLUGIN_HANDLED;
}

public OnMapStatsStatus()
{
    new leaders[MAP_LEADER_LIMIT];
    server_print("MAP_STATS_STATUS version=0.4.0 enabled=%d scope=connection_map leaders=%d limit=5 cooldown=2 bot_encounters=excluded",
        get_pcvar_num(g_enabled) != 0, BuildMapLeaders(leaders));
    return PLUGIN_HANDLED;
}
