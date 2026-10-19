#include <amxmodx>
#include <reapi>
#include <fakemeta>
#include <nvault>

// Human identity and readback failure are fixture-only; nVault stays real.
#define plugin_init ProductPluginInit
#define menu_setprop FixtureMenuSetProp
#define is_user_bot(%1) FixtureIsBot(%1)
#define get_user_authid(%1,%2,%3) FixtureAuthId(%1,%2,%3)
#define nvault_lookup(%1,%2,%3,%4,%5) FixtureLookup(%1,%2,%3,%4,%5)
#define nvault_set(%1,%2,%3) FixtureSet(%1,%2,%3)
#define client_print FixturePrint
#define client_print_color FixtureColorPrint
#define menu_display(%1,%2) FixtureDisplay(%1,%2)
#include <goldsrcops_weapon_selection.sma>
#undef plugin_init
#undef menu_setprop
#undef is_user_bot
#undef get_user_authid
#undef nvault_lookup
#undef nvault_set
#undef client_print
#undef client_print_color
#undef menu_display

new g_clients[2];
new g_auth[MAX_PLAYERS + 1][32];
new g_failures;
new g_menu_text[MAX_PLAYERS + 1][768];
new Trie:g_menu_titles;
new bool:g_fail_lookup;
new bool:g_fail_hud_lookup;
new bool:g_bot_identity;
new g_preference_writes;
new const KEY[] = "v1:STEAM_0:1:434343";
new const HUD_KEY[] = "hud:v1:STEAM_0:1:434343";
new g_last_print[256];
new g_previous_print[256];
new g_prints;
new g_color_prints;
new g_print_client;


// Synthetic clients have no observable ShowMenu transport; capture the exact native title input.
FixtureMenuSetProp(menu, property, any:...)
{
    if (property == MPROP_TITLE || property == MPROP_EXITNAME)
    {
        new text[384], index;
        while (index < charsmax(text) && (text[index] = getarg(2, index))) { index++; }
        text[index] = 0;
        if (property == MPROP_TITLE)
        {
            new key[16];
            num_to_str(menu, key, charsmax(key));
            TrieSetString(g_menu_titles, key, text);
        }
        return menu_setprop(menu, property, text);
    }
    return menu_setprop(menu, property, getarg(2));
}

FindUtf8Bytes(const text[], const fragment[])
{
    // Native menu/Trie strings and formatted translations may extend UTF-8 bytes differently.
    for (new start = 0; text[start]; start++)
    {
        new offset;
        while (fragment[offset] && text[start + offset]
            && (text[start + offset] & 255) == (fragment[offset] & 255)) { offset++; }
        if (!fragment[offset]) { return start; }
    }
    return -1;
}

CaptureMenuTitle(id, menu)
{
    new key[16];
    num_to_str(menu, key, charsmax(key));
    g_menu_text[id][0] = 0;
    TrieGetString(g_menu_titles, key, g_menu_text[id], charsmax(g_menu_text[]));
}

public plugin_init()
{
    g_menu_titles = TrieCreate();
    ProductPluginInit();
    register_srvcmd("goldsrcops_preferences_smoke", "RunPreferencesSmoke");
    register_srvcmd("goldsrcops_preferences_seed", "SeedPreferences");
    register_srvcmd("goldsrcops_preferences_verify", "VerifyPreferences");
}
bool:FixtureIsBot(id) { return g_bot_identity || (id != g_clients[0] && id != g_clients[1] && is_user_bot(id)); }
FixtureAuthId(id, buffer[], length) { return copy(buffer, length, g_auth[id]); }
FixtureLookup(vault, const key[], value[], length, &timestamp)
{
    return vault == g_preferences_vault && (g_fail_lookup || (g_fail_hud_lookup && equal(key, "hud:", 4)))
        ? 0 : nvault_lookup(vault, key, value, length, timestamp);
}
FixtureSet(vault, const key[], const value[])
{
    if (vault == g_preferences_vault) { g_preference_writes++; }
    return nvault_set(vault, key, value);
}
FixtureColorPrint(id, sender, const message[], any:...)
{
    #pragma unused sender
    g_color_prints++;
    new colored[512], plain[192], written;
    vformat(colored, charsmax(colored), message, 4);
    CheckPreferences(colored[0] == 4 && contain(colored, "^1") > 0, "private feedback has the shared color prefix and reset");
    CheckPreferences(strlen(colored) <= 190 && contain(colored, "ML_NOTFOUND") < 0, "colored preference feedback fits the chat payload");
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
    g_prints++;
    g_print_client = id;
    copy(g_previous_print, charsmax(g_previous_print), g_last_print);
    return vformat(g_last_print, charsmax(g_last_print), message, 4);
}
FixtureDisplay(id, menu)
{
    CaptureMenuTitle(id, menu);
    return menu_display(id, menu);
}
CheckPreferences(bool:passed, const label[])
{
    if (!passed) { g_failures++; server_print("PLAYER_PREFERENCES_ASSERTION_FAILED %s", label); }
}
bool:CreatePreferencesClients()
{
    if (get_playersnum() || g_clients[0] || g_preferences_vault == INVALID_HANDLE) { return false; }
    set_pcvar_num(g_enabled, 1);
    for (new index = 0; index < sizeof g_clients; index++)
    {
        new id = engfunc(EngFunc_CreateFakeClient, "Preferences fixture"), rejected[128];
        if (!id) { return false; }
        g_clients[index] = id;
        copy(g_auth[id], charsmax(g_auth[]), "STEAM_ID_PENDING");
        dllfunc(DLLFunc_ClientConnect, id, "Preferences fixture", "127.0.0.1", rejected);
        dllfunc(DLLFunc_ClientPutInServer, id);
        set_user_info(id, "lang", "ru");
        rg_set_user_team(id, TEAM_CT);
        set_member(id, m_iJoiningState, JOINED);
        client_putinserver(id);
        rg_round_respawn(id);
        remove_task(WELCOME_TASK_BASE + id);
    }
    return true;
}
bool:StoredPreferences(const expected[])
{
    new value[64], timestamp;
    return bool:(nvault_lookup(g_preferences_vault, KEY, value, charsmax(value), timestamp) && equal(value, expected));
}

public RunPreferencesSmoke()
{
    if (!CreatePreferencesClients()) { set_fail_state("Preferences fixture unavailable."); return PLUGIN_HANDLED; }
    new language, primary, pistol, value[64], timestamp;
    CheckPreferences(DecodePlayerPreferences("1 2 1 2", language, primary, pistol)
        && language == 2 && primary == 1 && pistol == 2, "canonical schema parsed");
    new const invalid[][] = { "", "2 2 1 2", "1 3 0 0", "1 0 3 0", "1 0 0 3",
        "1 -1 0 0", "1 0 0 -1", "1 0 01 0", "1 x 0 0", "1 0 0 0 ", "1 0 0 0 0", "^"1^" 0 0 0" };
    for (new index = 0; index < sizeof invalid; index++)
    {
        CheckPreferences(!DecodePlayerPreferences(invalid[index], language, primary, pistol), "malformed record rejected");
    }
    nvault_remove(g_preferences_vault, KEY);
    nvault_remove(g_preferences_vault, HUD_KEY);
    nvault_set(g_stats_vault, KEY, "1 7 3");
    new id = g_clients[0], other = g_clients[1];
    copy(g_auth[id], charsmax(g_auth[]), "STEAM_0:1:434343");
    client_authorized(id, g_auth[id]);
    CheckPreferences(g_preferences_ready[id] && !nvault_lookup(g_preferences_vault, KEY, value, charsmax(value), timestamp),
        "join is read-only with no default record");
    CheckPreferences(!PlayerPreferencesSaved(id), "new defaults are not described as saved");
    new writes = g_preference_writes;
    OpenPlayerSettings(id);
    CheckPreferences(g_preference_writes == writes && !nvault_lookup(g_preferences_vault, KEY, value, charsmax(value), timestamp), "settings inspection does not create defaults");
    ShowPlayerHelp(id);
    CheckPreferences(g_preference_writes == writes && !nvault_lookup(g_preferences_vault, KEY, value, charsmax(value), timestamp), "help does not create default preferences");
    OnLanguageSelected(id, g_language_menu, 1);
    OnWeaponSelected(id, g_menu[1], 1);
    OnPistolSelected(id, g_pistol_menu[1], 2);
    CheckPreferences(StoredPreferences("1 2 1 2"), "menu choices saved with readback");
    CheckPreferences(PlayerPreferencesSaved(id), "matching validated record is described as saved");
    nvault_lookup(g_preferences_vault, KEY, value, charsmax(value), timestamp);
    new saved_timestamp = timestamp;
    writes = g_preference_writes;
    OpenPlayerSettings(id);
    nvault_lookup(g_preferences_vault, KEY, value, charsmax(value), timestamp);
    CheckPreferences(g_preference_writes == writes && StoredPreferences("1 2 1 2") && timestamp == saved_timestamp, "settings inspection does not rewrite saved record");
    new hud_text[256];
    BuildPlayerHudText(id, hud_text, charsmax(hud_text));
    OnSettingsSelected(id, GetSettingsMenu(id, 1, 0), 2);
    OnSettingsSelected(id, GetSettingsMenu(id, 1, 1), 2);
    CheckPreferences(g_preference_writes == writes + 2 && StoredPreferences("1 2 1 2") && !g_hud_disabled[id], "HUD toggles save separately without rewriting language and weapons");
    CheckPreferences(StoredHudPreference("1 0") && PlayerHudPreferenceSaved(id), "explicit on is saved with readback");
    RunHelpPersistenceChecks(id);
    g_choice[id] = 0;
    CheckPreferences(!PlayerPreferencesSaved(id), "different session choice is not described as saved");
    g_choice[id] = 1;
    g_preferences_dirty[id] = PREFERENCE_LANGUAGE;
    writes = g_preference_writes;
    g_preferences_ready[id] = false;
    new settings_prints = g_prints;
    OpenPlayerSettings(id);
    OnPlayerMenuSelected(id, g_player_menu[1], 6);
    CheckPreferences(!g_preferences_ready[id] && g_preferences_dirty[id] == PREFERENCE_LANGUAGE
        && g_preference_writes == writes && g_prints == settings_prints,
        "direct and hub settings inspection cannot load or flush a pending preference");
    g_preferences_ready[id] = true;
    g_help_next[id] = 0.0;
    ShowPlayerHelp(id);
    CheckPreferences(g_preference_writes == writes && g_preferences_dirty[id] == PREFERENCE_LANGUAGE
        && StoredPreferences("1 2 1 2"), "help cannot flush pending preferences");
    CheckHelpFeedback(id, "GS_HELP_HUD_SAVED");
    CheckPreferences(!PlayerPreferencesSaved(id), "pending choice is not described as saved");
    g_preferences_dirty[id] = 0;
    OnWeaponSelected(id, g_menu[0], 2);
    OnWeaponSelected(id, g_menu[1], MENU_EXIT);
    OnPistolSelected(id, g_pistol_menu[1], 3);
    OnLanguageSelected(id, g_language_menu, MENU_EXIT);
    CheckPreferences(StoredPreferences("1 2 1 2"), "stale invalid and cancelled callbacks do not change record");
    CheckPreferences(!user_has_weapon(id, CSW_AK47), "selection does not change live inventory");
    rg_round_respawn(id);
    CheckPreferences(user_has_weapon(id, CSW_AK47) && user_has_weapon(id, CSW_DEAGLE), "restored loadout applied at spawn");
    new rejected[128];
    client_disconnected(id, false, rejected, charsmax(rejected));
    copy(g_auth[id], charsmax(g_auth[]), "STEAM_1:1:434343");
    client_putinserver(id);
    CheckPreferences(PlayerLanguage(id) == 1 && g_choice[id] == 1 && g_pistol_choice[id] == 2,
        "reconnect restores canonical Steam preferences despite client ru");

    copy(g_auth[other], charsmax(g_auth[]), "STEAM_0:1:434343");
    client_putinserver(other);
    OnLanguageSelected(other, g_language_menu, 0);
    CheckPreferences(!g_preferences_ready[other] && StoredPreferences("1 2 1 2"), "duplicate cannot overwrite live owner");
    CheckPreferences(!PlayerPreferencesSaved(other), "duplicate connection is not described as saved");
    copy(g_auth[other], charsmax(g_auth[]), "STEAM_ID_LAN");
    client_putinserver(other);
    OnLanguageSelected(other, g_language_menu, 1);
    CheckPreferences(!g_preferences_ready[other] && PlayerLanguage(other) == 1, "shared identity is connection-only");

    copy(g_auth[id], charsmax(g_auth[]), "STEAM_0:1:434344");
    nvault_remove(g_preferences_vault, "v1:STEAM_0:1:434344");
    client_putinserver(id);
    CheckPreferences(!g_language_override[id] && !g_choice[id] && !g_pistol_choice[id], "slot reuse clears other identity choices");
    copy(g_auth[id], charsmax(g_auth[]), "STEAM_ID_PENDING");
    client_putinserver(id);
    OnLanguageSelected(id, g_language_menu, 0);
    OnWeaponSelected(id, g_menu[0], 2);
    copy(g_auth[id], charsmax(g_auth[]), "STEAM_0:1:434343");
    client_authorized(id, g_auth[id]);
    CheckPreferences(g_language_override[id] == 1 && g_choice[id] == 2 && g_pistol_choice[id] == 2
        && StoredPreferences("1 1 2 2"), "late auth merges untouched pistol without replacing current choices");

    set_pcvar_num(g_enabled, 0);
    OnLanguageSelected(id, g_language_menu, 1);
    OnWeaponSelected(id, g_menu[0], 0);
    CheckPreferences(StoredPreferences("1 1 2 2"), "disabled callbacks cannot write");
    set_pcvar_num(g_enabled, 1);
    g_fail_lookup = true;
    CheckPreferences(!PlayerPreferencesSaved(id), "lookup failure is not described as saved");
    OnLanguageSelected(id, g_language_menu, 1);
    g_fail_lookup = false;
    CheckPreferences(g_preferences_blocked[id] && !g_preferences_ready[id], "failed readback blocks further writes");
    CheckPreferences(!PlayerPreferencesSaved(id), "blocked connection is not described as saved");
    OnWeaponSelected(id, g_menu[1], 0);
    CheckPreferences(StoredPreferences("1 2 2 2") && !g_choice[id], "failed storage keeps usable session choice without retry");

    RunSettingsFeedbackChecks(id, other);
    RunHudPreferenceChecks(id, other);
    nvault_set(g_preferences_vault, KEY, "2 99 99 99");
    client_putinserver(id);
    OnLanguageSelected(id, g_language_menu, 1);
    CheckPreferences(g_preferences_blocked[id] && StoredPreferences("2 99 99 99"), "corrupt record retained without overwrite");
    CheckPreferences(!PlayerPreferencesSaved(id), "corrupt record is not described as saved");
    nvault_close(g_preferences_vault);
    g_preferences_vault = INVALID_HANDLE;
    client_putinserver(id);
    OnLanguageSelected(id, g_language_menu, 1);
    new unavailable_writes = g_preference_writes;
    OnSettingsSelected(id, GetSettingsMenu(id, 1, 0), 2);
    CheckPreferences(PlayerLanguage(id) == 1 && !g_preferences_ready[id], "unavailable vault keeps connection selection");
    CheckPreferences(g_hud_disabled[id] && !PlayerHudPreferenceSaved(id) && g_preference_writes == unavailable_writes,
        "unavailable vault keeps a usable session HUD toggle without writes");
    CheckPreferences(!PlayerPreferencesSaved(id), "unavailable storage is not described as saved");
    CheckHelpFeedback(id, "GS_HELP_SESSION");
    g_preferences_vault = nvault_open(PREFERENCES_VAULT_NAME);
    CheckPreferences(nvault_lookup(g_stats_vault, KEY, value, charsmax(value), timestamp) && equal(value, "1 7 3"),
        "stats vault sentinel unchanged by preferences");
    nvault_remove(g_preferences_vault, KEY);
    nvault_remove(g_preferences_vault, HUD_KEY);
    server_print("PLAYER_SETTINGS_SMOKE=%s failures=%d languages=2 storage=real_nvault", g_failures ? "failed" : "passed", g_failures);
    server_print("PLAYER_PREFERENCES_SMOKE=%s failures=%d storage=real_nvault", g_failures ? "failed" : "passed", g_failures);
    return PLUGIN_HANDLED;
}

bool:StoredHudPreference(const expected[])
{
    new value[64], timestamp;
    return bool:(nvault_lookup(g_preferences_vault, HUD_KEY, value, charsmax(value), timestamp) && equal(value, expected));
}

CheckHelpFeedback(id, const key[])
{
    new expected[256], writes = g_preference_writes, dirty = g_preferences_dirty[id];
    new bool:hud_dirty = g_hud_preference_dirty[id];
    formatex(expected, charsmax(expected), "[GoldSrcOps] %L", PLAYER_LANGUAGES[PlayerLanguage(id)], key);
    g_help_next[id] = 0.0;
    ShowPlayerHelp(id);
    CheckPreferences(bool:equal(g_last_print, expected), "help summary reflects independent persistence states");
    CheckPreferences(g_preference_writes == writes && g_preferences_dirty[id] == dirty
        && g_hud_preference_dirty[id] == hud_dirty, "help summary does not write or consume pending preferences");
}

RunHelpPersistenceChecks(id)
{
    new const expected_keys[][] = { "GS_HELP_SESSION", "GS_HELP_HUD_SAVED", "GS_HELP_SETTINGS_SAVED", "GS_HELP_SAVED" };
    for (new language = 0; language < 2; language++)
    {
        for (new scenario = 0; scenario < 4; scenario++)
        {
            new record[32];
            formatex(record, charsmax(record), "1 %d 1 2", language + 1);
            if (scenario >= 2) { nvault_set(g_preferences_vault, KEY, record); }
            else { nvault_remove(g_preferences_vault, KEY); }
            if (scenario % 2) { nvault_set(g_preferences_vault, HUD_KEY, "1 0"); }
            else { nvault_remove(g_preferences_vault, HUD_KEY); }
            client_putinserver(id);
            g_language_override[id] = language + 1;
            CheckPreferences(PlayerPreferencesSaved(id) == (scenario >= 2)
                && PlayerHudPreferenceSaved(id) == bool:(scenario % 2), "help matrix uses independently verified nVault records");
            CheckHelpFeedback(id, expected_keys[scenario]);
            CheckHudFeedback(id, bool:(scenario % 2), scenario >= 2);
        }
    }
    // The final case restores the original EN loadout and saved enabled HUD.
    CheckPreferences(StoredPreferences("1 2 1 2") && StoredHudPreference("1 0"), "help matrix preserves the saved baseline");
}

CheckHudFeedback(id, bool:saved, bool:preferences_saved)
{
    new expected[192], expected_preferences[192], writes = g_preference_writes, prints = g_prints;
    new dirty = g_preferences_dirty[id], bool:hud_dirty = g_hud_preference_dirty[id], language = EffectivePlayerLanguage(id);
    formatex(expected, charsmax(expected), "%L", PLAYER_LANGUAGES[language], saved ? "GS_HUD_SAVED" : "GS_HUD_SESSION");
    formatex(expected_preferences, charsmax(expected_preferences), "%L", PLAYER_LANGUAGES[language],
        preferences_saved ? "GS_SETTINGS_SAVED" : "GS_SETTINGS_SESSION");
    OpenPlayerSettings(id);
    OpenPlayerSettings(id);
    CheckPreferences(g_prints == prints && g_preference_writes == writes && g_preferences_dirty[id] == dirty
        && g_hud_preference_dirty[id] == hud_dirty, "settings inspection is silent and does not write or consume pending changes");
    CheckPreferences(FindUtf8Bytes(g_menu_text[id], expected) >= 0 && FindUtf8Bytes(g_menu_text[id], expected_preferences) >= 0,
        "settings menu reflects independent verified persistence states");
    CheckHelpFeedback(id, preferences_saved
        ? (saved ? "GS_HELP_SAVED" : "GS_HELP_SETTINGS_SAVED")
        : (saved ? "GS_HELP_HUD_SAVED" : "GS_HELP_SESSION"));
}

RunSettingsFeedbackChecks(id, other)
{
    nvault_remove(g_preferences_vault, KEY);
    nvault_remove(g_preferences_vault, HUD_KEY);
    copy(g_auth[id], charsmax(g_auth[]), "STEAM_0:1:434343");
    copy(g_auth[other], charsmax(g_auth[]), "STEAM_ID_LAN");
    client_putinserver(id);
    client_putinserver(other);
    for (new language = 0; language < 2; language++)
    {
        new prints = g_prints, colors = g_color_prints;
        OnLanguageSelected(id, g_language_menu, language);
        CheckPreferences(g_prints == prints + 1 && g_color_prints == colors + 1 && g_print_client == id,
            "language change emits one private colored confirmation");
        new expected[192], status[96];
        formatex(status, charsmax(status), "%L", PLAYER_LANGUAGES[language], "GS_CHANGE_SAVED");
        formatex(expected, charsmax(expected), "[GoldSrcOps] %L", PLAYER_LANGUAGES[language], "GS_LANGUAGE_SELECTED",
            language == 0 ? "RU" : "EN", status);
        CheckPreferences(bool:equal(g_last_print, expected), "language confirmation uses the selected language and verified save");
        prints = g_prints;
        new writes = g_preference_writes;
        OnLanguageSelected(id, g_language_menu, language);
        CheckPreferences(g_prints == prints && g_preference_writes == writes, "same language is silent and does not rewrite nVault");
        OnWeaponSelected(id, g_menu[language], language + 1);
        CheckPreferences(g_prints == prints + 1 && contain(g_last_print, status) >= 0, "weapon change has one verified confirmation");
        prints = g_prints; writes = g_preference_writes;
        OnWeaponSelected(id, g_menu[language], language + 1);
        CheckPreferences(g_prints == prints && g_preference_writes == writes, "same primary choice is silent without rewriting nVault");
        OnPistolSelected(id, g_pistol_menu[language], language + 1);
        CheckPreferences(g_prints == prints + 1 && contain(g_last_print, status) >= 0, "pistol change has one verified confirmation");
        prints = g_prints; writes = g_preference_writes;
        OnPistolSelected(id, g_pistol_menu[language], language + 1);
        CheckPreferences(g_prints == prints && g_preference_writes == writes, "same pistol choice is silent without rewriting nVault");
        OpenPlayerSettings(id);
        new snapshot[768];
        copy(snapshot, charsmax(snapshot), g_menu_text[id]);
        OpenPlayerSettings(other);
        CheckPreferences(g_prints == prints && equal(snapshot, g_menu_text[id])
            && GetSettingsMenu(id, language, g_hud_disabled[id]) != GetSettingsMenu(other, 0, 0),
            "another client cannot replace private menu values");
        OnSettingsSelected(id, GetSettingsMenu(other, 0, 0), 2);
        CheckPreferences(g_prints == prints && g_preference_writes == writes, "another client's callback is rejected");
        colors = g_color_prints;
        OnSettingsSelected(id, GetSettingsMenu(id, language, g_hud_disabled[id]), 2);
        CheckPreferences(g_prints == prints + 1 && g_color_prints == colors + 1 && contain(g_last_print, status) >= 0,
            "HUD change emits one colored confirmation and silently refreshes settings");
    }
    new prints = g_prints;
    OnLanguageSelected(other, g_language_menu, 1);
    new session[96];
    formatex(session, charsmax(session), "%L", "en", "GS_CHANGE_SESSION");
    CheckPreferences(g_prints == prints + 1 && contain(g_last_print, session) >= 0, "LAN choice is explicitly connection-only");
    prints = g_prints;
    g_fail_hud_lookup = true;
    OnSettingsSelected(id, GetSettingsMenu(id, 1, g_hud_disabled[id]), 2);
    CheckPreferences(g_prints == prints + 1 && contain(g_last_print, session) >= 0,
        "failed HUD readback is not announced as saved");
    g_fail_hud_lookup = false;
    client_putinserver(id);
    new language_menu = GetSettingsMenu(id, 1, g_hud_disabled[id]);
    OpenPlayerSettings(id);
    new rejected[128];
    client_disconnected(id, false, rejected, charsmax(rejected));
    CheckPreferences(g_settings_menu[id][1][0] == -1 && g_settings_menu[id][1][1] == -1,
        "disconnect releases private menu resources");
    OnSettingsSelected(other, language_menu, 2);
    client_putinserver(id);
}

RunHudPreferenceChecks(id, other)
{
    new bool:disabled, value[64], timestamp;
    CheckPreferences(DecodeHudPreference("1 0", disabled) && !disabled
        && DecodeHudPreference("1 1", disabled) && disabled, "canonical HUD states parsed");
    new const invalid[][] = { "", "2 1", "1 2", "1 -1", "1 01", "1 1 ", " 1 1", "1 1 0", "^"1^" 1", "1^t1" };
    for (new index = 0; index < sizeof invalid; index++)
    {
        CheckPreferences(!DecodeHudPreference(invalid[index], disabled), "malformed HUD state rejected");
    }
    // Start with a legacy language/weapon record and no HUD record.
    nvault_set(g_preferences_vault, KEY, "1 2 1 2");
    nvault_remove(g_preferences_vault, HUD_KEY);
    copy(g_auth[other], charsmax(g_auth[]), "STEAM_ID_LAN");
    client_putinserver(other);
    copy(g_auth[id], charsmax(g_auth[]), "STEAM_0:1:434343");
    new writes = g_preference_writes;
    client_putinserver(id);
    CheckPreferences(!g_hud_disabled[id] && g_hud_preference_ready[id] && g_preference_writes == writes
        && !nvault_lookup(g_preferences_vault, HUD_KEY, value, charsmax(value), timestamp), "legacy join keeps enabled default without writing HUD");
    CheckPreferences(PlayerPreferencesSaved(id) && !PlayerHudPreferenceSaved(id), "saved language and weapons do not imply saved HUD");
    CheckHudFeedback(id, false, true);
    OnSettingsSelected(id, GetSettingsMenu(id, 1, 0), 2);
    CheckPreferences(g_hud_disabled[id] && StoredHudPreference("1 1") && StoredPreferences("1 2 1 2")
        && g_preference_writes == writes + 1, "explicit off writes only HUD record");
    CheckHudFeedback(id, true, true);
    new rejected[128];
    client_disconnected(id, false, rejected, charsmax(rejected));
    copy(g_auth[id], charsmax(g_auth[]), "STEAM_1:1:434343");
    client_putinserver(id);
    CheckPreferences(g_hud_disabled[id] && PlayerHudPreferenceSaved(id), "reconnect restores HUD off with normalized identity");
    new hud_text[256];
    writes = g_preference_writes;
    BuildPlayerHudText(id, hud_text, charsmax(hud_text));
    UpdatePlayerHuds();
    CheckPreferences(g_preference_writes == writes, "HUD formatting and periodic refresh never write");
    OnSettingsSelected(id, GetSettingsMenu(id, 1, 0), 2);
    OnSettingsSelected(id, GetSettingsMenu(id, 0, 1), 2);
    OnSettingsSelected(id, GetSettingsMenu(id, 1, 1), MENU_EXIT);
    CheckPreferences(g_hud_disabled[id] && g_preference_writes == writes, "stale wrong-language and cancelled toggles cannot write");
    set_pcvar_num(g_enabled, 0);
    OnSettingsSelected(id, GetSettingsMenu(id, 1, 1), 2);
    CheckPreferences(g_hud_disabled[id] && g_preference_writes == writes, "disabled toggle cannot write");
    set_pcvar_num(g_enabled, 1);
    g_bot_identity = true;
    OnSettingsSelected(id, GetSettingsMenu(id, 1, 1), 2);
    CheckPreferences(g_hud_disabled[id] && g_preference_writes == writes, "bot toggle cannot write");
    g_bot_identity = false;
    copy(g_auth[other], charsmax(g_auth[]), "STEAM_0:1:434343");
    client_putinserver(other);
    OnSettingsSelected(other, GetSettingsMenu(other, 0, 0), 2);
    CheckPreferences(g_hud_disabled[other] && !g_hud_preference_ready[other] && g_preference_writes == writes,
        "duplicate identity keeps session toggle without overwriting owner");
    CheckHudFeedback(other, false, false);
    copy(g_auth[other], charsmax(g_auth[]), "STEAM_ID_LAN");
    client_putinserver(other);
    OnSettingsSelected(other, GetSettingsMenu(other, 0, 0), 2);
    CheckPreferences(g_hud_disabled[other] && !g_hud_preference_ready[other] && g_preference_writes == writes,
        "shared identity cannot persist HUD");
    CheckHudFeedback(other, false, false);

    // Late identity restores untouched HUD, but keeps an explicit session toggle.
    copy(g_auth[id], charsmax(g_auth[]), "STEAM_ID_PENDING");
    client_putinserver(id);
    copy(g_auth[id], charsmax(g_auth[]), "STEAM_0:1:434343");
    client_authorized(id, g_auth[id]);
    CheckPreferences(g_hud_disabled[id] && g_preference_writes == writes, "late identity restores untouched HUD without writes");
    nvault_set(g_preferences_vault, HUD_KEY, "1 0");
    copy(g_auth[id], charsmax(g_auth[]), "STEAM_ID_PENDING");
    client_putinserver(id);
    OnSettingsSelected(id, GetSettingsMenu(id, 0, 0), 2);
    CheckPreferences(g_hud_disabled[id] && g_hud_preference_dirty[id] && g_preference_writes == writes,
        "pending identity keeps dirty HUD only in session");
    copy(g_auth[id], charsmax(g_auth[]), "STEAM_0:1:434343");
    client_authorized(id, g_auth[id]);
    CheckPreferences(g_hud_disabled[id] && StoredHudPreference("1 1") && !g_hud_preference_dirty[id]
        && g_preference_writes == writes + 1, "late identity preserves and saves explicit toggle exactly once");

    // Failure is uncertain delivery, so no automatic retry in this connection.
    g_fail_hud_lookup = true;
    OnSettingsSelected(id, GetSettingsMenu(id, 1, 1), 2);
    g_fail_hud_lookup = false;
    writes = g_preference_writes;
    CheckPreferences(!g_hud_disabled[id] && g_hud_preference_blocked[id] && !PlayerHudPreferenceSaved(id)
        && StoredHudPreference("1 0"), "HUD readback failure blocks despite possible persisted write");
    CheckHudFeedback(id, false, true);
    OnSettingsSelected(id, GetSettingsMenu(id, 1, 0), 2);
    EnsurePlayerPreferences(id);
    CheckPreferences(g_hud_disabled[id] && g_preference_writes == writes && StoredHudPreference("1 0"),
        "blocked HUD remains usable in session without retry");
    OnLanguageSelected(id, g_language_menu, 0);
    CheckPreferences(StoredPreferences("1 1 1 2") && g_hud_preference_blocked[id], "HUD failure does not block separate language record");
    client_putinserver(id);
    CheckPreferences(!g_hud_disabled[id] && PlayerHudPreferenceSaved(id), "reconnect reconciles HUD readback failure");
    CheckHudFeedback(id, true, true);
    nvault_set(g_preferences_vault, HUD_KEY, "1 2");
    client_putinserver(id);
    writes = g_preference_writes;
    OnSettingsSelected(id, GetSettingsMenu(id, 0, 0), 2);
    CheckPreferences(g_hud_disabled[id] && g_hud_preference_blocked[id] && StoredHudPreference("1 2")
        && g_preference_writes == writes && PlayerPreferencesSaved(id), "corrupt HUD retained without replacing separate saved preferences");
    CheckHudFeedback(id, false, true);
    copy(g_auth[id], charsmax(g_auth[]), "STEAM_0:1:434345");
    nvault_remove(g_preferences_vault, "v1:STEAM_0:1:434345");
    nvault_remove(g_preferences_vault, "hud:v1:STEAM_0:1:434345");
    client_putinserver(id);
    CheckPreferences(!g_hud_disabled[id] && !g_hud_preference_blocked[id] && !g_hud_preference_dirty[id], "slot reuse clears previous HUD state");
    OnSettingsSelected(id, GetSettingsMenu(id, 0, 0), 2);
    CheckPreferences(PlayerHudPreferenceSaved(id) && !PlayerPreferencesSaved(id), "HUD-only record does not create language or weapon defaults");
    CheckHudFeedback(id, true, false);
    nvault_remove(g_preferences_vault, "hud:v1:STEAM_0:1:434345");
    nvault_remove(g_preferences_vault, HUD_KEY);
    copy(g_auth[id], charsmax(g_auth[]), "STEAM_0:1:434343");
    client_putinserver(id);
    server_print("PLAYER_HUD_PREFERENCE_SMOKE=%s failures=%d storage=real_nvault", g_failures ? "failed" : "passed", g_failures);
}

public SeedPreferences()
{
    if (!CreatePreferencesClients()) { set_fail_state("Preferences seed unavailable."); return PLUGIN_HANDLED; }
    nvault_remove(g_preferences_vault, KEY);
    nvault_remove(g_preferences_vault, HUD_KEY);
    new id = g_clients[0];
    copy(g_auth[id], charsmax(g_auth[]), "STEAM_0:1:434343");
    client_authorized(id, g_auth[id]);
    OnLanguageSelected(id, g_language_menu, 1);
    OnWeaponSelected(id, g_menu[1], 1);
    OnPistolSelected(id, g_pistol_menu[1], 2);
    OnSettingsSelected(id, GetSettingsMenu(id, 1, 0), 2);
    CheckPreferences(StoredPreferences("1 2 1 2"), "reload seed stored");
    CheckPreferences(StoredHudPreference("1 1") && g_hud_disabled[id], "reload HUD off seed stored");
    server_print("PLAYER_PREFERENCES_SEED=%s failures=%d", g_failures ? "failed" : "passed", g_failures);
    return PLUGIN_HANDLED;
}
public VerifyPreferences()
{
    if (!CreatePreferencesClients()) { set_fail_state("Preferences verify unavailable."); return PLUGIN_HANDLED; }
    new id = g_clients[0];
    copy(g_auth[id], charsmax(g_auth[]), "STEAM_1:1:434343");
    client_authorized(id, g_auth[id]);
    CheckPreferences(PlayerLanguage(id) == 1 && g_choice[id] == 1 && g_pistol_choice[id] == 2,
        "fresh plugin restored persisted choices");
    CheckPreferences(g_hud_disabled[id] && PlayerHudPreferenceSaved(id), "fresh process on another map restores HUD off");
    OnSettingsSelected(id, GetSettingsMenu(id, 1, 1), 2);
    CheckPreferences(!g_hud_disabled[id] && StoredHudPreference("1 0"), "restored HUD can be enabled and saved");
    rg_round_respawn(id);
    CheckPreferences(user_has_weapon(id, CSW_AK47) && user_has_weapon(id, CSW_DEAGLE), "fresh plugin loadout applied at next spawn");
    server_print("PLAYER_PREFERENCES_RELOAD=%s failures=%d", g_failures ? "failed" : "passed", g_failures);
    return PLUGIN_HANDLED;
}
