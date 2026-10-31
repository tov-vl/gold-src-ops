#include <amxmodx>
#include <reapi>

new g_epoch[33];
new Float:g_origin[33][3];
new g_buttons[33];
new bool:g_moved[33];
new bool:g_reported[33];
new Float:g_firstRules[33];

public plugin_init()
{
    register_plugin("Fixture interaction evidence", "1.1.0", "");
    register_message(get_user_msgid("VGUIMenu"), "OnVguiMenu");
    register_clcmd("rules", "ObserveRules");
    register_clcmd("say_team /rules", "ObserveRulesAlias");
    register_message(get_user_msgid("MOTD"), "ObserveMotd");
    RegisterHookChain(RG_CBasePlayer_PostThink, "ObserveMovement", true);
}

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

BeginEpoch(player, bool:teamAlias)
{
    if (!is_user_alive(player) || g_epoch[player] >= 2)
        return;
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
}

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
    }
    return HC_CONTINUE;
}

public ObserveMotd(message_id, destination, player)
{
    if (get_msg_arg_int(1))
        log_amx("FIXTURE_MOTD_FINAL observed=1");
    return PLUGIN_CONTINUE;
}
