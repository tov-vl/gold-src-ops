#include <amxmodx>
#include <reapi>
#include <fakemeta>

// Only the synthetic fixture client bypasses the production bot exclusion.
// The shipped plugin is compiled separately, without these substitutions.
#define plugin_init ProductPluginInit
#define is_user_bot(%1) FixtureIsBot(%1)
#include <goldsrcops_weapon_selection.sma>
#undef plugin_init
#undef is_user_bot

new g_fixture_id;
new g_failures;

public plugin_init()
{
    ProductPluginInit();
    register_srvcmd("goldsrcops_loadout_smoke", "RunLoadoutSmoke");
}

bool:FixtureIsBot(id)
{
    return id != g_fixture_id && is_user_bot(id);
}

Check(bool:passed, const label[])
{
    if (!passed)
    {
        g_failures++;
        server_print("SPAWN_LOADOUT_ASSERTION_FAILED %s", label);
    }
}

public RunLoadoutSmoke()
{
    if (g_fixture_id || get_playersnum())
    {
        server_print("SPAWN_LOADOUT_SMOKE=refused; requires a fresh empty fixture");
        return PLUGIN_HANDLED;
    }

    g_fixture_id = engfunc(EngFunc_CreateFakeClient, "Loadout fixture");
    if (!g_fixture_id)
    {
        server_print("SPAWN_LOADOUT_SMOKE=failed; no fixture client slot");
        return PLUGIN_HANDLED;
    }
    new rejected[128];
    dllfunc(DLLFunc_ClientConnect, g_fixture_id, "Loadout fixture", "127.0.0.1", rejected);
    dllfunc(DLLFunc_ClientPutInServer, g_fixture_id);
    rg_set_user_team(g_fixture_id, TEAM_CT);
    set_member(g_fixture_id, m_iJoiningState, JOINED);
    set_pcvar_num(g_enabled, 1);

    Check(g_choice[g_fixture_id] == 0 && g_pistol_choice[g_fixture_id] == 0, "default choices");
    for (new primary = 0; primary < sizeof WEAPON_NAMES; primary++)
    {
        for (new pistol = 0; pistol < sizeof PISTOL_NAMES; pistol++)
        {
            g_choice[g_fixture_id] = primary;
            g_pistol_choice[g_fixture_id] = pistol;
            rg_round_respawn(g_fixture_id);
            Check(bool:is_user_alive(g_fixture_id), "living spawn");
            Check(rg_has_item_by_name(g_fixture_id, WEAPON_CLASSES[primary]), "selected primary");
            Check(rg_has_item_by_name(g_fixture_id, PISTOL_CLASSES[pistol]), "selected pistol");
            Check(rg_get_user_bpammo(g_fixture_id, WEAPON_IDS[primary]) == BACKPACK_AMMO[primary], "primary ammo");
            Check(rg_get_user_bpammo(g_fixture_id, PISTOL_IDS[pistol]) == PISTOL_AMMO[pistol], "pistol ammo");
            new ArmorType:armor_type;
            Check(rg_get_user_armor(g_fixture_id, armor_type) == 100 && armor_type == ARMOR_VESTHELM, "armor and helmet");
            for (new other = 0; other < sizeof PISTOL_NAMES; other++)
            {
                if (other != pistol)
                {
                    Check(!rg_has_item_by_name(g_fixture_id, PISTOL_CLASSES[other]), "single pistol");
                }
            }
        }
    }

    // Menu handlers store choices without replenishing a living player's ammo.
    rg_set_user_bpammo(g_fixture_id, WEAPON_M4A1, 1);
    rg_set_user_bpammo(g_fixture_id, WEAPON_DEAGLE, 1);
    OnWeaponSelected(g_fixture_id, g_menu, 1);
    OnPistolSelected(g_fixture_id, g_pistol_menu, 0);
    Check(g_choice[g_fixture_id] == 1 && g_pistol_choice[g_fixture_id] == 0, "menu choices retained");
    Check(rg_has_item_by_name(g_fixture_id, "weapon_m4a1") && rg_has_item_by_name(g_fixture_id, "weapon_deagle"), "no mid-life replacement");
    Check(rg_get_user_bpammo(g_fixture_id, WEAPON_M4A1) == 1 && rg_get_user_bpammo(g_fixture_id, WEAPON_DEAGLE) == 1, "no mid-life refill");
    OnPistolSelected(g_fixture_id, g_pistol_menu, MENU_EXIT);
    Check(g_pistol_choice[g_fixture_id] == 0, "cancel retains pistol");

    rg_set_user_team(g_fixture_id, TEAM_TERRORIST);
    rg_round_respawn(g_fixture_id);
    Check(rg_has_item_by_name(g_fixture_id, "weapon_ak47") && rg_has_item_by_name(g_fixture_id, "weapon_usp"), "choices survive team change");
    rg_give_item(g_fixture_id, "weapon_c4");
    rg_give_item(g_fixture_id, "weapon_hegrenade");
    rg_give_item(g_fixture_id, "weapon_knife");
    OnSpawnPost(g_fixture_id);
    Check(rg_has_item_by_name(g_fixture_id, "weapon_c4") && rg_has_item_by_name(g_fixture_id, "weapon_hegrenade"), "objective and grenade preserved");
    Check(rg_has_item_by_name(g_fixture_id, "weapon_knife"), "knife preserved");
    set_pcvar_num(g_enabled, 0);
    rg_set_user_bpammo(g_fixture_id, WEAPON_USP, 1);
    OnSpawnPost(g_fixture_id);
    Check(rg_get_user_bpammo(g_fixture_id, WEAPON_USP) == 1, "disabled hook leaves ammo alone");
    client_disconnected(g_fixture_id, false, rejected, charsmax(rejected));
    Check(g_choice[g_fixture_id] == 0 && g_pistol_choice[g_fixture_id] == 0, "disconnect resets choices");
    server_print("SPAWN_LOADOUT_SMOKE=%s combinations=9 failures=%d synthetic_client=1",
        g_failures ? "failed" : "passed", g_failures);
    return PLUGIN_HANDLED;
}
