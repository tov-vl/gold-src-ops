#include <amxmodx>
#include <reapi>
#include <fakemeta>
#include <nvault>
#include <goldsrcops-process-storage.inc>

#define RECOVERY_MAX_ROWS 4096

// Only identity and write observation are fixture seams; native nVault reads stay real.
#define plugin_init ProductPluginInit
#define is_user_bot(%1) RecoveryIsBot(%1)
#define get_user_authid(%1,%2,%3) RecoveryAuth(%1,%2,%3)
#define nvault_set(%1,%2,%3) RecoveryWrite(%1,%2,%3)
#define nvault_process_commit(%1) RecoveryCommit(%1)
#include <goldsrcops_weapon_selection.sma>
#undef plugin_init
#undef is_user_bot
#undef get_user_authid
#undef nvault_set
#undef nvault_process_commit

new g_client, g_auth[32], g_failures, g_writes, g_rows, g_kinds[6];

bool:RecoveryIsBot(id) { return id != g_client || !g_auth[0]; }
RecoveryAuth(id, text[], length) { return id == g_client ? copy(text, length, g_auth) : 0; }
RecoveryWrite(vault, const key[], const value[])
{
    #pragma unused vault, key, value
    g_writes++;
    return 0;
}
bool:RecoveryCommit(vault)
{
    #pragma unused vault
    g_writes++;
    return false;
}
Check(bool:condition) { if (!condition) { g_failures++; } }

public plugin_init()
{
    ProductPluginInit();
    register_srvcmd("goldsrcops_actual_copy_check", "RunRecovery");
}

bool:DecodeHex(const encoded[], output[], length)
{
    new count = strlen(encoded);
    if (!count || count % 2 || count / 2 > length) { return false; }
    for (new i = 0; i < count; i += 2)
    {
        new high = LookupHexDigit(encoded[i]), low = LookupHexDigit(encoded[i + 1]);
        if (high < 0 || low < 0 || !(high | low)) { return false; }
        output[i / 2] = (high << 4) | low;
    }
    output[count / 2] = 0;
    return true;
}

ResetIdentity(const key[])
{
    copy(g_auth, charsmax(g_auth), key[3]);
    ResetSavedStats(g_client);
    ResetPlayerPreferences(g_client);
    g_choice[g_client] = 0;
    g_pistol_choice[g_client] = 0;
    g_language_override[g_client] = 0;
    g_hud_disabled[g_client] = false;
}

CheckSemantic(vault, const key[], const value[], rank)
{
    new first, second, third;
    if (!vault)
    {
        Check(equal(key, "v1:", 3) && DecodeSavedStats(value, first, second));
        ResetIdentity(key);
        EnsureSavedStats(g_client);
        Check(g_saved_ready[g_client] && !g_saved_blocked[g_client]
            && g_saved_kills[g_client] == first && g_saved_deaths[g_client] == second
            && RankForKills(g_saved_kills[g_client]) == rank);
        g_kinds[1]++;
    }
    else if (equal(key, "v1:", 3))
    {
        Check(DecodePlayerPreferences(value, first, second, third));
        ResetIdentity(key);
        EnsurePlayerPreferences(g_client);
        Check(g_preferences_ready[g_client] && !g_preferences_blocked[g_client]
            && g_language_override[g_client] == first && g_choice[g_client] == second
            && g_pistol_choice[g_client] == third && LoadedPlayerPreferencesSaved(g_client));
        g_kinds[2]++;
    }
    else if (equal(key, "hud:", 4))
    {
        new bool:disabled;
        Check(DecodeHudPreference(value, disabled));
        ResetIdentity(key[4]);
        EnsurePlayerPreferences(g_client);
        Check(g_hud_preference_ready[g_client] && !g_hud_preference_blocked[g_client]
            && g_hud_disabled[g_client] == disabled && PlayerHudPreferenceSaved(g_client));
        g_kinds[3]++;
    }
    else if (equal(key, "streak:", 7))
    {
        Check(DecodeSavedStreak(value, first));
        ResetIdentity(key[7]);
        EnsureSavedStreak(g_client);
        Check(g_streak_ready[g_client] && !g_streak_blocked[g_client]
            && g_saved_streak[g_client] == first && g_record_streak[g_client] == 0);
        g_kinds[4]++;
    }
    else if (equal(key, "name:", 5))
    {
        new record[LeaderRecord], expected[64];
        Check(equal(value, "1 ", 2) && ValidSearchName(value[2]));
        copy(record[LeaderKey], charsmax(record[LeaderKey]), key[5]);
        copy(expected, charsmax(expected), value[2]);
        trim(expected);
        LoadSearchName(record);
        Check(bool:equal(record[LeaderSearchName], expected));
        g_kinds[5]++;
    }
    else { g_kinds[0]++; Check(false); }
}

public RunRecovery()
{
    if (g_client || get_playersnum()) { return PLUGIN_HANDLED; }
    set_pcvar_num(g_enabled, 1);
    Check(g_stats_vault != INVALID_HANDLE && g_preferences_vault != INVALID_HANDLE
        && g_leader_ready && !g_leader_errors
        && g_process_storage_capable && nvault_process_version() == 1);
    g_client = engfunc(EngFunc_CreateFakeClient, "Recovery fixture");
    if (!g_client) { Check(false); return PLUGIN_HANDLED; }
    new rejected[128];
    dllfunc(DLLFunc_ClientConnect, g_client, "Recovery fixture", "127.0.0.1", rejected);
    dllfunc(DLLFunc_ClientPutInServer, g_client);
    new file = fopen("addons/amxmodx/data/recovery-expectations.tsv", "rt");
    if (!file) { Check(false); return PLUGIN_HANDLED; }
    new line[512];
    fgets(file, line, charsmax(line));
    new expected = str_to_num(line);
    Check(expected >= 0 && expected <= RECOVERY_MAX_ROWS);
    while (!feof(file) && g_rows <= RECOVERY_MAX_ROWS)
    {
        fgets(file, line, charsmax(line)); trim(line);
        if (!line[0]) { continue; }
        new vault_text[4], stamp_text[16], key_hex[128], value_hex[256], rank_text[4], extra[4];
        if (parse(line, vault_text, charsmax(vault_text), stamp_text, charsmax(stamp_text),
            key_hex, charsmax(key_hex), value_hex, charsmax(value_hex), rank_text,
            charsmax(rank_text), extra, charsmax(extra)) != 5) { Check(false); break; }
        new key[64], value[128], actual[128], stamp;
        if (!DecodeHex(key_hex, key, charsmax(key)) || !DecodeHex(value_hex, value, charsmax(value)))
        { Check(false); break; }
        new vault = str_to_num(vault_text);
        if (vault < 0 || vault > 1) { Check(false); break; }
        Check(nvault_lookup(vault ? g_preferences_vault : g_stats_vault, key, actual,
            charsmax(actual), stamp) != 0 && equal(actual, value) && stamp == str_to_num(stamp_text));
        CheckSemantic(vault, key, value, str_to_num(rank_text));
        g_rows++;
    }
    fclose(file);
    Check(g_rows == expected && !g_kinds[0]
        && !g_writes && !g_storage_errors && !g_preferences_errors && !g_streak_errors);
    g_auth[0] = 0;
    server_print("ACTUAL_COPY_ENGINE=%s rows=%d stats=%d preferences=%d hud=%d streak=%d names=%d writes=%d failures=%d",
        g_failures ? "failed" : "passed", g_rows, g_kinds[1], g_kinds[2], g_kinds[3],
        g_kinds[4], g_kinds[5], g_writes, g_failures);
    return PLUGIN_HANDLED;
}
