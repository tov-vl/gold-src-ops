#include <amxmodx>
#include <reapi>
#include <fakemeta>
#include <hamsandwich>

// Synthetic identities and output capture exist only in the fixture binary.
#define plugin_init ProductPluginInit
#define is_user_bot(%1) FixtureIsBot(%1)
#define is_user_hltv(%1) FixtureIsHltv(%1)
#define is_user_connected(%1) FixtureIsConnected(%1)
#define client_print FixturePrint
#define get_gametime FixtureTime
#include <goldsrcops_weapon_selection.sma>
#undef plugin_init
#undef is_user_bot
#undef is_user_hltv
#undef is_user_connected
#undef client_print
#undef get_gametime

new g_clients[6];
new bool:g_human[MAX_PLAYERS + 1];
new g_hltv_id;
new g_disconnected_id;
new g_failures;
new g_prints;
new g_last_print[192];
new Float:g_clock = 10.0;

public plugin_init()
{
    ProductPluginInit();
    register_srvcmd("goldsrcops_map_stats_smoke", "RunMapStatsSmoke");
}

bool:FixtureIsBot(id)
{
    return !g_human[id] && is_user_bot(id);
}

bool:FixtureIsHltv(id)
{
    return id == g_hltv_id || is_user_hltv(id);
}

bool:FixtureIsConnected(id)
{
    return id != g_disconnected_id && is_user_connected(id);
}

Float:FixtureTime()
{
    return g_clock;
}

FixturePrint(id, type, const message[], any:...)
{
    #pragma unused id, type
    g_prints++;
    vformat(g_last_print, charsmax(g_last_print), message, 4);
    return strlen(g_last_print);
}

CheckStats(bool:passed, const label[])
{
    if (!passed)
    {
        g_failures++;
        server_print("MAP_STATS_ASSERTION_FAILED %s", label);
    }
}

EmitDeath(killer, victim)
{
    rg_round_respawn(victim);
    ExecuteHamB(Ham_TakeDamage, victim, killer, killer, 1000.0, DMG_BULLET);
}

public RunMapStatsSmoke()
{
    if (g_clients[0] || get_playersnum() || MaxClients < sizeof g_clients)
    {
        server_print("MAP_STATS_SMOKE=refused; requires fresh empty six-slot fixture");
        return PLUGIN_HANDLED;
    }
    for (new index = 0; index < sizeof g_clients; index++)
    {
        new name[32], rejected[128];
        formatex(name, charsmax(name), "Stats fixture %d", index + 1);
        new id = engfunc(EngFunc_CreateFakeClient, name);
        if (!id)
        {
            server_print("MAP_STATS_SMOKE=failed; fixture slot unavailable");
            return PLUGIN_HANDLED;
        }
        g_clients[index] = id;
        g_human[id] = true;
        dllfunc(DLLFunc_ClientConnect, id, name, "127.0.0.1", rejected);
        dllfunc(DLLFunc_ClientPutInServer, id);
        set_user_info(id, "lang", "en");
        rg_set_user_team(id, TEAM_CT);
        set_member(id, m_iJoiningState, JOINED);
        client_putinserver(id);
    }
    set_pcvar_num(g_enabled, 1);
    set_cvar_num("mp_friendlyfire", 1);
    // GameDLL initializes attacker identity on spawn, not only ClientPutInServer.
    for (new index = 0; index < sizeof g_clients; index++)
    {
        rg_round_respawn(g_clients[index]);
    }
    new killer = g_clients[0], victim = g_clients[1], bot = g_clients[2];
    rg_set_user_team(victim, TEAM_TERRORIST);

    // GameDLL death emits DeathMsg, proving registration and field extraction.
    EmitDeath(killer, victim);
    CheckStats(g_map_kills[killer] == 1 && g_map_deaths[victim] == 1, "enemy event counted once");
    EmitDeath(victim, victim);
    EmitDeath(0, victim);
    CheckStats(g_map_kills[victim] == 0 && g_map_deaths[victim] == 3, "suicide and world death without points");
    rg_set_user_team(victim, TEAM_CT);
    EmitDeath(killer, victim);
    CheckStats(g_map_kills[killer] == 1 && g_map_deaths[victim] == 4, "teamkill gives no points");

    g_human[bot] = false;
    EmitDeath(bot, victim);
    EmitDeath(killer, bot);
    CheckStats(g_map_kills[killer] == 1 && g_map_deaths[victim] == 4 && g_map_deaths[bot] == 0, "bot encounters excluded");
    g_human[bot] = true;
    g_hltv_id = bot;
    EmitDeath(bot, victim);
    EmitDeath(killer, bot);
    CheckStats(g_map_kills[killer] == 1 && g_map_deaths[victim] == 4 && g_map_deaths[bot] == 0, "HLTV encounters excluded");
    g_hltv_id = 0;
    rg_set_user_team(victim, TEAM_SPECTATOR);
    // Respawning a spectator would change its team before the event.
    RecordMapDeath(0, victim);
    RecordMapDeath(victim, killer);
    CheckStats(g_map_deaths[victim] == 4 && g_map_deaths[killer] == 0, "spectator death participants excluded");
    rg_set_user_team(victim, TEAM_TERRORIST);
    g_disconnected_id = killer;
    EmitDeath(killer, victim);
    EmitDeath(victim, killer);
    CheckStats(g_map_deaths[victim] == 4 && g_map_deaths[killer] == 0, "disconnected participants excluded");
    g_disconnected_id = 0;
    RecordMapDeath(MaxClients + 1, victim);
    RecordMapDeath(killer, MaxClients + 1);
    RecordMapDeath(-1, victim);
    RecordMapDeath(killer, 0);
    set_pcvar_num(g_enabled, 0);
    EmitDeath(killer, victim);
    ShowMapStats(killer);
    ShowMapLeaders(killer);
    CheckStats(g_map_kills[killer] == 1 && g_map_deaths[victim] == 4 && g_prints == 0, "invalid and disabled refuse");
    set_pcvar_num(g_enabled, 1);

    ShowMapStats(killer);
    CheckStats(contain(g_last_print, "K 1 | D 0 | K/D 1.00") >= 0, "zero deaths has finite ratio");
    new printed = g_prints;
    ShowMapStats(killer);
    CheckStats(g_prints == printed, "stats cooldown suppresses immediate repeat");
    g_clock += 2.0;
    ShowMapStats(killer);
    CheckStats(g_prints == printed + 1, "cooldown expires");
    rg_set_user_team(killer, TEAM_SPECTATOR);
    g_clock += 2.0;
    ShowMapStats(killer);
    CheckStats(g_prints == printed + 2, "spectator can read retained stats");

    for (new index = 0; index < sizeof g_clients; index++)
    {
        new id = g_clients[index];
        ResetMapStats(id);
        g_map_kills[id] = 6 - index;
        g_map_deaths[id] = index;
    }
    g_map_kills[victim] = g_map_kills[killer];
    g_map_deaths[victim] = 0;
    new leaders[MAP_LEADER_LIMIT];
    CheckStats(BuildMapLeaders(leaders) == 5 && leaders[0] == killer && leaders[1] == victim, "top limit and exact tie order");
    g_map_deaths[killer] = 2;
    BuildMapLeaders(leaders);
    CheckStats(leaders[0] == victim && leaders[1] == killer, "equal kills prefer fewer deaths");
    printed = g_prints;
    ShowMapLeaders(killer);
    CheckStats(g_prints == printed + 6, "top emits one header and five private lines");
    ShowMapLeaders(killer);
    CheckStats(g_prints == printed + 6, "top has its own cooldown");
    g_human[victim] = false;
    g_hltv_id = bot;
    g_disconnected_id = g_clients[3];
    CheckStats(BuildMapLeaders(leaders) == 3 && leaders[0] == killer, "top excludes bot HLTV and disconnected");
    g_human[victim] = true;
    g_hltv_id = 0;
    g_disconnected_id = 0;

    new rejected[128];
    client_disconnected(killer, false, rejected, charsmax(rejected));
    CheckStats(!g_map_kills[killer] && !g_map_deaths[killer] && g_stats_next[killer][1] == 0.0, "disconnect resets counters and cooldown");
    g_map_kills[killer] = 12;
    client_putinserver(killer);
    CheckStats(!g_map_kills[killer], "slot reuse resets counters");
    new name[32] = "100% ^nname^t^r";
    CleanStatsName(name);
    CheckStats(bool:equal(name, "100%  name  "), "nickname controls removed without format expansion");
    for (new index = 0; index < sizeof g_clients; index++)
    {
        ResetMapStats(g_clients[index]);
    }
    CheckStats(BuildMapLeaders(leaders) == 0, "fresh map state has no leaders");
    printed = g_prints;
    ShowMapLeaders(killer);
    CheckStats(g_prints == printed + 1 && contain(g_last_print, "No human kills or deaths") >= 0, "empty top is clear");
    ShowMapStats(0);
    ShowMapLeaders(MaxClients + 1);
    CheckStats(g_prints == printed + 1, "invalid command IDs refuse");
    OnMapStatsStatus();
    server_print("MAP_STATS_SMOKE=%s failures=%d synthetic_clients=6 native_damage=1",
        g_failures ? "failed" : "passed", g_failures);
    return PLUGIN_HANDLED;
}
