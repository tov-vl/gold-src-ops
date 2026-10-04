#include <amxmodx>
#include <reapi>
#include <fakemeta>

// Only the synthetic fixture client bypasses the production bot exclusion.
// The shipped plugin is compiled separately, without these substitutions.
#define plugin_init ProductPluginInit
#define is_user_bot(%1) FixtureIsBot(%1)
#define menu_display(%1,%2) FixtureMenuDisplay(%1,%2)
#include <goldsrcops_weapon_selection.sma>
#undef plugin_init
#undef is_user_bot
#undef menu_display

new g_fixture_id;
new g_failures;
new g_menu_displays;
new bool:g_bypass_bot = true;

public plugin_init()
{
    ProductPluginInit();
    register_srvcmd("goldsrcops_loadout_smoke", "RunLoadoutSmoke");
}

bool:FixtureIsBot(id)
{
    return (!g_bypass_bot || id != g_fixture_id) && is_user_bot(id);
}

FixtureMenuDisplay(id, menu)
{
    if (id == g_fixture_id)
    {
        g_menu_displays++;
    }
    return menu_display(id, menu);
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

    CheckFirstSpawn();
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

CheckFirstSpawn()
{
    new id = g_fixture_id;
    new task_id = WELCOME_TASK_BASE + id;
    client_putinserver(id);
    rg_round_respawn(id);
    Check(task_exists(task_id) && !g_welcomed[id], "first spawn schedules welcome");
    remove_task(task_id);
    ShowWelcome(task_id);
    Check(g_welcomed[id] && g_menu_displays == 1, "welcome opens primary once");
    menu_cancel(id);
    rg_round_respawn(id);
    ShowWelcome(task_id);
    Check(!task_exists(task_id) && g_menu_displays == 1, "respawn does not reopen welcome");

    client_putinserver(id);
    OnSpawnPost(id);
    OpenWeapons(id);
    Check(!task_exists(task_id) && g_welcomed[id], "manual guns cancels automatic menu");
    new displays = g_menu_displays;
    ShowWelcome(task_id);
    Check(g_menu_displays == displays, "manual menu is not reopened");

    client_putinserver(id);
    OnSpawnPost(id);
    remove_task(task_id);
    // The existing weapon menu stands in for another active menu.
    ShowWelcome(task_id);
    Check(g_welcomed[id] && g_menu_displays == displays, "active menu is not replaced");
    menu_cancel(id);

    client_putinserver(id);
    OnSpawnPost(id);
    remove_task(task_id);
    set_pcvar_num(g_enabled, 0);
    ShowWelcome(task_id);
    Check(!g_welcomed[id] && g_menu_displays == displays, "disabled delayed welcome refuses");
    OnSpawnPost(id);
    Check(!task_exists(task_id), "disabled spawn schedules nothing");
    set_pcvar_num(g_enabled, 1);

    rg_set_user_team(id, TEAM_SPECTATOR);
    OnSpawnPost(id);
    ShowWelcome(task_id);
    Check(!task_exists(task_id) && !g_welcomed[id], "spectator welcome refuses");
    rg_set_user_team(id, TEAM_CT);
    g_bypass_bot = false;
    OnSpawnPost(id);
    ShowWelcome(task_id);
    Check(!task_exists(task_id) && !g_welcomed[id], "bot welcome refuses");
    g_bypass_bot = true;

    set_entvar(id, var_deadflag, DEAD_DEAD);
    ShowWelcome(task_id);
    Check(!g_welcomed[id], "dead callback leaves next spawn eligible");
    rg_round_respawn(id);
    Check(bool:task_exists(task_id), "next living spawn retries welcome");
    new rejected[128];
    client_disconnected(id, false, rejected, charsmax(rejected));
    Check(!task_exists(task_id) && !g_welcomed[id], "disconnect cancels timer and state");
    client_putinserver(id);
    Check(!task_exists(task_id) && !g_welcomed[id], "reused client slot starts clean");
    ShowWelcome(WELCOME_TASK_BASE);
    ShowWelcome(WELCOME_TASK_BASE + MaxClients + 1);
    // Keep the remaining inventory scenarios free of a delayed welcome.
    g_welcomed[id] = true;
    server_print("FIRST_SPAWN_SMOKE=%s failures=%d synthetic_client=1", g_failures ? "failed" : "passed", g_failures);
}
