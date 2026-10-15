#pragma semicolon 1
#include <amxmodx>
#include <reapi>
#include <fakemeta>
#include <nvault>
#define plugin_init ProductPluginInit
#include <goldsrcops_weapon_selection.sma>
#undef plugin_init

// Research fixture only: no new product command or package is built.
new g_failures;
new g_folded[LEADER_INDEX_LIMIT][32];
new g_frame[2048];

public plugin_init()
{
    ProductPluginInit();
    register_srvcmd("goldsrcops_substring_research", "RunResearch");
}

Check(bool:condition, const label[])
{
    if (!condition) { g_failures++; server_print("SUBSTRING_ASSERTION_FAILED %s", label); }
}

// Fixed length-preserving map: ASCII A-Z and Russian U+0410..042F/U+0401.
// Every other validated UTF-8 byte is literal, without Unicode normalization.
Fold(const input[], output[32])
{
    copy(output, charsmax(output), input);
    for (new index = 0; output[index]; index++)
    {
        new first = output[index] & 255, second = output[index + 1] & 255;
        if (first >= 65 && first <= 90) { output[index] = first + 32; }
        else if (first == 208 && second >= 144 && second <= 175)
        {
            output[index] = second < 160 ? 208 : 209;
            output[index + 1] = second < 160 ? second + 32 : second - 32;
            index++;
        }
        else if (first == 208 && second == 129)
        {
            output[index] = 209; output[index + 1] = 145; index++;
        }
    }
}

bool:ValidPartial(const input[])
{
    if (!ValidSearchName(input)) { return false; }
    new query[32], scalars;
    copy(query, charsmax(query), input); trim(query);
    for (new index = 0; query[index]; index++)
    {
        if ((query[index] & 192) != 128) { scalars++; }
    }
    return scalars >= 3;
}

bool:Matches(const name[], const query[])
{
    new left[32], right[32]; Fold(name, left); Fold(query, right);
    return contain(left, right) >= 0;
}

FindPartial(const query[], matches[SAVED_LEADER_LIMIT], &count, &total, bool:cached)
{
    new folded_query[32]; Fold(query, folded_query);
    count = 0; total = 0;
    for (new index = 0; index < ArraySize(g_leader_records); index++)
    {
        new candidate[LeaderRecord], other[LeaderRecord], name[32], position;
        ArrayGetArray(g_leader_records, index, candidate);
        if (candidate[LeaderKills] < 0 || (!candidate[LeaderKills] && !candidate[LeaderDeaths])
            || !candidate[LeaderSearchName][0]) { continue; }
        if (cached) { copy(name, charsmax(name), g_folded[index]); }
        else { Fold(candidate[LeaderSearchName], name); }
        if (contain(name, folded_query) < 0) { continue; }
        total++;
        while (position < count)
        {
            ArrayGetArray(g_leader_records, matches[position], other);
            if (LeaderBefore(candidate, other)) { break; }
            position++;
        }
        if (position == SAVED_LEADER_LIMIT) { continue; }
        for (new move = min(count, SAVED_LEADER_LIMIT - 1); move > position; move--) { matches[move] = matches[move - 1]; }
        matches[position] = index;
        count = min(count + 1, SAVED_LEADER_LIMIT);
    }
}

Serialize(matches[SAVED_LEADER_LIMIT], count, total)
{
    formatex(g_frame, charsmax(g_frame), "GSLOOKUP 2 0123456789ABCDEF 2000000000 %d %d^n", count, total > count);
    for (new index = 0; index < count; index++)
    {
        new record[LeaderRecord], hex[63], written, row[128];
        ArrayGetArray(g_leader_records, matches[index], record);
        for (new byte = 0; record[LeaderSearchName][byte]; byte++)
        {
            written += formatex(hex[written], charsmax(hex) - written, "%02X", record[LeaderSearchName][byte] & 255);
        }
        formatex(row, charsmax(row), "GSPROW %d %d %d %d %s^n", index + 1, RankForKills(record[LeaderKills]), record[LeaderKills], record[LeaderDeaths], hex);
        add(g_frame, charsmax(g_frame), row);
    }
    new footer[96];
    formatex(footer, charsmax(footer), "GSPEND 2 0123456789ABCDEF 2000000000 %d %d^n", count, total > count);
    Check(strlen(g_frame) + strlen(footer) < sizeof g_frame, "complete frame fits byte bound");
    add(g_frame, charsmax(g_frame), footer);
}

Seed(const name[])
{
    ArrayClear(g_leader_records);
    for (new index = 0; index < LEADER_INDEX_LIMIT; index++)
    {
        new record[LeaderRecord];
        formatex(record[LeaderKey], charsmax(record[LeaderKey]), "fixture:%04d", LEADER_INDEX_LIMIT - index);
        record[LeaderKills] = record[LeaderDeaths] = 1000000000;
        copy(record[LeaderSearchName], charsmax(record[LeaderSearchName]), name);
        ArrayPushArray(g_leader_records, record);
    }
}

BuildCache()
{
    for (new index = 0; index < ArraySize(g_leader_records); index++)
    {
        new record[LeaderRecord]; ArrayGetArray(g_leader_records, index, record);
        Fold(record[LeaderSearchName], g_folded[index]);
    }
}

Measure(const label[], const name[], const query[], expected, mode)
{
    Seed(name);
    new Float:start = Float:engfunc(EngFunc_Time);
    BuildCache();
    new Float:cache_ms = (Float:engfunc(EngFunc_Time) - start) * 1000.0;
    new Float:sum, Float:maximum, matches[SAVED_LEADER_LIMIT], count, total, frame_bytes;
    for (new sample = 0; sample < 32; sample++)
    {
        start = Float:engfunc(EngFunc_Time);
        if (mode == 0) { FindSavedPlayers(query, matches, count, total); }
        else { FindPartial(query, matches, count, total, mode == 2); }
        Serialize(matches, count, total);
        new Float:elapsed = (Float:engfunc(EngFunc_Time) - start) * 1000.0;
        if (sample >= 2) { sum += elapsed; maximum = floatmax(maximum, elapsed); }
        Check(total == expected && count == min(expected, 10), "match count and truncation");
        frame_bytes = strlen(g_frame);
        if (count) { Check(matches[0] == LEADER_INDEX_LIMIT - 1, "private tie order preserved"); }
    }
    for (new index = 0; index < LEADER_INDEX_LIMIT; index++)
    {
        new record[LeaderRecord], key[40]; ArrayGetArray(g_leader_records, index, record);
        formatex(key, charsmax(key), "fixture:%04d", LEADER_INDEX_LIMIT - index);
        Check(equal(record[LeaderKey], key) && equal(record[LeaderSearchName], name)
            && record[LeaderKills] == 1000000000 && record[LeaderDeaths] == 1000000000, "source record unchanged");
    }
    server_print("SUBSTRING_BENCH case=%s mode=%d records=4096 samples=30 mean_ms=%.3f max_ms=%.3f cache_build_ms=%.3f frame_bytes=%d", label, mode, sum / 30.0, maximum, cache_ms, frame_bytes);
    Check(maximum < 25.0 && cache_ms < 1000.0, "prototype meets local pilot budgets");
}

public RunResearch()
{
    new const upper[] = "АБВГДЕЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ";
    new const lower[] = "абвгдежзийклмнопрстуфхцчшщъыьэюя";
    for (new letter = 0; letter < 32; letter++)
    {
        new name[] = {65,65,0,0,0}, query[] = {97,97,0,0,0};
        name[2] = upper[2 * letter]; name[3] = upper[2 * letter + 1];
        query[2] = lower[2 * letter]; query[3] = lower[2 * letter + 1];
        Check(Matches(name, query) && Matches(query, name), "all Russian case pairs are symmetric");
    }
    Check(Matches("[TAG]ALex", "alex"), "ASCII case and infix");
    Check(Matches("[TAG]ИГРОК", "игр"), "Russian case and infix");
    Check(Matches("ЯЮЭЩШЧЦХФУТСРПО", "яюэ"), "upper Russian range");
    new upper_special[] = {208,129,208,150,208,152,208,154,0};
    new lower_special[] = {209,145,208,182,208,184,0};
    Check(Matches(upper_special, lower_special), "U+0401 maps to U+0451");
    Check(!Matches(upper_special, "ежи"), "U+0451 and U+0435 remain distinct");
    Check(!Matches("Игрок", "Igr"), "no transliteration");
    Check(!Matches("café", "cafe"), "no accent removal");
    Check(!Matches("CAFÉ", "café"), "non-ASCII Latin case is literal");
    new decomposed[] = {97,102,101,204,129,0};
    Check(!Matches("café", decomposed), "no NFC normalization");
    new malformed[] = {97,98,128,0};
    Check(!ValidPartial(malformed), "malformed UTF-8 refused");
    Check(ValidPartial("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA") && !ValidPartial("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"), "31-byte cap retained");
    Check(!Matches("Straße", "STRASSE"), "no full Unicode expansion");
    Check(!Matches("ABC", "АВС"), "no mixed-script equivalence");
    Check(Matches("[TAG]A*B", "a*b"), "wildcard characters are literal");
    Check(ValidPartial("игр") && ValidPartial("abc") && !ValidPartial("иг") && !ValidPartial("  a  "), "minimum is three scalars after ASCII trim");
    Check(!ValidPartial("STEAM_") && !ValidPartial("abc^n"), "identifier and control rejection retained");
    Measure("ascii_all", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", 4096, 0);
    Measure("ascii_all", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "aaa", 4096, 1);
    Measure("ascii_all", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "aaa", 4096, 2);
    Measure("russian_all", "АААААААААААААААX", "ааа", 4096, 1);
    Measure("russian_all", "АААААААААААААААX", "ааа", 4096, 2);
    Measure("no_match", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "zzz", 0, 1);
    Measure("no_match", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "zzz", 0, 2);
    Measure("late_match", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAXYZ", "xyz", 4096, 1);
    Measure("late_match", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAXYZ", "xyz", 4096, 2);
    Measure("near_miss", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaab", 0, 1);
    Measure("near_miss", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaab", 0, 2);
    // A singleton before the top ten, distinct stored names, and excluded records.
    Seed("Other");
    new record[LeaderRecord];
    ArrayGetArray(g_leader_records, 4000, record); copy(record[LeaderSearchName], charsmax(record[LeaderSearchName]), "[TAG]Alex"); record[LeaderKills] = 1; record[LeaderDeaths] = 2; ArraySetArray(g_leader_records, 4000, record);
    ArrayGetArray(g_leader_records, 4001, record); copy(record[LeaderSearchName], charsmax(record[LeaderSearchName]), "AlexTwo"); record[LeaderKills] = 3; ArraySetArray(g_leader_records, 4001, record);
    ArrayGetArray(g_leader_records, 4002, record); copy(record[LeaderSearchName], charsmax(record[LeaderSearchName]), "AlexZero"); record[LeaderKills] = record[LeaderDeaths] = 0; ArraySetArray(g_leader_records, 4002, record);
    BuildCache();
    new matches[SAVED_LEADER_LIMIT], count, total; FindPartial("aLeX", matches, count, total, true); Serialize(matches, count, total);
    Check(count == 2 && total == 2 && matches[0] == 4001 && matches[1] == 4000, "outside top ten, ordering, and zero exclusion");
    Check(contain(g_frame, "416C657854776F") > 0 && contain(g_frame, "5B5441475D416C6578") > 0, "response contains each actual stored name");
    Check(contain(g_frame, "fixture:") < 0, "response excludes private keys");
    server_print("SUBSTRING_RESEARCH=%s failures=%d cache_payload_bytes=%d", g_failures ? "failed" : "passed", g_failures, LEADER_INDEX_LIMIT * 32 * 4);
    return PLUGIN_HANDLED;
}
