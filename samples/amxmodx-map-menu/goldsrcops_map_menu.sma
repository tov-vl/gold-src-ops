#pragma semicolon 1
#pragma tabsize 4

#include <amxmodx>
#include <reapi>

native goldsrcops_player_language(id);

new const MAP_CYCLE[] = "goldsrcops-mapcycle.txt";
new const MAP_NAMES[][] = { "de_dust2", "de_inferno", "de_nuke", "de_train", "cs_office" };
new const MAP_LANGUAGES[][] = { "ru", "en" };
new g_maps_enabled;
new g_maps_menu[2];
new g_current_map[32];

public plugin_natives()
{
    set_native_filter("MapNativeFilter");
}

public MapNativeFilter(const name[], index, trap)
{
    #pragma unused index
    return equal(name, "goldsrcops_player_language") && !trap ? PLUGIN_HANDLED : PLUGIN_CONTINUE;
}

MapLanguage(id)
{
    if (LibraryExists("goldsrcops_player_menu", LibType_Library))
    {
        new language = goldsrcops_player_language(id);
        if (language == 0 || language == 1) { return language; }
    }
    new language[16];
    get_user_info(id, "lang", language, charsmax(language));
    return !language[0] || equali(language, "ru") ? 0 : 1;
}

public plugin_init()
{
    register_plugin("GoldSrcOps Map Menu", "0.2.0", "GoldSrcOps");
    if (!register_dictionary("goldsrcops-map-menu.txt"))
    {
        set_fail_state("Map menu dictionary unavailable.");
        return;
    }
    g_maps_enabled = register_cvar("goldsrcops_maps_enabled", "0");
    AutoExecConfig(false, "goldsrcops-map-menu");
    register_clcmd("say /maps", "OpenMaps");
    register_clcmd("say_team /maps", "OpenMaps");
    register_clcmd("maps", "OpenMaps");
    register_clcmd("say /timeleft", "ShowTimeLeft");
    register_clcmd("say_team /timeleft", "ShowTimeLeft");
    register_srvcmd("goldsrcops_maps_status", "OnMapsStatus");
    get_mapname(g_current_map, charsmax(g_current_map));

    new callback = menu_makecallback("MapAvailable");
    for (new language = 0; language < sizeof MAP_LANGUAGES; language++)
    {
        new label[96];
        formatex(label, charsmax(label), "%L", MAP_LANGUAGES[language], "GS_MAP_TITLE");
        g_maps_menu[language] = menu_create(label, "OnMapSelected");
        for (new index = 0; index < sizeof MAP_NAMES; index++)
        {
            if (equal(MAP_NAMES[index], g_current_map))
            {
                formatex(label, charsmax(label), "%L", MAP_LANGUAGES[language], "GS_MAP_CURRENT", MAP_NAMES[index]);
            }
            else { copy(label, charsmax(label), MAP_NAMES[index]); }
            menu_additem(g_maps_menu[language], label, "", 0, callback);
        }
        formatex(label, charsmax(label), "%L", MAP_LANGUAGES[language], "GS_MAP_EXIT");
        menu_setprop(g_maps_menu[language], MPROP_EXITNAME, label);
    }
}

public plugin_end()
{
    for (new language = 0; language < sizeof MAP_LANGUAGES; language++)
    {
        menu_destroy(g_maps_menu[language]);
    }
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
        client_print(id, print_chat, "[GoldSrcOps] %L", MAP_LANGUAGES[MapLanguage(id)], "GS_MAP_UNAVAILABLE");
        return PLUGIN_HANDLED;
    }
    menu_display(id, g_maps_menu[MapLanguage(id)]);
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
    if (item < 0 || item >= sizeof MAP_NAMES
        || !get_pcvar_num(g_maps_enabled) || !IsMapPlayer(id)
        || menu != g_maps_menu[MapLanguage(id)]
        || equal(MAP_NAMES[item], g_current_map))
    {
        return PLUGIN_HANDLED;
    }
    // Recheck on selection: the file or policy may have changed with the menu open.
    if (!MapCycleReady() || !MapVotingEnabled())
    {
        client_print(id, print_chat, "[GoldSrcOps] %L", MAP_LANGUAGES[MapLanguage(id)], "GS_MAP_UNAVAILABLE");
        return PLUGIN_HANDLED;
    }
    new vote_id[8];
    num_to_str(item + 1, vote_id, charsmax(vote_id));
    client_print(id, print_chat, "[GoldSrcOps] %L", MAP_LANGUAGES[MapLanguage(id)], "GS_MAP_REQUESTED", MAP_NAMES[item]);
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
    server_print("MAP_MENU_STATUS version=0.2.0 enabled=%d cycle_ready=%d voting_enabled=%d maps=%d languages=ru,en language_bridge=%d timeleft=native",
        get_pcvar_num(g_maps_enabled) != 0, MapCycleReady(), MapVotingEnabled(), sizeof MAP_NAMES,
        LibraryExists("goldsrcops_player_menu", LibType_Library));
    return PLUGIN_HANDLED;
}
