#include <amxmodx>
#include <file>
#include <reapi>
#include <fakemeta>

// Only this fixture classifies one synthetic client as human and injects a
// rename failure. The shipped producer is compiled separately without overrides.
#define plugin_init ProductPluginInit
#define is_user_bot(%1) FixtureIsBot(%1)
#define rename_file FixtureRename
#include <goldsrcops_game_events.sma>
#undef plugin_init
#undef is_user_bot
#undef rename_file

new const FIXTURE_ROOT[] = "addons/amxmodx/data/producer-engine-smoke";
new g_fixtureClients[3];
new g_fixtureHuman;
new g_failures;
new g_renameAttempts;
new g_roundHooks;
new g_restartHooks;
new bool:g_failRename;
new bool:g_negativeDisabled;
new bool:g_started;
new g_casePath[PLATFORM_MAX_PATH];

public plugin_init()
{
    ProductPluginInit();
    RegisterHookChain(RG_RoundEnd, "ObserveRoundEnd", true);
    RegisterHookChain(RG_CSGameRules_RestartRound, "ObserveRestart", true);
    register_srvcmd("goldsrcops_producer_smoke", "RunProducerSmoke");
    register_srvcmd("goldsrcops_producer_disabled", "RunDisabledProducerSmoke");
}

bool:FixtureIsBot(id)
{
    return id != g_fixtureHuman && is_user_bot(id);
}

ProducerCheck(bool:passed, const label[])
{
    if (!passed)
    {
        g_failures++;
        server_print("PRODUCER_ASSERTION_FAILED %s", label);
    }
}

public ObserveRoundEnd(const WinStatus:status, const ScenarioEventEndRound:event, const Float:delay)
{
    #pragma unused status, event, delay
    g_roundHooks++;
    return HC_CONTINUE;
}

public ObserveRestart()
{
    g_restartHooks++;
    return HC_CONTINUE;
}

FixtureRename(const temporary[], const ready[], bool:useValveFs)
{
    g_renameAttempts++;
    ProducerCheck(file_exists(temporary) && file_size(temporary) > 0, "closed nonempty temporary file precedes rename");
    ProducerCheck(!file_exists(ready), "ready file absent before atomic handoff");
    if (g_failRename) { return 0; }
    new result = rename_file(temporary, ready, useValveFs);
    ProducerCheck(result != 0 && file_exists(ready) && !file_exists(temporary), "real rename completes handoff");
    return result;
}

EnableProducer(bool:enabled)
{
    set_pcvar_num(g_enabledCvar, enabled && !g_negativeDisabled ? 1 : 0);
}

BeginCase(const label[])
{
    formatex(g_casePath, charsmax(g_casePath), "%s/%s", FIXTURE_ROOT, label);
    ProducerCheck(mkdir(g_casePath) == 0, "fresh case directory created");
    set_pcvar_string(g_incomingPathCvar, g_casePath);
    set_pcvar_num(g_maxPendingCvar, 1000);
    EnableProducer(true);
    g_failRename = false;
    new before = g_restartHooks;
    rg_restart_round();
    ProducerCheck(g_restartHooks > before && !g_roundEventPublished, "native restart resets producer round guard");
}

EmitRound(const ScenarioEventEndRound:event = ROUND_TARGET_BOMB)
{
    new before = g_roundHooks;
    rg_round_end(10.0, WINSTATUS_TERRORISTS, event, "default", "default", true);
    ProducerCheck(g_roundHooks == before + 1, "real round end traverses registered hooks");
}

CountSuffix(const suffix[])
{
    new name[PLATFORM_MAX_PATH], FileType:type, count;
    new directory = open_dir(g_casePath, name, charsmax(name), type);
    ProducerCheck(directory != 0, "case directory is enumerable");
    if (!directory) { return -1; }
    do
    {
        new length = strlen(name), suffixLength = strlen(suffix);
        if (type != FileType_Directory && length >= suffixLength && equal(name[length - suffixLength], suffix)) { count++; }
    }
    while (next_file(directory, name, charsmax(name), type));
    close_dir(directory);
    return count;
}

CaptureProducerRecords(const label[], count, players, bots)
{
    ProducerCheck(CountSuffix(".json") == count, "expected ready record count");
    new manifestPath[PLATFORM_MAX_PATH];
    formatex(manifestPath, charsmax(manifestPath), "%s/records.tsv", FIXTURE_ROOT);
    new manifest = fopen(manifestPath, "ab");
    ProducerCheck(manifest != 0, "fixture record manifest available");
    if (!manifest) { return; }
    new name[PLATFORM_MAX_PATH], FileType:type;
    new directory = open_dir(g_casePath, name, charsmax(name), type);
    if (directory)
    {
        do
        {
            new length = strlen(name);
            if (type != FileType_Directory && length >= 5 && equal(name[length - 5], ".json"))
            {
                ProducerCheck(length == RECORD_ID_LENGTH + 5, "canonical ready file length");
                fprintf(manifest, "%s^t%s^t%d^t%d^n", label, name, players, bots);
            }
        }
        while (next_file(directory, name, charsmax(name), type));
        close_dir(directory);
    }
    fclose(manifest);
}

WriteOccupant(const filename[])
{
    new path[PLATFORM_MAX_PATH];
    formatex(path, charsmax(path), "%s/%s", g_casePath, filename);
    new file = fopen(path, "wb");
    ProducerCheck(file != 0, "backpressure evidence file created");
    if (file) { fputs(file, "retained-evidence"); fclose(file); }
}

public RunProducerSmoke()
{
    return RunFixture(false);
}

public RunDisabledProducerSmoke()
{
    return RunFixture(true);
}

RunFixture(bool:disabled)
{
    if (g_started || get_playersnum() || MaxClients < 3 || dir_exists(FIXTURE_ROOT))
    {
        server_print("PRODUCER_SMOKE=refused; requires fresh empty isolated fixture");
        return PLUGIN_HANDLED;
    }
    g_started = true;
    g_negativeDisabled = disabled;
    ProducerCheck(get_pcvar_num(g_enabledCvar) == 0, "producer is disabled by default");
    ProducerCheck(mkdir(FIXTURE_ROOT) == 0, "fixture root created");
    new manifestPath[PLATFORM_MAX_PATH];
    formatex(manifestPath, charsmax(manifestPath), "%s/records.tsv", FIXTURE_ROOT);
    new manifest = fopen(manifestPath, "wb");
    ProducerCheck(manifest != 0, "fresh manifest created");
    if (manifest) { fclose(manifest); }
    set_cvar_num("mp_round_infinite", 1);
    set_cvar_num("mp_autoteambalance", 0);
    set_cvar_num("mp_limitteams", 0);

    BeginCase("empty");
    EmitRound();
    ProducerCheck(g_roundEventPublished || disabled, "empty observable round consumes guard");
    ProducerCheck(CountSuffix(".json") == 0 && g_renameAttempts == 0, "empty round creates no spool file");
    for (new index = 0; index < sizeof g_fixtureClients; index++)
    {
        new name[32], rejected[128];
        formatex(name, charsmax(name), "Producer fixture %d", index + 1);
        new id = engfunc(EngFunc_CreateFakeClient, name);
        ProducerCheck(id != 0, "fixture client allocated");
        if (!id) { FinishProducer(); return PLUGIN_HANDLED; }
        g_fixtureClients[index] = id;
        dllfunc(DLLFunc_ClientConnect, id, name, "127.0.0.1", rejected);
        dllfunc(DLLFunc_ClientPutInServer, id);
        rg_set_user_team(id, index == 1 ? TEAM_TERRORIST : TEAM_CT);
        set_member(id, m_iJoiningState, JOINED);
        set_member(id, m_bJustConnected, false);
        set_member(id, m_iMenu, Menu_OFF);
    }
    // get_players(...,"h") uses the real proxy flag; no roster enumeration stub.
    set_entvar(g_fixtureClients[2], var_flags, get_entvar(g_fixtureClients[2], var_flags) | FL_PROXY);
    ProducerCheck(bool:is_user_hltv(g_fixtureClients[2]), "synthetic proxy classified as HLTV");
    EmitRound();
    ProducerCheck(CountSuffix(".json") == 0, "arrival after consumed empty round waits for restart");

    BeginCase("bot_only");
    EmitRound();
    ProducerCheck(CountSuffix(".json") == 1, "native round end commits bot-only record");
    CaptureProducerRecords("bot_only", 1, 2, 2);
    g_fixtureHuman = g_fixtureClients[0];
    BeginCase("mixed");
    EmitRound();
    CaptureProducerRecords("mixed", 1, 2, 1);
    rg_set_user_team(g_fixtureHuman, TEAM_SPECTATOR);
    BeginCase("spectator");
    EmitRound();
    CaptureProducerRecords("spectator", 1, 2, 1);
    rg_set_user_team(g_fixtureHuman, TEAM_CT);

    server_cmd("kick #%d", get_user_userid(g_fixtureClients[1]));
    server_exec();
    ProducerCheck(!is_user_connected(g_fixtureClients[1]), "real engine disconnect completed");
    BeginCase("after_disconnect");
    EmitRound();
    CaptureProducerRecords("after_disconnect", 1, 1, 0);

    BeginCase("duplicate");
    new before = g_emittedCount;
    EmitRound(ROUND_NONE);
    EmitRound(ROUND_GAME_COMMENCE);
    EmitRound(ROUND_GAME_RESTART);
    ProducerCheck(g_emittedCount == before && !g_roundEventPublished, "pseudo-rounds do not consume observable round");
    EmitRound();
    EmitRound();
    ProducerCheck(g_emittedCount == before + 1, "duplicate callback emits at most once");
    rg_restart_round();
    ProducerCheck(!g_roundEventPublished, "next native round admits a new event");
    EmitRound();
    CaptureProducerRecords("duplicate", 2, 1, 0);

    BeginCase("disabled_then_enabled");
    EnableProducer(false);
    EmitRound();
    ProducerCheck(CountSuffix(".json") == 0 && !g_roundEventPublished, "disabled producer leaves round unconsumed");
    EnableProducer(true);
    EmitRound();
    CaptureProducerRecords("disabled_then_enabled", 1, 1, 0);

    BeginCase("capacity_recovered");
    WriteOccupant("occupied.tmp");
    WriteOccupant("unknown.bin");
    new child[PLATFORM_MAX_PATH];
    formatex(child, charsmax(child), "%s/subdirectory", g_casePath);
    mkdir(child);
    new failed = g_failedCount;
    set_pcvar_num(g_maxPendingCvar, 0);
    EmitRound();
    ProducerCheck(g_failedCount == failed + 1 && !g_roundEventPublished, "invalid capacity fails without consuming round");
    set_pcvar_num(g_maxPendingCvar, 2);
    EmitRound();
    ProducerCheck(g_failedCount == failed + 2 && CountSuffix(".json") == 0, "tmp and unknown files apply backpressure");
    set_pcvar_num(g_maxPendingCvar, 3);
    EmitRound();
    CaptureProducerRecords("capacity_recovered", 1, 1, 0);
    ProducerCheck(CountSuffix(".tmp") == 1 && CountSuffix(".bin") == 1, "backpressure preserves prior evidence");

    BeginCase("write_recovered");
    failed = g_failedCount;
    ProducerCheck(SetFilePermissions(g_casePath, FPERM_U_READ | FPERM_U_EXEC), "real directory write fault installed");
    EmitRound();
    ProducerCheck(g_failedCount == failed + 1 && !g_roundEventPublished && CountSuffix(".tmp") == 0, "write failure leaves no committed or temporary record");
    ProducerCheck(SetFilePermissions(g_casePath, FPERM_U_READ | FPERM_U_WRITE | FPERM_U_EXEC), "fixture write permission restored");
    EmitRound();
    CaptureProducerRecords("write_recovered", 1, 1, 0);

    BeginCase("rename_recovered");
    failed = g_failedCount;
    g_failRename = true;
    EmitRound();
    ProducerCheck(g_failedCount == failed + 1 && !g_roundEventPublished && CountSuffix(".tmp") == 0 && CountSuffix(".json") == 0, "failed rename removes only its temporary record");
    g_failRename = false;
    EmitRound();
    CaptureProducerRecords("rename_recovered", 1, 1, 0);
    server_cmd("kick #%d", get_user_userid(g_fixtureHuman));
    server_exec();
    ProducerCheck(!is_user_connected(g_fixtureHuman), "last non-HLTV participant disconnected");
    BeginCase("hltv_only");
    EmitRound();
    ProducerCheck(CountSuffix(".json") == 0 && (g_roundEventPublished || disabled), "HLTV-only roster produces no record");
    ProducerCheck(g_emittedCount == 10 && g_renameAttempts == 11, "ten real records and one injected rename refusal");
    FinishProducer();
    return PLUGIN_HANDLED;
}

FinishProducer()
{
    server_print("PRODUCER_ROSTER_SMOKE=%s failures=%d synthetic_clients=3", g_failures ? "failed" : "passed", g_failures);
    server_print("PRODUCER_LIFECYCLE_SMOKE=%s failures=%d native_round_hooks=%d native_restart_hooks=%d", g_failures ? "failed" : "passed", g_failures, g_roundHooks, g_restartHooks);
    server_print("PRODUCER_SPOOL_SMOKE=%s failures=%d real_filesystem=1", g_failures ? "failed" : "passed", g_failures);
    server_print("PRODUCER_SMOKE=%s failures=%d human_activity_policy=unchanged", g_failures ? "failed" : "passed", g_failures);
}
