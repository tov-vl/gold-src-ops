#include <amxmodx>
#include <reapi>
#include <fakemeta>
#include <nvault>
#include <goldsrcops-process-storage>

native nvault_test_clock();
native nvault_test_failure(enabled);

#define plugin_init ProductPluginInit
#define is_user_bot(%1) FixtureIsBot(%1)
#define get_user_authid(%1,%2,%3) FixtureAuthId(%1,%2,%3)
#define get_user_name(%1,%2,%3) FixtureName(%1,%2,%3)
#define client_print FixturePrint
#define client_print_color FixtureColorPrint
#include <goldsrcops_weapon_selection.sma>
#undef plugin_init
#undef is_user_bot
#undef get_user_authid
#undef get_user_name
#undef client_print
#undef client_print_color

new g_clients[32];
new bool:g_human[MAX_PLAYERS + 1];
new g_auth[MAX_PLAYERS + 1][32];
new g_names[MAX_PLAYERS + 1][32];
new g_failures, g_messages;
new g_timings[10000];
bool:FixtureIsBot(id) { return !g_human[id] && is_user_bot(id); }
FixtureAuthId(id, buffer[], length) { return copy(buffer, length, g_auth[id]); }
FixtureName(id, buffer[], length) { return copy(buffer, length, g_names[id]); }
FixturePrint(id, type, const message[], any:...)
{
    #pragma unused id, type, message
    g_messages++; return 1;
}
FixtureColorPrint(id, sender, const message[], any:...)
{
    #pragma unused id, sender, message
    g_messages++; return 1;
}

public plugin_init()
{
    ProductPluginInit();
    register_srvcmd("goldsrcops_storage_benchmark", "BenchmarkStorage");
    register_srvcmd("goldsrcops_storage_fail_stats", "FailStorage");
    register_srvcmd("goldsrcops_storage_fail_preferences", "FailStorage");
    register_srvcmd("goldsrcops_storage_fail_streak", "FailStorage");
    register_srvcmd("goldsrcops_storage_fail_hud", "FailStorage");
    register_srvcmd("goldsrcops_storage_fail_name", "FailStorage");
}

CheckStorage(bool:ok, const label[])
{
    if (!ok) { g_failures++; server_print("PROCESS_STORAGE_ASSERTION_FAILED %s",label); }
}
CreateStoragePlayers(count)
{
    if (get_playersnum() || MaxClients < count || g_stats_vault == INVALID_HANDLE || g_preferences_vault == INVALID_HANDLE)
    {
        set_fail_state("Fresh storage process and enough synthetic slots required."); return;
    }
    set_pcvar_num(g_enabled,1);
    for (new index=0;index<count;index++)
    {
        new name[32],rejected[128];formatex(name,charsmax(name),"Storage actor %d",index);
        new id=engfunc(EngFunc_CreateFakeClient,name);if (!id) { set_fail_state("No synthetic slot.");return; }
        g_clients[index]=id;g_human[id]=true;
        copy(g_names[id],charsmax(g_names[]),name);
        formatex(g_auth[id],charsmax(g_auth[]),"STEAM_0:1:%d",464600+index);
        dllfunc(DLLFunc_ClientConnect,id,name,"127.0.0.1",rejected);dllfunc(DLLFunc_ClientPutInServer,id);
        rg_set_user_team(id,index%2 ? TEAM_TERRORIST : TEAM_CT);set_member(id,m_iJoiningState,JOINED);
        client_putinserver(id);client_authorized(id,g_auth[id]);rg_round_respawn(id);remove_task(WELCOME_TASK_BASE+id);
    }
}
public BenchmarkStorage()
{
    CreateStoragePlayers(32);
    for (new index=0;index<sizeof g_timings;index++)
    {
        new start=nvault_test_clock();RecordMapDeath(g_clients[index%32],g_clients[(index+1)%32]);
        g_timings[index]=nvault_test_clock()-start;
    }
    SortIntegers(g_timings,sizeof g_timings,Sort_Ascending);
    CheckStorage(g_timings[9899]<=5000,"complete score handler p99 wall time at most five milliseconds");
    CheckStorage(!g_storage_errors && !g_preferences_errors,"no hidden benchmark write failure");
    new kills,deaths;
    for (new index=0;index<32;index++) { kills+=g_saved_kills[g_clients[index]];deaths+=g_saved_deaths[g_clients[index]]; }
    CheckStorage(kills==10000 && deaths==10000,"all benchmark events stored without drops");
    server_print("PROCESS_STORAGE_TIMING samples=10000 identities=32 handler_p99_us=%d max_us=%d",g_timings[9899],g_timings[9999]);
    server_print("PROCESS_STORAGE_BENCHMARK=%s failures=%d",g_failures ? "failed":"passed",g_failures);
    return PLUGIN_HANDLED;
}
public FailStorage()
{
    CreateStoragePlayers(2);new id=g_clients[0],other=g_clients[1],command[64];read_argv(0,command,charsmax(command));
    g_saved_kills[id]=24;SavePlayerStats(id);g_messages=0;
    CheckStorage(nvault_test_failure(1)==1,"fixed test fault enabled");
    if (equal(command,"goldsrcops_storage_fail_stats"))
    {
        RecordMapDeath(id,other);
        CheckStorage(g_stats_vault==INVALID_HANDLE && !g_saved_ready[id] && !g_saved_ready[other]
            && g_saved_blocked[id] && g_saved_blocked[other],"stats failure invalidates all saved views");
        CheckStorage(g_map_kills[id]==1 && g_map_deaths[other]==1,"connection/map counters continue");
    }
    else
    {
        if (equal(command,"goldsrcops_storage_fail_preferences")) { g_choice[id]=1;g_preferences_dirty[id]=PREFERENCE_PRIMARY;SavePlayerPreferences(id); }
        else if (equal(command,"goldsrcops_storage_fail_streak")) { g_record_streak[id]=g_saved_streak[id];RecordSavedStreak(id); }
        else if (equal(command,"goldsrcops_storage_fail_hud")) { g_hud_disabled[id]=true;g_hud_preference_dirty[id]=true;SaveHudPreference(id); }
        else { copy(g_names[id],charsmax(g_names[]),"Unconfirmed name");g_leader_name_recorded[id]=false;RememberLeaderName(id); }
        CheckStorage(g_preferences_vault==INVALID_HANDLE && !PlayerPreferencesSaved(id) && !PlayerPreferencesSaved(other)
            && !PlayerHudPreferenceSaved(id) && !g_streak_ready[id],"preference failure invalidates all saved views");
    }
    CheckStorage(!g_messages,"failed commit does not announce a saved promotion or record");
    CheckStorage(nvault_test_failure(0)==1,"fixed test fault cleared for cleanup");
    server_print("PROCESS_STORAGE_FAILURE=%s failures=%d",g_failures ? "failed":"passed",g_failures);
    return PLUGIN_HANDLED;
}
