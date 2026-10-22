#include <amxmodx>
#include <reapi>
#include <fakemeta>
#include <hamsandwich>
#include <nvault>

// Identity/output substitutions are fixture-only; storage uses the real nVault.
#define plugin_init ProductPluginInit
#define is_user_bot(%1) FixtureIsBot(%1)
#define is_user_hltv(%1) FixtureIsHltv(%1)
#define get_user_authid(%1,%2,%3) FixtureAuthId(%1,%2,%3)
#define client_print FixturePrint
#define client_print_color FixtureColorPrint
#define show_motd(%1,%2,%3) FixtureMotd(%1,%2,%3)
#define nvault_set(%1,%2,%3) FixtureStatsSet(%1,%2,%3)
#define nvault_lookup(%1,%2,%3,%4,%5) FixtureVaultLookup(%1,%2,%3,%4,%5)
#define server_print FixtureTransportPrint
#include <goldsrcops_weapon_selection.sma>
#undef server_print
#undef plugin_init
#undef is_user_bot
#undef is_user_hltv
#undef get_user_authid
#undef client_print
#undef client_print_color
#undef show_motd
#undef nvault_set
#undef nvault_lookup

new g_clients[3];
new bool:g_human[MAX_PLAYERS + 1];
new bool:g_hltv[MAX_PLAYERS + 1];
new g_auth[MAX_PLAYERS + 1][32];
new g_failures;
new g_prints;
new g_color_text[192];
new g_print_target;
new g_last_print[192];
new g_previous_print[192];
new bool:g_fail_readback;
new g_leader_html[1536];
new g_leader_title[64];
new g_motds;
new g_motd_target;
new g_expected_motd_target;
new g_vault_writes;
new g_vault_reads;
new bool:g_lookup_quiet;
new g_lookup_frame[2048];
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
    register_srvcmd("goldsrcops_player_lookup_smoke", "RunPlayerLookupSmoke");
    register_srvcmd("goldsrcops_player_lookup_load", "RunPlayerLookupLoad");
    register_srvcmd("goldsrcops_leaderboard_smoke", "RunLeaderboardSmoke");
    register_srvcmd("goldsrcops_public_leaderboard_smoke", "RunPublicLeaderboardSmoke");
    register_srvcmd("goldsrcops_standing_smoke", "RunPlayerStandingSmoke");
    register_srvcmd("goldsrcops_profile_smoke", "RunPlayerProfileSmoke");
    register_srvcmd("goldsrcops_kd_ratio_smoke", "RunKillDeathRatioSmoke");
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

bool:FixtureIsHltv(id) { return g_hltv[id] || is_user_hltv(id); }

FixtureAuthId(id, buffer[], length)
{
    return copy(buffer, length, g_auth[id]);
}

FixtureColorPrint(id, sender, const message[], any:...)
{
    #pragma unused sender
    new colored[512], plain[192], written;
    vformat(colored, charsmax(colored), message, 4);
    CheckSaved(strlen(colored) <= 190 && contain(colored, "ML_NOTFOUND") < 0, "colored transport respects chat byte budget");
    copy(g_color_text, charsmax(g_color_text), colored);
    for (new index = 0; colored[index] && written < charsmax(plain); index++)
    {
        if (colored[index] != 1 && colored[index] != 4) { plain[written++] = colored[index]; }
    }
    plain[written] = 0;
    return FixturePrint(id, print_chat, "%s", plain);
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
    new complete[512];
    vformat(complete, charsmax(complete), message, 4);
    CheckSaved(strlen(complete) <= 190 && contain(complete, "ML_NOTFOUND") < 0,
        "complete localized chat fits the transport before truncation");
    copy(g_last_print, charsmax(g_last_print), complete);
    return strlen(g_last_print);
}

FixtureVaultLookup(vault, const key[], value[], maxlen, &timestamp)
{
    g_vault_reads++;
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
    CheckSaved(id == (g_expected_motd_target ? g_expected_motd_target : g_clients[0]), "player MOTD stays private");
    CheckSaved(strlen(title) > 0 && strlen(title) <= 31, "MOTD caption fits the client byte budget");
    g_motd_target = id;
    copy(g_leader_html, charsmax(g_leader_html), html);
    copy(g_leader_title, charsmax(g_leader_title), title);
    CheckSaved(contain(html, "background:#18212b") >= 0 && contain(html, "font:18px Arial") >= 0
        && contain(html, "</body></html>") == strlen(html) - 14,
        "all MOTD surfaces share a readable complete bounded shell");
    CheckSaved(contain(html, "^4") < 0 && contain(html, "^1") < 0, "chat colors never enter HTML");
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

RunLeaderboardRankChecks(player)
{
    new const kills[] = { 1000000000, 500, 499, 250, 249, 100, 99, 25, 24, 0 };
    new const rank_keys[][] = { "GS_LEGEND", "GS_LEGEND", "GS_ELITE", "GS_ELITE", "GS_VETERAN",
        "GS_VETERAN", "GS_FIGHTER", "GS_FIGHTER", "GS_RECRUIT", "GS_RECRUIT" };
    new names[SAVED_LEADER_LIMIT][32], scores[SAVED_LEADER_LIMIT][2];
    for (new row = 0; row < SAVED_LEADER_LIMIT; row++)
    {
        copy(names[row], charsmax(names[]), g_leader_names[row]);
        scores[row][0] = g_leader_scores[row][0]; scores[row][1] = g_leader_scores[row][1];
        formatex(g_leader_names[row], charsmax(g_leader_names[]), "Rank fixture %d", row + 1);
        g_leader_scores[row][0] = kills[row]; g_leader_scores[row][1] = row + 1;
    }
    for (new language = 0; language < sizeof PLAYER_LANGUAGES; language++)
    {
        g_language_override[player] = language + 1;
        g_stats_next[player][3] = 0.0;
        ShowSavedLeaders(player);
        CheckSaved(FindProfileUtf8(g_leader_html, language == 0 ? "<th>Ранг<th>K<th>D" : "<th>Rank<th>K<th>D") >= 0,
            "overall leaders localize the rank column without changing score columns");
        for (new row = 0; row < sizeof kills; row++)
        {
            new expected[160];
            formatex(expected, charsmax(expected), "<tr><td>%d<td>Rank fixture %d<td>%L<td>%d<td>%d",
                row + 1, row + 1, PLAYER_LANGUAGES[language], rank_keys[row], kills[row], row + 1);
            CheckSaved(FindProfileUtf8(g_leader_html, expected) >= 0,
                "each displayed score maps to the expected rank at both sides of every threshold");
        }
    }
    for (new row = 0; row < SAVED_LEADER_LIMIT; row++)
    {
        copy(g_leader_names[row], charsmax(g_leader_names[]), names[row]);
        g_leader_scores[row][0] = scores[row][0]; g_leader_scores[row][1] = scores[row][1];
    }
    // Live progress may cross a tier while the displayed score snapshot is still cached.
    new record[LeaderRecord], bool:dirty = g_leader_dirty, Float:refresh_after = g_leader_refresh_after;
    ArrayGetArray(g_leader_records, g_leader_top[0], record);
    UpdateLeaderScore(record[LeaderKey], 500, record[LeaderDeaths]);
    g_leader_refresh_after = get_gametime() + LEADER_REFRESH_INTERVAL;
    for (new language = 0; language < sizeof PLAYER_LANGUAGES; language++)
    {
        g_language_override[player] = language + 1;
        g_stats_next[player][3] = 0.0;
        ShowSavedLeaders(player);
        new expected[160];
        formatex(expected, charsmax(expected), "<tr><td>1<td>%L<td>%L<td>120<td>2",
            PLAYER_LANGUAGES[language], "GS_ALL_UNKNOWN", PLAYER_LANGUAGES[language], "GS_VETERAN");
        CheckSaved(FindProfileUtf8(g_leader_html, expected) >= 0,
            "rank and kills remain on the same cached snapshot after a live tier change");
    }
    ArraySetArray(g_leader_records, g_leader_top[0], record);
    g_leader_dirty = dirty; g_leader_refresh_after = refresh_after;
    server_print("PLAYER_LEADERBOARD_RANK_SMOKE=%s failures=%d languages=2 boundaries=10",
        g_failures ? "failed" : "passed", g_failures);
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
        formatex(expected, charsmax(expected), "%L", PLAYER_LANGUAGES[language], "GS_TOPALL_TITLE");
        CheckSaved(equal(g_leader_title, expected) && contain(g_leader_html, "ML_NOTFOUND") < 0,
            "RU and EN menu dispatch opens localized overall leaders");
        CheckSaved(FindProfileUtf8(g_leader_title, language == 0 ? "Общий рейтинг" : "Overall leaders") == 0
            && strlen(g_leader_title) == (language == 0 ? 25 : 15),
            "overall caption preserves every RU/EN UTF-8 byte");
        formatex(expected, charsmax(expected), "<h2>%L</h2>", PLAYER_LANGUAGES[language], "GS_TOPALL");
        CheckSaved(FindProfileUtf8(g_leader_html, expected) >= 0, "overall leaders keep the full inner heading");
        CheckSaved(contain(g_leader_html, "<tr><td>10<td>") >= 0 && contain(g_leader_html, "<tr><td>11<td>") < 0
            && contain(g_leader_html, "STEAM_") < 0 && contain(g_leader_html, "<script>") < 0
            && contain(g_leader_html, "&lt;script&gt;&amp;&quot;%s") >= 0
            && contain(g_leader_html, "Кириллица") >= 0, "ten bounded rows escape HTML and hide identities while preserving UTF-8");
        new motds = g_motds;
        ShowSavedLeaders(player);
        CheckSaved(g_motds == motds, "repeated display is throttled");
    }
    RunLeaderboardRankChecks(player);
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
    new const worst_names[][] = {
        "&&&&&&&&&&&&&&&&&&&&&&&&&&&&&&&", "ABCDEFGHIJKLMNOPQRSTUVWXYZabcde", "ЖЖЖЖЖЖЖЖЖЖЖЖЖЖЖX"
    };
    new bool:saved_ready = g_saved_ready[player], saved_key[40];
    new saved_kills = g_saved_kills[player], saved_deaths = g_saved_deaths[player];
    copy(saved_key, charsmax(saved_key), g_saved_key[player]);
    g_saved_ready[player] = true;
    copy(g_saved_key[player], charsmax(g_saved_key[]), "v1:STEAM_0:1:800013");
    g_saved_kills[player] = 0; g_saved_deaths[player] = 0;
    for (new sample = 0; sample < sizeof worst_names; sample++)
    {
        for (new rank = 0; rank < SAVED_LEADER_LIMIT; rank++)
        {
            copy(g_leader_names[rank], charsmax(g_leader_names[]), worst_names[sample]);
            g_leader_scores[rank][0] = STATS_COUNTER_LIMIT;
            g_leader_scores[rank][1] = STATS_COUNTER_LIMIT;
        }
        for (new language = 0; language < sizeof PLAYER_LANGUAGES; language++)
        {
            g_language_override[player] = language + 1;
            g_stats_next[player][3] = 0.0;
            ShowSavedLeaders(player);
            new expected_footer[160];
            formatex(expected_footer, charsmax(expected_footer), "%L", PLAYER_LANGUAGES[language], "GS_STANDING_UNRANKED");
            CheckSaved(strlen(g_leader_html) < 1535 && contain(g_leader_html, "</body></html>") >= 0
                && contain(g_leader_html, "<tr><td>10<td>") >= 0
                && FindProfileUtf8(g_leader_html, expected_footer) >= 0,
                "ten maximum ranked rows and longest footer fit RU and EN with complete closing markup");
            if (sample)
            {
                CheckSaved(FindProfileUtf8(g_leader_html, worst_names[sample]) >= 0,
                    "maximum plain ASCII and UTF-8 names remain complete in the ranked table");
            }
            else
            {
                CheckSaved(contain(g_leader_html, "<td>&amp;&amp;&amp;&amp;&amp;&amp;&amp;&amp;...<td>") >= 0,
                    "expanded HTML names end on complete entities with an explicit ellipsis");
                server_print("PLAYER_LEADERBOARD_BUDGET language=%s bytes=%d limit=1535 rows=10",
                    PLAYER_LANGUAGES[language], strlen(g_leader_html));
            }
        }
    }
    g_saved_ready[player] = saved_ready;
    copy(g_saved_key[player], charsmax(g_saved_key[]), saved_key);
    g_saved_kills[player] = saved_kills; g_saved_deaths[player] = saved_deaths;

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
        CheckSaved(contain(g_leader_html, expected) >= 0 && contain(g_leader_html, "<tr><td>10<td>") >= 0
            && contain(g_leader_html, "<tr><td>11<td>") < 0 && contain(g_leader_html, "STEAM_") < 0,
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
    new scalar_name[32];
    CheckSaved(DecodeSearchQuery("F09F9880", scalar_name) && strlen(scalar_name) == 4, "valid four-byte UTF-8 scalar accepted");
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
    g_map_kills[g_clients[0]] = 7;
    g_map_deaths[g_clients[0]] = 2;
    ShowPlayerProfile(g_clients[0]);
    CheckSaved(contain(g_leader_html, "Score: K 7 | D 2 | K/D 3.50") >= 0,
        "seed a nonzero connection score before process and map replacement");
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
    new writes = g_vault_writes;
    ShowPlayerProfile(player);
    CheckSaved(contain(g_leader_html, "Saved best streak: 7") >= 0 && contain(g_leader_html, "Your place: 12 of 13.") >= 0
        && contain(g_leader_html, "Streak: 0 | Best: 0") >= 0 && g_vault_writes == writes,
        "fresh process profile restores saved record and standing with new connection counters and no writes");
    for (new language = 0; language < sizeof PLAYER_LANGUAGES; language++)
    {
        g_language_override[player] = language + 1;
        g_stats_next[player][4] = 0.0;
        ShowPlayerProfile(player);
        CheckProfileScore(language, "K 0 | D 0 | K/D 0.00");
    }
    CheckSaved(g_vault_writes == writes, "map replacement profile projection remains read only");
    server_print("PLAYER_PROFILE_RELOAD=%s failures=%d", g_failures ? "failed" : "passed", g_failures);
    server_print("PLAYER_SAVED_STREAK_RELOAD=%s failures=%d", g_failures ? "failed" : "passed", g_failures);
    return PLUGIN_HANDLED;
}


// Native dictionary output and source literals may sign-extend UTF-8 bytes differently.
FindProfileUtf8(const text[], const expected[])
{
    for (new start = 0; text[start]; start++)
    {
        new offset;
        while (expected[offset] && text[start + offset]
            && (text[start + offset] & 0xff) == (expected[offset] & 0xff)) { offset++; }
        if (!expected[offset]) { return start; }
    }
    return -1;
}

CheckProfileScore(language, const score[])
{
    new heading[128], expected[192];
    formatex(heading, charsmax(heading), "<h2>%L</h2>", PLAYER_LANGUAGES[language], "GS_PROFILE_SESSION");
    new start = FindProfileUtf8(g_leader_html, heading);
    formatex(expected, charsmax(expected), "%s%s", language == 0 ? "Счет: " : "Score: ", score);
    new position = FindProfileUtf8(g_leader_html, expected);
    CheckSaved(start >= 0 && position >= start + strlen(heading),
        "connection score is distinct from saved totals and belongs below its heading");
    CheckSaved(FindProfileUtf8(g_leader_html, language == 0
        ? "Сброс при переподключении или смене карты."
        : "Resets on reconnect or map change.") > position,
        "complete localized connection reset rule follows the score");
}

public RunPlayerProfileSmoke()
{
    if (!CreateStatsClients()) { set_fail_state("Profile fixture unavailable."); return PLUGIN_HANDLED; }
    SeedLeaderboardRecords();
    new player = g_clients[0], rejected[128];
    new const keys[][] = { "v1:STEAM_0:1:800001", "hud:v1:STEAM_0:1:800001",
        "name:v1:STEAM_0:1:800001", "streak:v1:STEAM_0:1:800001" };
    new const values[][] = { "1 2 1 2", "1 1", "1 <script>&^"%s", "1 7" };
    for (new index = 0; index < sizeof keys; index++)
    {
        nvault_set(g_preferences_vault, keys[index], values[index]);
        nvault_touch(g_preferences_vault, keys[index], 123456789);
    }
    JoinStandingPlayer(player, 1);
    // Replace the join-time name once before verifying that profile reads preserve it.
    nvault_set(g_preferences_vault, keys[2], values[2]);
    nvault_touch(g_preferences_vault, keys[2], 123456789);
    nvault_touch(g_stats_vault, keys[0], 123456789);
    g_kill_streak[player] = 2;
    g_best_streak[player] = 5;
    g_map_kills[player] = 7;
    g_map_deaths[player] = 2;
    new writes = g_vault_writes, reads = g_leader_reads;
    new guide_reads = g_vault_reads, guide_motds = g_motds;
    g_preferences_dirty[player] = PREFERENCE_PRIMARY;
    g_hud_preference_dirty[player] = true;
    g_stats_next[player][5] = 0.0;
    amxclient_cmd(player, "say", "/ranks");
    amxclient_cmd(player, "say_team", "/ranks");
    amxclient_cmd(player, "ranks");
    CheckSaved(g_motds == guide_motds + 1, "rank guide aliases share one cooldown");
    g_stats_next[player][5] = 0.0;
    amxclient_cmd(player, "say_team", "/ranks");
    g_stats_next[player][5] = 0.0;
    amxclient_cmd(player, "ranks");
    CheckSaved(g_motds == guide_motds + 3 && g_motd_target == player, "all rank guide aliases dispatch privately");
    CheckSaved(g_vault_reads == guide_reads && g_vault_writes == writes
        && g_preferences_dirty[player] == PREFERENCE_PRIMARY && g_hud_preference_dirty[player],
        "rank guide never loads storage or flushes pending preference writes");
    CheckSaved(contain(g_leader_html, "STEAM_") < 0 && contain(g_leader_html, "<script>") < 0
        && contain(g_leader_html, "800001") < 0, "static guide exposes no player data");
    g_preferences_dirty[player] = 0;
    g_hud_preference_dirty[player] = false;
    g_stats_next[player][5] = 0.0;
    g_hltv[player] = true;
    ShowRankGuide(player);
    g_hltv[player] = false;
    CheckSaved(g_motds == guide_motds + 3 && g_stats_next[player][5] == 0.0, "HLTV cannot consume guide output or cooldown");
    ShowRankGuide(player);
    g_expected_motd_target = g_clients[1];
    ShowRankGuide(g_clients[1]);
    g_expected_motd_target = 0;
    CheckSaved(g_motds == guide_motds + 5 && g_motd_target == g_clients[1], "another player's guide admission is independent");
    server_print("PLAYER_RANK_GUIDE_STORAGE_SMOKE=%s failures=%d real_nvault=1 aliases=3", g_failures ? "failed" : "passed", g_failures);
    guide_reads = g_vault_reads;
    guide_motds = g_motds;
    g_preferences_dirty[player] = PREFERENCE_PRIMARY;
    g_hud_preference_dirty[player] = true;
    g_stats_next[player][6] = 0.0;
    amxclient_cmd(player, "say", "/rules");
    amxclient_cmd(player, "say_team", "/rules");
    amxclient_cmd(player, "rules");
    CheckSaved(g_motds == guide_motds + 1, "rules aliases share one cooldown");
    g_stats_next[player][6] = 0.0;
    amxclient_cmd(player, "say_team", "/rules");
    g_stats_next[player][6] = 0.0;
    amxclient_cmd(player, "rules");
    CheckSaved(g_motds == guide_motds + 3 && g_motd_target == player, "all rules aliases dispatch privately");
    CheckSaved(g_vault_reads == guide_reads && g_vault_writes == writes
        && g_preferences_dirty[player] == PREFERENCE_PRIMARY && g_hud_preference_dirty[player],
        "rules never load storage or flush pending preference writes");
    CheckSaved(contain(g_leader_html, "STEAM_") < 0 && contain(g_leader_html, "<script>") < 0, "rules expose no player identity or stored name");
    g_preferences_dirty[player] = 0;
    g_hud_preference_dirty[player] = false;
    g_stats_next[player][6] = 0.0;
    g_hltv[player] = true;
    ShowGameplayRules(player);
    g_hltv[player] = false;
    CheckSaved(g_motds == guide_motds + 3 && g_stats_next[player][6] == 0.0, "HLTV cannot consume rules output or cooldown");
    ShowGameplayRules(player);
    g_expected_motd_target = g_clients[1];
    ShowGameplayRules(g_clients[1]);
    g_expected_motd_target = 0;
    CheckSaved(g_motds == guide_motds + 5 && g_motd_target == g_clients[1], "rules admission is private and independent per player");
    server_print("PLAYER_RULES_STORAGE_SMOKE=%s failures=%d real_nvault=1 aliases=3", g_failures ? "failed" : "passed", g_failures);
    for (new language = 0; language < sizeof PLAYER_LANGUAGES; language++)
    {
        g_language_override[player] = language + 1;
        g_stats_next[player][4] = 0.0;
        new motds = g_motds, prints = g_prints, expected[192];
        ShowPlayerProfile(player);
        CheckSaved(g_motds == motds + 1 && g_prints == prints, "profile emits one private MOTD without chat spam");
        CheckSaved(contain(g_leader_html, "K 10 | D 1 | K/D 10.00") >= 0,
            "profile uses saved totals and existing ratio semantics");
        CheckProfileScore(language, "K 7 | D 2 | K/D 3.50");
        CheckSaved(FindProfileUtf8(g_leader_title, language == 0 ? "Мой профиль" : "My profile") == 0
            && strlen(g_leader_title) == (language == 0 ? 21 : 10),
            "profile caption preserves every RU/EN UTF-8 byte");
        formatex(expected, charsmax(expected), "<h2>%L</h2>", PLAYER_LANGUAGES[language], "GS_PROFILE");
        CheckSaved(FindProfileUtf8(g_leader_html, expected) >= 0, "profile keeps the full inner heading");
        CheckSaved(FindProfileUtf8(g_leader_html, language == 0 ? "Ранг: Новичок | K 10 | До следующего 15" : "Saved rank: Recruit | K 10 | 15 kills") >= 0,
            "profile shows saved rank and remaining kills");
        formatex(expected, charsmax(expected), "%L", PLAYER_LANGUAGES[language], "GS_STANDING_POSITION", 12, 12);
        CheckSaved(contain(g_leader_html, expected) >= 0, "profile shares overall standing with rank and topall");
        CheckSaved(FindProfileUtf8(g_leader_html, language == 0 ? "Рекорд серии: 7" : "Saved best streak: 7") >= 0
            && FindProfileUtf8(g_leader_html, language == 0 ? "Серия: 2 | Лучшая: 5" : "Streak: 2 | Best: 5") >= 0,
            "saved record and connection series remain distinct");
        CheckSaved(contain(g_leader_html, "STEAM_") < 0 && contain(g_leader_html, "<script>") < 0
            && contain(g_leader_html, "800001") < 0 && contain(g_leader_html, "ML_NOTFOUND") < 0
            && contain(g_leader_html, "</body></html>") == strlen(g_leader_html) - 14,
            "complete bounded profile exposes no identity or stored name");
        ShowPlayerProfile(player);
        OnPlayerMenuSelected(player, g_player_menu[language], 8);
        CheckSaved(g_motds == motds + 1 && g_stats_next[player][4] == get_gametime() + 5.0,
            "direct and menu requests share a five-second cooldown");
        g_stats_next[player][4] = get_gametime();
        OnPlayerMenuSelected(player, g_player_menu[1 - language], 8);
        CheckSaved(g_motds == motds + 1, "stale language menu cannot open a profile");
        OnPlayerMenuSelected(player, g_player_menu[language], 8);
        CheckSaved(g_motds == motds + 2, "ninth menu item opens profile at cooldown boundary");
    }
    new value[64], stamp;
    for (new index = 0; index < sizeof keys; index++)
    {
        CheckSaved(nvault_lookup(g_preferences_vault, keys[index], value, charsmax(value), stamp)
            && equal(value, values[index]) && stamp == 123456789, "profile preserves preference namespaces and timestamps");
    }
    CheckSaved(nvault_lookup(g_stats_vault, keys[0], value, charsmax(value), stamp)
        && equal(value, "1 10 1") && stamp == 123456789 && writes == g_vault_writes && reads == g_leader_reads,
        "profile preserves statistics and never writes or scans vault files");
    CheckSaved(g_choice[player] == 1 && g_pistol_choice[player] == 2 && g_hud_disabled[player]
        && g_kill_streak[player] == 2 && g_best_streak[player] == 5
        && g_map_kills[player] == 7 && g_map_deaths[player] == 2,
        "profile leaves preferences HUD and active counters intact");

    new motds = g_motds;
    g_stats_next[player][4] = 0.0;
    amxclient_cmd(player, "say", "/profile");
    g_stats_next[player][4] = 0.0;
    amxclient_cmd(player, "say_team", "/profile");
    g_stats_next[player][4] = 0.0;
    amxclient_cmd(player, "profile");
    CheckSaved(g_motds == motds + 3, "chat team-chat and console aliases dispatch to private profile");
    g_stats_next[player][0] = 0.0;
    ShowMapStats(player);
    g_stats_next[player][2] = 0.0;
    ShowPlayerRank(player);
    CheckSaved(contain(g_last_print, "Your place: 12 of 12.") >= 0, "profile cooldown does not consume stats or rank commands");

    JoinStandingPlayer(player, 11);
    g_saved_kills[player] = 500;
    SavePlayerStats(player);
    ShowPlayerProfile(player);
    CheckSaved(contain(g_leader_html, "Highest rank reached") >= 0 && contain(g_leader_html, "Your place: 1 of 12.") >= 0,
        "highest rank remains explicit while standing uses the existing cached snapshot");
    g_saved_deaths[player] = 3;
    g_saved_kills[player] = 110;
    SavePlayerStats(player);
    g_stats_next[player][4] = 0.0;
    ShowPlayerProfile(player);
    CheckSaved(contain(g_leader_html, "Your place: 1 of 12.") >= 0, "profile retains shared standing during cache cooldown");
    g_leader_refresh_after = 0.0;
    g_stats_next[player][4] = 0.0;
    ShowPlayerProfile(player);
    CheckSaved(contain(g_leader_html, "Your place: 2 of 12.") >= 0, "profile advances standing only with shared snapshot");

    JoinStandingPlayer(player, 14);
    ShowPlayerProfile(player);
    CheckSaved(contain(g_leader_html, "K 0 | D 0 | K/D 0.00") >= 0
        && contain(g_leader_html, "after your first saved kill or death") >= 0
        && contain(g_leader_html, "Saved best streak: 0 (since record tracking began)") >= 0,
        "new player gets zero known counters and explicit unranked state");
    RecordMapDeath(0, player);
    g_stats_next[player][4] = 0.0;
    ShowPlayerProfile(player);
    CheckSaved(contain(g_leader_html, "when the leaderboard refreshes") >= 0, "first score stays pending until shared refresh");
    CheckProfileScore(1, "K 0 | D 1 | K/D 0.00");
    g_leader_refresh_after = 0.0;
    g_stats_next[player][4] = 0.0;
    ShowPlayerProfile(player);
    CheckSaved(contain(g_leader_html, "Your place: 13 of 13.") >= 0, "deaths-only participant becomes ranked after refresh");

    JoinStandingPlayer(player, 1);
    ShowPlayerProfile(player);
    CheckSaved(contain(g_leader_html, "Saved best streak: 7") >= 0 && contain(g_leader_html, "Streak: 0 | Best: 0") >= 0,
        "reconnect restores saved record and resets connection series");
    CheckProfileScore(1, "K 0 | D 0 | K/D 0.00");
    g_map_kills[player] = 7;
    g_map_deaths[player] = 2;
    writes = g_vault_writes;
    for (new unavailable = 0; unavailable < 3; unavailable++)
    {
        new stats_vault = g_stats_vault, preference_vault = g_preferences_vault;
        g_saved_ready[player] = unavailable != 0;
        g_leader_ready = unavailable != 1;
        g_streak_ready[player] = unavailable != 2;
        if (unavailable == 0) { g_stats_vault = INVALID_HANDLE; }
        if (unavailable == 2) { g_preferences_vault = INVALID_HANDLE; }
        g_stats_next[player][4] = 0.0;
        ShowPlayerProfile(player);
        CheckSaved(contain(g_leader_html, "Streak: 0 | Best: 0") >= 0, "partial failures preserve connection series");
        CheckSaved(contain(g_leader_html, unavailable == 0 ? "Saved totals are unavailable" : "K 10 | D 1") >= 0,
            "record or leaderboard failure does not hide healthy totals");
        CheckSaved(contain(g_leader_html, unavailable == 2 ? "Saved best streak is unavailable" : "Saved best streak: 7") >= 0,
            "statistics or leaderboard failure does not hide healthy record");
        if (unavailable < 2) { CheckSaved(contain(g_leader_html, "Your overall place is temporarily unavailable") >= 0, "failed source never fabricates a position"); }
        for (new language = 0; language < sizeof PLAYER_LANGUAGES; language++)
        {
            g_language_override[player] = language + 1;
            g_stats_next[player][4] = 0.0;
            ShowPlayerProfile(player);
            CheckProfileScore(language, "K 7 | D 2 | K/D 3.50");
        }
        g_stats_vault = stats_vault;
        g_preferences_vault = preference_vault;
    }
    g_leader_ready = true;
    client_putinserver(player);
    g_streak_blocked[player] = true;
    ShowPlayerProfile(player);
    CheckSaved(contain(g_leader_html, "Saved best streak is unavailable") >= 0, "blocked record is unavailable even if a cached value exists");
    g_streak_blocked[player] = false;
    g_preferences_blocked[player] = true;
    g_stats_next[player][4] = 0.0;
    ShowPlayerProfile(player);
    CheckSaved(contain(g_leader_html, "Saved best streak is unavailable") >= 0, "blocked base preferences guard is authoritative");
    CheckSaved(g_vault_writes == writes, "partial failures do not create or repair records");

    client_disconnected(player, false, rejected, charsmax(rejected));
    copy(g_auth[player], charsmax(g_auth[]), "STEAM_ID_PENDING");
    client_putinserver(player);
    g_language_override[player] = 2;
    g_preferences_dirty[player] = PREFERENCE_LANGUAGE | PREFERENCE_PRIMARY;
    g_hud_preference_dirty[player] = true;
    g_choice[player] = 2;
    g_kill_streak[player] = 3;
    g_best_streak[player] = 4;
    g_map_kills[player] = 7;
    g_map_deaths[player] = 0;
    // Identity is now known but the callback has not loaded it: a view must not perform that transition.
    copy(g_auth[player], charsmax(g_auth[]), "STEAM_0:1:800001");
    writes = g_vault_writes;
    ShowPlayerProfile(player);
    g_stats_next[player][4] = 0.0;
    OnPlayerMenuSelected(player, g_player_menu[1], 8);
    CheckSaved(contain(g_leader_html, "Saved totals are unavailable") >= 0
        && contain(g_leader_html, "Saved best streak is unavailable") >= 0
        && contain(g_leader_html, "Streak: 3 | Best: 4") >= 0
        && !g_saved_ready[player] && !g_preferences_ready[player] && !g_streak_ready[player]
        && g_preferences_dirty[player] == (PREFERENCE_LANGUAGE | PREFERENCE_PRIMARY) && g_hud_preference_dirty[player]
        && g_choice[player] == 2 && g_vault_writes == writes,
        "profile and menu projection do not load late identity or flush pending preferences");
    CheckProfileScore(1, "K 7 | D 0 | K/D 7.00");
    client_authorized(player, g_auth[player]);
    g_stats_next[player][4] = 0.0;
    ShowPlayerProfile(player);
    CheckSaved(contain(g_leader_html, "K 10 | D 1") >= 0 && contain(g_leader_html, "Saved best streak: 7") >= 0,
        "authorization callback makes stored fields visible on the next view");

    g_saved_kills[player] = STATS_COUNTER_LIMIT;
    g_saved_streak[player] = STATS_COUNTER_LIMIT;
    g_kill_streak[player] = STATS_COUNTER_LIMIT;
    g_best_streak[player] = STATS_COUNTER_LIMIT;
    g_map_kills[player] = STATS_COUNTER_LIMIT;
    for (new language = 0; language < sizeof PLAYER_LANGUAGES; language++)
    {
        g_language_override[player] = language + 1;
        for (new maximum_deaths = 0; maximum_deaths < 2; maximum_deaths++)
        {
            g_saved_deaths[player] = maximum_deaths ? STATS_COUNTER_LIMIT : 0;
            g_map_deaths[player] = g_saved_deaths[player];
            g_stats_next[player][4] = 0.0;
            ShowPlayerProfile(player);
            CheckProfileScore(language, maximum_deaths
                ? "K 1000000000 | D 1000000000 | K/D 1.00"
                : "K 1000000000 | D 0 | K/D 1000000000.00");
            new expected_streak[192];
            formatex(expected_streak, charsmax(expected_streak), "%L", PLAYER_LANGUAGES[language],
                "GS_STREAK_STATS", STATS_COUNTER_LIMIT, STATS_COUNTER_LIMIT);
            CheckSaved(strlen(g_leader_html) < charsmax(g_leader_html)
                && contain(g_leader_html, "</body></html>") == strlen(g_leader_html) - 14
                && FindProfileUtf8(g_leader_html, expected_streak) >= 0 && contain(g_leader_html, "ML_NOTFOUND") < 0,
                "maximal saved and connection counters retain the last line and closing markup");
            server_print("PLAYER_PROFILE_SCORE_BUDGET language=%s deaths=%d bytes=%d limit=1535",
                PLAYER_LANGUAGES[language], g_map_deaths[player], strlen(g_leader_html));
        }
    }
    new alive = is_user_alive(player);
    rg_set_user_team(player, TEAM_SPECTATOR);
    set_entvar(player, var_deadflag, DEAD_DEAD);
    g_stats_next[player][4] = 0.0;
    motds = g_motds;
    ShowPlayerProfile(player);
    CheckSaved(g_motds == motds + 1, "dead spectators can read their own profile");
    rg_set_user_team(player, TEAM_CT);
    set_entvar(player, var_deadflag, alive ? DEAD_NO : DEAD_DEAD);
    g_stats_next[player][4] = 0.0;
    motds = g_motds;
    ShowPlayerProfile(0);
    ShowPlayerProfile(MAX_PLAYERS + 1);
    g_human[player] = false;
    ShowPlayerProfile(player);
    g_human[player] = true;
    g_hltv[player] = true;
    ShowPlayerProfile(player);
    g_hltv[player] = false;
    set_pcvar_num(g_enabled, 0);
    ShowPlayerProfile(player);
    set_pcvar_num(g_enabled, 1);
    CheckSaved(g_motds == motds && g_stats_next[player][4] == 0.0, "invalid bot HLTV and disabled requests have no output or cooldown side effect");
    OnStatusCommand();
    OnMapStatsStatus();
    server_print("PLAYER_PROFILE_SMOKE=%s failures=%d languages=2 storage=real_nvault display=private_motd",
        g_failures ? "failed" : "passed", g_failures);
    return PLUGIN_HANDLED;
}

new g_snapshot_frame[2048];

FixtureTransportPrint(const message[], any:...)
{
    new line[192];
    vformat(line, charsmax(line), message, 2);
    if (equal(line, "GSLOOKUP ", 9) || equal(line, "GSPROW ", 7) || equal(line, "GSPEND ", 7))
    {
        CheckSaved(strlen(g_lookup_frame) + strlen(line) + 1 < sizeof g_lookup_frame, "lookup frame stays within 2048 bytes");
        add(g_lookup_frame, charsmax(g_lookup_frame), line);
        add(g_lookup_frame, charsmax(g_lookup_frame), "^n");
        if (g_lookup_quiet) { return; }
    }
    if (equal(line, "GSL", 3))
    {
        add(g_snapshot_frame, charsmax(g_snapshot_frame), line);
        add(g_snapshot_frame, charsmax(g_snapshot_frame), "^n");
    }
    server_print("%s", line);
}

public RunPublicLeaderboardSmoke()
{
    if (!CreateStatsClients()) { set_fail_state("Public leaderboard fixture unavailable."); return PLUGIN_HANDLED; }
    SeedLeaderboardRecords();
    new writes = g_vault_writes, reads = g_leader_reads, rebuilds = g_leader_rebuilds;
    g_snapshot_frame[0] = 0;
    OnLeaderboardSnapshot();
    new header[64], footer[64], stamp = get_systime();
    formatex(header, charsmax(header), "GSLEADER 1 %d 10^n", stamp);
    formatex(footer, charsmax(footer), "GSLEND 1 %d 10^n", stamp);
    CheckSaved(equal(g_snapshot_frame, header, strlen(header)) && contain(g_snapshot_frame, footer) > 0,
        "version timestamp and complete ten-row frame");
    CheckSaved(contain(g_snapshot_frame, "GSLROW 1 2 120 2 -^nGSLROW 2 2 120 2 3C7363726970743E26222573^n") > 0,
        "authoritative tied order and markup encoded as bytes");
    CheckSaved(contain(g_snapshot_frame, "D09AD0B8D180D0B8D0BBD0BBD0B8D186D0B0") > 0
        && contain(g_snapshot_frame, "GSLROW 10 ") > 0 && contain(g_snapshot_frame, "GSLROW 11 ") < 0,
        "UTF-8 nickname and ten-row ceiling");
    CheckSaved(contain(g_snapshot_frame, "STEAM_") < 0 && contain(g_snapshot_frame, "v1:") < 0,
        "identity and storage keys excluded");
    CheckSaved(writes == g_vault_writes && reads == g_leader_reads && rebuilds == g_leader_rebuilds,
        "export uses shared cache without writes file scan or rebuild during cooldown");
    UpdateLeaderScore("v1:STEAM_0:1:800010", 999, 0);
    g_snapshot_frame[0] = 0;
    OnLeaderboardSnapshot();
    CheckSaved(contain(g_snapshot_frame, "GSLROW 1 2 120 2 -") > 0,
        "dirty state does not bypass shared cooldown");
    g_leader_refresh_after = 0.0;
    g_snapshot_frame[0] = 0;
    OnLeaderboardSnapshot();
    CheckSaved(contain(g_snapshot_frame, "GSLROW 1 4 999 0 ") > 0,
        "next shared refresh publishes updated rank and order");
    CheckSaved(writes == g_vault_writes && reads == g_leader_reads,
        "cache refresh does not write or scan files");
    g_leader_count = 0;
    g_snapshot_frame[0] = 0;
    OnLeaderboardSnapshot();
    formatex(header, charsmax(header), "GSLEADER 1 %d 0^nGSLEND 1 %d 0^n", get_systime(), get_systime());
    CheckSaved(bool:equal(g_snapshot_frame, header), "empty frame remains complete");
    g_leader_ready = false;
    g_snapshot_frame[0] = 0;
    OnLeaderboardSnapshot();
    CheckSaved(bool:equal(g_snapshot_frame, "GSLEADER 1 unavailable^n"), "unavailable source has no rows");
    g_leader_ready = true;
    set_pcvar_num(g_enabled, 0);
    g_snapshot_frame[0] = 0;
    OnLeaderboardSnapshot();
    CheckSaved(bool:equal(g_snapshot_frame, "GSLEADER 1 unavailable^n"), "disabled source has no rows");
    set_pcvar_num(g_enabled, 1);
    server_print("PUBLIC_LEADERBOARD_SMOKE=%s failures=%d", g_failures ? "failed" : "passed", g_failures);
    return PLUGIN_HANDLED;
}


CopyLeaderboardRows(output[], capacity)
{
    new start = contain(g_snapshot_frame, "^n") + 1, end = contain(g_snapshot_frame, "GSLEND ");
    CheckSaved(start > 0 && end > start, "top-ten frame has bounded row section");
    copy(output, min(capacity, end - start), g_snapshot_frame[start]);
}

new const LOOKUP_NONCE[] = "0123456789abcdef";

LookupFixtureKey(index, key[], length)
{
    formatex(key, length, "v1:STEAM_0:1:%d", 900000 + index);
}

SeedLookupPlayer(index, kills, deaths, const name[])
{
    new key[48], value[64];
    LookupFixtureKey(index, key, charsmax(key));
    formatex(value, charsmax(value), "1 %d %d", kills, deaths);
    nvault_set(g_stats_vault, key, value);
    formatex(key, charsmax(key), "name:v1:STEAM_0:1:%d", 900000 + index);
    formatex(value, charsmax(value), "1 %s", name);
    nvault_set(g_preferences_vault, key, value);
}

QueryLookup(const hex[], bool:reset = true, bool:partial = false)
{
    if (reset) { g_lookup_after = 0.0; }
    g_lookup_frame[0] = 0;
    EmitPlayerLookup(LOOKUP_NONCE, hex, partial);
}

QueryLookupName(const name[], bool:partial = false)
{
    new hex[63], written;
    for (new index = 0; name[index]; index++)
    {
        written += formatex(hex[written], charsmax(hex) - written, "%02X", name[index] & 255);
    }
    QueryLookup(hex, true, partial);
}

ReopenLookupVaults()
{
    CloseLeaderFixture();
    nvault_close(g_preferences_vault);
    g_preferences_vault = nvault_open(PREFERENCES_VAULT_NAME);
    CheckSaved(g_preferences_vault != INVALID_HANDLE, "preferences vault reopens");
    OpenLeaderFixture();
    CheckSaved(g_stats_vault != INVALID_HANDLE && g_leader_ready, "statistics vault and index reopen");
}

// Every seeded statistics/name entry and non-name preference survives queries and reopen.
PreserveLookupVault(bool:verify)
{
    static values[63][64], timestamps[63], present[63];
    for (new index = 0; index < 63; index++)
    {
        new key[48], value[64], stamp, vault;
        if (index < 30) { LookupFixtureKey(index + 1, key, charsmax(key)); vault = g_stats_vault; }
        else if (index < 60) { formatex(key, charsmax(key), "name:v1:STEAM_0:1:%d", 900001 + index - 30); vault = g_preferences_vault; }
        else
        {
            new const keys[][] = {"v1:STEAM_0:1:900019", "hud:v1:STEAM_0:1:900019", "streak:v1:STEAM_0:1:900019"};
            copy(key, charsmax(key), keys[index - 60]); vault = g_preferences_vault;
        }
        new found = nvault_lookup(vault, key, value, charsmax(value), stamp);
        if (verify)
        {
            CheckSaved(found == present[index] && (!found || (equal(value, values[index]) && stamp == timestamps[index])),
                "statistics names preferences HUD and streak values/timestamps preserved");
        }
        else { present[index] = found; timestamps[index] = stamp; copy(values[index], charsmax(values[]), value); }
    }
}

RunSubstringChecks()
{
    QueryLookupName("tWi", true);
    CheckSaved(contain(g_lookup_frame, "GSLOOKUP 2 ") == 0 && contain(g_lookup_frame, " 10 1^n") > 0
        && contain(g_lookup_frame, "GSPROW 1 2 120 12 5477696E^n") > 0
        && contain(g_lookup_frame, "GSPROW 10 1 30 3 5477696E^n") > 0 && contain(g_lookup_frame, "GSPROW 11") < 0,
        "v2 returns ten sorted duplicates and truncation with actual names");
    new marker[16], version[8], nonce[20], stamp[16], footer[80];
    parse(g_lookup_frame, marker, charsmax(marker), version, charsmax(version), nonce, charsmax(nonce), stamp, charsmax(stamp));
    formatex(footer, charsmax(footer), "GSPEND 2 %s %s 10 1^n", LOOKUP_NONCE, stamp);
    CheckSaved(contain(g_lookup_frame, footer) == strlen(g_lookup_frame) - strlen(footer), "v2 trailer correlates all header fields");
    QueryLookupName("cAs", true);
    CheckSaved(contain(g_lookup_frame, " 3 0^n") > 0
        && contain(g_lookup_frame, "GSPROW 1 0 2 1 5B5441475D43415345^nGSPROW 2 0 2 2 436173655461696C^nGSPROW 3 0 1 2 43617365^n") > 0,
        "substring emits distinct actual names in score order, not query copies");
    CheckSaved(contain(g_lookup_frame, "STEAM_") < 0 && contain(g_lookup_frame, "v1:") < 0, "v2 does not expose identity keys");
    QueryLookup("61");
    CheckSaved(contain(g_lookup_frame, " 0 0^n") > 0, "v1 still accepts a one-scalar exact query");
    QueryLookupName("<tag>", true);
    CheckSaved(contain(g_lookup_frame, "3C7461673E2226") > 0 && contain(g_lookup_frame, "<tag>") < 0, "v2 markup remains literal and hex encoded");
    new const literal_names[][] = {"CAFÉ", "Straße", "A*B", "ABC"};
    new const folded_names[][] = {"cafÉ", "straße", "a*b", "abc"};
    for (new index = 0; index < sizeof literal_names; index++)
    {
        new folded[32]; FoldSearchName(literal_names[index], folded);
        CheckSaved(bool:equal(folded, folded_names[index]), "non-Russian non-ASCII and punctuation stay literal");
    }
    new decomposed[] = {65,70,69,204,129,0}, expected_decomposed[] = {97,102,101,204,129,0}, folded_decomposed[32];
    FoldSearchName(decomposed, folded_decomposed);
    CheckSaved(bool:equal(folded_decomposed, expected_decomposed), "combining marks are not normalized");
    QueryLookupName("КИР", true);
    CheckSaved(contain(g_lookup_frame, "D09AD0B8D180D0B8D0BBD0BBD0B8D186D0B0") > 0, "Russian infix uses original spelling outside top ten");
    new const upper[] = "АБВГДЕЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ", lower[] = "абвгдежзийклмнопрстуфхцчшщъыьэюя";
    for (new letter = 0; letter < 32; letter++)
    {
        new name[] = {65,65,0,0,0}, expected[] = {97,97,0,0,0}, folded[32];
        name[2] = upper[letter * 2]; name[3] = upper[letter * 2 + 1];
        expected[2] = lower[letter * 2]; expected[3] = lower[letter * 2 + 1];
        FoldSearchName(name, folded);
        CheckSaved(bool:equal(folded, expected), "all Russian pairs follow the fixed map");
        FoldSearchName(expected, folded);
        CheckSaved(bool:equal(folded, expected), "lowercase mapping is stable");
    }
    QueryLookup("D191D0B6D0B8", true, true);
    CheckSaved(contain(g_lookup_frame, "D081D096D098D09A") > 0, "special Russian case pair maps without losing original bytes");
    new const misses[][] = {"ежи", "Igr", "<*>" , "Zero", "Absent", "WrongVersion", "Missing"};
    for (new index = 0; index < sizeof misses; index++)
    {
        QueryLookupName(misses[index], true);
        CheckSaved(contain(g_lookup_frame, " 0 0^n") > 0 && contain(g_lookup_frame, "GSPEND 2 ") > 0,
            "no fuzzy/wildcard or fallback matches and no zero/missing/invalid names");
    }
    QueryLookup("F09F9880F09F9880F09F9880", true, true);
    CheckSaved(contain(g_lookup_frame, " 1 0^n") > 0, "three four-byte scalars accepted");
    new const short_queries[][] = {"61", "6162", "D0B8D0B3", "F09F9880F09F9880", "202061622020"};
    for (new index = 0; index < sizeof short_queries; index++)
    {
        QueryLookup(short_queries[index], true, true);
        CheckSaved(bool:equal(g_lookup_frame, "GSLOOKUP 2 invalid^n"), "minimum counts trimmed scalars not bytes");
    }
    QueryLookup("2043617320", true, true);
    CheckSaved(contain(g_lookup_frame, " 3 0^n") > 0, "v2 trims outer ASCII spaces");
    QueryLookupName("ЯЯЯЯЯЯЯЯЯЯЯЯЯЯЯa", true);
    CheckSaved(contain(g_lookup_frame, " 1 0^n") > 0, "maximum 31-byte substring accepted");
    new matches[SAVED_LEADER_LIMIT], count, total, record[LeaderRecord], key[40];
    FindSavedPlayers("equ", matches, count, total, true);
    ArrayGetArray(g_leader_records, matches[0], record);
    LookupFixtureKey(22, key, charsmax(key));
    CheckSaved(count == 2 && total == 2 && equal(record[LeaderKey], key), "v2 preserves identical-record private tie order");
    new original_kills = record[LeaderKills], position = matches[0];
    record[LeaderKills] = -1; ArraySetArray(g_leader_records, position, record);
    QueryLookupName("equ", true);
    CheckSaved(contain(g_lookup_frame, " 1 0^n") > 0, "v2 excludes deleted index records");
    record[LeaderKills] = original_kills; ArraySetArray(g_leader_records, position, record);
    for (new mode = 0; mode < 2; mode++)
    {
        QueryLookup("43617365", true, bool:mode);
        new Float:deadline = g_lookup_after;
        QueryLookup("43617365", false, !bool:mode);
        CheckSaved(contain(g_lookup_frame, "busy 10") > 0 && g_lookup_after == deadline, "opposite mode cannot bypass or extend successful admission");
        QueryLookup("GG", true, bool:mode);
        QueryLookup("43617365", false, !bool:mode);
        CheckSaved(contain(g_lookup_frame, "busy 10") > 0, "invalid queries share admission across versions");
    }
    g_lookup_after = get_gametime() + 0.25;
    QueryLookup("43617365", false, true);
    CheckSaved(contain(g_lookup_frame, "busy 1") > 0, "fractional wait rounds up");
    g_lookup_after = get_gametime() - 0.01;
    QueryLookup("43617365", false, true);
    CheckSaved(contain(g_lookup_frame, " 3 0^n") > 0, "expired admission permits v2");
    for (new source_state = 0; source_state < 5; source_state++)
    {
        new saved_stats = g_stats_vault, saved_prefs = g_preferences_vault;
        if (source_state == 0) { set_pcvar_num(g_enabled, 0); }
        if (source_state == 1) { set_pcvar_num(g_lookup_enabled, 0); }
        if (source_state == 2) { g_leader_ready = false; }
        if (source_state == 3) { g_stats_vault = INVALID_HANDLE; }
        if (source_state == 4) { g_preferences_vault = INVALID_HANDLE; }
        QueryLookupName("cas", true);
        CheckSaved(contain(g_lookup_frame, "GSLOOKUP 2 ") == 0 && contain(g_lookup_frame, "unavailable") > 0, "v2 fails closed when source unavailable");
        set_pcvar_num(g_enabled, 1); set_pcvar_num(g_lookup_enabled, 1); g_leader_ready = true;
        g_stats_vault = saved_stats; g_preferences_vault = saved_prefs;
        QueryLookup("43617365", false);
        CheckSaved(contain(g_lookup_frame, "busy") > 0, "restoring source does not reset shared cooldown");
    }
    g_lookup_frame[0] = 0;
    EmitPlayerLookup("bad;nonce", "636173", true);
    CheckSaved(bool:equal(g_lookup_frame, "GSLOOKUP 2 invalid^n"), "v2 rejects invalid nonce");
    g_lookup_after = 0.0; g_lookup_frame[0] = 0;
    server_cmd("goldsrcops_player_lookup_v2 0123456789abcdef 636173 extra"); server_exec();
    CheckSaved(bool:equal(g_lookup_frame, "GSLOOKUP 2 invalid^n"), "registered v2 rejects extra arguments");
    QueryLookup("43617365", false);
    CheckSaved(contain(g_lookup_frame, "busy") > 0, "malformed v2 argument list consumes shared slot");
    g_lookup_after = 0.0; g_lookup_frame[0] = 0;
    server_cmd("goldsrcops_player_lookup_v2 0123456789abcdef 636173"); server_exec();
    CheckSaved(contain(g_lookup_frame, " 3 0^n") > 0 && contain(g_lookup_frame, "GSPEND 2 ") > 0, "registered v2 handler emits complete result");
    QueryLookupName("cas");
    CheckSaved(contain(g_lookup_frame, " 0 0^n") > 0, "v1 remains exact after v2");
}

public RunPlayerLookupSmoke()
{
    nvault_prune(g_stats_vault, 0, 0);
    nvault_prune(g_preferences_vault, 0, 0);
    for (new index = 1; index <= 12; index++) { SeedLookupPlayer(index, index * 10, index, "Twin"); }
    SeedLookupPlayer(13, 0, 0, "Zero");
    SeedLookupPlayer(14, 1, 2, "Кириллица");
    SeedLookupPlayer(15, 1, 2, "ЯЯЯЯЯЯЯЯЯЯЯЯЯЯЯa");
    SeedLookupPlayer(16, 1, 2, "STEAM_0:1:123");
    new malformed[] = { 192, 175, 0 };
    SeedLookupPlayer(17, 1, 2, malformed);
    SeedLookupPlayer(18, 1, 2, "");
    SeedLookupPlayer(19, 1, 2, "Case");
    SeedLookupPlayer(20, 1, 2, "<tag>^"&");
    SeedLookupPlayer(21, 1, 2, "Ctrl^nX");
    SeedLookupPlayer(22, 42, 5, "Equal");
    SeedLookupPlayer(23, 42, 5, "Equal");
    SeedLookupPlayer(24, 1, 2, "Player");
    SeedLookupPlayer(25, 2, 1, "[TAG]CASE");
    SeedLookupPlayer(26, 2, 2, "CaseTail");
    new emoji_name[] = {240,159,152,128,240,159,152,128,240,159,152,128,0};
    SeedLookupPlayer(27, 1, 2, emoji_name);
    new special_name[] = {208,129,208,150,208,152,208,154,0};
    SeedLookupPlayer(28, 1, 2, special_name);
    SeedLookupPlayer(29, 1, 2, "Absent");
    nvault_remove(g_preferences_vault, "name:v1:STEAM_0:1:900029");
    SeedLookupPlayer(30, 1, 2, "WrongVersion");
    nvault_set(g_preferences_vault, "name:v1:STEAM_0:1:900030", "2 WrongVersion");
    nvault_set(g_preferences_vault, "v1:STEAM_0:1:900019", "1 2 1 2");
    nvault_set(g_preferences_vault, "hud:v1:STEAM_0:1:900019", "1 1");
    nvault_set(g_preferences_vault, "streak:v1:STEAM_0:1:900019", "1 7");
    CloseLeaderFixture();
    OpenLeaderFixture();
    CheckSaved(g_leader_ready && ArraySize(g_leader_records) == 30, "load all offline records including those outside top ten");
    CheckSaved(get_pcvar_num(g_lookup_enabled) == 0, "lookup defaults off");
    QueryLookupName("Twin");
    CheckSaved(contain(g_lookup_frame, "unavailable") > 0, "default-off returns unavailable");
    QueryLookupName("twi", true);
    CheckSaved(contain(g_lookup_frame, "GSLOOKUP 2 ") == 0 && contain(g_lookup_frame, "unavailable") > 0, "v2 shares default-off enable flag");
    set_pcvar_num(g_lookup_enabled, 1);
    QueryLookup("5477696E", false);
    CheckSaved(contain(g_lookup_frame, "busy 10") > 0, "enabling does not erase unavailable cooldown");
    g_snapshot_frame[0] = 0;
    OnLeaderboardSnapshot();
    static original_top[2048];
    CopyLeaderboardRows(original_top, charsmax(original_top));
    PreserveLookupVault(false);
    new writes = g_vault_writes, reads = g_vault_reads, files = g_leader_reads, rebuilds = g_leader_rebuilds;
    QueryLookupName("Twin");
    CheckSaved(contain(g_lookup_frame, " 10 1^n") > 0 && contain(g_lookup_frame, "GSPROW 1 2 120 12 5477696E^n") > 0
        && contain(g_lookup_frame, "GSPROW 10 1 30 3 5477696E^n") > 0 && contain(g_lookup_frame, "GSPROW 11") < 0,
        "ten duplicate names sorted by scores with explicit truncation");
    new footer[80], frame_marker[16], frame_version[8], frame_nonce[20], frame_time[16];
    parse(g_lookup_frame, frame_marker, charsmax(frame_marker), frame_version, charsmax(frame_version),
        frame_nonce, charsmax(frame_nonce), frame_time, charsmax(frame_time));
    formatex(footer, charsmax(footer), "GSPEND 1 %s %s 10 1^n", LOOKUP_NONCE, frame_time);
    CheckSaved(contain(g_lookup_frame, footer) > 0 && contain(g_lookup_frame, "STEAM_") < 0, "complete correlated frame without keys");
    QueryLookupName("Equal");
    CheckSaved(contain(g_lookup_frame, "GSPROW 1 1 42 5 ") > 0 && contain(g_lookup_frame, "GSPROW 2 1 42 5 ") > 0,
        "identical records are not collapsed");
    new matches[SAVED_LEADER_LIMIT], count, total, record[LeaderRecord], key[40];
    FindSavedPlayers("Equal", matches, count, total);
    ArrayGetArray(g_leader_records, matches[0], record);
    LookupFixtureKey(22, key, charsmax(key));
    CheckSaved(equal(record[LeaderKey], key) && count == 2 && total == 2, "private key breaks identical-score ties");
    QueryLookupName("Кириллица");
    CheckSaved(contain(g_lookup_frame, "GSPROW 1 0 1 2 D09AD0B8D180D0B8D0BBD0BBD0B8D186D0B0") > 0,
        "offline Cyrillic player below top ten is found");
    QueryLookupName("ЯЯЯЯЯЯЯЯЯЯЯЯЯЯЯa");
    CheckSaved(contain(g_lookup_frame, " 1 0^n") > 0, "31-byte UTF-8 name is accepted");
    QueryLookupName("<tag>^"&");
    CheckSaved(contain(g_lookup_frame, "3C7461673E2226") > 0 && contain(g_lookup_frame, "<tag>") < 0, "markup travels only as hex");
    QueryLookup("204361736520");
    CheckSaved(contain(g_lookup_frame, "43617365") > 0, "outer ASCII spaces trimmed consistently");
    QueryLookupName("case");
    CheckSaved(contain(g_lookup_frame, " 0 0^n") > 0, "case-sensitive exact match");
    QueryLookupName("Zero");
    CheckSaved(contain(g_lookup_frame, " 0 0^n") > 0, "zero results remain excluded");
    QueryLookupName("Player");
    CheckSaved(contain(g_lookup_frame, " 1 0^n") > 0, "only actual Player name matches, not missing-name fallbacks");
    QueryLookupName("Missing");
    CheckSaved(contain(g_lookup_frame, " 0 0^n") > 0, "empty match has complete valid frame");
    new scalar_name[32];
    CheckSaved(DecodeSearchQuery("F09F9880", scalar_name) && strlen(scalar_name) == 4, "valid four-byte UTF-8 scalar accepted");
    new const invalid[][] = { "", "0", "GG", "00", "20", "0A", "7F", "C285", "C0AF", "E080AF", "EDA080", "F4908080", "F0808080", "E282", "80", "535445414D5F", "737465616d5f", "4141414141414141414141414141414141414141414141414141414141414141" };
    for (new index = 0; index < sizeof invalid; index++)
    {
        QueryLookup(invalid[index]);
        CheckSaved(bool:equal(g_lookup_frame, "GSLOOKUP 1 invalid^n"), "malformed query rejected");
        QueryLookup(invalid[index], true, true);
        CheckSaved(bool:equal(g_lookup_frame, "GSLOOKUP 2 invalid^n"), "v2 rejects malformed input identically");
    }
    QueryLookup("43617365", false);
    CheckSaved(contain(g_lookup_frame, "busy") > 0, "invalid query consumes cooldown without retry");
    g_lookup_frame[0] = 0;
    EmitPlayerLookup("bad;nonce", "43617365");
    CheckSaved(bool:equal(g_lookup_frame, "GSLOOKUP 1 invalid^n"), "nonce cannot inject transport fields");
    g_lookup_after = 0.0;
    g_lookup_frame[0] = 0;
    server_cmd("goldsrcops_player_lookup 0123456789abcdef 43617365 extra");
    server_exec();
    CheckSaved(bool:equal(g_lookup_frame, "GSLOOKUP 1 invalid^n"), "server handler rejects extra arguments");
    g_lookup_frame[0] = 0;
    server_cmd("goldsrcops_player_lookup 0123456789abcdef 43617365");
    server_exec();
    CheckSaved(contain(g_lookup_frame, "GSPROW 1 0 1 2 43617365") > 0, "registered server handler returns expected row");
    new preferences = g_preferences_vault;
    g_preferences_vault = INVALID_HANDLE;
    QueryLookupName("Case");
    CheckSaved(contain(g_lookup_frame, "unavailable") > 0, "missing preferences is unavailable, not empty");
    g_preferences_vault = preferences;
    g_leader_ready = false;
    QueryLookupName("Case");
    CheckSaved(contain(g_lookup_frame, "unavailable") > 0, "invalid index is unavailable");
    g_leader_ready = true;
    g_snapshot_frame[0] = 0;
    OnLeaderboardSnapshot();
    static current_top[2048];
    CopyLeaderboardRows(current_top, charsmax(current_top));
    CheckSaved(bool:equal(original_top, current_top), "lookup does not alter shared top-ten cache");
    CheckSaved(writes == g_vault_writes && reads == g_vault_reads && files == g_leader_reads && rebuilds == g_leader_rebuilds,
        "lookup does not touch vaults files or leaderboard refresh");
    RunSubstringChecks();
    CheckSaved(writes == g_vault_writes && reads == g_vault_reads && files == g_leader_reads && rebuilds == g_leader_rebuilds,
        "both modes perform no vault IO or leaderboard rebuild");
    g_snapshot_frame[0] = 0;
    OnLeaderboardSnapshot();
    CopyLeaderboardRows(current_top, charsmax(current_top));
    CheckSaved(bool:equal(original_top, current_top), "v2 preserves existing top-ten rows");
    PreserveLookupVault(true);
    ReopenLookupVaults();
    PreserveLookupVault(true);
    QueryLookupName("cAs", true);
    CheckSaved(contain(g_lookup_frame, " 3 0^n") > 0, "substring survives real vault close and reload");
    // Existing identity path must refresh search names only after successful readback.
    if (!CreateStatsClients()) { set_fail_state("Lookup identity fixture unavailable."); return PLUGIN_HANDLED; }
    new player = g_clients[0];
    copy(g_auth[player], charsmax(g_auth[]), "STEAM_0:1:900024");
    EnsureSavedStats(player);
    new observed_name[32];
    get_user_name(player, observed_name, charsmax(observed_name));
    g_leader_name_recorded[player] = false;
    RememberLeaderName(player);
    new position;
    CheckSaved(TrieGetCell(g_leader_keys, g_saved_key[player], position), "loaded player belongs to index");
    ArrayGetArray(g_leader_records, position, record);
    CheckSaved(bool:equal(record[LeaderSearchName], observed_name), "successful existing name write updates cache");
    QueryLookupName(observed_name, true);
    CheckSaved(contain(g_lookup_frame, " 1 0^n") > 0, "substring sees name confirmed by readback");
    QueryLookupName("Player", true);
    CheckSaved(contain(g_lookup_frame, " 0 0^n") > 0, "substring drops the prior name after confirmed rename");
    new name_writes = g_vault_writes;
    UpdateSearchName(g_saved_key[player], "");
    g_leader_name_recorded[player] = false;
    RememberLeaderName(player);
    ArrayGetArray(g_leader_records, position, record);
    CheckSaved(equal(record[LeaderSearchName], observed_name) && g_vault_writes == name_writes,
        "equal stored name refreshes cache without another write");
    UpdateSearchName(g_saved_key[player], "Prior confirmed");
    g_leader_name_recorded[player] = false;
    g_fail_readback = true;
    RememberLeaderName(player);
    g_fail_readback = false;
    ArrayGetArray(g_leader_records, position, record);
    CheckSaved(bool:equal(record[LeaderSearchName], "Prior confirmed"), "failed readback does not publish unconfirmed name");
    QueryLookupName("prior", true);
    CheckSaved(contain(g_lookup_frame, " 1 0^n") > 0, "substring keeps the last confirmed name after failed readback");
    QueryLookupName(observed_name, true);
    CheckSaved(contain(g_lookup_frame, " 0 0^n") > 0, "substring cannot publish unconfirmed name");
    server_print("PLAYER_LOOKUP_SMOKE=%s failures=%d storage=real_nvault", g_failures ? "failed" : "passed", g_failures);
    return PLUGIN_HANDLED;
}

public RunPlayerLookupLoad()
{
    nvault_prune(g_stats_vault, 0, 0);
    nvault_prune(g_preferences_vault, 0, 0);
    for (new index = 0; index < LEADER_INDEX_LIMIT; index++) { SeedLookupPlayer(index, 1000000000, 1000000000, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"); }
    CloseLeaderFixture();
    new Float:start = Float:engfunc(EngFunc_Time);
    OpenLeaderFixture();
    new Float:startup_ms = (Float:engfunc(EngFunc_Time) - start) * 1000.0;
    CheckSaved(g_leader_ready && ArraySize(g_leader_records) == LEADER_INDEX_LIMIT, "full 4096-record index loads");
    set_pcvar_num(g_lookup_enabled, 1);
    static stamps[LEADER_INDEX_LIMIT], name_stamps[LEADER_INDEX_LIMIT];
    for (new index = 0; index < LEADER_INDEX_LIMIT; index++)
    {
        new key[48], value[64];
        LookupFixtureKey(index, key, charsmax(key));
        nvault_lookup(g_stats_vault, key, value, charsmax(value), stamps[index]);
        formatex(key, charsmax(key), "name:v1:STEAM_0:1:%d", 900000 + index);
        nvault_lookup(g_preferences_vault, key, value, charsmax(value), name_stamps[index]);
    }
    new reads = g_vault_reads, writes = g_vault_writes, files = g_leader_reads;
    new Float:total_ms[2], Float:maximum_ms[2];
    g_lookup_quiet = true;
    for (new mode = 0; mode < 2; mode++)
    {
        for (new sample = 0; sample < 32; sample++)
        {
            start = Float:engfunc(EngFunc_Time);
            QueryLookup(mode ? "616161" : "41414141414141414141414141414141414141414141414141414141414141", true, bool:mode);
            new Float:elapsed_ms = (Float:engfunc(EngFunc_Time) - start) * 1000.0;
            if (sample >= 2) { total_ms[mode] += elapsed_ms; maximum_ms[mode] = floatmax(maximum_ms[mode], elapsed_ms); }
            CheckSaved(contain(g_lookup_frame, " 10 1^n") > 0 && contain(g_lookup_frame, "GSPROW 10 ") > 0,
                "capacity query stays truncated and complete in both modes");
        }
    }
    start = Float:engfunc(EngFunc_Time);
    for (new attempt = 0; attempt < 1000; attempt++)
    {
        QueryLookup("41414141414141414141414141414141414141414141414141414141414141", false, bool:(attempt % 2));
        CheckSaved(contain(g_lookup_frame, "busy") > 0, "repeated calls cannot bypass cooldown");
    }
    new Float:busy_ms = (Float:engfunc(EngFunc_Time) - start) * 1000.0;
    g_lookup_quiet = false;
    CheckSaved(reads == g_vault_reads && writes == g_vault_writes && files == g_leader_reads, "load probe performs no vault IO");
    ReopenLookupVaults();
    for (new index = 0; index < LEADER_INDEX_LIMIT; index++)
    {
        new key[48], value[64], stamp;
        LookupFixtureKey(index, key, charsmax(key));
        CheckSaved(nvault_lookup(g_stats_vault, key, value, charsmax(value), stamp) && equal(value, "1 1000000000 1000000000") && stamp == stamps[index],
            "all statistics values and timestamps preserved");
        formatex(key, charsmax(key), "name:v1:STEAM_0:1:%d", 900000 + index);
        CheckSaved(nvault_lookup(g_preferences_vault, key, value, charsmax(value), stamp) && equal(value, "1 AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA") && stamp == name_stamps[index],
            "all name values and timestamps preserved");
    }
    CheckSaved(maximum_ms[0] < 25.0 && maximum_ms[1] < 25.0 && startup_ms < 1000.0, "lookup under 25ms and index initialization under 1s on constrained fixture");
    for (new mode = 0; mode < 2; mode++)
    {
        server_print("PLAYER_LOOKUP_TIMING version=%d samples=30 mean_ms=%.3f max_ms=%.3f", mode + 1, total_ms[mode] / 30.0, maximum_ms[mode]);
    }
    server_print("PLAYER_LOOKUP_LOAD=%s failures=%d records=4096 samples=30 mean_ms=%.3f max_ms=%.3f startup_ms=%.3f busy_1000_ms=%.3f name_payload_bytes=%d",
        g_failures ? "failed" : "passed", g_failures, total_ms[1] / 30.0, maximum_ms[1], startup_ms, busy_ms, 32 * 4 * LEADER_INDEX_LIMIT);
    return PLUGIN_HANDLED;
}

public RunKillDeathRatioSmoke()
{
    if (!CreateStatsClients()) { set_fail_state("K/D fixture unavailable."); return PLUGIN_HANDLED; }
    SeedLeaderboardRecords();
    new player = g_clients[0];
    JoinStandingPlayer(player, 1);
    g_human[g_clients[1]] = false;
    g_human[g_clients[2]] = false;
    new const scores[][2] = { { 0, 0 }, { 0, 1 }, { 7, 0 }, { 7, 1 }, { 7, 2 }, { 1, 3 }, { 2, 3 }, { 1, 8 }, { 3, 8 }, { 1, 200 }, { 1, 201 }, { 1, 199 }, { 199, 200 }, { 198, 200 }, { 399, 200 }, { 999999999, 1000000000 }, { 1000000000, 999999999 }, { 1000000000, 0 }, { 1000000000, 1 }, { 1000000000, 3 }, { 1000000000, 1000000000 }, { 994999999, 1000000000 }, { 995000000, 1000000000 }, { 995000001, 1000000000 } };
    new const expected[][] = { "0.00", "0.00", "7.00", "7.00", "3.50", "0.33", "0.67", "0.13", "0.38", "0.01", "0.00", "0.01", "1.00", "0.99", "2.00", "1.00", "1.00", "1000000000.00", "1000000000.00", "333333333.33", "1.00", "0.99", "1.00", "1.00" };
    new writes = g_vault_writes, ratio[14], needle[96], full[192];
    for (new index = 0; index < sizeof scores; index++)
    {
        FormatKillDeathRatio(scores[index][0], scores[index][1], ratio, charsmax(ratio));
        CheckSaved(equal(ratio, expected[index]) != 0, "exact half-up K/D including carry and maximum counters");
        g_saved_kills[player] = g_map_kills[player] = scores[index][0];
        g_saved_deaths[player] = g_map_deaths[player] = scores[index][1];
        formatex(needle, charsmax(needle), "K %d | D %d | K/D %s", scores[index][0], scores[index][1], expected[index]);
        for (new language = 0; language < sizeof PLAYER_LANGUAGES; language++)
        {
            g_language_override[player] = language + 1;
            g_stats_next[player][0] = 0.0;
            ShowMapStats(player);
            formatex(full, charsmax(full), "[GoldSrcOps] %L", PLAYER_LANGUAGES[language], "GS_SAVED_STATS",
                scores[index][0], scores[index][1], expected[index]);
            CheckSaved(equal(g_last_print, full) && g_print_target == player, "saved stats keep full localized ratio text privately");
            g_stats_next[player][4] = 0.0;
            ShowPlayerProfile(player);
            CheckSaved(contain(g_leader_html, needle) >= 0, "saved profile uses exact rounded ratio");
            CheckProfileScore(language, needle);
            CheckSaved(contain(g_leader_html, "</body></html>") == strlen(g_leader_html) - 14,
                "ratio boundary leaves complete profile markup");
            new vault = g_stats_vault;
            g_stats_vault = INVALID_HANDLE;
            g_saved_ready[player] = false;
            g_stats_next[player][0] = 0.0;
            ShowMapStats(player);
            formatex(full, charsmax(full), "[GoldSrcOps] %L", PLAYER_LANGUAGES[language], "GS_MAP_STATS",
                scores[index][0], scores[index][1], expected[index]);
            CheckSaved(equal(g_last_print, full) && g_print_target == player, "unavailable saved stats use complete localized map ratio");
            g_stats_vault = vault;
            g_saved_ready[player] = true;
            g_stats_next[player][1] = 0.0;
            ShowMapLeaders(player);
            new name[32];
            get_user_name(player, name, charsmax(name));
            CleanStatsName(name);
            formatex(full, charsmax(full), "[GoldSrcOps] %L", PLAYER_LANGUAGES[language], "GS_TOP_ROW",
                1, name, scores[index][0], scores[index][1], expected[index]);
            if (!scores[index][0] && !scores[index][1])
            {
                formatex(full, charsmax(full), "[GoldSrcOps] %L", PLAYER_LANGUAGES[language], "GS_TOP_EMPTY");
            }
            CheckSaved(equal(g_last_print, full) && g_print_target == player, "map leader ratio or zero-score exclusion matches localization");
        }
    }
    g_saved_kills[player] = 7;
    g_saved_deaths[player] = 2;
    g_map_kills[player] = 2;
    g_map_deaths[player] = 3;
    g_stats_next[player][4] = 0.0;
    ShowPlayerProfile(player);
    CheckSaved(contain(g_leader_html, "K 7 | D 2 | K/D 3.50") >= 0, "saved section uses saved fraction");
    CheckProfileScore(1, "K 2 | D 3 | K/D 0.67");
    CheckSaved(g_vault_writes == writes, "ratio projections never write storage");
    server_print("PLAYER_KD_RATIO_SMOKE=%s failures=%d cases=%d languages=2 storage_writes=none",
        g_failures ? "failed" : "passed", g_failures, sizeof scores);
    return PLUGIN_HANDLED;
}
