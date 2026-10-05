#include <amxmodx>
#include <reapi>
#include <fakemeta>
#include <nvault>

// Human identity and readback failure are fixture-only; nVault stays real.
#define plugin_init ProductPluginInit
#define is_user_bot(%1) FixtureIsBot(%1)
#define get_user_authid(%1,%2,%3) FixtureAuthId(%1,%2,%3)
#define nvault_lookup(%1,%2,%3,%4,%5) FixtureLookup(%1,%2,%3,%4,%5)
#define nvault_set(%1,%2,%3) FixtureSet(%1,%2,%3)
#define client_print FixturePrint
#define menu_display(%1,%2) FixtureDisplay(%1,%2)
#include <goldsrcops_weapon_selection.sma>
#undef plugin_init
#undef is_user_bot
#undef get_user_authid
#undef nvault_lookup
#undef nvault_set
#undef client_print
#undef menu_display

new g_clients[2];
new g_auth[MAX_PLAYERS + 1][32];
new g_failures;
new bool:g_fail_lookup;
new g_preference_writes;
new const KEY[] = "v1:STEAM_0:1:434343";

public plugin_init()
{
    ProductPluginInit();
    register_srvcmd("goldsrcops_preferences_smoke", "RunPreferencesSmoke");
    register_srvcmd("goldsrcops_preferences_seed", "SeedPreferences");
    register_srvcmd("goldsrcops_preferences_verify", "VerifyPreferences");
}
bool:FixtureIsBot(id) { return id != g_clients[0] && id != g_clients[1] && is_user_bot(id); }
FixtureAuthId(id, buffer[], length) { return copy(buffer, length, g_auth[id]); }
FixtureLookup(vault, const key[], value[], length, &timestamp)
{
    return g_fail_lookup && vault == g_preferences_vault ? 0 : nvault_lookup(vault, key, value, length, timestamp);
}
FixtureSet(vault, const key[], const value[])
{
    if (vault == g_preferences_vault) { g_preference_writes++; }
    return nvault_set(vault, key, value);
}
FixturePrint(id, type, const message[], any:...)
{
    #pragma unused id, type, message
    return 0;
}
FixtureDisplay(id, menu)
{
    #pragma unused id, menu
    return 1;
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
    OnSettingsSelected(id, g_settings_menu[1][0], 2);
    OnSettingsSelected(id, g_settings_menu[1][1], 2);
    CheckPreferences(g_preference_writes == writes && StoredPreferences("1 2 1 2") && !g_hud_disabled[id], "HUD formatting and toggles do not rewrite preferences");
    g_choice[id] = 0;
    CheckPreferences(!PlayerPreferencesSaved(id), "different session choice is not described as saved");
    g_choice[id] = 1;
    g_preferences_dirty[id] = PREFERENCE_LANGUAGE;
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

    nvault_set(g_preferences_vault, KEY, "2 99 99 99");
    client_putinserver(id);
    OnLanguageSelected(id, g_language_menu, 1);
    CheckPreferences(g_preferences_blocked[id] && StoredPreferences("2 99 99 99"), "corrupt record retained without overwrite");
    CheckPreferences(!PlayerPreferencesSaved(id), "corrupt record is not described as saved");
    nvault_close(g_preferences_vault);
    g_preferences_vault = INVALID_HANDLE;
    client_putinserver(id);
    OnLanguageSelected(id, g_language_menu, 1);
    CheckPreferences(PlayerLanguage(id) == 1 && !g_preferences_ready[id], "unavailable vault keeps connection selection");
    CheckPreferences(!PlayerPreferencesSaved(id), "unavailable storage is not described as saved");
    g_preferences_vault = nvault_open(PREFERENCES_VAULT_NAME);
    CheckPreferences(nvault_lookup(g_stats_vault, KEY, value, charsmax(value), timestamp) && equal(value, "1 7 3"),
        "stats vault sentinel unchanged by preferences");
    nvault_remove(g_preferences_vault, KEY);
    server_print("PLAYER_PREFERENCES_SMOKE=%s failures=%d storage=real_nvault", g_failures ? "failed" : "passed", g_failures);
    return PLUGIN_HANDLED;
}

public SeedPreferences()
{
    if (!CreatePreferencesClients()) { set_fail_state("Preferences seed unavailable."); return PLUGIN_HANDLED; }
    nvault_remove(g_preferences_vault, KEY);
    new id = g_clients[0];
    copy(g_auth[id], charsmax(g_auth[]), "STEAM_0:1:434343");
    client_authorized(id, g_auth[id]);
    OnLanguageSelected(id, g_language_menu, 1);
    OnWeaponSelected(id, g_menu[1], 1);
    OnPistolSelected(id, g_pistol_menu[1], 2);
    CheckPreferences(StoredPreferences("1 2 1 2"), "reload seed stored");
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
    rg_round_respawn(id);
    CheckPreferences(user_has_weapon(id, CSW_AK47) && user_has_weapon(id, CSW_DEAGLE), "fresh plugin loadout applied at next spawn");
    server_print("PLAYER_PREFERENCES_RELOAD=%s failures=%d", g_failures ? "failed" : "passed", g_failures);
    return PLUGIN_HANDLED;
}
