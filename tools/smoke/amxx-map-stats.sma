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
new g_previous_print[192];
new g_print_target;
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
    #pragma unused type
    g_print_target = id;
    g_prints++;
    copy(g_previous_print, charsmax(g_previous_print), g_last_print);
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
    set_entvar(killer, var_health, 60.0);
    new Float:armor = get_entvar(killer, var_armorvalue);
    new active_weapon = get_member(killer, m_pActiveItem);
    new weapons_before[32], weapons_after[32], count_before, count_after;
    get_user_weapons(killer, weapons_before, count_before);
    EmitDeath(killer, victim);
    CheckStats(g_map_kills[killer] == 1 && g_map_deaths[victim] == 1, "enemy event counted once");
    get_user_weapons(killer, weapons_after, count_after);
    new bool:inventory_unchanged = count_before == count_after;
    for (new index = 0; index < count_before; index++)
    {
        if (weapons_before[index] != weapons_after[index]) { inventory_unchanged = false; }
    }
    CheckStats(get_entvar(killer, var_health) == 75.0, "native enemy kill restores default HP");
    CheckStats(get_entvar(killer, var_armorvalue) == armor
        && get_member(killer, m_pActiveItem) == active_weapon && inventory_unchanged, "healing preserves armor and inventory");
    EmitDeath(victim, victim);
    EmitDeath(0, victim);
    CheckStats(g_map_kills[victim] == 0 && g_map_deaths[victim] == 3, "suicide and world death without points");
    rg_set_user_team(victim, TEAM_CT);
    EmitDeath(killer, victim);
    CheckStats(g_map_kills[killer] == 1 && g_map_deaths[victim] == 4, "teamkill gives no points");
    CheckStats(get_entvar(killer, var_health) == 75.0, "suicide world and teamkill do not heal attacker");

    g_human[bot] = false;
    EmitDeath(bot, victim);
    EmitDeath(killer, bot);
    CheckStats(g_map_kills[killer] == 1 && g_map_deaths[victim] == 4 && g_map_deaths[bot] == 0, "bot encounters excluded");
    g_human[bot] = true;
    g_hltv_id = bot;
    EmitDeath(bot, victim);
    EmitDeath(killer, bot);
    CheckStats(g_map_kills[killer] == 1 && g_map_deaths[victim] == 4 && g_map_deaths[bot] == 0, "HLTV encounters excluded");
    CheckStats(get_entvar(killer, var_health) == 75.0, "bot and HLTV encounters do not heal");
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
    CheckStats(g_prints == printed + 3, "cooldown expires for stats and streaks together");
    rg_set_user_team(killer, TEAM_SPECTATOR);
    g_clock += 2.0;
    ShowMapStats(killer);
    CheckStats(g_prints == printed + 6, "spectator can read retained stats");

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
    RunKillHealChecks(killer, victim);
    RunKillAmmoChecks(killer, victim);
    RunKillStreakChecks(killer, victim, bot);
    server_print("MAP_STATS_SMOKE=%s failures=%d synthetic_clients=6 native_damage=1",
        g_failures ? "failed" : "passed", g_failures);
    return PLUGIN_HANDLED;
}

RunKillStreakChecks(killer, victim, bot)
{
    rg_set_user_team(killer, TEAM_CT);
    rg_set_user_team(victim, TEAM_TERRORIST);
    set_pcvar_num(g_enabled, 1);
    for (new language = 0; language < 2; language++)
    {
        ResetMapStats(killer);
        ResetMapStats(victim);
        g_language_override[killer] = language + 1;
        rg_round_respawn(killer);
        new prints = g_prints, milestones;
        for (new count = 1; count <= 11; count++)
        {
            EmitDeath(killer, victim);
            CheckStats(g_kill_streak[killer] == count && g_best_streak[killer] == count,
                "native enemy kills advance current and best once");
            if (count == 3 || count == 5 || count == 10)
            {
                milestones++;
                new expected[192];
                formatex(expected, charsmax(expected), "[GoldSrcOps] %L", PLAYER_LANGUAGES[language], "GS_STREAK_MILESTONE", count);
                CheckStats(equal(g_last_print, expected) && g_print_target == killer,
                    "milestone message is translated and private");
            }
            CheckStats(g_prints == prints + milestones, "only 3 5 10 announce once per life");
        }
        new expected[192];
        formatex(expected, charsmax(expected), "[GoldSrcOps] %L", PLAYER_LANGUAGES[language], "GS_STREAK_STATS", 11, 11);
        ShowMapStats(killer);
        CheckStats(equal(g_previous_print, expected) && g_print_target == killer,
            "stats includes translated current and best without replacing totals");
        prints = g_prints;
        ShowMapStats(killer);
        CheckStats(g_prints == prints, "streak stats shares command cooldown");
        EmitDeath(victim, killer);
        CheckStats(!g_kill_streak[killer] && g_best_streak[killer] == 11, "enemy death resets current not best");
        RecordMapDeath(killer, victim);
        CheckStats(!g_kill_streak[killer] && g_best_streak[killer] == 11, "posthumous kill cannot start new streak");
        rg_round_respawn(killer);
        prints = g_prints;
        for (new count = 0; count < 3; count++) { EmitDeath(killer, victim); }
        CheckStats(g_kill_streak[killer] == 3 && g_best_streak[killer] == 11
            && g_prints == prints + 1, "new life can earn milestone again without lowering best");
    }

    new prints = g_prints;
    rg_set_user_team(victim, TEAM_CT);
    EmitDeath(killer, victim);
    rg_set_user_team(victim, TEAM_TERRORIST);
    g_human[victim] = false;
    EmitDeath(killer, victim);
    g_human[victim] = true;
    g_hltv_id = victim;
    EmitDeath(killer, victim);
    g_hltv_id = 0;
    g_disconnected_id = killer;
    RecordMapDeath(killer, victim);
    g_disconnected_id = 0;
    rg_set_user_team(killer, TEAM_SPECTATOR);
    RecordMapDeath(killer, victim);
    rg_set_user_team(killer, TEAM_CT);
    CheckStats(g_kill_streak[killer] == 3 && g_prints == prints, "excluded kills do not advance or announce");

    // Scoring exclusions must not let a player carry a streak through death.
    for (new kind = 0; kind < 6; kind++)
    {
        g_kill_streak[killer] = 7;
        if (kind == 0) { EmitDeath(killer, killer); }
        if (kind == 1) { EmitDeath(0, killer); }
        if (kind == 2) { EmitDeath(bot, killer); }
        if (kind == 3) { g_human[bot] = false; EmitDeath(bot, killer); g_human[bot] = true; }
        if (kind == 4) { g_hltv_id = bot; EmitDeath(bot, killer); g_hltv_id = 0; }
        if (kind == 5) { set_pcvar_num(g_enabled, 0); EmitDeath(victim, killer); set_pcvar_num(g_enabled, 1); }
        CheckStats(!g_kill_streak[killer] && g_best_streak[killer] == 11,
            "suicide world team bot HLTV and disabled deaths reset only current");
    }
    rg_round_respawn(killer);
    set_pcvar_num(g_enabled, 0);
    EmitDeath(killer, victim);
    set_pcvar_num(g_enabled, 1);
    CheckStats(!g_kill_streak[killer], "disabled addon does not advance streak");
    g_kill_streak[killer] = STATS_COUNTER_LIMIT;
    g_best_streak[killer] = STATS_COUNTER_LIMIT;
    EmitDeath(killer, victim);
    CheckStats(g_kill_streak[killer] == STATS_COUNTER_LIMIT && g_best_streak[killer] == STATS_COUNTER_LIMIT,
        "streak counters saturate without overflow");
    new rejected[128];
    client_disconnected(killer, false, rejected, charsmax(rejected));
    CheckStats(!g_kill_streak[killer] && !g_best_streak[killer], "disconnect clears both streak counters");
    g_kill_streak[killer] = 7;
    g_best_streak[killer] = 9;
    client_putinserver(killer);
    CheckStats(!g_kill_streak[killer] && !g_best_streak[killer], "slot reuse cannot inherit streak counters");
    g_kill_streak[killer] = 7;
    g_best_streak[killer] = 9;
    ResetMapStats(killer);
    CheckStats(!g_kill_streak[killer] && !g_best_streak[killer], "fresh connection map reset clears streak counters");
    server_print("KILL_STREAK_SMOKE=%s failures=%d native_damage=1 languages=2 storage=none", g_failures ? "failed" : "passed", g_failures);
}

RunKillHealChecks(killer, victim)
{
    rg_set_user_team(killer, TEAM_CT);
    rg_set_user_team(victim, TEAM_TERRORIST);
    rg_round_respawn(killer);
    set_entvar(killer, var_health, 92.0);
    EmitDeath(killer, victim);
    CheckStats(get_entvar(killer, var_health) == 100.0, "native kill capped at 100 HP");
    set_entvar(killer, var_health, 60.5);
    EmitDeath(killer, victim);
    CheckStats(get_entvar(killer, var_health) == 75.5, "fractional health retained");
    set_pcvar_num(g_kill_heal, 0);
    EmitDeath(killer, victim);
    CheckStats(get_entvar(killer, var_health) == 75.5, "zero disables healing but not scoring");
    set_pcvar_num(g_kill_heal, -10);
    EmitDeath(killer, victim);
    CheckStats(get_entvar(killer, var_health) == 75.5, "negative amount disabled");
    set_pcvar_num(g_kill_heal, 2147483647);
    EmitDeath(killer, victim);
    CheckStats(get_entvar(killer, var_health) == 100.0, "large amount bounded without overflow");
    set_entvar(killer, var_health, 125.0);
    EmitDeath(killer, victim);
    CheckStats(get_entvar(killer, var_health) == 125.0, "external boosted health not reduced");
    set_pcvar_num(g_kill_heal, 15);
    EmitDeath(killer, killer);
    new Float:dead_health = get_entvar(killer, var_health);
    RecordMapDeath(killer, victim);
    CheckStats(!is_user_alive(killer) && get_entvar(killer, var_health) == dead_health, "posthumous kill does not revive or heal");
    rg_round_respawn(killer);
    set_entvar(killer, var_health, 60.0);
    g_disconnected_id = killer;
    RecordMapDeath(killer, victim);
    g_disconnected_id = 0;
    rg_set_user_team(killer, TEAM_SPECTATOR);
    RecordMapDeath(killer, victim);
    rg_set_user_team(killer, TEAM_CT);
    set_pcvar_num(g_enabled, 0);
    EmitDeath(killer, victim);
    set_pcvar_num(g_enabled, 1);
    CheckStats(get_entvar(killer, var_health) == 60.0, "disconnected spectator and global disable do not heal");
    OnStatusCommand();
    server_print("KILL_HEAL_SMOKE=%s failures=%d native_damage=1", g_failures ? "failed" : "passed", g_failures);
}

RunKillAmmoChecks(killer, victim)
{
    rg_set_user_team(killer, TEAM_CT);
    rg_set_user_team(victim, TEAM_TERRORIST);
    for (new primary = 0; primary < sizeof WEAPON_IDS; primary++)
    {
        for (new pistol = 0; pistol < sizeof PISTOL_IDS; pistol++)
        {
            g_choice[killer] = primary;
            g_pistol_choice[killer] = pistol;
            rg_round_respawn(killer);
            rg_set_user_bpammo(killer, WEAPON_IDS[primary], 1);
            rg_set_user_bpammo(killer, PISTOL_IDS[pistol], 1);
            rg_set_user_ammo(killer, WEAPON_IDS[primary], 2);
            rg_set_user_ammo(killer, PISTOL_IDS[pistol], 2);
            new active = get_member(killer, m_pActiveItem);
            new weapons_before[32], count_before, weapons_after[32], count_after;
            get_user_weapons(killer, weapons_before, count_before);
            EmitDeath(killer, victim);
            get_user_weapons(killer, weapons_after, count_after);
            CheckStats(rg_get_user_bpammo(killer, WEAPON_IDS[primary]) == BACKPACK_AMMO[primary]
                && rg_get_user_bpammo(killer, PISTOL_IDS[pistol]) == PISTOL_AMMO[pistol], "native kill refills all nine loadouts including shared 9mm");
            CheckStats(rg_get_user_ammo(killer, WEAPON_IDS[primary]) == 2
                && rg_get_user_ammo(killer, PISTOL_IDS[pistol]) == 2
                && get_member(killer, m_pActiveItem) == active && count_before == count_after, "reserve refill leaves clip active weapon and inventory alone");
        }
    }
    // Next-spawn MP5/USP choices must not replace the held M4A1/Deagle.
    g_choice[killer] = 0;
    g_pistol_choice[killer] = 0;
    new reserve_9mm_index = rg_get_weapon_info(WEAPON_MP5N, WI_AMMO_TYPE);
    new reserve_45_index = rg_get_weapon_info(WEAPON_USP, WI_AMMO_TYPE);
    set_member(killer, m_rgAmmo, 7, reserve_9mm_index);
    set_member(killer, m_rgAmmo, 8, reserve_45_index);
    rg_set_user_bpammo(killer, WEAPON_M4A1, 150);
    EmitDeath(killer, victim);
    CheckStats(!user_has_weapon(killer, _:WEAPON_MP5N) && !user_has_weapon(killer, _:WEAPON_USP)
        && get_member(killer, m_rgAmmo, reserve_9mm_index) == 7 && get_member(killer, m_rgAmmo, reserve_45_index) == 8,
        "next-spawn selection and unowned ammo pools untouched");
    CheckStats(rg_get_user_bpammo(killer, WEAPON_M4A1) == 150, "external surplus reserve retained");
    rg_set_user_bpammo(killer, WEAPON_M4A1, 1);
    set_pcvar_num(g_kill_ammo, 0);
    new kills = g_map_kills[killer];
    set_entvar(killer, var_health, 60.0);
    EmitDeath(killer, victim);
    CheckStats(rg_get_user_bpammo(killer, WEAPON_M4A1) == 1 && g_map_kills[killer] == kills + 1
        && get_entvar(killer, var_health) == 75.0, "ammo disable preserves scoring and healing");
    set_pcvar_num(g_kill_ammo, 1);
    rg_set_user_team(victim, TEAM_CT);
    EmitDeath(killer, victim);
    rg_set_user_team(victim, TEAM_TERRORIST);
    EmitDeath(victim, victim);
    EmitDeath(0, victim);
    g_human[victim] = false;
    EmitDeath(killer, victim);
    g_human[victim] = true;
    g_hltv_id = victim;
    EmitDeath(killer, victim);
    g_hltv_id = 0;
    g_disconnected_id = killer;
    RecordMapDeath(killer, victim);
    g_disconnected_id = 0;
    rg_set_user_team(killer, TEAM_SPECTATOR);
    RecordMapDeath(killer, victim);
    rg_set_user_team(killer, TEAM_CT);
    set_pcvar_num(g_enabled, 0);
    EmitDeath(killer, victim);
    set_pcvar_num(g_enabled, 1);
    CheckStats(rg_get_user_bpammo(killer, WEAPON_M4A1) == 1, "excluded kills and global disable do not refill");
    EmitDeath(killer, killer);
    new dead_ammo = rg_get_user_bpammo(killer, WEAPON_M4A1);
    RecordMapDeath(killer, victim);
    CheckStats(!is_user_alive(killer) && rg_get_user_bpammo(killer, WEAPON_M4A1) == dead_ammo, "posthumous kill cannot refill");
    OnStatusCommand();
    server_print("KILL_AMMO_SMOKE=%s failures=%d native_damage=1 loadouts=9", g_failures ? "failed" : "passed", g_failures);
}
