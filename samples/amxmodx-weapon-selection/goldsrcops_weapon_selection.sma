#pragma semicolon 1
#pragma tabsize 4

#include <amxmodx>
#include <reapi>

new const WEAPON_NAMES[][] = { "MP5", "AK-47", "M4A1" };
new const WEAPON_CLASSES[][] = { "weapon_mp5navy", "weapon_ak47", "weapon_m4a1" };
new const WeaponIdType:WEAPON_IDS[] = { WEAPON_MP5N, WEAPON_AK47, WEAPON_M4A1 };
new const BACKPACK_AMMO[] = { 120, 90, 90 };

new g_enabled;
new g_menu;
new g_choice[MAX_PLAYERS + 1];

public plugin_init()
{
    register_plugin("GoldSrcOps Weapon Selection", "0.1.1", "GoldSrcOps");
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
}

public plugin_end()
{
    menu_destroy(g_menu);
}

public OnStatusCommand()
{
    server_print("WEAPON_SELECTION_STATUS version=0.1.1 enabled=%d choices=%d",
        get_pcvar_num(g_enabled) != 0, sizeof WEAPON_NAMES);
    return PLUGIN_HANDLED;
}

public client_putinserver(id)
{
    g_choice[id] = 0;
}

public client_disconnected(id)
{
    g_choice[id] = 0;
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
    return PLUGIN_HANDLED;
}

public OnSpawnPost(id)
{
    if (!get_pcvar_num(g_enabled) || !IsPlaying(id) || !is_user_alive(id))
    {
        return HC_CONTINUE;
    }

    new choice = g_choice[id];
    // Заменяется только основное оружие; пистолет, нож и предметы цели сохраняются.
    if (!rg_remove_items_by_slot(id, PRIMARY_WEAPON_SLOT))
    {
        log_amx("Weapon selection: primary slot removal failed.");
        return HC_CONTINUE;
    }

    if (rg_give_item(id, WEAPON_CLASSES[choice]) <= 0)
    {
        log_amx("Weapon selection: selected primary unavailable; restoring MP5.");
        choice = 0;
        if (rg_give_item(id, WEAPON_CLASSES[choice]) <= 0)
        {
            return HC_CONTINUE;
        }
    }

    rg_set_user_bpammo(id, WEAPON_IDS[choice], BACKPACK_AMMO[choice]);
    return HC_CONTINUE;
}
