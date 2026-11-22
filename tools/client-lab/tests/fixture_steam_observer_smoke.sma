#include <amxmodx>
#include <reapi>
#include <fakemeta>

new g_checks, g_failures, g_receipts, g_commands, g_timeouts, g_reads, g_refusals;
new g_userId = 41, g_lan;
new bool:g_otherConnected, bool:g_ready;
new g_auth[32], g_lastReceipt[128];
new g_liveMask;
new bool:g_engineInventory;

#define plugin_init SteamObserverInit
#define client_authorized SteamObserverAuthorized
#define client_disconnected SteamObserverDisconnected
#define is_user_connected SyntheticConnected
#define is_user_alive SyntheticAlive
#define is_user_bot SyntheticBot
#define is_user_hltv SyntheticHltv
#define get_user_userid SyntheticUserId
#define get_user_authid SyntheticAuth
#define get_user_weapons SyntheticWeapons
#define get_cvar_num SyntheticLan
#define log_amx CaptureSteamLog
#define client_cmd CaptureClientInstruction
#include <fixture_steam_observer.sma>
#undef plugin_init
#undef client_authorized
#undef client_disconnected
#undef get_user_weapons

public plugin_init()
{
    SteamObserverInit();
    register_srvcmd("gso_fixture_steam_observer_smoke", "RunSteamObserverSmoke");
}

SyntheticConnected(player) { return player == 1 || player == 2 || (player == 3 && g_otherConnected); }
SyntheticAlive(player) { return (player == 1 || player == 3) && g_ready; }
SyntheticBot(player) { return player == 2; }
SyntheticHltv(player) { return player == 4; }
SyntheticUserId(player) { return player == 1 ? g_userId : 90 + player; }
SyntheticAuth(player, auth[], length)
{
    return copy(auth, length, player == 1 ? g_auth : "STEAM_0:1:101");
}
SyntheticWeapons(player, weapons[32], &num)
{
    Verify(player == 1 && num == 0, "inventory read is scoped to the authorized player");
    g_reads++;
    if (g_engineInventory) { return get_user_weapons(player, weapons, num); }
    weapons[0] = CSW_KNIFE;
    num = 1;
    return g_liveMask;
}
SyntheticLan(const cvar[])
{
    Verify(bool:equal(cvar, "sv_lan"), "armed by the Steam fixture only");
    return g_lan;
}

CaptureClientInstruction(player, const command[], any:...)
{
    Verify(player == 1 && equal(command, "gso_lab_steam_observed") && g_receipts == g_commands + 1,
        "one passive snapshot is requested after inventory is recorded");
    g_commands++;
    return 1;
}

CaptureSteamLog(const format[], any:...)
{
    new line[160];
    vformat(line, charsmax(line), format, 2);
    if (contain(line, "FIXTURE_STEAM_OBSERVE_SPAWN ") == 0)
    {
        g_receipts++;
        copy(g_lastReceipt, charsmax(g_lastReceipt), line);
        server_print("%s", line);
    }
    if (contain(line, "FIXTURE_STEAM_OBSERVE_TIMEOUT ") == 0) { g_timeouts++; }
    if (contain(line, "FIXTURE_STEAM_OBSERVE_REFUSED ") == 0) { g_refusals++; }
    return 1;
}

Verify(bool:condition, const label[])
{
    g_checks++;
    if (!condition)
    {
        g_failures++;
        server_print("FIXTURE_STEAM_OBSERVER_ASSERTION_FAILED %s", label);
    }
}

SetReported(const auth[])
{
    copy(g_auth, charsmax(g_auth), auth);
}

ResetObservation()
{
    FinishSteamObservation();
    g_observePhase = NotArmed;
    g_spawnHookReady = true;
    g_userId = 41;
    g_lan = 0;
    g_otherConnected = false;
    g_ready = true;
    g_receipts = 0;
    g_commands = 0;
    g_reads = 0;
    g_timeouts = 0;
    g_refusals = 0;
    g_liveMask = (1 << CSW_MP5NAVY) | (1 << CSW_USP) | (1 << CSW_KNIFE);
    g_steamIds[1][0] = 0;
    SetReported("STEAM_0:1:101");
}

ArmAndDisconnect()
{
    SteamObserverAuthorized(1, g_auth);
    ArmSteamObservation();
    Verify(g_observePhase == AwaitDisconnect && task_exists(OBSERVE_DEADLINE), "first connection armed exactly once");
    ObserveAuthorizedSpawn(1);
    SteamObserverDisconnected(2);
    Verify(g_observePhase == AwaitDisconnect && g_reads == 0, "first spawn and foreign disconnect ignored");
    SteamObserverDisconnected(1);
    Verify(g_observePhase == AwaitAuthorizedSpawn && !g_steamIds[1][0], "disconnected slot identity cleared");
}

public RunSteamObserverSmoke()
{
    ResetObservation();
    ArmSteamObservation();
    Verify(g_refusals == 1 && g_observePhase == NotArmed, "a reported ID alone cannot arm");
    SteamObserverAuthorized(1, g_auth);
    g_lan = 1;
    ArmSteamObservation();
    g_lan = 0;
    g_spawnHookReady = false;
    ArmSteamObservation();
    g_spawnHookReady = true;
    g_otherConnected = true;
    ArmSteamObservation();
    Verify(g_refusals == 4 && g_observePhase == NotArmed, "unsafe fixture and multiple humans refused");

    ResetObservation();
    ArmAndDisconnect();
    SteamObserverAuthorized(1, g_auth);
    ObserveAuthorizedSpawn(1);
    g_userId = 42;
    SteamObserverAuthorized(1, "STEAM_ID_LAN");
    ObserveAuthorizedSpawn(1);
    SteamObserverAuthorized(1, "STEAM_ID_PENDING");
    ObserveAuthorizedSpawn(1);
    SteamObserverAuthorized(1, "STEAM_0:1:102");
    ObserveAuthorizedSpawn(1);
    Verify(g_receipts == 0 && g_reads == 0, "old userid, LAN, pending, and other account ignored");

    SetReported("STEAM_1:1:101");
    SteamObserverAuthorized(1, g_auth);
    g_ready = false;
    ObserveAuthorizedSpawn(1);
    g_ready = true;
    g_liveMask = (1 << CSW_AK47) | (1 << CSW_DEAGLE) | (1 << CSW_KNIFE) | (1 << 31);
    ObserveAuthorizedSpawn(1);
    new expected[128];
    formatex(expected, charsmax(expected),
        "FIXTURE_STEAM_OBSERVE_SPAWN authorized=1 same_account=1 new_userid=1 inventory=0x%08x",
        g_liveMask & 0x7FFFFFFE);
    Verify(bool:equal(g_lastReceipt, expected), "matching new account observes actual AK47 and Deagle");
    g_liveMask = 0;
    ObserveAuthorizedSpawn(1);
    ArmSteamObservation();
    Verify(g_receipts == 1 && g_reads == 1 && g_commands == 1 && equal(g_lastReceipt, expected)
        && g_observePhase == Terminal && !task_exists(OBSERVE_DEADLINE), "later spawns cannot replace the receipt");

    ResetObservation();
    ArmAndDisconnect();
    g_userId = 43;
    ObserveAuthorizedSpawn(1);
    Verify(!g_receipts && !g_reads, "a spawn before the authorization forward cannot be evidence");
    SteamObserverAuthorized(1, g_auth);
    Verify(!g_receipts && !g_reads, "authorization does not turn a previous spawn into evidence");
    ObserveAuthorizedSpawn(1);
    formatex(expected, charsmax(expected),
        "FIXTURE_STEAM_OBSERVE_SPAWN authorized=1 same_account=1 new_userid=1 inventory=0x%08x", g_liveMask);
    Verify(g_receipts == 1 && g_commands == 1 && equal(g_lastReceipt, expected),
        "late auth records actual default weapons if the first eligible spawn has them");

    ResetObservation();
    ArmAndDisconnect();
    g_userId = 44;
    SteamObserverAuthorized(1, g_auth);
    g_observeDeadline = get_gametime() - 1.0;
    ObserveAuthorizedSpawn(1);
    ObserveAuthorizedSpawn(1);
    Verify(g_timeouts == 1 && !g_commands && !g_reads && !task_exists(OBSERVE_DEADLINE),
        "expired observation cannot read inventory or request a snapshot");

    new auth[32];
    Verify(!NormalizeObservationSteamId("STEAM_0:0:0", auth, charsmax(auth))
        && !NormalizeObservationSteamId("STEAM_0:1:0101", auth, charsmax(auth))
        && !NormalizeObservationSteamId("STEAM_1:1:2147483648", auth, charsmax(auth))
        && !NormalizeObservationSteamId("STEAM_0:1:123x", auth, charsmax(auth)),
        "noncanonical Steam IDs rejected");


    CheckEngineInventoryAtSpawn();

    if (!g_failures) { server_print("FIXTURE_STEAM_OBSERVER_SMOKE=passed checks=%d failures=0", g_checks); }
}

CheckEngineInventoryAtSpawn()
{
    new player = engfunc(EngFunc_CreateFakeClient, "Steam inventory fixture");
    Verify(player == 1, "empty server supplied the expected synthetic client slot");
    if (player != 1) { return; }
    new rejected[128];
    dllfunc(DLLFunc_ClientConnect, player, "Steam inventory fixture", "127.0.0.1", rejected);
    dllfunc(DLLFunc_ClientPutInServer, player);
    rg_set_user_team(player, TEAM_CT);
    set_member(player, m_iJoiningState, JOINED);
    set_member(player, m_bJustConnected, false);
    set_member(player, m_iMenu, Menu_OFF);
    set_member(player, m_bMissionBriefing, false);

    ResetObservation();
    ArmAndDisconnect();
    g_userId = 45;
    SteamObserverAuthorized(player, g_auth);
    g_engineInventory = true;
    // Register a test grant in the same order as the live addon and observer.
    new HookChain:grant = RegisterHookChain(RG_CBasePlayer_Spawn, "GrantFixtureWeapons", true);
    new HookChain:observe = RegisterHookChain(RG_CBasePlayer_Spawn, "ObserveAuthorizedSpawn", true);
    rg_round_respawn(player);
    DisableHookChain(observe);
    DisableHookChain(grant);

    new expected[128];
    formatex(expected, charsmax(expected),
        "FIXTURE_STEAM_OBSERVE_SPAWN authorized=1 same_account=1 new_userid=1 inventory=0x%08x",
        (1 << CSW_AK47) | (1 << CSW_DEAGLE));
    Verify(g_receipts == 1 && g_reads == 1 && equal(g_lastReceipt, expected),
        "real spawn post hook reads the just-granted engine inventory");
}

public GrantFixtureWeapons(player)
{
    Verify(bool:rg_remove_all_items(player), "real spawn inventory cleared");
    Verify(rg_give_item(player, "weapon_ak47") > 0, "real rifle granted");
    Verify(rg_give_item(player, "weapon_deagle") > 0, "real pistol granted");
    return HC_CONTINUE;
}
