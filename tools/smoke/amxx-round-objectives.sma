#include <amxmodx>
#include <file>
#include <reapi>
#include <fakemeta>
#include <hamsandwich>

// The unchanged addon runs here with only the three synthetic identities exempt
// from bot exclusion. The unchanged producer is a separate companion plugin.
#define plugin_init ProductPluginInit
#define is_user_bot(%1) FixtureIsBot(%1)
#include <goldsrcops_weapon_selection.sma>
#undef plugin_init
#undef is_user_bot

new g_clients[3];
new g_failures;
new g_mode;
new g_stage;
new g_bomb;
new g_roundEnds;
new g_restarts;
new g_plants;
new g_defuses;
new g_explosions;
new g_killsBefore[3], g_deathsBefore[3];
new g_ctWins, g_tWins;
new Float:g_started, Float:g_active, Float:g_planted, Float:g_ended;
new bool:g_blocked;
new g_spool[180];

public plugin_init()
{
    ProductPluginInit();
    register_srvcmd("goldsrcops_bomb_explode", "RunExplosion");
    register_srvcmd("goldsrcops_bomb_defuse", "RunDefuse");
    register_srvcmd("goldsrcops_round_timeout", "RunTimeout");
    register_srvcmd("goldsrcops_bomb_blocked", "RunBlocked");
    RegisterHookChain(RG_RoundEnd, "ObserveObjectiveEnd", true);
    RegisterHookChain(RG_CSGameRules_RestartRound, "BeforeRestart");
    RegisterHookChain(RG_CSGameRules_RestartRound, "AfterRestart", true);
    RegisterHookChain(RG_CSGameRules_OnRoundFreezeEnd, "AfterFreeze", true);
    RegisterHookChain(RG_PlantBomb, "AfterPlant", true);
    RegisterHookChain(RG_CGrenade_DefuseBombEnd, "AfterDefuse", true);
    RegisterHookChain(RG_CGrenade_ExplodeBomb, "AfterExplosion", true);
}

bool:FixtureIsBot(id)
{
    return id != g_clients[0] && id != g_clients[1] && id != g_clients[2] && is_user_bot(id);
}

ObjectiveCheck(bool:passed, const label[])
{
    if (!passed) { g_failures++; server_print("ROUND_OBJECTIVE_ASSERTION_FAILED %s", label); }
}

public RunExplosion() { return BeginObjective(1, false); }
public RunDefuse() { return BeginObjective(2, false); }
public RunTimeout() { return BeginObjective(3, false); }
public RunBlocked() { return BeginObjective(1, true); }

BeginObjective(mode, bool:blocked)
{
    if (g_mode || get_playersnum() || MaxClients < sizeof g_clients)
    {
        server_print("ROUND_OBJECTIVE_SMOKE=refused; fresh empty isolated process required");
        return PLUGIN_HANDLED;
    }
    g_mode = mode;
    g_blocked = blocked;
    g_started = get_gametime();
    server_print("ROUND_OBJECTIVE_BEGIN mode=%d time=%.3f", mode, g_started);
    server_cmd("exec multiplayer-profile.cfg"); server_exec();
    ObjectiveCheck(get_cvar_num("mp_forcerespawn") == 1 && get_cvar_num("mp_roundrespawn_time") == -1, "managed forced-respawn policy");
    ObjectiveCheck(get_cvar_num("mp_freezetime") == 5 && get_cvar_float("mp_roundtime") == 2.5, "managed real round timers");
    ObjectiveCheck(get_cvar_num("mp_c4timer") == 35 && get_cvar_num("mp_respawn_immunitytime") == 2, "managed bomb and immunity timers");
    ObjectiveCheck(get_cvar_num("mp_round_infinite") == 0, "normal objective checks enabled at process startup");
    new command[64];
    if (blocked) { copy(command, charsmax(command), "goldsrcops_bomb_blocked"); }
    else if (mode == 1) { copy(command, charsmax(command), "goldsrcops_bomb_explode"); }
    else if (mode == 2) { copy(command, charsmax(command), "goldsrcops_bomb_defuse"); }
    else { copy(command, charsmax(command), "goldsrcops_round_timeout"); }
    mkdir("addons/amxmodx/data/round-objectives");
    formatex(g_spool, charsmax(g_spool), "addons/amxmodx/data/round-objectives/%s", command);
    ObjectiveCheck(!dir_exists(g_spool) && mkdir(g_spool) == 0, "fresh objective spool");
    set_cvar_string("goldsrcops_spool_incoming", g_spool);
    set_cvar_num("goldsrcops_events_enabled", 0);
    for (new i = 0; i < sizeof g_clients; i++)
    {
        new name[32], rejected[128]; formatex(name, charsmax(name), "Objective fixture %d", i + 1);
        new id = engfunc(EngFunc_CreateFakeClient, name); g_clients[i] = id;
        ObjectiveCheck(id != 0, "three synthetic clients allocated");
        if (!id) { FinishObjective(); return PLUGIN_HANDLED; }
        dllfunc(DLLFunc_ClientConnect, id, name, "127.0.0.1", rejected);
        dllfunc(DLLFunc_ClientPutInServer, id);
        rg_set_user_team(id, i == 0 ? TEAM_TERRORIST : TEAM_CT);
        set_member(id, m_iJoiningState, JOINED);
        set_member(id, m_bJustConnected, false);
        set_member(id, m_iMenu, Menu_OFF);
        set_member(id, m_bMissionBriefing, false);
        client_putinserver(id);
        g_choice[id] = i; g_pistol_choice[id] = i; g_welcomed[id] = true;
        rg_round_respawn(id);
    }
    // Bootstrap through the normal admin restart path, then observe subsequent
    // GameRules completion/restart. No test calls rg_round_end/rg_restart_round.
    server_cmd("sv_restart 1"); server_exec();
    set_task(0.1, "PollObjective");
    return PLUGIN_HANDLED;
}

public BeforeRestart()
{
    if (g_stage == 3)
    {
        for (new i = 0; i < sizeof g_clients; i++)
        { g_killsBefore[i] = g_map_kills[g_clients[i]]; g_deathsBefore[i] = g_map_deaths[g_clients[i]]; }
    }
    return HC_CONTINUE;
}

public AfterRestart()
{
    if (g_mode) { server_print("ROUND_OBJECTIVE_RESTART stage=%d time=%.3f freeze=%d", g_stage, get_gametime(), get_member_game(m_bFreezePeriod)); }
    if (g_stage == 1 || g_stage == 2) { ObjectiveCheck(false, "no bootstrap restart after scenario admission"); }
    if (g_stage == 3)
    {
        g_restarts++;
        ObjectiveCheck(get_gametime() >= g_ended + 4.8, "natural restart observes winner delay");
        for (new i = 0; i < sizeof g_clients; i++)
        {
            new id = g_clients[i];
            ObjectiveCheck(bool:is_user_alive(id), "next round respawns all clients");
            ObjectiveCheck(rg_has_item_by_name(id, WEAPON_CLASSES[i]) && rg_has_item_by_name(id, PISTOL_CLASSES[i]), "next round restores per-client loadout");
            ObjectiveCheck(g_map_kills[id] == g_killsBefore[i] && g_map_deaths[id] == g_deathsBefore[i], "native restart preserves map statistics");
            ObjectiveCheck(Float:get_member(id, m_flSpawnProtectionEndTime) > get_gametime() && bool:get_member(id, m_bSpawnProtectionEffects), "next round applies fresh protection");
        }
        g_stage = 4;
    }
    return HC_CONTINUE;
}

public AfterFreeze()
{
    if (g_mode) { server_print("ROUND_OBJECTIVE_FREEZE_END stage=%d time=%.3f started=%d winner=%d pending=%.3f", g_stage, get_gametime(), get_member_game(m_bGameStarted), get_member_game(m_iRoundWinStatus), Float:get_member_game(m_flRestartRoundTime)); }
    if (!g_mode) { return HC_CONTINUE; }
    if (g_stage == 0)
    {
        // Game_Commencing can schedule another bootstrap restart after the
        // first freeze. Admit stimuli only once normal scoring has started.
        new started = get_member_game(m_bGameStarted);
        new winner = get_member_game(m_iRoundWinStatus);
        new Float:pending = get_member_game(m_flRestartRoundTime);
        if (!started) { rg_check_win_conditions(); return HC_CONTINUE; }
        ObjectiveCheck(winner == _:WINSTATUS_NONE && pending == 0.0, "scored-round admission has no pending winner or restart");
        server_print("ROUND_OBJECTIVE_ADMITTED started=%d winner=%d pending=%.3f", started, winner, pending);
        g_active = get_gametime();
        if (g_blocked) { set_cvar_num("mp_round_infinite", 1); }
        ObjectiveCheck(get_member_game(m_iRoundTime) == 150, "native round duration is 150 seconds");
        ObjectiveCheck(bool:get_member_game(m_bMapHasBombTarget), "real bomb map detected");
        g_ctWins = get_member_game(m_iNumCTWins); g_tWins = get_member_game(m_iNumTerroristWins);
        set_cvar_num("goldsrcops_events_enabled", 1);
        // Seed actual addon statistics with native combat, then let forced
        // respawn happen automatically before exercising the objective.
        for (new i = 0; i < sizeof g_clients; i++)
        {
            set_entvar(g_clients[i], var_button, IN_ATTACK);
            ExecuteHamB(Ham_Player_PreThink, g_clients[i]);
            set_entvar(g_clients[i], var_button, 0);
        }
        ExecuteHamB(Ham_TakeDamage, g_clients[1], g_clients[0], g_clients[0], 1000.0, DMG_BULLET);
        ObjectiveCheck(g_map_kills[g_clients[0]] == 1 && g_map_deaths[g_clients[1]] == 1, "native death seeds persistent round-boundary statistics");
        g_stage = 1;
    }
    else if (g_stage == 4)
    {
        ObjectiveCheck(g_map_kills[g_clients[0]] >= 1 && g_map_deaths[g_clients[1]] >= 1, "seeded statistics survive completion and restart");
        for (new i = 0; i < sizeof g_clients; i++)
        {
            ExecuteHamB(Ham_Player_PreThink, g_clients[i]);
            ObjectiveCheck(Float:get_member(g_clients[i], m_flSpawnProtectionEndTime) == 0.0 && !get_member(g_clients[i], m_bSpawnProtectionEffects), "fresh protection expires naturally during freeze");
        }
        FinishObjective();
    }
    return HC_CONTINUE;
}

public AfterPlant(owner, const Float:origin[3], const Float:angles[3])
{
    #pragma unused owner, origin, angles
    if (g_stage == 2) { g_plants++; g_bomb = GetHookChainReturn(ATYPE_INTEGER); }
    return HC_CONTINUE;
}

public AfterDefuse(bomb, player, bool:defused)
{
    #pragma unused bomb, player
    if (g_mode && defused) { g_defuses++; }
    return HC_CONTINUE;
}

public AfterExplosion(bomb, trace, damage)
{
    #pragma unused bomb, trace, damage
    if (g_mode) { g_explosions++; }
    return HC_CONTINUE;
}

public ObserveObjectiveEnd(const WinStatus:status, const ScenarioEventEndRound:event, const Float:delay)
{
    if (!g_mode || event == ROUND_NONE || event == ROUND_GAME_COMMENCE || event == ROUND_GAME_RESTART) { return HC_CONTINUE; }
    g_roundEnds++;
    g_ended = get_gametime();
    ObjectiveCheck(g_stage == 2, "objective follows active-round scenario admission");
    new ScenarioEventEndRound:expected = g_mode == 1 ? ROUND_TARGET_BOMB : g_mode == 2 ? ROUND_BOMB_DEFUSED : ROUND_TARGET_SAVED;
    ObjectiveCheck(event == expected && status == (g_mode == 1 ? WINSTATUS_TERRORISTS : WINSTATUS_CTS), "natural objective event and winning team");
    ObjectiveCheck(delay >= 4.9 && g_roundEnds == 1, "one round completion with native restart delay");
    ObjectiveCheck(g_mode != 3 || g_ended - g_active >= 149.8, "timeout waits for real managed round timer");
    ObjectiveCheck(g_mode != 1 || g_ended - g_planted >= 34.8, "explosion waits for real managed C4 timer");
    ObjectiveCheck(g_mode != 2 || g_ended - g_planted >= 4.8, "defuse waits for real native kit timer");
    ObjectiveCheck(get_member_game(m_iNumCTWins) == g_ctWins + (g_mode == 1 ? 0 : 1) && get_member_game(m_iNumTerroristWins) == g_tWins + (g_mode == 1 ? 1 : 0), "native score advances exactly once");
    g_stage = 3;
    return HC_CONTINUE;
}

public PollObjective()
{
    if (g_stage == 5) { return; }
    new Float:now = get_gametime();
    // Fake clients have no client command stream. Pump the same native
    // PreThink path as the multiplayer fixture so GameDLL owns respawn timing.
    if (g_stage == 1) { ExecuteHamB(Ham_Player_PreThink, g_clients[1]); }
    if (now - g_started > (g_mode == 3 ? 180.0 : 65.0))
    {
        ObjectiveCheck(false, "natural objective completion reached before deadline");
        FinishObjective(); return;
    }
    if (g_stage == 1 && is_user_alive(g_clients[1]))
    {
        ObjectiveCheck(Float:get_member(g_clients[1], m_flSpawnProtectionEndTime) > now, "forced respawn remains enabled before objective");
        g_stage = 2;
        if (g_mode != 3)
        {
            ObjectiveCheck(!get_member_game(m_bFreezePeriod) && get_member_game(m_iRoundWinStatus) == WINSTATUS_NONE, "planting stimulus follows freeze in a scored round");
            // Controlled native planting/use are stimuli; normal grenade Think
            // and GameRules determine outcome, score, timing and round restart.
            new Float:origin[3], Float:safe[3]; get_entvar(g_clients[1], var_origin, origin);
            get_entvar(g_clients[0], var_origin, safe);
            g_planted = now;
            new bomb = rg_plant_bomb(g_clients[0], origin);
            ObjectiveCheck(is_entity(bomb) && bomb == g_bomb && g_plants == 1, "real native C4 planting observed");
            if (is_entity(bomb)) { ObjectiveCheck(floatabs(Float:get_member(bomb, m_Grenade_flC4Blow) - now - 35.0) < 0.2, "native planted C4 uses managed timer"); }
            if (g_mode == 2) { rg_give_defusekit(g_clients[1], true); }
            else
            {
                // The real T spawn is outside this CT-spawn bomb's blast radius.
                for (new i = 0; i < sizeof g_clients; i++) { engfunc(EngFunc_SetOrigin, g_clients[i], safe); }
            }
        }
    }
    if (g_stage == 2 && g_mode == 2 && is_entity(g_bomb))
    {
        set_entvar(g_clients[1], var_flags, get_entvar(g_clients[1], var_flags) | FL_ONGROUND);
        ExecuteHamB(Ham_Use, g_bomb, g_clients[1], g_clients[1], 2, 1.0);
    }
    set_task(0.1, "PollObjective");
}

CountReadyRecords()
{
    new name[128], FileType:type, count;
    new directory = open_dir(g_spool, name, charsmax(name), type);
    if (!directory) { return -1; }
    do
    {
        new length = strlen(name);
        if (type != FileType_Directory && length >= 5 && equal(name[length - 5], ".json")) { count++; }
        else if (type != FileType_Directory) { ObjectiveCheck(false, "no incomplete spool records remain"); }
    }
    while (next_file(directory, name, charsmax(name), type));
    close_dir(directory); return count;
}

FinishObjective()
{
    if (g_stage == 5) { return; }
    ObjectiveCheck(g_roundEnds == 1 && g_restarts == 1, "one natural completion and one following restart");
    ObjectiveCheck(CountReadyRecords() == 1, "exactly one native producer record across round boundary");
    ObjectiveCheck(g_mode == 3 ? g_plants == 0 : g_plants == 1, "expected planting count");
    ObjectiveCheck(g_mode == 2 ? g_defuses == 1 && g_explosions == 0 : g_defuses == 0, "expected defuse outcome");
    ObjectiveCheck(g_mode != 1 || g_explosions == 1, "expected native bomb explosion");
    g_stage = 5;
    server_print("ROUND_OBJECTIVE_CONTROL plants=%d explosions=%d ends=%d", g_plants, g_explosions, g_roundEnds);
    server_print("ROUND_RESTART_SMOKE=%s failures=%d restarts=%d", g_failures ? "failed" : "passed", g_failures, g_restarts);
    server_print("ROUND_PRODUCER_SMOKE=%s failures=%d records=%d", g_failures ? "failed" : "passed", g_failures, CountReadyRecords());
    server_print("ROUND_OBJECTIVE_SMOKE=%s failures=%d mode=%d native_ends=%d duration=%.3f", g_failures ? "failed" : "passed", g_failures, g_mode, g_roundEnds, get_gametime() - g_started);
}
