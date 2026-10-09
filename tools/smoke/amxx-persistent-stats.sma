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
#define show_motd(%1,%2,%3) FixtureMotd(%1,%2,%3)
#define nvault_set(%1,%2,%3) FixtureStatsSet(%1,%2,%3)
#define nvault_lookup(%1,%2,%3,%4,%5) FixtureVaultLookup(%1,%2,%3,%4,%5)
#include <goldsrcops_weapon_selection.sma>
#undef plugin_init
#undef is_user_bot
#undef get_user_authid
#undef client_print
#undef show_motd
#undef nvault_set
#undef nvault_lookup

new g_clients[3];
new bool:g_human[MAX_PLAYERS + 1];
new g_auth[MAX_PLAYERS + 1][32];
new g_failures;
new g_prints;
new g_print_target;
new g_last_print[192];
new g_previous_print[192];
new bool:g_fail_readback;
new g_leader_html[1536];
new g_leader_title[64];
new g_motds;
new g_vault_writes;
new g_streak_writes;
new bool:g_fail_streak_readback;
new bool:g_drop_streak_write;
new bool:g_streak_write_seen;
new g_streak_print[192];
new g_streak_print_target;
new const DURABLE_KEY[] = "v1:STEAM_0:1:424242";

public plugin_init()
{
    ProductPluginInit();
    register_srvcmd("goldsrcops_leaderboard_smoke", "RunLeaderboardSmoke");
    register_srvcmd("goldsrcops_standing_smoke", "RunPlayerStandingSmoke");
    register_srvcmd("goldsrcops_saved_streak_smoke", "RunSavedStreakSmoke");
    register_srvcmd("goldsrcops_saved_streak_seed", "SeedSavedStreak");
    register_srvcmd("goldsrcops_saved_streak_verify", "VerifySavedStreak");
    register_srvcmd("goldsrcops_leaderboard_seed", "SeedLeaderboardRestart");
    register_srvcmd("goldsrcops_leaderboard_verify", "VerifyLeaderboardRestart");
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
    if (contain(message, "%L") >= 0 && numargs() >= 5)
    {
        new key[64];
        for (new index = 0; index < charsmax(key); index++)
        {
            key[index] = getarg(4, index);
            if (!key[index]) { break; }
        }
        if (equal(key, "GS_STREAK_SAVED") || equal(key, "GS_STREAK_UNAVAILABLE") || equal(key, "GS_STREAK_RECORD"))
        {
            vformat(g_streak_print, charsmax(g_streak_print), message, 4);
            g_streak_print_target = id;
        }
    }
    g_prints++;
    g_print_target = id;
    copy(g_previous_print, charsmax(g_previous_print), g_last_print);
    vformat(g_last_print, charsmax(g_last_print), message, 4);
    return strlen(g_last_print);
}

FixtureVaultLookup(vault, const key[], value[], maxlen, &timestamp)
{
    if (g_fail_readback || (g_fail_streak_readback && g_streak_write_seen && equal(key, "streak:", 7))) { return 0; }
    return nvault_lookup(vault, key, value, maxlen, timestamp);
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
    g_prints = 0;
    ShowMapStats(player);
    CheckSaved(contain(g_last_print, "Saved totals: K 1 | D 0 | K/D 1.00") >= 0 && g_prints == 3, "saved private response with connection streak");

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
    // Keep this rank-specific test below an existing streak record.
    nvault_set(g_preferences_vault, "streak:v1:STEAM_0:1:424246", "1 100");
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
    CheckSaved(g_prints == 2 && g_print_target == player
        && contain(g_previous_print, "Saved rank: Recruit | K 0 | 25 kills to Fighter.") >= 0,
        "private initial rank from saved totals");
    CheckSaved(!nvault_lookup(g_stats_vault, key, value, charsmax(value), timestamp), "rank query never creates a zero record");
    ShowPlayerRank(player);
    CheckSaved(g_prints == 2, "rank repeat throttled");
    ShowMapStats(player);
    CheckSaved(g_prints == 5, "rank cooldown independent of stats");
    g_stats_next[player][2] = get_gametime();
    ShowPlayerRank(player);
    CheckSaved(g_prints == 7, "rank cooldown expires");

    nvault_set(g_stats_vault, key, "1 24 7");
    client_putinserver(player);
    ShowPlayerRank(player);
    CheckSaved(contain(g_previous_print, "Recruit | K 24 | 1 kills to Fighter.") >= 0
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
    CheckSaved(contain(g_previous_print, "Fighter | K 26 | 74 kills to Veteran.") >= 0,
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
    CheckSaved(contain(g_previous_print, "Legend | K 1000000000 | Highest rank reached.") >= 0,
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


FixtureMotd(id, const html[], const title[])
{
    CheckSaved(id == g_clients[0], "leaderboard MOTD stays private");
    copy(g_leader_html, charsmax(g_leader_html), html);
    copy(g_leader_title, charsmax(g_leader_title), title);
    g_motds++;
    return 1;
}
FixtureStatsSet(vault, const key[], const value[])
{
    g_vault_writes++;
    if (equal(key, "streak:", 7))
    {
        g_streak_writes++;
        g_streak_write_seen = true;
        if (g_drop_streak_write) { return 0; }
    }
    return nvault_set(vault, key, value);
}

CloseLeaderFixture()
{
    if (g_stats_vault != INVALID_HANDLE) { nvault_close(g_stats_vault); g_stats_vault = INVALID_HANDLE; }
    ArrayDestroy(g_leader_records);
    TrieDestroy(g_leader_keys);
}
OpenLeaderFixture()
{
    PrepareLeaderIndex();
    g_stats_vault = nvault_open(STATS_VAULT_NAME);
    LoadLeaderScores();
    g_leader_refresh_after = 0.0;
    RefreshSavedLeaders();
}
LeaderFixturePath(path[], length, const extension[])
{
    new directory[192];
    get_localinfo("amxx_datadir", directory, charsmax(directory));
    formatex(path, length, "%s/vault/%s.%s", directory, STATS_VAULT_NAME, extension);
}
SeedLeaderboardRecords()
{
    nvault_prune(g_stats_vault, 0, 0);
    nvault_prune(g_preferences_vault, 0, 0);
    for (new index = 1; index <= 12; index++)
    {
        new key[40], value[40];
        formatex(key, charsmax(key), "v1:STEAM_0:1:%d", 800000 + index);
        formatex(value, charsmax(value), "1 %d %d", index >= 11 ? 120 : index * 10, index >= 11 ? 2 : index);
        nvault_set(g_stats_vault, key, value);
    }
    nvault_set(g_stats_vault, "v1:STEAM_0:1:800013", "1 0 0");
    nvault_set(g_preferences_vault, "name:v1:STEAM_0:1:800012", "1 <script>&^"%s");
    nvault_set(g_preferences_vault, "name:v1:STEAM_0:1:800010", "1 Кириллица");
    CloseLeaderFixture();
    OpenLeaderFixture();
}
bool:TopKey(rank, const expected[])
{
    if (rank < 0 || rank >= g_leader_count) { return false; }
    new record[LeaderRecord];
    ArrayGetArray(g_leader_records, g_leader_top[rank], record);
    return bool:equal(record[LeaderKey], expected);
}
WriteLeaderInsert(file, const key[], const value[])
{
    fwrite(file, 3, BLOCK_CHAR);
    fwrite(file, get_systime(), BLOCK_INT);
    fwrite(file, strlen(key), BLOCK_CHAR);
    fwrite_blocks(file, key, strlen(key), BLOCK_CHAR);
    fwrite(file, strlen(value), BLOCK_SHORT);
    fwrite_blocks(file, value, strlen(value), BLOCK_CHAR);
}

public RunLeaderboardSmoke()
{
    if (!CreateStatsClients()) { set_fail_state("Leaderboard fixture unavailable."); return PLUGIN_HANDLED; }
    SeedLeaderboardRecords();
    CheckSaved(g_leader_ready && ArraySize(g_leader_records) == 13 && g_leader_count == 10,
        "closed legacy vault imports all disconnected players with a ten-row bound");
    CheckSaved(TopKey(0, "v1:STEAM_0:1:800011") && TopKey(1, "v1:STEAM_0:1:800012")
        && TopKey(9, "v1:STEAM_0:1:800003"), "kills deaths and canonical identity produce deterministic order");
    new player = g_clients[0], victim = g_clients[1], key[48], before[64], after[64], timestamp, before_stamp;
    nvault_lookup(g_stats_vault, "v1:STEAM_0:1:800011", before, charsmax(before), before_stamp);
    new reads = g_leader_reads, rebuilds = g_leader_rebuilds, writes = g_vault_writes;
    for (new language = 0; language < sizeof PLAYER_LANGUAGES; language++)
    {
        g_language_override[player] = language + 1;
        g_stats_next[player][3] = 0.0;
        OnPlayerMenuSelected(player, g_player_menu[language], 7);
        new expected[64];
        formatex(expected, charsmax(expected), "%L", PLAYER_LANGUAGES[language], "GS_TOPALL");
        CheckSaved(equal(g_leader_title, expected) && contain(g_leader_html, "ML_NOTFOUND") < 0,
            "RU and EN menu dispatch opens localized overall leaders");
        CheckSaved(contain(g_leader_html, "#10 ") >= 0 && contain(g_leader_html, "#11 ") < 0
            && contain(g_leader_html, "STEAM_") < 0 && contain(g_leader_html, "<script>") < 0
            && contain(g_leader_html, "&lt;script&gt;&amp;&quot;%s") >= 0
            && contain(g_leader_html, "Кириллица") >= 0, "ten bounded rows escape HTML and hide identities while preserving UTF-8");
        new motds = g_motds;
        ShowSavedLeaders(player);
        CheckSaved(g_motds == motds, "repeated display is throttled");
    }
    CheckSaved(g_vault_writes == writes && g_leader_reads == reads && g_leader_rebuilds == rebuilds,
        "inspection uses the shared cache without writes file reads or repeated sorting");
    nvault_lookup(g_stats_vault, "v1:STEAM_0:1:800011", after, charsmax(after), timestamp);
    CheckSaved(equal(before, after) && before_stamp == timestamp, "index and display preserve legacy value and timestamp");

    copy(g_auth[player], charsmax(g_auth[]), "STEAM_1:1:800011");
    client_putinserver(player);
    CheckSaved(g_saved_ready[player], "existing identity joins without a reset");
    copy(g_auth[victim], charsmax(g_auth[]), "STEAM_0:1:800012");
    client_putinserver(victim);
    rg_set_user_team(player, TEAM_CT);
    rg_set_user_team(victim, TEAM_TERRORIST);
    rg_round_respawn(player);
    rg_round_respawn(victim);
    ExecuteHamB(Ham_TakeDamage, victim, player, player, 1000.0, DMG_BULLET);
    CheckSaved(g_saved_kills[player] == 121 && g_saved_deaths[victim] == 3 && g_leader_dirty,
        "native enemy death updates the verified saved source and invalidates cache");
    RefreshSavedLeaders();
    CheckSaved(g_leader_scores[0][0] == 120, "global refresh interval bounds sorting during bursts");
    g_leader_refresh_after = 0.0;
    RefreshSavedLeaders();
    CheckSaved(g_leader_scores[0][0] == 121 && g_leader_scores[1][1] == 3,
        "next refresh publishes new saved totals");
    new rejected[128];
    client_disconnected(player, false, rejected, charsmax(rejected));
    CheckSaved(TopKey(0, "v1:STEAM_0:1:800011"), "disconnect retains overall standing");
    formatex(key, charsmax(key), "name:%s", "v1:STEAM_0:1:800011");
    CheckSaved(nvault_lookup(g_preferences_vault, key, after, charsmax(after), timestamp)
        && contain(after, "Saved stats fixture") == 2, "join stores bounded name metadata separately");
    new record_count = ArraySize(g_leader_records);
    CheckSaved(AddLeaderKey("v1:STEAM_0:1:800011") && ArraySize(g_leader_records) == record_count
        && !AddLeaderKey("v1:STEAM_1:1:800011"), "canonical index refuses aliases and duplicate rows");

    new motds = g_motds, prints = g_prints;
    g_stats_next[player][3] = 0.0;
    set_pcvar_num(g_enabled, 0);
    ShowSavedLeaders(player);
    set_pcvar_num(g_enabled, 1);
    g_human[player] = false;
    ShowSavedLeaders(player);
    ShowSavedLeaders(0);
    g_human[player] = true;
    CheckSaved(g_motds == motds && g_prints == prints, "disabled bot and invalid callers receive no output");
    rg_set_user_team(player, TEAM_SPECTATOR);
    g_stats_next[player][3] = 0.0;
    ShowSavedLeaders(player);
    CheckSaved(g_motds == motds + 1, "spectators may inspect overall leaders");
    new escaped[64];
    EscapeLeaderName("&&&&&&&&&&&&&&&&&&&&&&&&&&&&&&&", escaped, charsmax(escaped));
    CheckSaved(strlen(escaped) <= 63 && contain(escaped, "...") >= 0 && contain(escaped, "&amp;") == 0,
        "worst-case escaping truncates only complete entities");
    for (new rank = 0; rank < SAVED_LEADER_LIMIT; rank++)
    {
        copy(g_leader_names[rank], charsmax(g_leader_names[]), "&&&&&&&&&&&&&&&&&&&&&&&&&&&&&&&");
        g_leader_scores[rank][0] = STATS_COUNTER_LIMIT;
        g_leader_scores[rank][1] = STATS_COUNTER_LIMIT;
    }
    for (new language = 0; language < sizeof PLAYER_LANGUAGES; language++)
    {
        g_language_override[player] = language + 1;
        g_stats_next[player][3] = 0.0;
        ShowSavedLeaders(player);
        CheckSaved(strlen(g_leader_html) < 1535 && contain(g_leader_html, "</pre></body></html>") >= 0,
            "maximal names counters and personal footer fit RU and EN MOTD including closing markup");
    }

    CloseLeaderFixture();
    new path[256];
    LeaderFixturePath(path, charsmax(path), "journal");
    new file = fopen(path, "wb");
    WriteLeaderInsert(file, "v1:STEAM_0:1:800022", "1 999 1");
    WriteLeaderInsert(file, "v1:STEAM_0:1:800011", "1 1 1");
    fwrite(file, 4, BLOCK_CHAR);
    new removed[] = "v1:STEAM_0:1:800012";
    fwrite(file, strlen(removed), BLOCK_CHAR);
    fwrite_blocks(file, removed, strlen(removed), BLOCK_CHAR);
    fclose(file);
    OpenLeaderFixture();
    CheckSaved(g_leader_ready && TopKey(0, "v1:STEAM_0:1:800022") && g_leader_scores[0][0] == 999
        && !nvault_lookup(g_stats_vault, removed, after, charsmax(after), timestamp),
        "native crash replay includes journal-only insertion replacement and removal");
    CheckSaved(TopKey(1, "v1:STEAM_0:1:800010"), "replayed removal cannot leave a phantom leader");

    // These malformed bytes never enter the real source vault.
    new const malformed[] = "addons/amxmodx/data/leaderboard-fixture-invalid.bin";
    file = fopen(malformed, "wb"); fwrite(file, 0x12345678, BLOCK_INT); fclose(file);
    CheckSaved(!ReadLeaderVault(malformed), "bad vault header rejected");
    file = fopen(malformed, "wb"); fwrite(file, 0x6E564C54, BLOCK_INT); fwrite(file, 0x0200, BLOCK_SHORT);
    fwrite(file, LEADER_INDEX_LIMIT + 1, BLOCK_INT); fclose(file);
    CheckSaved(!ReadLeaderVault(malformed), "oversized entry count rejected before traversal");
    file = fopen(malformed, "wb"); fwrite(file, 3, BLOCK_CHAR); fwrite(file, 1, BLOCK_CHAR); fclose(file);
    CheckSaved(!ReadLeaderJournal(malformed), "truncated journal rejected");
    file = fopen(malformed, "wb"); fwrite(file, 5, BLOCK_CHAR); fclose(file);
    CheckSaved(!ReadLeaderJournal(malformed), "unknown journal operation rejected");
    file = fopen(malformed, "wb"); fwrite(file, 0, BLOCK_CHAR); fwrite(file, 1, BLOCK_CHAR); fclose(file);
    CheckSaved(!ReadLeaderJournal(malformed), "journal bytes after end marker rejected");
    delete_file(malformed);

    nvault_set(g_stats_vault, "v1:STEAM_0:1:800022", "2 999 1");
    CloseLeaderFixture(); OpenLeaderFixture();
    CheckSaved(!g_leader_ready && g_leader_count == 0, "invalid current score hides the entire table");
    g_stats_next[player][3] = 0.0;
    motds = g_motds;
    ShowSavedLeaders(player);
    CheckSaved(g_motds == motds && contain(g_last_print, "Overall leaders are temporarily unavailable") >= 0,
        "unavailable index never presents partial success");
    nvault_lookup(g_stats_vault, "v1:STEAM_0:1:800022", after, charsmax(after), timestamp);
    CheckSaved(bool:equal(after, "2 999 1"), "rejected source record remains unchanged");

    nvault_prune(g_stats_vault, 0, 0);
    CloseLeaderFixture(); OpenLeaderFixture();
    for (new index = 1; index <= LEADER_INDEX_LIMIT; index++)
    {
        formatex(key, charsmax(key), "v1:STEAM_0:1:%d", 900000 + index);
        UpdateLeaderScore(key, index, 1);
    }
    CheckSaved(g_leader_ready && ArraySize(g_leader_records) == LEADER_INDEX_LIMIT, "documented capacity remains usable");
    g_leader_refresh_after = 0.0;
    RefreshSavedLeaders();
    CheckSaved(g_leader_count == 10 && g_leader_scores[0][0] == LEADER_INDEX_LIMIT, "full-capacity in-memory ranking stays bounded");
    copy(g_saved_key[player], charsmax(g_saved_key[]), "v1:STEAM_0:1:900001");
    g_saved_ready[player] = true;
    CheckSaved(SavedPlayerPosition(player) == LEADER_INDEX_LIMIT && g_leader_total == LEADER_INDEX_LIMIT,
        "personal position remains bounded and correct at full capacity");
    UpdateLeaderScore("v1:STEAM_0:1:999999", 1, 1);
    CheckSaved(!g_leader_ready && g_leader_count == 0 && ArraySize(g_leader_records) == LEADER_INDEX_LIMIT,
        "capacity overflow disables ranking without unbounded allocation");
    server_print("PLAYER_LEADERBOARD_SMOKE=%s failures=%d", g_failures ? "failed" : "passed", g_failures);
    return PLUGIN_HANDLED;
}

public SeedLeaderboardRestart()
{
    SeedLeaderboardRecords();
    nvault_set(g_stats_vault, DURABLE_KEY, "1 0 2");
    server_print("PLAYER_LEADERBOARD_SEED=passed failures=0");
    return PLUGIN_HANDLED;
}
public VerifyLeaderboardRestart()
{
    new value[64], stamp;
    CheckSaved(g_leader_ready && ArraySize(g_leader_records) == 14 && g_leader_dirty,
        "fresh process enumerates preexisting statistics without client joins");
    RefreshSavedLeaders();
    CheckSaved(g_leader_count == 10 && TopKey(0, "v1:STEAM_0:1:800011") && TopKey(9, "v1:STEAM_0:1:800003")
        && equal(g_leader_names[1], "<script>&^"%s"), "new process and map preserve all leaders and metadata");
    CheckSaved(nvault_lookup(g_stats_vault, DURABLE_KEY, value, charsmax(value), stamp) && equal(value, "1 0 2"),
        "predecessor-compatible statistics remain intact");
    if (!CreateStatsClients()) { set_fail_state("Standing reload fixture unavailable."); return PLUGIN_HANDLED; }
    JoinStandingPlayer(g_clients[0], 1);
    ShowPlayerRank(g_clients[0]);
    CheckSaved(contain(g_last_print, "Your place: 12 of 13.") >= 0, "fresh process restores personal place including offline death-only player");
    ShowSavedLeaders(g_clients[0]);
    CheckSaved(contain(g_leader_html, "Your place: 12 of 13.") >= 0, "fresh map restores the same personal MOTD footer");
    server_print("PLAYER_LEADERBOARD_RELOAD=%s failures=%d", g_failures ? "failed" : "passed", g_failures);
    return PLUGIN_HANDLED;
}


JoinStandingPlayer(id, number)
{
    new rejected[128];
    client_disconnected(id, false, rejected, charsmax(rejected));
    formatex(g_auth[id], charsmax(g_auth[]), "STEAM_0:1:%d", 800000 + number);
    client_putinserver(id);
}

public RunPlayerStandingSmoke()
{
    if (!CreateStatsClients()) { set_fail_state("Standing fixture unavailable."); return PLUGIN_HANDLED; }
    SeedLeaderboardRecords();
    new player = g_clients[0], rejected[128], value[64], before_stamp, after_stamp;
    JoinStandingPlayer(player, 1);
    CheckSaved(g_leader_total == 12, "denominator includes offline scores and excludes zero-zero records");
    nvault_lookup(g_stats_vault, "v1:STEAM_0:1:800001", value, charsmax(value), before_stamp);
    new writes = g_vault_writes, reads = g_leader_reads, rebuilds = g_leader_rebuilds;
    for (new language = 0; language < sizeof PLAYER_LANGUAGES; language++)
    {
        new expected[160];
        g_language_override[player] = language + 1;
        formatex(expected, charsmax(expected), "%L", PLAYER_LANGUAGES[language], "GS_STANDING_POSITION", 12, 12);
        g_stats_next[player][2] = 0.0;
        new prints = g_prints;
        ShowPlayerRank(player);
        CheckSaved(g_prints == prints + 2 && g_print_target == player && contain(g_last_print, expected) >= 0,
            "RU and EN rank adds exactly one private personal-place line");
        ShowPlayerRank(player);
        CheckSaved(g_prints == prints + 2, "existing rank cooldown covers progress and standing together");
        g_stats_next[player][3] = 0.0;
        ShowSavedLeaders(player);
        CheckSaved(contain(g_leader_html, expected) >= 0 && contain(g_leader_html, "#10 ") >= 0
            && contain(g_leader_html, "#11 ") < 0 && contain(g_leader_html, "STEAM_") < 0,
            "outside-top-ten player gets a private footer without an extra public row or identity");
    }
    nvault_lookup(g_stats_vault, "v1:STEAM_0:1:800001", value, charsmax(value), after_stamp);
    CheckSaved(g_vault_writes == writes && g_leader_reads == reads && g_leader_rebuilds == rebuilds
        && equal(value, "1 10 1") && before_stamp == after_stamp,
        "rank and MOTD preserve source values timestamps and cache without storage writes or file reads");

    JoinStandingPlayer(player, 11);
    ShowPlayerRank(player);
    CheckSaved(contain(g_last_print, "Your place: 1 of 12.") >= 0, "exact tie follows the canonical key order");
    ShowSavedLeaders(player);
    CheckSaved(contain(g_leader_html, "Your place:") < 0, "top-ten player does not get a duplicate personal footer");
    g_saved_deaths[player] = 3;
    SavePlayerStats(player);
    g_stats_next[player][2] = 0.0;
    ShowPlayerRank(player);
    CheckSaved(contain(g_last_print, "Your place: 1 of 12.") >= 0 && TopKey(0, "v1:STEAM_0:1:800011"),
        "personal place and top ten stay on the same snapshot during the refresh window");
    g_leader_refresh_after = 0.0;
    RefreshSavedLeaders();
    g_stats_next[player][2] = 0.0;
    ShowPlayerRank(player);
    CheckSaved(contain(g_last_print, "Your place: 2 of 12.") >= 0 && TopKey(0, "v1:STEAM_0:1:800012"),
        "fewer deaths wins and both views advance together after refresh");
    JoinStandingPlayer(player, 11);
    ShowPlayerRank(player);
    CheckSaved(contain(g_last_print, "Your place: 2 of 12.") >= 0 && g_saved_deaths[player] == 3,
        "reconnect retains saved standing and resets only command cooldown");

    JoinStandingPlayer(player, 14);
    ShowPlayerRank(player);
    CheckSaved(contain(g_last_print, "after your first saved kill or death") >= 0 && g_leader_total == 12,
        "new identity is unranked and does not inflate denominator");
    ShowSavedLeaders(player);
    CheckSaved(contain(g_leader_html, "after your first saved kill or death") >= 0, "MOTD explains absent personal results");
    RecordMapDeath(0, player);
    g_stats_next[player][2] = 0.0;
    ShowPlayerRank(player);
    CheckSaved(contain(g_last_print, "when the leaderboard refreshes") >= 0 && g_leader_total == 12,
        "first score within the cache window is pending rather than a fabricated position");
    g_stats_next[player][3] = 0.0;
    ShowSavedLeaders(player);
    CheckSaved(contain(g_leader_html, "when the leaderboard refreshes") >= 0, "MOTD shares the first-score pending state");
    g_leader_refresh_after = 0.0;
    RefreshSavedLeaders();
    g_stats_next[player][2] = 0.0;
    ShowPlayerRank(player);
    CheckSaved(contain(g_last_print, "Your place: 13 of 13.") >= 0, "death-only result joins the shared denominator after refresh");
    rg_set_user_team(player, TEAM_SPECTATOR);
    g_stats_next[player][2] = 0.0;
    ShowPlayerRank(player);
    CheckSaved(contain(g_last_print, "Your place: 13 of 13.") >= 0, "spectator keeps personal standing");

    new prints = g_prints, motds = g_motds;
    g_stats_next[player][2] = 0.0; g_stats_next[player][3] = 0.0;
    set_pcvar_num(g_enabled, 0);
    ShowPlayerRank(player); ShowSavedLeaders(player);
    set_pcvar_num(g_enabled, 1);
    g_human[player] = false;
    ShowPlayerRank(player); ShowSavedLeaders(player);
    ShowPlayerRank(0); ShowSavedLeaders(0);
    g_human[player] = true;
    CheckSaved(g_prints == prints && g_motds == motds, "disabled bot and invalid callers receive neither standing surface");

    client_disconnected(player, false, rejected, charsmax(rejected));
    copy(g_auth[player], charsmax(g_auth[]), "STEAM_ID_PENDING");
    client_putinserver(player);
    ShowPlayerRank(player);
    CheckSaved(contain(g_last_print, "Saved rank unavailable") >= 0, "pending identity never reuses the previous occupant position");
    ShowSavedLeaders(player);
    CheckSaved(contain(g_leader_html, "Your overall place is temporarily unavailable") >= 0,
        "leaderboard remains readable while personal identity is unavailable");
    JoinStandingPlayer(player, 14);
    g_leader_ready = false;
    ShowPlayerRank(player);
    CheckSaved(contain(g_previous_print, "Saved rank: Recruit") >= 0
        && contain(g_last_print, "Your overall place is temporarily unavailable") >= 0,
        "unavailable index keeps the saved rank tier without a stale personal position");
    g_leader_ready = true;
    nvault_prune(g_stats_vault, 0, 0);
    CloseLeaderFixture(); OpenLeaderFixture();
    JoinStandingPlayer(player, 14);
    motds = g_motds;
    ShowSavedLeaders(player);
    CheckSaved(g_motds == motds && contain(g_previous_print, "No saved human scores yet") >= 0
        && contain(g_last_print, "after your first saved kill or death") >= 0 && g_leader_total == 0,
        "empty table explains the first-score requirement without a zero-of-zero position");
    server_print("PLAYER_STANDING_SMOKE=%s failures=%d languages=2 snapshot=shared storage_writes=none",
        g_failures ? "failed" : "passed", g_failures);
    return PLUGIN_HANDLED;
}

new const STREAK_TEST_KEY[] = "streak:v1:STEAM_0:1:910001";

StreakEnemyKill(player, victim)
{
    rg_set_user_team(victim, TEAM_TERRORIST);
    rg_round_respawn(victim);
    ExecuteHamB(Ham_TakeDamage, victim, player, player, 1000.0, DMG_BULLET);
}

public RunSavedStreakSmoke()
{
    if (!CreateStatsClients() || g_preferences_vault == INVALID_HANDLE)
    {
        set_fail_state("Saved streak fixture unavailable.");
        return PLUGIN_HANDLED;
    }
    new best, value[96], stamp, expected[192];
    CheckSaved(DecodeSavedStreak("1 0", best) && !best, "zero record decodes without a default write");
    CheckSaved(DecodeSavedStreak("1 1000000000", best) && best == STATS_COUNTER_LIMIT, "record upper bound");
    new const invalid[][] = { "", "2 7", "1 -1", "1 x", "1 7 8", "1 1000000001", "1 999999999999",
        "1 01", "1 7 ", " 1 7", "^"1^" 7", "1 7x" };
    for (new index = 0; index < sizeof invalid; index++)
    {
        CheckSaved(!DecodeSavedStreak(invalid[index], best), "noncanonical record rejected");
    }
    new player = g_clients[0], victim = g_clients[1], duplicate = g_clients[2];
    nvault_remove(g_preferences_vault, STREAK_TEST_KEY);
    g_streak_print[0] = 0;
    RecordKillStreak(player);
    CheckSaved(g_kill_streak[player] == 1 && !g_streak_ready[player] && !g_streak_writes,
        "unknown identity cannot create a record");
    copy(g_auth[player], charsmax(g_auth[]), "STEAM_0:1:910001");
    nvault_set(g_stats_vault, "v1:STEAM_0:1:910001", "1 7 3");
    nvault_touch(g_stats_vault, "v1:STEAM_0:1:910001", 123456789);
    nvault_set(g_preferences_vault, "v1:STEAM_0:1:910001", "1 2 1 2");
    nvault_touch(g_preferences_vault, "v1:STEAM_0:1:910001", 123456789);
    nvault_set(g_preferences_vault, "hud:v1:STEAM_0:1:910001", "1 1");
    nvault_touch(g_preferences_vault, "hud:v1:STEAM_0:1:910001", 123456789);
    client_authorized(player, g_auth[player]);
    CheckSaved(g_streak_ready[player] && !g_saved_streak[player] && !g_record_streak[player] && !g_streak_writes,
        "late identity loads empty record without importing unknown streak");
    new writes = g_vault_writes, prints = g_prints;
    ShowMapStats(player);
    CheckSaved(g_vault_writes == writes && g_prints == prints + 3 && g_streak_print_target == player
        && contain(g_streak_print, "Saved best streak: 0") >= 0,
        "three private stats lines are read-only including absent saved record");
    ShowMapStats(player);
    CheckSaved(g_prints == prints + 3, "existing stats cooldown covers record line");
    StreakEnemyKill(player, victim);
    CheckSaved(g_saved_streak[player] == 1 && g_record_streak[player] == 1 && g_kill_streak[player] == 2
        && nvault_lookup(g_preferences_vault, STREAK_TEST_KEY, value, charsmax(value), stamp) && equal(value, "1 1"),
        "native kill persists only the authorized part of a life");
    CheckSaved(contain(g_streak_print, "New saved streak record: 1!") >= 0 && g_streak_print_target == player,
        "native saved record notification stays private");
    CheckSaved(nvault_lookup(g_preferences_vault, "v1:STEAM_0:1:910001", value, charsmax(value), stamp)
        && equal(value, "1 2 1 2") && stamp == 123456789, "record does not change legacy preference value or timestamp");
    CheckSaved(nvault_lookup(g_preferences_vault, "hud:v1:STEAM_0:1:910001", value, charsmax(value), stamp)
        && equal(value, "1 1") && stamp == 123456789, "record does not change HUD value or timestamp");

    for (new language = 0; language < sizeof PLAYER_LANGUAGES; language++)
    {
        nvault_set(g_preferences_vault, STREAK_TEST_KEY, "1 2");
        client_putinserver(player);
        rg_round_respawn(player);
        g_language_override[player] = language + 1;
        new before = g_streak_writes;
        prints = g_prints;
        StreakEnemyKill(player, victim);
        StreakEnemyKill(player, victim);
        CheckSaved(g_streak_writes == before && g_prints == prints && g_saved_streak[player] == 2,
            "lower and equal streaks neither rewrite nor announce record");
        StreakEnemyKill(player, victim);
        formatex(expected, charsmax(expected), "[GoldSrcOps] %L", PLAYER_LANGUAGES[language], "GS_STREAK_RECORD", 3);
        CheckSaved(g_streak_writes == before + 1 && g_prints == prints + 1
            && equal(g_last_print, expected) && g_print_target == player,
            "RU EN new record replaces coincident milestone with one confirmed message");
        StreakEnemyKill(player, victim);
        CheckSaved(g_saved_streak[player] == 4 && g_streak_writes == before + 2, "record increases monotonically within life");
        g_stats_next[player][0] = 0.0;
        ShowMapStats(player);
        formatex(expected, charsmax(expected), "[GoldSrcOps] %L", PLAYER_LANGUAGES[language], "GS_STREAK_SAVED", 4);
        CheckSaved(bool:equal(g_streak_print, expected), "RU EN stats distinguishes saved record from current and map best");
        RecordMapDeath(0, player);
        CheckSaved(!g_record_streak[player] && !g_kill_streak[player] && g_saved_streak[player] == 4,
            "world death ends both life counters and retains saved record");
    }
    new rejected[128];
    client_disconnected(player, false, rejected, charsmax(rejected));
    client_putinserver(player);
    rg_round_respawn(player);
    CheckSaved(g_saved_streak[player] == 4 && !g_record_streak[player] && !g_best_streak[player],
        "reconnect restores only the saved record");
    copy(g_auth[duplicate], charsmax(g_auth[]), "STEAM_1:1:910001");
    client_putinserver(duplicate);
    rg_round_respawn(duplicate);
    writes = g_streak_writes;
    RecordKillStreak(duplicate);
    CheckSaved(!g_streak_ready[duplicate] && g_streak_writes == writes, "duplicate connected identity cannot save record");
    client_disconnected(player, false, rejected, charsmax(rejected));
    EnsureSavedStreak(duplicate);
    CheckSaved(g_streak_ready[duplicate] && g_saved_streak[duplicate] == 4 && !g_record_streak[duplicate],
        "duplicate acquires record only after owner leaves without importing its previous life");
    client_disconnected(duplicate, false, rejected, charsmax(rejected));
    copy(g_auth[duplicate], charsmax(g_auth[]), "STEAM_ID_PENDING");
    client_putinserver(player);
    rg_round_respawn(player);

    writes = g_streak_writes;
    rg_set_user_team(victim, TEAM_CT);
    RecordMapDeath(player, victim);
    rg_set_user_team(victim, TEAM_TERRORIST);
    g_human[victim] = false;
    RecordMapDeath(player, victim);
    g_human[victim] = true;
    rg_set_user_team(player, TEAM_SPECTATOR);
    RecordMapDeath(player, victim);
    rg_set_user_team(player, TEAM_CT);
    set_pcvar_num(g_enabled, 0);
    RecordMapDeath(player, victim);
    set_pcvar_num(g_enabled, 1);
    CheckSaved(!g_record_streak[player] && g_streak_writes == writes, "team bot spectator and disabled kills cannot advance saved streak");
    for (new kind = 0; kind < 4; kind++)
    {
        g_record_streak[player] = 3;
        if (kind == 0) { RecordMapDeath(player, player); }
        if (kind == 1) { g_human[victim] = false; RecordMapDeath(victim, player); g_human[victim] = true; }
        if (kind == 2) { rg_set_user_team(victim, TEAM_CT); RecordMapDeath(victim, player); rg_set_user_team(victim, TEAM_TERRORIST); }
        if (kind == 3) { set_pcvar_num(g_enabled, 0); RecordMapDeath(victim, player); set_pcvar_num(g_enabled, 1); }
        CheckSaved(!g_record_streak[player] && g_saved_streak[player] == 4, "excluded deaths clear record life counter without lowering saved best");
    }
    ExecuteHamB(Ham_TakeDamage, player, victim, victim, 1000.0, DMG_BULLET);
    RecordMapDeath(player, victim);
    CheckSaved(!g_record_streak[player] && g_streak_writes == writes, "posthumous kill cannot advance saved record");
    rg_round_respawn(player);

    nvault_set(g_preferences_vault, STREAK_TEST_KEY, "2 99");
    client_putinserver(player);
    writes = g_streak_writes;
    RecordKillStreak(player);
    CheckSaved(g_streak_blocked[player] && !g_streak_ready[player] && g_streak_writes == writes
        && nvault_lookup(g_preferences_vault, STREAK_TEST_KEY, value, charsmax(value), stamp) && equal(value, "2 99"),
        "corrupt record remains intact and blocks writes");
    g_stats_next[player][0] = 0.0;
    ShowMapStats(player);
    CheckSaved(contain(g_streak_print, "unavailable") >= 0 && g_saved_ready[player] && g_preferences_ready[player],
        "record failure is explicit and leaves saved stats and preferences available");
    g_preferences_dirty[player] = PREFERENCE_PRIMARY;
    SavePlayerPreferences(player);
    CheckSaved(g_preferences_ready[player] && !g_preferences_dirty[player], "record-only failure does not block preference write");
    nvault_set(g_preferences_vault, STREAK_TEST_KEY, "1 4");
    nvault_set(g_preferences_vault, "v1:STEAM_0:1:910001", "2 1 1 1");
    client_putinserver(player);
    RecordKillStreak(player);
    CheckSaved(!g_streak_ready[player] && g_streak_writes == writes, "blocked preference identity cannot be bypassed");
    nvault_set(g_preferences_vault, "v1:STEAM_0:1:910001", "1 2 1 2");

    for (new failure = 0; failure < 2; failure++)
    {
        nvault_set(g_preferences_vault, STREAK_TEST_KEY, "1 4");
        client_putinserver(player);
        g_record_streak[player] = 4;
        g_streak_write_seen = false;
        g_fail_streak_readback = failure == 0;
        g_drop_streak_write = failure == 1;
        prints = g_prints;
        writes = g_streak_writes;
        RecordKillStreak(player);
        g_fail_streak_readback = false;
        g_drop_streak_write = false;
        CheckSaved(g_streak_blocked[player] && !g_streak_ready[player] && g_saved_streak[player] == 4
            && g_streak_writes == writes + 1 && g_prints == prints, "missing or mismatched readback never announces saved success");
        RecordKillStreak(player);
        EnsureSavedStreak(player);
        CheckSaved(g_streak_writes == writes + 1, "uncertain write is never automatically retried");
        client_putinserver(player);
        CheckSaved(g_streak_ready[player] && g_saved_streak[player] == (failure == 0 ? 5 : 4),
            "reconnect reconciles actual vault state without replay");
    }
    new vault = g_preferences_vault;
    g_preferences_vault = INVALID_HANDLE;
    client_putinserver(player);
    writes = g_streak_writes;
    RecordKillStreak(player);
    CheckSaved(!g_streak_ready[player] && g_kill_streak[player] == 1 && g_streak_writes == writes,
        "unavailable vault retains local streak without persistence");
    g_preferences_vault = vault;
    nvault_set(g_preferences_vault, STREAK_TEST_KEY, "1 1000000000");
    client_putinserver(player);
    g_record_streak[player] = STATS_COUNTER_LIMIT;
    writes = g_streak_writes;
    RecordKillStreak(player);
    CheckSaved(g_record_streak[player] == STATS_COUNTER_LIMIT && g_saved_streak[player] == STATS_COUNTER_LIMIT
        && g_streak_writes == writes, "saturation neither wraps nor rewrites saved record");
    g_human[player] = false;
    g_stats_next[player][0] = 0.0;
    prints = g_prints;
    ShowMapStats(player);
    ShowMapStats(0);
    g_human[player] = true;
    set_pcvar_num(g_enabled, 0);
    ShowMapStats(player);
    set_pcvar_num(g_enabled, 1);
    CheckSaved(g_prints == prints, "record display honors invalid bot and disabled gates");
    OnMapStatsStatus();
    server_print("PLAYER_SAVED_STREAK_SMOKE=%s failures=%d languages=2 storage=real_nvault native_damage=1",
        g_failures ? "failed" : "passed", g_failures);
    return PLUGIN_HANDLED;
}

public SeedSavedStreak()
{
    SeedLeaderboardRestart();
    if (!CreateStatsClients()) { set_fail_state("Record seed fixture unavailable."); return PLUGIN_HANDLED; }
    nvault_set(g_preferences_vault, "v1:STEAM_0:1:800001", "1 2 1 2");
    nvault_touch(g_preferences_vault, "v1:STEAM_0:1:800001", 123456789);
    nvault_set(g_preferences_vault, "hud:v1:STEAM_0:1:800001", "1 1");
    nvault_touch(g_preferences_vault, "hud:v1:STEAM_0:1:800001", 123456789);
    JoinStandingPlayer(g_clients[0], 1);
    rg_round_respawn(g_clients[0]);
    for (new count = 0; count < 7; count++) { RecordKillStreak(g_clients[0]); }
    CheckSaved(g_saved_streak[g_clients[0]] == 7 && g_streak_writes == 7, "seed saved record through real vault writes");
    server_print("PLAYER_SAVED_STREAK_SEED=%s failures=%d", g_failures ? "failed" : "passed", g_failures);
    return PLUGIN_HANDLED;
}

public VerifySavedStreak()
{
    VerifyLeaderboardRestart();
    new player = g_clients[0], value[64], stamp;
    CheckSaved(g_streak_ready[player] && g_saved_streak[player] == 7 && !g_record_streak[player]
        && !g_kill_streak[player] && !g_best_streak[player] && !g_streak_writes,
        "fresh process and different map restore saved record without importing active streak");
    CheckSaved(nvault_lookup(g_preferences_vault, "v1:STEAM_0:1:800001", value, charsmax(value), stamp)
        && equal(value, "1 2 1 2") && stamp == 123456789 && g_hud_disabled[player],
        "record recovery retains old preferences and HUD");
    ShowMapStats(player);
    CheckSaved(contain(g_streak_print, "Saved best streak: 7") >= 0, "restored record is visible in stats");
    server_print("PLAYER_SAVED_STREAK_RELOAD=%s failures=%d", g_failures ? "failed" : "passed", g_failures);
    return PLUGIN_HANDLED;
}
