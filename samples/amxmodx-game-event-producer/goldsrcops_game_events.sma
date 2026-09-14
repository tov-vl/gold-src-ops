#pragma semicolon 1
#pragma tabsize 4

#include <amxmodx>
#include <file>
#include <reapi>

#define PLUGIN_NAME "GoldSrcOps Game Event Producer"
#define PLUGIN_VERSION "0.1.0"
#define PLUGIN_AUTHOR "GoldSrcOps"

#define SPOOL_VERSION 1
#define MAX_MAP_NAME_LENGTH 128
#define MAX_INCOMING_PATH_LENGTH 180
#define MAX_PAYLOAD_LENGTH 512
#define MAX_SPOOL_RECORD_BYTES 4096
#define RECORD_ID_LENGTH 36
#define RECORD_ID_ATTEMPTS 8

new const HEX_DIGITS[] = "0123456789abcdef";

new g_enabledCvar;
new g_incomingPathCvar;
new bool:g_roundEventPublished;
new g_emittedCount;
new g_failedCount;
new g_ignoredCount;

public plugin_init()
{
    register_plugin(PLUGIN_NAME, PLUGIN_VERSION, PLUGIN_AUTHOR);

    g_enabledCvar = register_cvar("goldsrcops_events_enabled", "0", FCVAR_NONE);
    g_incomingPathCvar = register_cvar(
        "goldsrcops_spool_incoming",
        "addons/amxmodx/data/goldsrcops-spool/incoming",
        FCVAR_NONE);

    RegisterHookChain(RG_RoundEnd, "OnRoundEndPost", true);
    RegisterHookChain(RG_CSGameRules_RestartRound, "OnRoundRestartPost", true);
    register_srvcmd("goldsrcops_events_status", "OnStatusCommand");
}

public OnRoundEndPost(
    const WinStatus:status,
    const ScenarioEventEndRound:event,
    const Float:delay)
{
    #pragma unused status, delay

    if (!get_pcvar_num(g_enabledCvar))
    {
        return HC_CONTINUE;
    }

    if (!IsObservableRoundEnd(event) || g_roundEventPublished)
    {
        g_ignoredCount++;
        return HC_CONTINUE;
    }

    new map[MAX_MAP_NAME_LENGTH + 1];
    get_mapname(map, charsmax(map));
    if (!IsSafeMapName(map))
    {
        g_failedCount++;
        log_amx("Game-event spool publish failed: invalid map name.");
        return HC_CONTINUE;
    }

    new occurredAtUtc[21];
    if (!FormatUtcTimestamp(get_systime(), occurredAtUtc, charsmax(occurredAtUtc)))
    {
        g_failedCount++;
        log_amx("Game-event spool publish failed: UTC timestamp conversion failed.");
        return HC_CONTINUE;
    }

    new players[MAX_PLAYERS];
    new playerCount;
    new botCount;
    get_players(players, playerCount, "h");

    for (new index = 0; index < playerCount; index++)
    {
        if (is_user_bot(players[index]))
        {
            botCount++;
        }
    }

    if (!PublishRoundEnded(occurredAtUtc, map, playerCount, botCount))
    {
        g_failedCount++;
        log_amx("Game-event spool publish failed: record was not committed.");
        return HC_CONTINUE;
    }

    g_roundEventPublished = true;
    g_emittedCount++;
    return HC_CONTINUE;
}

public OnRoundRestartPost()
{
    g_roundEventPublished = false;
    return HC_CONTINUE;
}

public OnStatusCommand()
{
    server_print(
        "[GoldSrcOps] enabled=%d emitted=%d failed=%d ignored=%d",
        get_pcvar_num(g_enabledCvar),
        g_emittedCount,
        g_failedCount,
        g_ignoredCount);

    return PLUGIN_HANDLED;
}

bool:PublishRoundEnded(
    const occurredAtUtc[],
    const map[],
    const playerCount,
    const botCount)
{
    new incomingPath[PLATFORM_MAX_PATH];
    get_pcvar_string(g_incomingPathCvar, incomingPath, charsmax(incomingPath));
    trim(incomingPath);

    if (!IsSafeRelativePath(incomingPath) || !dir_exists(incomingPath))
    {
        return false;
    }

    new recordId[RECORD_ID_LENGTH + 1];
    new temporaryPath[PLATFORM_MAX_PATH];
    new readyPath[PLATFORM_MAX_PATH];
    new payload[MAX_PAYLOAD_LENGTH];

    for (new attempt = 0; attempt < RECORD_ID_ATTEMPTS; attempt++)
    {
        GenerateRecordId(recordId, charsmax(recordId));
        formatex(
            temporaryPath,
            charsmax(temporaryPath),
            "%s/%s.tmp",
            incomingPath,
            recordId);
        formatex(
            readyPath,
            charsmax(readyPath),
            "%s/%s.json",
            incomingPath,
            recordId);

        if (file_exists(temporaryPath) || file_exists(readyPath))
        {
            continue;
        }

        new payloadLength = formatex(
            payload,
            charsmax(payload),
            "{^"spoolVersion^":%d,^"recordId^":^"%s^",^"event^":{^"type^":^"round.ended^",^"occurredAtUtc^":^"%s^",^"map^":^"%s^",^"players^":%d,^"bots^":%d}}",
            SPOOL_VERSION,
            recordId,
            occurredAtUtc,
            map,
            playerCount,
            botCount);
        if (payloadLength <= 0 ||
            payloadLength >= charsmax(payload) ||
            payloadLength > MAX_SPOOL_RECORD_BYTES)
        {
            return false;
        }

        return CommitRecord(temporaryPath, readyPath, payload, payloadLength);
    }

    return false;
}

bool:CommitRecord(
    const temporaryPath[],
    const readyPath[],
    const payload[],
    const payloadLength)
{
    new file = fopen(temporaryPath, "wb");
    if (!file)
    {
        return false;
    }

    if (!SetFilePermissions(temporaryPath, FPERM_U_READ | FPERM_U_WRITE))
    {
        fclose(file);
        delete_file(temporaryPath);
        return false;
    }

    new writeResult = fputs(file, payload);
    new flushResult = fflush(file);
    fclose(file);

    if (writeResult != 0 ||
        flushResult != 0 ||
        file_size(temporaryPath, FSOPT_BYTES_COUNT) != payloadLength)
    {
        delete_file(temporaryPath);
        return false;
    }

    if (!rename_file(temporaryPath, readyPath, true))
    {
        delete_file(temporaryPath);
        return false;
    }

    return true;
}

bool:IsObservableRoundEnd(const ScenarioEventEndRound:event)
{
    return event != ROUND_NONE &&
        event != ROUND_GAME_COMMENCE &&
        event != ROUND_GAME_RESTART;
}

bool:IsSafeMapName(const value[])
{
    new length = strlen(value);
    if (length <= 0 || length > MAX_MAP_NAME_LENGTH ||
        !IsAsciiLetterOrDigit(value[0]) ||
        !IsAsciiLetterOrDigit(value[length - 1]))
    {
        return false;
    }

    for (new index = 0; index < length; index++)
    {
        new character = value[index];
        if (!IsAsciiLetterOrDigit(character) && character != '_' && character != '-')
        {
            return false;
        }
    }

    return true;
}

bool:IsSafeRelativePath(const value[])
{
    new length = strlen(value);
    if (length <= 0 || length > MAX_INCOMING_PATH_LENGTH ||
        value[0] == '/' || value[0] == 92 ||
        value[length - 1] == '/' || value[length - 1] == 92 ||
        contain(value, "..") != -1)
    {
        return false;
    }

    for (new index = 0; index < length; index++)
    {
        new character = value[index];
        if (IsAsciiLetterOrDigit(character) ||
            character == '_' || character == '-' ||
            character == '.' || character == '/')
        {
            continue;
        }

        return false;
    }

    return true;
}

bool:IsAsciiLetterOrDigit(const character)
{
    return (character >= '0' && character <= '9') ||
        (character >= 'A' && character <= 'Z') ||
        (character >= 'a' && character <= 'z');
}

GenerateRecordId(output[], const maximumLength)
{
    if (maximumLength < RECORD_ID_LENGTH)
    {
        output[0] = '^0';
        return;
    }

    new bytes[16];
    for (new index = 0; index < sizeof(bytes); index++)
    {
        bytes[index] = random_num(0, 255);
    }

    bytes[6] = (bytes[6] & 0x0f) | 0x40;
    bytes[8] = (bytes[8] & 0x3f) | 0x80;

    new outputIndex;
    for (new byteIndex = 0; byteIndex < sizeof(bytes); byteIndex++)
    {
        if (byteIndex == 4 || byteIndex == 6 || byteIndex == 8 || byteIndex == 10)
        {
            output[outputIndex++] = '-';
        }

        output[outputIndex++] = HEX_DIGITS[(bytes[byteIndex] >> 4) & 0x0f];
        output[outputIndex++] = HEX_DIGITS[bytes[byteIndex] & 0x0f];
    }

    output[outputIndex] = '^0';
}

bool:FormatUtcTimestamp(const timestamp, output[], const maximumLength)
{
    if (timestamp < 0 || maximumLength < 20)
    {
        output[0] = '^0';
        return false;
    }

    new days = timestamp / 86400;
    new secondsOfDay = timestamp % 86400;
    new year = 1970;

    new daysInYear = IsLeapYear(year) ? 366 : 365;
    while (days >= daysInYear)
    {
        days -= daysInYear;
        year++;
        daysInYear = IsLeapYear(year) ? 366 : 365;
    }

    new month = 1;
    new daysInMonth = DaysInMonth(year, month);
    while (days >= daysInMonth)
    {
        days -= daysInMonth;
        month++;
        daysInMonth = DaysInMonth(year, month);
    }

    new day = days + 1;
    new hour = secondsOfDay / 3600;
    new minute = (secondsOfDay % 3600) / 60;
    new second = secondsOfDay % 60;

    return formatex(
        output,
        maximumLength,
        "%04d-%02d-%02dT%02d:%02d:%02dZ",
        year,
        month,
        day,
        hour,
        minute,
        second) == 20;
}

bool:IsLeapYear(const year)
{
    return (year % 4 == 0 && year % 100 != 0) || year % 400 == 0;
}

DaysInMonth(const year, const month)
{
    static const daysByMonth[] = { 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };
    if (month == 2 && IsLeapYear(year))
    {
        return 29;
    }

    return daysByMonth[month - 1];
}
