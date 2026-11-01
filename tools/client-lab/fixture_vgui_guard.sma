#include <amxmodx>
#include <reapi>
#include <nvault>

native goldsrcops_player_language(id);

new g_epoch[33];
new Float:g_origin[33][3];
new g_buttons[33];
new bool:g_moved[33];
new bool:g_reported[33];
new Float:g_firstRules[33];
new g_panel[33];
new g_readiness;
new bool:g_spawnReady[33], bool:g_setupSeen[33], bool:g_setupAck[33], bool:g_setupFailed[33];
new g_setupStep[33], g_setupLanguage[33];
new const PANELS[][] = { "rules", "ranks", "profile", "topall" };

// Fresh disposable fixture data only. No connected player's identity is replaced.
public plugin_precache()
{
    new stats = nvault_open("goldsrcops-player-stats-v1");
    new names = nvault_open("goldsrcops-player-preferences-v1");
    if (stats == INVALID_HANDLE || names == INVALID_HANDLE)
        set_fail_state("Synthetic fixture vault unavailable.");
    nvault_set(stats, "v1:STEAM_0:0:900000001", "1 500 10");
    nvault_set(stats, "v1:STEAM_0:0:900000002", "1 100 20");
    nvault_set(stats, "v1:STEAM_0:0:900000003", "1 25 30");
    nvault_set(names, "name:v1:STEAM_0:0:900000001", "1 Lab Alpha");
    nvault_set(names, "name:v1:STEAM_0:0:900000002", "1 Lab Bravo");
    nvault_set(names, "name:v1:STEAM_0:0:900000003", "1 Lab Charlie");
    nvault_close(stats);
    nvault_close(names);
}

public plugin_natives()
{
    set_native_filter("SetupNativeFilter");
}

public SetupNativeFilter(const name[], index, trap)
{
    #pragma unused index
    return equal(name, "goldsrcops_player_language") && !trap ? PLUGIN_HANDLED : PLUGIN_CONTINUE;
}

public plugin_init()
{
    register_plugin("Fixture interaction evidence", "1.4.0", "");
    g_readiness = register_cvar("gso_lab_readiness", "0");
    register_message(get_user_msgid("VGUIMenu"), "OnVguiMenu");
    register_clcmd("rules", "ObserveRules");
    register_clcmd("say_team /rules", "ObserveRulesAlias");
    register_clcmd("ranks", "ObserveRanks");
    register_clcmd("say_team /ranks", "ObserveRanksAlias");
    register_clcmd("profile", "ObserveProfile");
    register_clcmd("say_team /profile", "ObserveProfileAlias");
    register_clcmd("topall", "ObserveTopall");
    register_clcmd("say_team /topall", "ObserveTopallAlias");
    register_message(get_user_msgid("MOTD"), "ObserveMotd");
    RegisterHookChain(RG_CBasePlayer_PostThink, "ObserveMovement", true);
    register_clcmd("say_team /settings", "ObserveSetupSettings");
    register_clcmd("say_team /gso_lab_ready_ru", "ObserveSetupRu");
    register_clcmd("say_team /gso_lab_ready_en", "ObserveSetupEn");
    register_clcmd("guns", "ObserveGuns");
    register_clcmd("say_team /guns", "ObserveGuns");
    register_clcmd("menuselect", "ObserveGunsSelection");
    register_message(get_user_msgid("ShowMenu"), "ObserveGunsMenu");
    RegisterHookChain(RG_CBasePlayer_Spawn, "ObserveGunsSpawn", true);
}

// Opt-in for weapons runs only. Fixed one-shot client aliases, never supplied text.
public client_putinserver(player)
{
    g_spawnReady[player] = false;
    g_setupSeen[player] = false;
    g_setupAck[player] = false;
    g_setupFailed[player] = false;
    g_setupStep[player] = 0;
    if (get_pcvar_num(g_readiness)) { set_task(0.2, "ReadinessJoin", player + 100); }
}

public ReadinessJoin(task)
{
    new player = task - 100;
    if (!is_user_connected(player) || !get_pcvar_num(g_readiness)) { return; }
    log_amx("FIXTURE_GUNS_READY phase=connected");
    client_cmd(player, "gso_lab_join");
}

public ReadinessSpawn(task)
{
    new player = task - 200;
    if (!is_user_alive(player) || !get_pcvar_num(g_readiness)) { return; }
    log_amx("FIXTURE_GUNS_READY phase=spawn");
    client_cmd(player, "gso_lab_spawn");
}

public ObserveSetupSettings(player)
{
    if (get_pcvar_num(g_readiness) && is_user_alive(player) && g_spawnReady[player] && !g_setupSeen[player])
    {
        g_setupSeen[player] = true;
        log_amx("FIXTURE_GUNS_SETUP phase=settings");
    }
    return PLUGIN_CONTINUE;
}

ObserveSetupAck(player, language)
{
    if (!get_pcvar_num(g_readiness) || !is_user_alive(player) || !g_spawnReady[player]
        || !g_setupSeen[player] || g_setupAck[player] || g_setupFailed[player] || g_setupStep[player] != 3
        || g_setupLanguage[player] != language || !LibraryExists("goldsrcops_player_menu", LibType_Library)) { return; }
    if (goldsrcops_player_language(player) != language) { return; }
    g_setupAck[player] = true;
    log_amx("FIXTURE_GUNS_READY phase=setup-ack language=%s", language == 0 ? "ru" : "en");
    client_cmd(player, "gso_lab_setup_ack");
}

public ObserveSetupRu(player) { ObserveSetupAck(player, 0); return PLUGIN_HANDLED; }
public ObserveSetupEn(player) { ObserveSetupAck(player, 1); return PLUGIN_HANDLED; }

public OnVguiMenu(message_id, destination, player)
{
    new menu = get_msg_arg_int(1);
    if (player >= 1 && player <= 32 && (menu == 2 || menu == 26 || menu == 27))
    {
        log_amx("FIXTURE_TEAM_VGUI suppressed=%d", menu);
        return PLUGIN_HANDLED;
    }
    return PLUGIN_CONTINUE;
}

BeginEpoch(player, bool:teamAlias, panel = 0)
{
    if (!is_user_alive(player) || g_epoch[player] >= 2 || (g_epoch[player] > 0 && g_panel[player] != panel))
        return;
    g_panel[player] = panel;
    ++g_epoch[player];
    get_entvar(player, var_origin, g_origin[player]);
    g_buttons[player] = 0;
    g_moved[player] = false;
    g_reported[player] = false;
    if (!teamAlias)
        g_firstRules[player] = get_gametime();
    new cooldown = teamAlias && get_gametime() - g_firstRules[player] >= 5.0;
    // Clear the legacy AMXX HUD menu only. MOTD contents and close state are untouched.
    show_menu(player, 0, "^n", 1);
    log_amx("FIXTURE_RULES_COMMAND epoch=%d team_alias=%d cooldown_verified=%d", g_epoch[player], teamAlias, cooldown);
    log_amx("FIXTURE_PANEL_COMMAND panel=%s epoch=%d team_alias=%d cooldown_verified=%d", PANELS[panel], g_epoch[player], teamAlias, cooldown);
}

public ObserveRanks(player) { BeginEpoch(player, false, 1); return PLUGIN_CONTINUE; }
public ObserveRanksAlias(player) { BeginEpoch(player, true, 1); return PLUGIN_CONTINUE; }
public ObserveProfile(player) { BeginEpoch(player, false, 2); return PLUGIN_CONTINUE; }
public ObserveProfileAlias(player) { BeginEpoch(player, true, 2); return PLUGIN_CONTINUE; }
public ObserveTopall(player) { BeginEpoch(player, false, 3); return PLUGIN_CONTINUE; }
public ObserveTopallAlias(player) { BeginEpoch(player, true, 3); return PLUGIN_CONTINUE; }

public ObserveRules(player)
{
    BeginEpoch(player, false);
    return PLUGIN_CONTINUE;
}

public ObserveRulesAlias(player)
{
    BeginEpoch(player, true);
    return PLUGIN_CONTINUE;
}

public ObserveMovement(player)
{
    ObserveGunsMovement(player);
    if (!is_user_alive(player) || g_epoch[player] < 1 || g_reported[player])
        return HC_CONTINUE;
    new buttons = get_entvar(player, var_button);
    g_buttons[player] |= buttons & (IN_BACK | IN_MOVELEFT);
    new Float:origin[3];
    get_entvar(player, var_origin, origin);
    new Float:dx = origin[0] - g_origin[player][0];
    new Float:dy = origin[1] - g_origin[player][1];
    if (dx * dx + dy * dy > 64.0)
        g_moved[player] = true;
    if (g_moved[player] && (g_buttons[player] & (IN_BACK | IN_MOVELEFT)) == (IN_BACK | IN_MOVELEFT))
    {
        g_reported[player] = true;
        log_amx("FIXTURE_CONTROL epoch=%d back_observed=1 left_observed=1 horizontal_distance_over_8=1", g_epoch[player]);
        log_amx("FIXTURE_PANEL_CONTROL panel=%s epoch=%d back_observed=1 left_observed=1 horizontal_distance_over_8=1", PANELS[g_panel[player]], g_epoch[player]);
    }
    return HC_CONTINUE;
}

new g_guns[33], g_guns_step[33], g_guns_menu[33], g_guns_inventory[33][6];
new bool:g_guns_failed[33], bool:g_guns_respawn[33], bool:g_guns_movement[33];
new Float:g_guns_origin[33][3], g_guns_buttons[33];
new g_guns_text[33][768];

GunsInventory(player, values[6])
{
    values[0] = get_entvar(player, var_weapons);
    values[1] = get_user_weapon(player);
    get_user_ammo(player, CSW_MP5NAVY, values[2], values[3]);
    get_user_ammo(player, CSW_USP, values[4], values[5]);
}

GunsBackdrop(player, bool:enabled)
{
    // Opaque scene-only fade makes the real AMXX menu pixels deterministic.
    // No menu text, callback, weapon, view angle or player origin is supplied here.
    message_begin(MSG_ONE, get_user_msgid("ScreenFade"), _, player);
    write_short(0);
    write_short(0);
    write_short(enabled ? 5 : 0);
    write_byte(0); write_byte(0); write_byte(0);
    write_byte(enabled ? 255 : 0);
    message_end();
}

GunsMovementStart(player)
{
    get_entvar(player, var_origin, g_guns_origin[player]);
    g_guns_buttons[player] = 0;
    g_guns_movement[player] = false;
}

public ObserveGuns(player)
{
    if (!is_user_alive(player)) { return PLUGIN_CONTINUE; }
    if (get_pcvar_num(g_readiness) && !g_setupAck[player]) { return PLUGIN_CONTINUE; }
    if (!g_guns[player])
    {
        g_guns[player] = 1;
        g_guns_step[player] = 0;
        g_guns_failed[player] = false;
        g_guns_respawn[player] = false;
        GunsInventory(player, g_guns_inventory[player]);
        log_amx("FIXTURE_GUNS_START mp5=%d usp=%d", user_has_weapon(player, CSW_MP5NAVY), user_has_weapon(player, CSW_USP));
    }
    return PLUGIN_CONTINUE;
}

public ObserveGunsMenu(message_id, destination, player)
{
    if (player < 1 || player > 32 || !g_guns[player]) { return PLUGIN_CONTINUE; }
    new text[512]; get_msg_arg_string(4, text, charsmax(text));
    if (strlen(g_guns_text[player]) + strlen(text) >= charsmax(g_guns_text[])) { g_guns_failed[player] = true; return PLUGIN_CONTINUE; }
    add(g_guns_text[player], charsmax(g_guns_text[]), text);
    if (get_msg_arg_int(3)) { return PLUGIN_CONTINUE; }
    if (contain(g_guns_text[player], "MP5") >= 0 && contain(g_guns_text[player], "AK-47") >= 0 && contain(g_guns_text[player], "M4A1") >= 0)
        g_guns_menu[player] = 1;
    else if (contain(g_guns_text[player], "USP") >= 0 && contain(g_guns_text[player], "Glock") >= 0 && contain(g_guns_text[player], "Desert Eagle") >= 0)
        g_guns_menu[player] = 2;
    else { g_guns_menu[player] = 0; }
    g_guns_text[player][0] = 0;
    if (g_guns_menu[player]) { set_task(0.05, "GunsShowBackdrop", player); }
    return PLUGIN_CONTINUE;
}

public GunsShowBackdrop(player)
{
    // Send outside the ShowMenu message hook, after the original message completes.
    if (is_user_alive(player) && g_guns[player] && g_guns_menu[player]) { GunsBackdrop(player, true); }
}

public ObserveGunsSelection(player)
{
    if (get_pcvar_num(g_readiness) && g_setupSeen[player] && !g_setupAck[player])
    {
        new argument[8]; read_argv(1, argument, charsmax(argument));
        new key = str_to_num(argument), step = g_setupStep[player]++;
        if ((step == 0 && key != 2) || (step == 1 && key != 1 && key != 2)
            || (step == 2 && key != 10) || step > 2) { g_setupFailed[player] = true; }
        if (step == 1) { g_setupLanguage[player] = key - 1; }
        log_amx("FIXTURE_GUNS_SETUP step=%d valid=%d", step + 1, !g_setupFailed[player]);
        return PLUGIN_CONTINUE;
    }
    if (player < 1 || player > 32 || !g_guns[player] || !g_guns_menu[player] || !is_user_alive(player)) { return PLUGIN_CONTINUE; }
    new argument[8]; read_argv(1, argument, charsmax(argument));
    new key = str_to_num(argument), step = ++g_guns_step[player];
    new const KEYS[] = { 10, 2, 3, 2, 10 };
    new const MENUS[] = { 1, 1, 2, 1, 2 };
    if (step > sizeof KEYS || key != KEYS[step - 1] || g_guns_menu[player] != MENUS[step - 1]) { g_guns_failed[player] = true; }
    g_guns_menu[player] = 0;
    if (key == 10 || key == 3) { GunsBackdrop(player, false); GunsMovementStart(player); }
    set_task(0.1, "ObserveGunsInventory", player);
    return PLUGIN_CONTINUE;
}

public ObserveGunsInventory(player)
{
    if (!is_user_alive(player) || !g_guns[player]) { return; }
    new values[6], unchanged = 1;
    GunsInventory(player, values);
    for (new i = 0; i < sizeof values; i++) { if (values[i] != g_guns_inventory[player][i]) { unchanged = 0; } }
    if (!unchanged) { g_guns_failed[player] = true; }
    log_amx("FIXTURE_GUNS_SELECT step=%d inventory_unchanged=%d", g_guns_step[player], unchanged);
    if (g_guns_step[player] == 5 && !g_guns_failed[player]) { set_task(3.0, "GunsRespawnRequest", player); }
}

ObserveGunsMovement(player)
{
    if (!is_user_alive(player) || !g_guns[player] || g_guns_menu[player] || g_guns_movement[player]) { return; }
    g_guns_buttons[player] |= get_entvar(player, var_button) & (IN_BACK | IN_MOVELEFT);
    new Float:origin[3]; get_entvar(player, var_origin, origin);
    new Float:dx = origin[0] - g_guns_origin[player][0], Float:dy = origin[1] - g_guns_origin[player][1];
    if (dx * dx + dy * dy > 64.0 && g_guns_buttons[player] == (IN_BACK | IN_MOVELEFT))
    {
        g_guns_movement[player] = true;
        log_amx("FIXTURE_GUNS_CONTROL step=%d respawn=%d movement=1", g_guns_step[player], g_guns_respawn[player]);
    }
}

public GunsRespawnRequest(player)
{
    if (!is_user_alive(player) || g_guns_step[player] != 5 || g_guns_failed[player] || !g_guns_movement[player]) { return; }
    // One real death in this disposable fixture only. Never grant or replace items.
    g_guns_respawn[player] = true;
    user_kill(player, 1);
    log_amx("FIXTURE_GUNS_DEATH requested=1");
}

public ObserveGunsSpawn(player)
{
    if (get_pcvar_num(g_readiness) && is_user_alive(player) && !g_spawnReady[player])
    {
        g_spawnReady[player] = true;
        set_task(0.2, "ReadinessSpawn", player + 200);
    }
    if (g_guns_respawn[player] && is_user_alive(player)) { set_task(0.2, "ObserveGunsLoadout", player); }
    return HC_CONTINUE;
}

public ObserveGunsLoadout(player)
{
    if (!is_user_alive(player) || !g_guns_respawn[player]) { return; }
    new ak_clip, ak_reserve, de_clip, de_reserve;
    get_user_ammo(player, CSW_AK47, ak_clip, ak_reserve);
    get_user_ammo(player, CSW_DEAGLE, de_clip, de_reserve);
    new valid = user_has_weapon(player, CSW_AK47) && user_has_weapon(player, CSW_DEAGLE)
        && !user_has_weapon(player, CSW_MP5NAVY) && !user_has_weapon(player, CSW_USP)
        && ak_clip == 30 && ak_reserve == 90 && de_clip == 7 && de_reserve == 35;
    log_amx("FIXTURE_GUNS_RESPAWN selected_loadout=%d", valid);
    GunsMovementStart(player);
}

public client_disconnected(player)
{
    remove_task(player);
    remove_task(player + 100);
    remove_task(player + 200);
    g_spawnReady[player] = false;
    g_setupSeen[player] = false;
    g_setupAck[player] = false;
    g_setupFailed[player] = false;
    g_setupStep[player] = 0;
    g_guns[player] = 0;
    g_guns_menu[player] = 0;
    g_guns_respawn[player] = false;
    g_guns_text[player][0] = 0;
}

public ObserveMotd(message_id, destination, player)
{
    if (get_msg_arg_int(1))
        log_amx("FIXTURE_MOTD_FINAL observed=1");
    return PLUGIN_CONTINUE;
}
