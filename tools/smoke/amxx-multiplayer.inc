// Included only by the isolated spawn fixture, never by the shipped addon.
new g_multi_clients[3];
new g_multi_stage;
new Float:g_multi_started;
new Float:g_multi_death_time;
new Float:g_multi_pending;
new bool:g_multi_pending_seen;
new g_multi_survivor_item;

bool:IsMultiplayerFixture(id)
{
    return id > 0 && (id == g_multi_clients[0] || id == g_multi_clients[1] || id == g_multi_clients[2]);
}

MultiCheck(bool:passed, const label[])
{
    if (!passed)
    {
        g_failures++;
        server_print("MULTIPLAYER_ASSERTION_FAILED %s", label);
    }
}

MultiInput(id, buttons)
{
    // Pump the real GameDLL input path; never call RemoveSpawnProtection directly.
    set_entvar(id, var_button, 0);
    set_member(id, m_afButtonLast, 0);
    ExecuteHamB(Ham_Player_PreThink, id);
    set_entvar(id, var_button, buttons);
    ExecuteHamB(Ham_Player_PreThink, id);
    set_entvar(id, var_button, 0);
    ExecuteHamB(Ham_Player_PreThink, id);
}

MultiDamage(attacker, victim, Float:amount)
{
    ExecuteHamB(Ham_TakeDamage, victim, attacker, attacker, amount, DMG_BULLET);
}

MultiInventory(id, primary, pistol)
{
    MultiCheck(rg_has_item_by_name(id, WEAPON_CLASSES[primary]), "per-client primary retained");
    MultiCheck(rg_has_item_by_name(id, PISTOL_CLASSES[pistol]), "per-client pistol retained");
    MultiCheck(rg_get_user_bpammo(id, WEAPON_IDS[primary]) == BACKPACK_AMMO[primary], "spawn primary reserve restored");
    MultiCheck(rg_get_user_bpammo(id, PISTOL_IDS[pistol]) == PISTOL_AMMO[pistol], "spawn pistol reserve restored");
}

public RunMultiplayerSmoke()
{
    return StartMultiplayerSmoke(false);
}

public RunUnprotectedMultiplayerSmoke()
{
    return StartMultiplayerSmoke(true);
}

StartMultiplayerSmoke(bool:without_protection)
{
    if (g_fixture_id || get_playersnum() || MaxClients < sizeof g_multi_clients)
    {
        server_print("MULTIPLAYER_SMOKE=refused; requires fresh empty three-slot fixture");
        return PLUGIN_HANDLED;
    }
    server_cmd("exec multiplayer-profile.cfg");
    server_exec();
    MultiCheck(get_cvar_num("mp_forcerespawn") == 1, "managed one-second respawn policy");
    MultiCheck(get_cvar_num("mp_roundrespawn_time") == -1, "managed round respawn policy");
    MultiCheck(get_cvar_num("mp_respawn_immunitytime") == 2, "managed two-second protection policy");
    MultiCheck(get_cvar_num("mp_respawn_immunity_effects") == 1, "managed protection effects policy");
    MultiCheck(get_cvar_num("mp_respawn_immunity_force_unset") == 2, "managed attack-only cancellation policy");
    MultiCheck(get_cvar_num("mp_friendlyfire") == 0, "managed friendly-fire policy");
    // Remove round freezes/balancing only for deterministic synthetic placement.
    set_cvar_num("mp_freezetime", 0);
    set_cvar_num("mp_autoteambalance", 0);
    set_cvar_num("mp_limitteams", 0);
    set_cvar_num("mp_round_infinite", 1);
    if (without_protection) { set_cvar_num("mp_respawn_immunitytime", 0); }
    for (new index = 0; index < sizeof g_multi_clients; index++)
    {
        new name[32], rejected[128];
        formatex(name, charsmax(name), "Interaction fixture %d", index + 1);
        new id = engfunc(EngFunc_CreateFakeClient, name);
        if (!id)
        {
            MultiCheck(false, "synthetic client allocated");
            FinishMultiplayer();
            return PLUGIN_HANDLED;
        }
        g_multi_clients[index] = id;
        dllfunc(DLLFunc_ClientConnect, id, name, "127.0.0.1", rejected);
        dllfunc(DLLFunc_ClientPutInServer, id);
        rg_set_user_team(id, index == 1 ? TEAM_TERRORIST : TEAM_CT);
        set_member(id, m_iJoiningState, JOINED);
        // Complete synthetic joining; GameRules skips PlayerSpawn for a new join.
        set_member(id, m_bJustConnected, false);
        set_member(id, m_iMenu, Menu_OFF);
        set_member(id, m_bMissionBriefing, false);
        client_putinserver(id);
        // Different loadouts expose accidental cross-client state reuse.
        g_choice[id] = index;
        g_pistol_choice[id] = index;
        g_welcomed[id] = true;
        rg_round_respawn(id);
        MultiInventory(id, index, index);
    }
    new attacker = g_multi_clients[0], victim = g_multi_clients[1], teammate = g_multi_clients[2];
    MultiCheck(Float:get_member(victim, m_flSpawnProtectionEndTime) > get_gametime(), "new spawn is protected");
    MultiCheck(bool:get_member(victim, m_bSpawnProtectionEffects), "new spawn protection effects active");
    new Float:health = get_entvar(victim, var_health);
    MultiDamage(attacker, victim, 25.0);
    MultiCheck(get_entvar(victim, var_health) == health, "spawn protection blocks native enemy damage");
    MultiCheck(g_map_kills[attacker] == 0 && g_map_deaths[victim] == 0, "blocked damage grants no kill or death");
    MultiInput(victim, IN_FORWARD);
    MultiCheck(Float:get_member(victim, m_flSpawnProtectionEndTime) > get_gametime(), "movement preserves attack-only protection");
    MultiInput(victim, IN_ATTACK);
    MultiCheck(Float:get_member(victim, m_flSpawnProtectionEndTime) == 0.0, "primary attack removes protection");
    MultiCheck(!get_member(victim, m_bSpawnProtectionEffects), "attack clears protection effects");
    MultiDamage(attacker, victim, 25.0);
    MultiCheck(get_entvar(victim, var_health) < health, "native enemy damage applies after primary attack");
    rg_round_respawn(victim);
    MultiInput(victim, IN_ATTACK2);
    MultiCheck(Float:get_member(victim, m_flSpawnProtectionEndTime) == 0.0, "secondary attack removes protection");
    MultiInput(teammate, IN_ATTACK);
    health = get_entvar(teammate, var_health);
    MultiDamage(attacker, teammate, 25.0);
    MultiCheck(get_entvar(teammate, var_health) == health, "native teammate damage blocked without friendly fire");
    MultiCheck(g_map_kills[attacker] == 0 && g_map_deaths[teammate] == 0, "teammate hit does not grant points");
    if (g_failures)
    {
        FinishMultiplayer();
        return PLUGIN_HANDLED;
    }
    // Let real engine time expire immunity; no clock or protection-state writes.
    rg_round_respawn(victim);
    g_multi_started = get_gametime();
    g_multi_stage = 1;
    set_task(0.1, "PollMultiplayer");
    return PLUGIN_HANDLED;
}

public PollMultiplayer()
{
    new attacker = g_multi_clients[0], victim = g_multi_clients[1];
    ExecuteHamB(Ham_Player_PreThink, victim);
    new Float:now = get_gametime();
    if (g_multi_stage == 1 && now - g_multi_started >= 2.2)
    {
        MultiCheck(Float:get_member(victim, m_flSpawnProtectionEndTime) == 0.0, "protection expires through native PreThink");
        MultiCheck(!get_member(victim, m_bSpawnProtectionEffects), "expiry clears protection effects");
        new Float:health = get_entvar(victim, var_health);
        MultiDamage(attacker, victim, 25.0);
        MultiCheck(get_entvar(victim, var_health) < health, "native damage applies after timed expiry");
        // One real death, followed only by the native GameDLL respawn scheduler.
        MultiInput(attacker, IN_ATTACK);
        set_entvar(attacker, var_health, 60.0);
        rg_set_user_bpammo(attacker, WEAPON_MP5N, 1);
        g_multi_survivor_item = get_member(attacker, m_pActiveItem);
        MultiDamage(attacker, victim, 1000.0);
        MultiCheck(!is_user_alive(victim), "native enemy damage causes death");
        MultiCheck(g_map_kills[attacker] == 1 && g_map_deaths[victim] == 1, "native death counted exactly once");
        MultiCheck(get_entvar(attacker, var_health) == 75.0, "eligible enemy kill heals only killer");
        MultiCheck(rg_get_user_bpammo(attacker, WEAPON_MP5N) == BACKPACK_AMMO[0], "eligible enemy kill refills killer reserve");
        MultiCheck(get_member(attacker, m_pActiveItem) == g_multi_survivor_item, "kill preserves killer active item");
        g_multi_death_time = now;
        g_multi_stage = 2;
    }
    else if (g_multi_stage == 2)
    {
        new Float:pending = get_member(victim, m_flRespawnPending);
        if (pending > 0.0 && !g_multi_pending_seen)
        {
            g_multi_pending_seen = true;
            g_multi_pending = pending;
            MultiCheck(pending >= g_multi_death_time + 0.9, "native respawn is delayed by policy");
        }
        if (is_user_alive(victim))
        {
            MultiCheck(g_multi_pending_seen && now + 0.1 >= g_multi_pending, "automatic spawn follows recorded native deadline");
            MultiCheck(now - g_multi_death_time >= 0.9 && now - g_multi_death_time < 7.0, "automatic spawn completes inside bounded window");
            MultiCheck(Float:get_member(victim, m_flSpawnProtectionEndTime) > now, "automatic spawn renews protection");
            MultiInventory(victim, 1, 1);
            MultiInventory(attacker, 0, 0);
            MultiInventory(g_multi_clients[2], 2, 2);
            MultiCheck(g_map_kills[attacker] == 1 && g_map_deaths[victim] == 1, "automatic spawn does not duplicate death accounting");
            MultiCheck(get_member(attacker, m_pActiveItem) == g_multi_survivor_item, "other player respawn preserves survivor active item");
            // A second real death must stay dead with automatic respawn disabled.
            set_cvar_num("mp_forcerespawn", 0);
            MultiInput(victim, IN_ATTACK);
            MultiDamage(attacker, victim, 1000.0);
            MultiCheck(!is_user_alive(victim), "disabled-respawn scenario starts with real death");
            g_multi_started = now;
            g_multi_stage = 3;
        }
        else if (now - g_multi_death_time > 7.0)
        {
            MultiCheck(false, "automatic respawn deadline exceeded");
            FinishMultiplayer();
            return;
        }
    }
    else if (g_multi_stage == 3 && now - g_multi_started > 4.5)
    {
        MultiCheck(!is_user_alive(victim), "disabled automatic respawn leaves player dead");
        MultiCheck(Float:get_member(victim, m_flRespawnPending) == 0.0, "disabled automatic respawn has no pending claim");
        // Existing reconnect fixtures cover vault reload; check only local slot isolation here.
        new rejected[128];
        client_disconnected(victim, false, rejected, charsmax(rejected));
        client_putinserver(victim);
        MultiCheck(g_choice[victim] == 0 && g_pistol_choice[victim] == 0, "reused connection clears both selections");
        MultiCheck(g_choice[g_multi_clients[2]] == 2 && g_pistol_choice[g_multi_clients[2]] == 2, "slot reset preserves other player's selections");
        FinishMultiplayer();
        return;
    }
    set_task(0.1, "PollMultiplayer");
}

FinishMultiplayer()
{
    server_print("SPAWN_PROTECTION_SMOKE=%s failures=%d native_input=1 native_damage=1", g_failures ? "failed" : "passed", g_failures);
    server_print("AUTOMATIC_RESPAWN_SMOKE=%s failures=%d native_scheduler=1", g_failures ? "failed" : "passed", g_failures);
    server_print("MULTIPLAYER_ISOLATION_SMOKE=%s failures=%d synthetic_clients=3", g_failures ? "failed" : "passed", g_failures);
    // The runtime waits for this final marker, after every asynchronous checkpoint.
    server_print("MULTIPLAYER_SMOKE=%s failures=%d human_acceptance=0", g_failures ? "failed" : "passed", g_failures);
}
