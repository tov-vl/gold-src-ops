#include <amxmodx>
#include <reapi>

enum ObservePhase
{
    NotArmed, AwaitDisconnect, AwaitAuthorizedSpawn, Terminal
}

const OBSERVE_DEADLINE = 1210;
const Float:OBSERVE_SECONDS = 220.0;
new ObservePhase:g_observePhase;
new bool:g_spawnHookReady;
new g_originalSlot, g_originalUserId;
new g_originalSteamId[32], g_steamIds[MAX_PLAYERS + 1][32];
new Float:g_observeDeadline;

public plugin_init()
{
    register_plugin("Fixture Steam reconnect inventory", "1.0", "");
    register_srvcmd("gso_fixture_steam_observe_arm", "ArmSteamObservation");
}

public plugin_cfg()
{
    // The addon grants weapons synchronously in its plugin_init-registered spawn post hook.
    // Register this post hook only after plugin_init has run for every plugin.
    new name[1], version[1], author[1], status[16], file[1];
    new addon = is_plugin_loaded("goldsrcops_weapon_selection.amxx", true);
    if (addon < 0) { return; }
    get_plugin(addon, file, 0, name, 0, version, 0, author, 0, status, charsmax(status));
    if (!equal(status, "running")) { return; }
    g_spawnHookReady = RegisterHookChain(RG_CBasePlayer_Spawn, "ObserveAuthorizedSpawn", true) != INVALID_HOOKCHAIN;
}

bool:SteamObservationHuman(player)
{
    return is_user_connected(player) && !is_user_bot(player) && !is_user_hltv(player);
}

bool:NormalizeObservationSteamId(const raw[], normalized[], length)
{
    normalized[0] = 0;
    new digits = strlen(raw) - 10;
    if (digits < 1 || digits > 10 || !equal(raw, "STEAM_", 6)
        || (raw[6] != '0' && raw[6] != '1') || raw[7] != ':'
        || (raw[8] != '0' && raw[8] != '1') || raw[9] != ':'
        || (raw[10] == '0' && (digits > 1 || raw[8] == '0'))
        || (digits == 10 && strcmp(raw[10], "2147483647") > 0))
    {
        return false;
    }
    for (new index = 10; raw[index]; index++)
    {
        if (raw[index] < '0' || raw[index] > '9') { return false; }
    }
    copy(normalized, length, raw);
    normalized[6] = '0';
    return true;
}

public client_authorized(player, const authid[])
{
    NormalizeObservationSteamId(authid, g_steamIds[player], charsmax(g_steamIds[]));
}

bool:MatchesReportedAuthorization(player, const canonical[])
{
    if (!canonical[0] || !equal(g_steamIds[player], canonical)) { return false; }
    new raw[32], normalized[32];
    get_user_authid(player, raw, charsmax(raw));
    return NormalizeObservationSteamId(raw, normalized, charsmax(normalized)) && bool:equal(normalized, canonical);
}

public ArmSteamObservation()
{
    if (g_observePhase != NotArmed) { return PLUGIN_HANDLED; }
    if (!g_spawnHookReady || get_cvar_num("sv_lan") != 0)
    {
        log_amx("FIXTURE_STEAM_OBSERVE_REFUSED reason=fixture_prerequisite");
        return PLUGIN_HANDLED;
    }
    new human, humans;
    for (new player = 1; player <= MaxClients; player++)
    {
        if (SteamObservationHuman(player)) { human = player; humans++; }
    }
    if (humans != 1 || !is_user_alive(human) || get_user_userid(human) <= 0
        || !MatchesReportedAuthorization(human, g_steamIds[human]))
    {
        log_amx("FIXTURE_STEAM_OBSERVE_REFUSED reason=first_authorized_player");
        return PLUGIN_HANDLED;
    }
    g_originalSlot = human;
    g_originalUserId = get_user_userid(human);
    copy(g_originalSteamId, charsmax(g_originalSteamId), g_steamIds[human]);
    g_observePhase = AwaitDisconnect;
    g_observeDeadline = get_gametime() + OBSERVE_SECONDS;
    set_task(OBSERVE_SECONDS, "ExpireSteamObservation", OBSERVE_DEADLINE);
    log_amx("FIXTURE_STEAM_OBSERVE_ARMED authorized=1");
    return PLUGIN_HANDLED;
}

public client_disconnected(player)
{
    g_steamIds[player][0] = 0;
    if (g_observePhase == AwaitDisconnect && player == g_originalSlot
        && get_user_userid(player) == g_originalUserId && SteamObservationWithinDeadline())
    {
        g_observePhase = AwaitAuthorizedSpawn;
        log_amx("FIXTURE_STEAM_OBSERVE_DISCONNECT observed=1");
    }
}

bool:SteamObservationWithinDeadline()
{
    if (get_gametime() < g_observeDeadline) { return true; }
    ExpireSteamObservation();
    return false;
}

public ExpireSteamObservation()
{
    if (g_observePhase == AwaitDisconnect || g_observePhase == AwaitAuthorizedSpawn)
    {
        log_amx("FIXTURE_STEAM_OBSERVE_TIMEOUT stage=%s",
            g_observePhase == AwaitDisconnect ? "disconnect" : "authorized_spawn");
        FinishSteamObservation();
    }
}

FinishSteamObservation()
{
    g_observePhase = Terminal;
    g_originalSteamId[0] = 0;
    remove_task(OBSERVE_DEADLINE);
}

public ObserveAuthorizedSpawn(player)
{
    if (g_observePhase != AwaitAuthorizedSpawn || !SteamObservationWithinDeadline()
        || !SteamObservationHuman(player) || !is_user_alive(player))
    {
        return HC_CONTINUE;
    }
    new userid = get_user_userid(player);
    if (userid <= 0 || userid == g_originalUserId || !MatchesReportedAuthorization(player, g_originalSteamId))
    {
        return HC_CONTINUE;
    }
    new weapons[32], count;
    // Snapshot on the first eligible spawn, in the same hook invocation as the grant.
    new mask = get_user_weapons(player, weapons, count) & 0x7FFFFFFE;
    FinishSteamObservation();
    log_amx("FIXTURE_STEAM_OBSERVE_SPAWN authorized=1 same_account=1 new_userid=1 inventory=0x%08x", mask);
    client_cmd(player, "gso_lab_steam_observed");
    return HC_CONTINUE;
}
