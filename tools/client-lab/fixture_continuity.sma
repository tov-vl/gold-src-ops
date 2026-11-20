#include <amxmodx>
#include <reapi>

enum FixturePhase
{
    Idle, WaitRoundEnd, WaitRoundStart, WaitClientReceipt,
    WaitDisconnect, WaitRejoin, WaitSpawn, ConfirmSpawn, Finished, TimedOut
}
const CONTINUITY_DEADLINE_TASK = 1204;
const Float:CONTINUITY_TOTAL_SECONDS = 255.0;
const Float:CONTINUITY_RECEIPT_SECONDS = 8.0;
const Float:CONTINUITY_DISCONNECT_SECONDS = 20.0;
const Float:CONTINUITY_REJOIN_SECONDS = 100.0;
const Float:CONTINUITY_SPAWN_SECONDS = 15.0;
new FixturePhase:g_phase;
new g_player, g_firstUserId, g_secondUserId;
new bool:g_roundSpawn;
new Float:g_continuityDeadline, Float:g_stageDeadline;
new g_playerName[32], g_originalAuth[32];

public plugin_init()
{
    register_plugin("Fixture round and reconnect evidence", "1.3", "");
    register_srvcmd("gso_fixture_continuity_begin", "BeginContinuity");
    register_clcmd("say_team /gso_lab_continuity_received", "OnReconnectRequestReceived");
    register_logevent("OnRoundEnd", 2, "1=Round_End");
    register_logevent("OnRoundStart", 2, "1=Round_Start");
    RegisterHookChain(RG_CBasePlayer_Spawn, "OnSpawn", true);
}

bool:IsHuman(player)
{
    return is_user_connected(player) && !is_user_bot(player) && !is_user_hltv(player);
}

bool:SelectedLoadout(player)
{
    return user_has_weapon(player, CSW_AK47) && user_has_weapon(player, CSW_DEAGLE)
        && !user_has_weapon(player, CSW_MP5NAVY) && !user_has_weapon(player, CSW_USP);
}

public BeginContinuity()
{
    if (g_phase != Idle) { return PLUGIN_HANDLED; }
    new humans, bots;
    for (new player = 1; player <= MaxClients; player++)
    {
        if (IsHuman(player)) { humans++; g_player = player; }
        else if (is_user_connected(player) && is_user_bot(player)) { bots++; }
    }
    if (humans != 1 || bots != 2 || !is_user_alive(g_player) || !SelectedLoadout(g_player))
    {
        log_amx("FIXTURE_CONTINUITY_REFUSED boundary=initial_loadout");
        return PLUGIN_HANDLED;
    }
    g_firstUserId = get_user_userid(g_player);
    get_user_name(g_player, g_playerName, charsmax(g_playerName));
    get_user_authid(g_player, g_originalAuth, charsmax(g_originalAuth));
    g_phase = WaitRoundEnd;
    g_continuityDeadline = get_gametime() + CONTINUITY_TOTAL_SECONDS;
    ArmContinuityDeadline(CONTINUITY_TOTAL_SECONDS);
    log_amx("FIXTURE_CONTINUITY_ARMED selected=1 bots=2");
    return PLUGIN_HANDLED;
}

public OnRoundEnd()
{
    if (g_phase != WaitRoundEnd) { return; }
    g_phase = WaitRoundStart;
    log_amx("FIXTURE_CONTINUITY_ROUND_END observed=1");
}

public OnRoundStart()
{
    if (g_phase != WaitRoundStart) { return; }
    set_task(0.45, "CheckNextRound", 1201);
}

public OnSpawn(player)
{
    if (player != g_player || !is_user_alive(player)) { return HC_CONTINUE; }
    if (g_phase == WaitRoundStart) { g_roundSpawn = true; }
    else if (g_phase == WaitSpawn)
    {
        g_phase = ConfirmSpawn;
        set_task(0.45, "CheckReconnectedSpawn", 1202);
    }
    return HC_CONTINUE;
}

public CheckNextRound()
{
    if (g_phase != Idle && get_gametime() >= g_stageDeadline) { ExpireContinuity(); return; }
    if (g_phase != WaitRoundStart || !IsHuman(g_player) || !is_user_alive(g_player)
        || get_user_userid(g_player) != g_firstUserId) { return; }
    log_amx("FIXTURE_CONTINUITY_NEXT_ROUND spawn=%d selected=%d", g_roundSpawn, SelectedLoadout(g_player));
    if (!g_roundSpawn || !SelectedLoadout(g_player))
    {
        g_phase = Finished;
        remove_task(CONTINUITY_DEADLINE_TASK);
        return;
    }
    g_phase = WaitClientReceipt;
    ArmContinuityDeadline(CONTINUITY_RECEIPT_SECONDS);
    log_amx("FIXTURE_CONTINUITY_REQUEST_SENT observed=1");
    client_cmd(g_player, "gso_lab_continuity_request");
}

ArmContinuityDeadline(Float:duration)
{
    remove_task(CONTINUITY_DEADLINE_TASK);
    new Float:remaining = g_continuityDeadline - get_gametime();
    if (remaining <= 0.0)
    {
        ExpireContinuity();
        return;
    }
    duration = floatmin(remaining, duration);
    g_stageDeadline = get_gametime() + duration;
    set_task(duration, "ExpireContinuity", CONTINUITY_DEADLINE_TASK);
}

public ExpireContinuity()
{
    new stage[24];
    switch (g_phase)
    {
        case WaitRoundEnd: copy(stage, charsmax(stage), "round_end");
        case WaitRoundStart: copy(stage, charsmax(stage), "round_start");
        case WaitClientReceipt: copy(stage, charsmax(stage), "client_receipt");
        case WaitDisconnect: copy(stage, charsmax(stage), "disconnect");
        case WaitRejoin: copy(stage, charsmax(stage), "rejoin");
        case WaitSpawn, ConfirmSpawn: copy(stage, charsmax(stage), "spawn");
        default: return;
    }
    remove_task(CONTINUITY_DEADLINE_TASK);
    g_phase = TimedOut;
    log_amx("FIXTURE_CONTINUITY_TIMEOUT stage=%s", stage);
}

public OnReconnectRequestReceived(player)
{
    if (g_phase != Idle && get_gametime() >= g_stageDeadline) { ExpireContinuity(); return PLUGIN_HANDLED; }
    if (g_phase != WaitClientReceipt || player != g_player || !IsHuman(player)
        || !is_user_alive(player) || get_user_userid(player) != g_firstUserId) { return PLUGIN_HANDLED; }
    log_amx("FIXTURE_CONTINUITY_CLIENT_RECEIVED observed=1");
    g_phase = WaitDisconnect;
    ArmContinuityDeadline(CONTINUITY_DISCONNECT_SECONDS);
    log_amx("FIXTURE_CONTINUITY_CLIENT_CLOSE_ARMED observed=1");
    return PLUGIN_HANDLED;
}

public client_disconnected(player)
{
    if (g_phase != Idle && get_gametime() >= g_stageDeadline) { ExpireContinuity(); return; }
    if (g_phase != WaitDisconnect || player != g_player) { return; }
    g_phase = WaitRejoin;
    ArmContinuityDeadline(CONTINUITY_REJOIN_SECONDS);
    log_amx("FIXTURE_CONTINUITY_DISCONNECT observed=1");
}

public client_putinserver(player)
{
    if (g_phase != Idle && get_gametime() >= g_stageDeadline) { ExpireContinuity(); return; }
    if (g_phase != WaitRejoin || !IsHuman(player)) { return; }
    new name[32];
    get_user_name(player, name, charsmax(name));
    if (!equal(name, g_playerName) || get_user_userid(player) == g_firstUserId) { return; }
    g_player = player;
    g_secondUserId = get_user_userid(player);
    g_phase = WaitSpawn;
    ArmContinuityDeadline(CONTINUITY_SPAWN_SECONDS);
    log_amx("FIXTURE_CONTINUITY_RECONNECT new_connection=1");
}

public CheckReconnectedSpawn()
{
    if (g_phase != Idle && get_gametime() >= g_stageDeadline) { ExpireContinuity(); return; }
    if (g_phase != ConfirmSpawn || !IsHuman(g_player) || !is_user_alive(g_player)
        || get_user_userid(g_player) != g_secondUserId) { return; }
    g_phase = Finished;
    remove_task(CONTINUITY_DEADLINE_TASK);
    new auth[32];
    get_user_authid(g_player, auth, charsmax(auth));
    new bool:lan = bool:equal(auth, "STEAM_ID_LAN");
    new bool:same = bool:equal(auth, g_originalAuth);
    new bool:defaultSet = user_has_weapon(g_player, CSW_MP5NAVY) && user_has_weapon(g_player, CSW_USP)
        && !user_has_weapon(g_player, CSW_AK47) && !user_has_weapon(g_player, CSW_DEAGLE);
    log_amx("FIXTURE_CONTINUITY_RECONNECTED_SPAWN lan_identity=%d same_identity=%d selected=%d default=%d",
        lan, same, SelectedLoadout(g_player), defaultSet);
}
