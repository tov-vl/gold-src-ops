#include <amxmodx>
#include <reapi>

new g_checks, g_failures;
new g_userId = 41;
new bool:g_sameName, bool:g_selectedLoadout;
new g_requests, g_closeArmed, g_disconnects, g_rejoins, g_spawns, g_timeouts;
new g_lastTimeout[24];

#define plugin_init ContinuityInit
#define client_disconnected ContinuityDisconnect
#define client_putinserver ContinuityPutInServer
#define is_user_connected SyntheticConnected
#define is_user_alive SyntheticAlive
#define is_user_bot SyntheticBot
#define is_user_hltv SyntheticHltv
#define get_user_userid SyntheticUserId
#define get_user_name SyntheticName
#define get_user_authid SyntheticAuth
#define user_has_weapon SyntheticWeapon
#define client_cmd CaptureClientInstruction
#define log_amx CaptureContinuityLog
#include <fixture_continuity.sma>
#undef plugin_init
#undef client_disconnected
#undef client_putinserver
#undef log_amx

public plugin_init()
{
    ContinuityInit();
    register_srvcmd("gso_fixture_continuity_smoke", "RunContinuitySmoke");
}

SyntheticConnected(player) { return player >= 1 && player <= 3; }
SyntheticAlive(player) { return player == 1; }
SyntheticBot(player) { return player == 2 || player == 3; }
SyntheticHltv(player) { return player == 0; }
SyntheticUserId(player) { return player == 1 ? g_userId : 90 + player; }
SyntheticName(player, name[], length)
{
    return copy(name, length, (player == 1 && g_sameName) ? "Fixture player" : "Another player");
}
SyntheticAuth(player, auth[], length)
{
    return copy(auth, length, player == 1 ? "STEAM_ID_LAN" : "BOT");
}
SyntheticWeapon(player, weapon)
{
    return player == 1 && (g_selectedLoadout
        ? weapon == CSW_AK47 || weapon == CSW_DEAGLE
        : weapon == CSW_MP5NAVY || weapon == CSW_USP);
}

CaptureClientInstruction(player, const command[], any:...)
{
    Verify(player == 1, "only the selected client receives an alias");
    if (equal(command, "gso_lab_continuity_request")) g_requests++;
    else Verify(false, "unexpected client command");
    return 1;
}

CaptureContinuityLog(const format[], any:...)
{
    new text[160]; vformat(text, charsmax(text), format, 2);
    if (containi(text, "FIXTURE_CONTINUITY_CLIENT_CLOSE_ARMED ") == 0) g_closeArmed++;
    if (containi(text, "FIXTURE_CONTINUITY_DISCONNECT ") == 0) g_disconnects++;
    if (containi(text, "FIXTURE_CONTINUITY_RECONNECT ") == 0) g_rejoins++;
    if (containi(text, "FIXTURE_CONTINUITY_RECONNECTED_SPAWN ") == 0) g_spawns++;
    new const timeoutPrefix[] = "FIXTURE_CONTINUITY_TIMEOUT stage=";
    if (containi(text, timeoutPrefix) == 0)
    {
        copy(g_lastTimeout, charsmax(g_lastTimeout), text[sizeof timeoutPrefix - 1]);
        g_timeouts++;
    }
    return 1;
}

Verify(bool:valid, const label[])
{
    g_checks++;
    if (!valid)
    {
        g_failures++;
        server_print("FIXTURE_CONTINUITY_ASSERTION_FAILED %s", label);
    }
}

ResetToNextRound()
{
    remove_task(CONTINUITY_DEADLINE_TASK);
    remove_task(1201);
    remove_task(1202);
    g_phase = Idle;
    g_roundSpawn = false;
    g_userId = 41;
    g_sameName = true;
    g_selectedLoadout = true;
    g_requests = 0;
    g_closeArmed = 0;
    g_disconnects = 0;
    g_rejoins = 0;
    g_spawns = 0;
    g_timeouts = 0;
    g_lastTimeout[0] = 0;
    BeginContinuity();
    Verify(g_phase == WaitRoundEnd && task_exists(CONTINUITY_DEADLINE_TASK), "overall deadline armed");
    OnRoundEnd();
    OnSpawn(1);
    CheckNextRound();
    Verify(g_phase == WaitClientReceipt && g_requests == 1, "round sends one request and waits for client receipt");
}

VerifyTimeout(const expected[])
{
    Verify(g_phase == TimedOut && g_timeouts == 1 && equal(g_lastTimeout, expected), "stage timeout recorded exactly once");
    Verify(!task_exists(CONTINUITY_DEADLINE_TASK), "terminal expiry removed the phase timer");
}

public RunContinuitySmoke()
{
    ResetToNextRound();
    OnReconnectRequestReceived(2);
    ContinuityDisconnect(1);
    Verify(g_phase == WaitClientReceipt && !g_disconnects && g_requests == 1, "foreign receipt and disconnect before receipt ignored");
    OnReconnectRequestReceived(1);
    OnReconnectRequestReceived(1);
    Verify(g_phase == WaitDisconnect && g_closeArmed == 1 && g_requests == 1,
        "valid receipt permits one owned close without a server retry command");
    ContinuityDisconnect(1);
    Verify(g_phase == WaitRejoin && g_disconnects == 1, "disconnect is recorded before rejoin");
    Verify(g_stageDeadline - get_gametime() > 90.0
        && g_stageDeadline <= g_continuityDeadline, "rejoin budget includes the worker schedule and stays bounded");
    ContinuityPutInServer(1);
    Verify(g_phase == WaitRejoin && !g_rejoins, "original connection id cannot count as a rejoin");
    g_userId = 42;
    g_sameName = false;
    ContinuityPutInServer(1);
    Verify(g_phase == WaitRejoin && !g_rejoins, "unrelated client cannot count as a rejoin");
    g_sameName = true;
    ContinuityPutInServer(1);
    Verify(g_phase == WaitSpawn && g_rejoins == 1 && !g_spawns, "new connection precedes spawn confirmation");
    Verify(g_requests == 1, "ordinary client setup owns team join");
    g_selectedLoadout = false;
    OnSpawn(1);
    remove_task(1202);
    CheckReconnectedSpawn();
    CheckReconnectedSpawn();
    Verify(g_phase == Finished && g_spawns == 1 && g_requests == 1 &&
        !task_exists(CONTINUITY_DEADLINE_TASK), "native spawn evidence is emitted once without a client command");

    ResetToNextRound();
    g_stageDeadline = get_gametime() - 1.0;
    OnReconnectRequestReceived(1);
    VerifyTimeout("client_receipt");
    Verify(!g_closeArmed && g_requests == 1, "late receipt cannot arm the owned close");

    ResetToNextRound();
    OnReconnectRequestReceived(1);
    g_stageDeadline = get_gametime() - 1.0;
    ContinuityDisconnect(1);
    ContinuityDisconnect(1);
    VerifyTimeout("disconnect");
    Verify(!g_disconnects && !g_rejoins && g_requests == 1, "cleanup disconnect cannot count after expiry");

    ResetToNextRound();
    OnReconnectRequestReceived(1);
    ContinuityDisconnect(1);
    g_stageDeadline = get_gametime() - 1.0;
    g_userId = 42;
    ContinuityPutInServer(1);
    VerifyTimeout("rejoin");
    Verify(!g_rejoins && g_requests == 1, "late connection cannot advance the expired test");

    ResetToNextRound();
    OnReconnectRequestReceived(1);
    ContinuityDisconnect(1);
    g_userId = 42;
    ContinuityPutInServer(1);
    g_stageDeadline = get_gametime() - 1.0;
    OnSpawn(1);
    remove_task(1202);
    CheckReconnectedSpawn();
    VerifyTimeout("spawn");
    Verify(!g_spawns && g_requests == 1, "late spawn cannot produce final acknowledgement");

    server_print("FIXTURE_CONTINUITY_SMOKE=%s checks=%d failures=%d", g_failures ? "failed" : "passed", g_checks, g_failures);
    return PLUGIN_HANDLED;
}
