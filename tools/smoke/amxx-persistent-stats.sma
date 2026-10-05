#include <amxmodx>
#include <reapi>
#include <fakemeta>
#include <hamsandwich>
#include <nvault>

// Identity/output substitutions are fixture-only; storage uses the real nVault.
#define plugin_init ProductPluginInit
#define is_user_bot(%1) FixtureIsBot(%1)
#define get_user_authid(%1,%2,%3) FixtureAuthId(%1,%2,%3)
#define client_print FixturePrint
#define nvault_lookup(%1,%2,%3,%4,%5) FixtureVaultLookup(%1,%2,%3,%4,%5)
#include <goldsrcops_weapon_selection.sma>
#undef plugin_init
#undef is_user_bot
#undef get_user_authid
#undef client_print
#undef nvault_lookup

new g_clients[3];
new bool:g_human[MAX_PLAYERS + 1];
new g_auth[MAX_PLAYERS + 1][32];
new g_failures;
new g_prints;
new g_print_target;
new g_last_print[192];
new bool:g_fail_readback;
new const DURABLE_KEY[] = "v1:STEAM_0:1:424242";

public plugin_init()
{
    ProductPluginInit();
    register_srvcmd("goldsrcops_persistent_stats_smoke", "RunPersistentStatsSmoke");
    register_srvcmd("goldsrcops_persistent_stats_seed", "SeedRestartStats");
    register_srvcmd("goldsrcops_persistent_stats_verify", "VerifyRestartStats");
}

bool:FixtureIsBot(id)
{
    return !g_human[id] && is_user_bot(id);
}

FixtureAuthId(id, buffer[], length)
{
    return copy(buffer, length, g_auth[id]);
}

FixturePrint(id, type, const message[], any:...)
{
    #pragma unused type
    g_prints++;
    g_print_target = id;
    vformat(g_last_print, charsmax(g_last_print), message, 4);
    return strlen(g_last_print);
}

FixtureVaultLookup(vault, const key[], value[], maxlen, &timestamp)
{
    return g_fail_readback ? 0 : nvault_lookup(vault, key, value, maxlen, timestamp);
}

CheckSaved(bool:passed, const label[])
{
    if (!passed)
    {
        g_failures++;
        server_print("PERSISTENT_STATS_ASSERTION_FAILED %s", label);
    }
}

bool:CreateStatsClients()
{
    if (get_playersnum() || g_clients[0] || MaxClients < sizeof g_clients
        || g_stats_vault == INVALID_HANDLE)
    {
        server_print("PERSISTENT_STATS_SMOKE=refused; fresh isolated fixture and nVault required");
        return false;
    }
    for (new index = 0; index < sizeof g_clients; index++)
    {
        new name[32], rejected[128];
        formatex(name, charsmax(name), "Saved stats fixture %d", index);
        new id = engfunc(EngFunc_CreateFakeClient, name);
        if (!id)
        {
            return false;
        }
        g_clients[index] = id;
        g_human[id] = true;
        copy(g_auth[id], charsmax(g_auth[]), "STEAM_ID_PENDING");
        dllfunc(DLLFunc_ClientConnect, id, name, "127.0.0.1", rejected);
        dllfunc(DLLFunc_ClientPutInServer, id);
        set_user_info(id, "lang", "en");
        rg_set_user_team(id, TEAM_CT);
        set_member(id, m_iJoiningState, JOINED);
        client_putinserver(id);
        rg_round_respawn(id);
    }
    set_pcvar_num(g_enabled, 1);
    return true;
}

public RunPersistentStatsSmoke()
{
    if (!CreateStatsClients())
    {
        return PLUGIN_HANDLED;
    }
    new key[40], kills, deaths, timestamp, value[64];
    CheckSaved(BuildStatsKey("STEAM_0:1:424242", key, charsmax(key))
        && equal(key, DURABLE_KEY), "valid Steam key");
    CheckSaved(BuildStatsKey("STEAM_1:1:424242", key, charsmax(key))
        && equal(key, DURABLE_KEY), "universe normalization");
    new const rejected_ids[][] = { "", "BOT", "HLTV", "STEAM_ID_PENDING",
        "STEAM_ID_LAN", "VALVE_ID_LAN", "STEAM_0:0:0", "STEAM_2:1:42",
        "STEAM_0:2:42", "STEAM_0:1:00", "STEAM_0:1:-1", "STEAM_0:1:42x",
        "STEAM_0:1:2147483648", "STEAM_0:1:12345678901", "name", "127.0.0.1" };
    for (new index = 0; index < sizeof rejected_ids; index++)
    {
        CheckSaved(!BuildStatsKey(rejected_ids[index], key, charsmax(key)) && !key[0], "shared or invalid identity rejected");
    }
    CheckSaved(DecodeSavedStats("1 7 3", kills, deaths) && kills == 7 && deaths == 3, "schema parsed");
    new const rejected_values[][] = { "", "2 7 3", "1 -1 3", "1 1 x", "1 1 2 3",
        "1 1000000001 0", "1 999999999999 0", "1 01 2", "1 1 2 ", "^"1^" 1 2" };
    for (new index = 0; index < sizeof rejected_values; index++)
    {
        CheckSaved(!DecodeSavedStats(rejected_values[index], kills, deaths), "malformed record rejected");
    }

    new player = g_clients[0], victim = g_clients[1], duplicate = g_clients[2];
    nvault_remove(g_stats_vault, DURABLE_KEY);
    RecordMapDeath(0, player);
    CheckSaved(!g_saved_ready[player] && g_map_deaths[player] == 1, "pending identity remains session only");
    copy(g_auth[player], charsmax(g_auth[]), "STEAM_0:1:424242");
    client_authorized(player, g_auth[player]);
    CheckSaved(g_saved_ready[player] && g_saved_deaths[player] == 0, "late auth loads without importing unknown score");
    CheckSaved(!nvault_lookup(g_stats_vault, DURABLE_KEY, value, charsmax(value), timestamp), "empty join never writes a zero record");
    copy(g_auth[victim], charsmax(g_auth[]), "STEAM_0:0:424243");
    nvault_remove(g_stats_vault, "v1:STEAM_0:0:424243");
    client_putinserver(victim);
    rg_set_user_team(victim, TEAM_TERRORIST);
    ExecuteHamB(Ham_TakeDamage, victim, player, player, 1000.0, DMG_BULLET);
    CheckSaved(g_saved_kills[player] == 1 && g_saved_deaths[victim] == 1, "native DeathMsg saved both players");
    CheckSaved(nvault_lookup(g_stats_vault, DURABLE_KEY, value, charsmax(value), timestamp)
        && equal(value, "1 1 0"), "native kill has real storage readback");
    new rejected[128];
    client_disconnected(player, false, rejected, charsmax(rejected));
    client_putinserver(player);
    CheckSaved(g_saved_ready[player] && g_saved_kills[player] == 1
        && !g_map_kills[player] && !g_map_deaths[player], "reconnect restores totals not map leaders");
    ShowMapStats(player);
    CheckSaved(contain(g_last_print, "Saved totals: K 1 | D 0 | K/D 1.00") >= 0 && g_prints == 2, "saved private response with connection streak");

    copy(g_auth[duplicate], charsmax(g_auth[]), "STEAM_1:1:424242");
    client_putinserver(duplicate);
    RecordMapDeath(0, duplicate);
    CheckSaved(!g_saved_ready[duplicate] && nvault_lookup(g_stats_vault, DURABLE_KEY,
        value, charsmax(value), timestamp) && equal(value, "1 1 0"), "duplicate live identity cannot overwrite");
    set_pcvar_num(g_enabled, 0);
    RecordMapDeath(0, player);
    CheckSaved(!g_saved_deaths[player], "disabled does not write");
    set_pcvar_num(g_enabled, 1);
    client_disconnected(player, false, rejected, charsmax(rejected));
    EnsureSavedStats(duplicate);
    CheckSaved(g_saved_ready[duplicate] && g_saved_kills[duplicate] == 1
        && !g_saved_deaths[duplicate], "duplicate can load only after owner leaves");

    copy(g_auth[player], charsmax(g_auth[]), "STEAM_0:1:424244");
    nvault_set(g_stats_vault, "v1:STEAM_0:1:424244", "2 99 99");
    client_putinserver(player);
    RecordMapDeath(0, player);
    CheckSaved(g_saved_blocked[player] && !g_saved_ready[player]
        && nvault_lookup(g_stats_vault, "v1:STEAM_0:1:424244", value, charsmax(value), timestamp)
        && equal(value, "2 99 99"), "corrupt record retained not overwritten");
    copy(g_auth[player], charsmax(g_auth[]), "STEAM_0:1:424245");
    nvault_set(g_stats_vault, "v1:STEAM_0:1:424245", "1 1000000000 1000000000");
    client_putinserver(player);
    RecordMapDeath(0, player);
    CheckSaved(g_saved_deaths[player] == STATS_COUNTER_LIMIT, "counter saturation avoids overflow");

    nvault_close(g_stats_vault);
    g_stats_vault = INVALID_HANDLE;
    client_putinserver(player);
    RecordMapDeath(0, player);
    CheckSaved(!g_saved_ready[player] && g_map_deaths[player] == 1, "unavailable vault keeps session behavior");
    g_stats_vault = nvault_open(STATS_VAULT_NAME);
    client_disconnected(duplicate, false, rejected, charsmax(rejected));
    copy(g_auth[player], charsmax(g_auth[]), "STEAM_0:1:424242");
    client_putinserver(player);
    CheckSaved(g_saved_ready[player] && g_saved_kills[player] == 1, "closed and reopened real vault restores totals");
    CheckPlayerRanks(player, victim);
    server_print("PERSISTENT_STATS_SMOKE=%s failures=%d real_nvault=1 native_damage=1",
        g_failures ? "failed" : "passed", g_failures);
    return PLUGIN_HANDLED;
}

CheckPlayerRanks(player, victim)
{
    new const kills[] = { 0, 24, 25, 99, 100, 249, 250, 499, 500, STATS_COUNTER_LIMIT };
    new const ranks[] = { 0, 0, 1, 1, 2, 2, 3, 3, 4, 4 };
    for (new index = 0; index < sizeof kills; index++)
    {
        CheckSaved(RankForKills(kills[index]) == ranks[index], "rank threshold boundary");
    }
    new const key[] = "v1:STEAM_0:1:424246";
    new value[64], timestamp, rejected[128];
    nvault_remove(g_stats_vault, key);
    copy(g_auth[player], charsmax(g_auth[]), "STEAM_0:1:424246");
    client_putinserver(player);
    g_prints = 0;
    remove_task(WELCOME_TASK_BASE + player);
    ShowWelcome(WELCOME_TASK_BASE + player);
    CheckSaved(g_prints == 2 && g_print_target == player
        && contain(g_last_print, "Saved rank: Recruit | K 0") >= 0,
        "first welcome shows private saved progress");
    ShowWelcome(WELCOME_TASK_BASE + player);
    CheckSaved(g_prints == 2 && g_stats_next[player][2] == 0.0,
        "welcome is once and does not consume manual rank cooldown");
    g_prints = 0;
    ShowPlayerRank(player);
    CheckSaved(g_prints == 1 && g_print_target == player
        && contain(g_last_print, "Saved rank: Recruit | K 0 | 25 kills to Fighter.") >= 0,
        "private initial rank from saved totals");
    CheckSaved(!nvault_lookup(g_stats_vault, key, value, charsmax(value), timestamp), "rank query never creates a zero record");
    ShowPlayerRank(player);
    CheckSaved(g_prints == 1, "rank repeat throttled");
    ShowMapStats(player);
    CheckSaved(g_prints == 3, "rank cooldown independent of stats");
    g_stats_next[player][2] = get_gametime();
    ShowPlayerRank(player);
    CheckSaved(g_prints == 4, "rank cooldown expires");

    nvault_set(g_stats_vault, key, "1 24 7");
    client_putinserver(player);
    ShowPlayerRank(player);
    CheckSaved(contain(g_last_print, "Recruit | K 24 | 1 kills to Fighter.") >= 0
        && nvault_lookup(g_stats_vault, key, value, charsmax(value), timestamp)
        && equal(value, "1 24 7"), "rank read preserves existing schema record");
    rg_round_respawn(victim);
    new prints = g_prints;
    ExecuteHamB(Ham_TakeDamage, victim, player, player, 1000.0, DMG_BULLET);
    CheckSaved(g_saved_kills[player] == 25 && RankForKills(g_saved_kills[player]) == 1,
        "native enemy kill advances rank");
    CheckSaved(g_prints == prints + 1 && g_print_target == player
        && contain(g_last_print, "Rank up: Fighter | K 25.") >= 0,
        "promotion privately follows confirmed native kill");
    rg_round_respawn(victim);
    prints = g_prints;
    ExecuteHamB(Ham_TakeDamage, victim, player, player, 1000.0, DMG_BULLET);
    CheckSaved(g_saved_kills[player] == 26 && g_prints == prints,
        "next kill does not repeat promotion");
    client_disconnected(player, false, rejected, charsmax(rejected));
    client_putinserver(player);
    ShowPlayerRank(player);
    CheckSaved(contain(g_last_print, "Fighter | K 26 | 74 kills to Veteran.") >= 0,
        "rank restored after reconnect and cooldown reset");

    for (new rank = 2; rank < sizeof RANK_KILLS; rank++)
    {
        formatex(value, charsmax(value), "1 %d 7", RANK_KILLS[rank] - 1);
        nvault_set(g_stats_vault, key, value);
        client_putinserver(player);
        rg_round_respawn(victim);
        prints = g_prints;
        ExecuteHamB(Ham_TakeDamage, victim, player, player, 1000.0, DMG_BULLET);
        new promotion[64];
        new rank_name[32];
        formatex(rank_name, charsmax(rank_name), "%L", "en", RANK_KEYS[rank]);
        formatex(promotion, charsmax(promotion), "Rank up: %s | K %d.", rank_name, RANK_KILLS[rank]);
        CheckSaved(g_prints == prints + 1 && g_print_target == player
            && contain(g_last_print, promotion) >= 0, "each higher threshold emits one private promotion");
    }

    nvault_set(g_stats_vault, key, "1 24 7");
    client_putinserver(player);
    rg_round_respawn(victim);
    prints = g_prints;
    g_fail_readback = true;
    ExecuteHamB(Ham_TakeDamage, victim, player, player, 1000.0, DMG_BULLET);
    g_fail_readback = false;
    CheckSaved(!g_saved_ready[player] && g_saved_blocked[player] && g_prints == prints,
        "failed storage readback suppresses promotion");

    nvault_set(g_stats_vault, key, "1 1000000000 7");
    client_putinserver(player);
    ShowPlayerRank(player);
    CheckSaved(contain(g_last_print, "Legend | K 1000000000 | Highest rank reached.") >= 0,
        "highest rank has no overflow or next tier");
    nvault_set(g_stats_vault, key, "2 99 99");
    client_putinserver(player);
    ShowPlayerRank(player);
    CheckSaved(contain(g_last_print, "Saved rank unavailable") >= 0
        && nvault_lookup(g_stats_vault, key, value, charsmax(value), timestamp)
        && equal(value, "2 99 99"), "corrupt record never produces a saved rank or overwrite");

    copy(g_auth[player], charsmax(g_auth[]), "STEAM_ID_PENDING");
    client_putinserver(player);
    ShowPlayerRank(player);
    CheckSaved(contain(g_last_print, "Saved rank unavailable") >= 0, "pending identity has no persistent rank");
    remove_task(WELCOME_TASK_BASE + player);
    prints = g_prints;
    ShowWelcome(WELCOME_TASK_BASE + player);
    CheckSaved(g_prints == prints + 1 && contain(g_last_print, "/guns:") >= 0,
        "pending identity welcome never invents a saved rank");
    prints = g_prints;
    g_stats_next[player][2] = 0.0;
    set_pcvar_num(g_enabled, 0);
    ShowPlayerRank(player);
    CheckSaved(g_prints == prints, "disabled rank emits nothing");
    set_pcvar_num(g_enabled, 1);
    nvault_close(g_stats_vault);
    g_stats_vault = INVALID_HANDLE;
    copy(g_auth[player], charsmax(g_auth[]), "STEAM_0:1:424246");
    client_putinserver(player);
    ShowPlayerRank(player);
    CheckSaved(contain(g_last_print, "Saved rank unavailable") >= 0, "unavailable vault never invents a rank");
    g_stats_vault = nvault_open(STATS_VAULT_NAME);
    server_print("PLAYER_RANK_SMOKE=%s failures=%d thresholds=5 read_only=1 native_damage=1",
        g_failures ? "failed" : "passed", g_failures);
    server_print("RANK_FEEDBACK_SMOKE=%s failures=%d welcome=private promotion=after_readback",
        g_failures ? "failed" : "passed", g_failures);
}

public SeedRestartStats()
{
    if (!CreateStatsClients())
    {
        return PLUGIN_HANDLED;
    }
    nvault_remove(g_stats_vault, DURABLE_KEY);
    new player = g_clients[0];
    copy(g_auth[player], charsmax(g_auth[]), "STEAM_0:1:424242");
    client_putinserver(player);
    RecordMapDeath(0, player);
    RecordMapDeath(0, player);
    server_print("PERSISTENT_STATS_SEED=%s", g_saved_ready[player] && g_saved_deaths[player] == 2 ? "passed" : "failed");
    return PLUGIN_HANDLED;
}

public VerifyRestartStats()
{
    if (!CreateStatsClients())
    {
        return PLUGIN_HANDLED;
    }
    new player = g_clients[0];
    copy(g_auth[player], charsmax(g_auth[]), "STEAM_1:1:424242");
    client_putinserver(player);
    CheckSaved(g_saved_ready[player] && !g_saved_kills[player] && g_saved_deaths[player] == 2
        && !g_map_deaths[player], "fresh plugin/process restored saved identity");
    server_print("PERSISTENT_STATS_RELOAD=%s failures=%d", g_failures ? "failed" : "passed", g_failures);
    return PLUGIN_HANDLED;
}
