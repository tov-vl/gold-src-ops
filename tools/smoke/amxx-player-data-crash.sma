#include <amxmodx>
#include <reapi>
#include <fakemeta>
#include <nvault>

// Only synthetic identities bypass bot filtering. Storage and persistence helpers
// remain the product implementation, with real nVault and no storage wrappers.
#define plugin_init ProductPluginInit
#define plugin_end ProductPluginEnd
#define is_user_bot(%1) FixtureIsBot(%1)
#define get_user_authid(%1,%2,%3) FixtureAuthId(%1,%2,%3)
#define get_user_name(%1,%2,%3) FixtureName(%1,%2,%3)
#include <goldsrcops_weapon_selection.sma>
#undef plugin_init
#undef plugin_end
#undef is_user_bot
#undef get_user_authid
#undef get_user_name

new g_clients[2];
new g_auth[MAX_PLAYERS + 1][32];
new g_names[MAX_PLAYERS + 1][32];
new g_failures;
new const IDENTITIES[][] = { "STEAM_0:1:454541", "STEAM_0:1:454542" };

bool:FixtureIsBot(id) { return id != g_clients[0] && id != g_clients[1] && is_user_bot(id); }
FixtureAuthId(id, buffer[], length) { return copy(buffer, length, g_auth[id]); }
FixtureName(id, buffer[], length) { return copy(buffer, length, g_names[id]); }

public plugin_init()
{
    ProductPluginInit();
    register_srvcmd("goldsrcops_crash_baseline", "SeedBaseline");
    register_srvcmd("goldsrcops_crash_write", "WriteBeforeCrash");
    register_srvcmd("goldsrcops_crash_recover", "VerifyRecovery");
    register_srvcmd("goldsrcops_crash_reopen", "VerifyReopen");
}

public plugin_end()
{
    server_print("PLAYER_DATA_PLUGIN_END=called");
    ProductPluginEnd();
}

CheckCrash(bool:passed, const label[])
{
    if (!passed) { g_failures++; server_print("PLAYER_DATA_CRASH_ASSERTION_FAILED %s", label); }
}

CreateClients(bool:reverse, bool:updated_names = false)
{
    if (get_playersnum() || g_clients[0] || g_stats_vault == INVALID_HANDLE || g_preferences_vault == INVALID_HANDLE)
    {
        set_fail_state("Fresh isolated process and both real vaults required.");
        return;
    }
    set_pcvar_num(g_enabled, 1);
    for (new slot = 0; slot < sizeof g_clients; slot++)
    {
        new index = reverse ? 1 - slot : slot;
        new name[32];
        formatex(name, charsmax(name), "%s persistence %s", updated_names ? "Updated" : "Crash", index ? "B" : "A");
        new id = engfunc(EngFunc_CreateFakeClient, name), rejected[128];
        if (!id) { set_fail_state("Synthetic client unavailable."); return; }
        g_clients[index] = id;
        copy(g_names[id], charsmax(g_names[]), name);
        copy(g_auth[id], charsmax(g_auth[]), IDENTITIES[index]);
        dllfunc(DLLFunc_ClientConnect, id, name, "127.0.0.1", rejected);
        dllfunc(DLLFunc_ClientPutInServer, id);
        set_user_info(id, "lang", "ru");
        rg_set_user_team(id, index ? TEAM_TERRORIST : TEAM_CT);
        set_member(id, m_iJoiningState, JOINED);
        client_putinserver(id);
        client_authorized(id, g_auth[id]);
        rg_round_respawn(id);
        remove_task(WELCOME_TASK_BASE + id);
    }
    CheckCrash(reverse ? g_clients[0] > g_clients[1] : g_clients[0] < g_clients[1],
        "fresh slot order matches the requested identity permutation");
}

StoreChoices(id, language, primary, pistol, bool:hud_disabled)
{
    g_language_override[id] = language;
    g_choice[id] = primary;
    g_pistol_choice[id] = pistol;
    g_preferences_dirty[id] = 7;
    SavePlayerPreferences(id);
    EnsureHudPreference(id);
    g_hud_disabled[id] = hud_disabled;
    g_hud_preference_dirty[id] = true;
    SaveHudPreference(id);
}

VerifyNames(bool:updated)
{
    for (new index = 0; index < sizeof g_clients; index++)
    {
        new key[48], saved_key[40], expected[40], value[64], stamp;
        CheckCrash(BuildStatsKey(IDENTITIES[index], saved_key, charsmax(saved_key)), "synthetic name identity canonical");
        formatex(key, charsmax(key), "name:%s", saved_key);
        formatex(expected, charsmax(expected), "1 %s persistence %s", updated ? "Updated" : "Crash", index ? "B" : "A");
        CheckCrash(nvault_lookup(g_preferences_vault, key, value, charsmax(value), stamp) && equal(value, expected),
            "saved search name exists before connection callbacks can replace it");
    }
}

VerifyState(bool:updated, bool:fresh)
{
    for (new index = 0; index < sizeof g_clients; index++)
    {
        new id = g_clients[index];
        new kills = index ? (updated ? 101 : 100) : (updated ? 25 : 24);
        new deaths = index ? (updated ? 8 : 7) : (updated ? 4 : 3);
        new language = updated ? (index ? 1 : 2) : (index ? 2 : 1);
        new primary = updated ? (index ? 0 : 1) : (index ? 2 : 0);
        new pistol = updated ? (index ? 0 : 2) : (index ? 1 : 0);
        new bool:hud = (updated && index == 0) || (!updated && index == 1);
        server_print("PLAYER_DATA_STATE index=%d kills=%d deaths=%d language=%d primary=%d pistol=%d hud=%d stats_ready=%d prefs_ready=%d hud_ready=%d",
            index, g_saved_kills[id], g_saved_deaths[id], g_language_override[id],
            g_choice[id], g_pistol_choice[id], g_hud_disabled[id],
            g_saved_ready[id], g_preferences_ready[id], g_hud_preference_ready[id]);
        CheckCrash(g_saved_ready[id] && !g_saved_blocked[id]
            && g_saved_kills[id] == kills && g_saved_deaths[id] == deaths,
            "saved counters belong to the same Steam identity");
        CheckCrash(RankForKills(g_saved_kills[id]) == (index ? 2 : (updated ? 1 : 0)),
            "rank derives from restored kills including threshold crossing");
        CheckCrash(g_preferences_ready[id] && !g_preferences_blocked[id] && !g_preferences_dirty[id]
            && PlayerPreferencesSaved(id) && g_language_override[id] == language
            && g_choice[id] == primary && g_pistol_choice[id] == pistol,
            "confirmed language and weapon choices recovered per identity");
        CheckCrash(PlayerHudPreferenceSaved(id) && g_hud_disabled[id] == hud,
            "independent HUD preference recovered per identity");
        CheckCrash(g_streak_ready[id] && !g_streak_blocked[id] && g_saved_streak[id] == (updated ? 1 : 0),
            "best streak survives as a saved preference");
        if (fresh)
        {
            CheckCrash(!g_map_kills[id] && !g_map_deaths[id] && !g_kill_streak[id] && !g_record_streak[id],
                "connection and map counters are not restored as durable totals");
        }
    }
    CheckCrash(!g_storage_errors && !g_preferences_errors, "no persistence readback error");
}

public SeedBaseline()
{
    CreateClients(false);
    for (new index = 0; index < sizeof g_clients; index++)
    {
        new id = g_clients[index];
        g_saved_kills[id] = index ? 100 : 24;
        g_saved_deaths[id] = index ? 7 : 3;
        SavePlayerStats(id);
        StoreChoices(id, index ? 2 : 1, index ? 2 : 0, index ? 1 : 0, bool:index);
    }
    VerifyState(false, false);
    VerifyNames(false);
    server_print("PLAYER_DATA_BASELINE=%s failures=%d", g_failures ? "failed" : "passed", g_failures);
    return PLUGIN_HANDLED;
}

public WriteBeforeCrash()
{
    CreateClients(false);
    VerifyState(false, true);
    // Exercise the product's score persistence path; native combat is covered
    // separately. No disconnect, vault close or flush is performed here.
    RecordMapDeath(g_clients[0], g_clients[1]);
    RecordMapDeath(g_clients[1], g_clients[0]);
    StoreChoices(g_clients[0], 2, 1, 2, true);
    StoreChoices(g_clients[1], 1, 0, 0, false);
    for (new index = 0; index < sizeof g_clients; index++)
    {
        new name[32]; formatex(name, charsmax(name), "Updated persistence %s", index ? "B" : "A");
        set_user_info(g_clients[index], "name", name);
        set_entvar(g_clients[index], var_netname, name);
        copy(g_names[g_clients[index]], charsmax(g_names[]), name);
        g_leader_name_recorded[g_clients[index]] = false;
        RememberLeaderName(g_clients[index]);
    }
    VerifyState(true, false);
    VerifyNames(true);
    server_print("PLAYER_DATA_BEFORE_KILL=%s failures=%d", g_failures ? "failed" : "passed", g_failures);
    return PLUGIN_HANDLED;
}

public VerifyRecovery()
{
    VerifyNames(true);
    CreateClients(true, true);
    VerifyState(true, true);
    server_print("PLAYER_DATA_RECOVERED=%s failures=%d", g_failures ? "failed" : "passed", g_failures);
    return PLUGIN_HANDLED;
}

public VerifyReopen()
{
    VerifyNames(true);
    CreateClients(false, true);
    VerifyState(true, true);
    server_print("PLAYER_DATA_REOPENED=%s failures=%d", g_failures ? "failed" : "passed", g_failures);
    return PLUGIN_HANDLED;
}
