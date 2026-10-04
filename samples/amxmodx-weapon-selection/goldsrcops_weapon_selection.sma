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

new g_enabled;
new g_menu;
new g_pistol_menu;
new g_choice[MAX_PLAYERS + 1];
new g_pistol_choice[MAX_PLAYERS + 1];

public plugin_init()
{
    register_plugin("GoldSrcOps Weapon Selection", "0.2.0", "GoldSrcOps");
    g_enabled = register_cvar("goldsrcops_weapons_enabled", "0");
    AutoExecConfig(false, "goldsrcops-weapon-selection");
    register_clcmd("say /guns", "OpenWeapons");
    register_clcmd("say_team /guns", "OpenWeapons");
    register_clcmd("guns", "OpenWeapons");
    register_srvcmd("goldsrcops_weapons_status", "OnStatusCommand");
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
    server_print("WEAPON_SELECTION_STATUS version=0.2.0 enabled=%d choices=%d pistol_choices=%d armor=100 helmet=1",
        get_pcvar_num(g_enabled) != 0, sizeof WEAPON_NAMES, sizeof PISTOL_NAMES);
    return PLUGIN_HANDLED;
}

public client_putinserver(id)
{
    g_choice[id] = 0;
    g_pistol_choice[id] = 0;
}

public client_disconnected(id)
{
    g_choice[id] = 0;
    g_pistol_choice[id] = 0;
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
    return HC_CONTINUE;
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
