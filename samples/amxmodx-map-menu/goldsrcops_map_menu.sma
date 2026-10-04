#pragma semicolon 1
#pragma tabsize 4

#include <amxmodx>
#include <reapi>

new const MAP_CYCLE[] = "goldsrcops-mapcycle.txt";
new const MAP_NAMES[][] = { "de_dust2", "de_inferno", "de_nuke", "de_train", "cs_office" };
new g_maps_enabled;
new g_maps_menu;
new g_current_map[32];

public plugin_init()
{
    register_plugin("GoldSrcOps Map Menu", "0.1.0", "GoldSrcOps");
    g_maps_enabled = register_cvar("goldsrcops_maps_enabled", "0");
    AutoExecConfig(false, "goldsrcops-map-menu");
    register_clcmd("say /maps", "OpenMaps");
    register_clcmd("say_team /maps", "OpenMaps");
    register_clcmd("maps", "OpenMaps");
    register_clcmd("say /timeleft", "ShowTimeLeft");
    register_clcmd("say_team /timeleft", "ShowTimeLeft");
    register_srvcmd("goldsrcops_maps_status", "OnMapsStatus");
    get_mapname(g_current_map, charsmax(g_current_map));

    g_maps_menu = menu_create("Vote for a map", "OnMapSelected");
    new callback = menu_makecallback("MapAvailable");
    for (new index = 0; index < sizeof MAP_NAMES; index++)
    {
        new label[48];
        formatex(label, charsmax(label), "%s%s", MAP_NAMES[index],
            equal(MAP_NAMES[index], g_current_map) ? " (current)" : "");
        menu_additem(g_maps_menu, label, "", 0, callback);
    }
}

public plugin_end()
{
    menu_destroy(g_maps_menu);
}

bool:IsMapPlayer(id)
{
    if (!is_user_connected(id) || is_user_bot(id) || is_user_hltv(id))
    {
        return false;
    }
    new TeamName:team = get_member(id, m_iTeam);
    return team == TEAM_TERRORIST || team == TEAM_CT;
}

bool:MapCycleReady()
{
    new cycle[64];
    get_cvar_string("mapcyclefile", cycle, charsmax(cycle));
    if (!equal(cycle, MAP_CYCLE) || file_size(MAP_CYCLE) > 256)
    {
        return false;
    }
    new file = fopen(MAP_CYCLE, "rt");
    if (!file)
    {
        return false;
    }

    // Native votemap IDs are positions in the validated mapcycle, not menu IDs.
    new line[64], count;
    new bool:valid = true;
    while (!feof(file))
    {
        fgets(file, line, charsmax(line));
        trim(line);
        if (!line[0])
        {
            continue;
        }
        if (count >= sizeof MAP_NAMES || !equal(line, MAP_NAMES[count]) || !is_map_valid(line))
        {
            valid = false;
            break;
        }
        count++;
    }
    fclose(file);
    return valid && count == sizeof MAP_NAMES;
}

bool:MapVotingEnabled()
{
    new flags[32];
    get_cvar_string("mp_vote_flags", flags, charsmax(flags));
    return contain(flags, "m") >= 0;
}

public OpenMaps(id)
{
    if (!get_pcvar_num(g_maps_enabled) || !IsMapPlayer(id))
    {
        return PLUGIN_HANDLED;
    }
    if (!MapCycleReady() || !MapVotingEnabled())
    {
        client_print(id, print_chat, "[GoldSrcOps] Map voting is unavailable.");
        return PLUGIN_HANDLED;
    }
    menu_display(id, g_maps_menu);
    return PLUGIN_HANDLED;
}

public MapAvailable(id, menu, item)
{
    #pragma unused id, menu
    if (item < 0 || item >= sizeof MAP_NAMES || equal(MAP_NAMES[item], g_current_map))
    {
        return ITEM_DISABLED;
    }
    return ITEM_ENABLED;
}

public OnMapSelected(id, menu, item)
{
    if (menu != g_maps_menu || item < 0 || item >= sizeof MAP_NAMES
        || !get_pcvar_num(g_maps_enabled) || !IsMapPlayer(id)
        || equal(MAP_NAMES[item], g_current_map))
    {
        return PLUGIN_HANDLED;
    }
    // Recheck on selection: the file or policy may have changed with the menu open.
    if (!MapCycleReady() || !MapVotingEnabled())
    {
        client_print(id, print_chat, "[GoldSrcOps] Map voting is unavailable.");
        return PLUGIN_HANDLED;
    }
    new vote_id[8];
    num_to_str(item + 1, vote_id, charsmax(vote_id));
    client_print(id, print_chat, "[GoldSrcOps] Vote requested for %s. Check console for the game's result.", MAP_NAMES[item]);
    engclient_cmd(id, "votemap", vote_id);
    return PLUGIN_HANDLED;
}

public ShowTimeLeft(id)
{
    if (get_pcvar_num(g_maps_enabled) && is_user_connected(id) && !is_user_bot(id) && !is_user_hltv(id))
    {
        engclient_cmd(id, "timeleft");
    }
    return PLUGIN_HANDLED;
}

public OnMapsStatus()
{
    server_print("MAP_MENU_STATUS version=0.1.0 enabled=%d cycle_ready=%d voting_enabled=%d maps=%d",
        get_pcvar_num(g_maps_enabled) != 0, MapCycleReady(), MapVotingEnabled(), sizeof MAP_NAMES);
    return PLUGIN_HANDLED;
}
