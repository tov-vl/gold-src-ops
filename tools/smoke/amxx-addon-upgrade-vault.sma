#include <amxmodx>
#include <nvault>

public plugin_init()
{
    register_plugin("Addon vault rehearsal", "1.0.0", "GoldSrcOps");
    register_srvcmd("upgrade_vault_seed", "Seed");
    register_srvcmd("upgrade_vault_verify", "Verify");
    register_srvcmd("upgrade_vault_advance", "Advance");
}
public Seed()
{
    new stats = nvault_open("goldsrcops-player-stats-v1");
    new prefs = nvault_open("goldsrcops-player-preferences-v1");
    if (stats == INVALID_HANDLE || prefs == INVALID_HANDLE) { set_fail_state("Vault open failed"); return; }
    nvault_set(stats, "v1:STEAM_0:1:424242", "1 37 9");
    nvault_set(prefs, "v1:STEAM_0:1:424242", "1 2 1 2");
    nvault_set(prefs, "hud:v1:STEAM_0:1:424242", "1 1");
    nvault_set(prefs, "streak:v1:STEAM_0:1:424242", "1 7");
    nvault_set(prefs, "name:v1:STEAM_0:1:424242", "1 Rehearsal");
    nvault_close(stats);
    nvault_close(prefs);
    server_print("UPGRADE_VAULT_SEED=passed");
}
public Advance()
{
    new stats = nvault_open("goldsrcops-player-stats-v1");
    if (stats == INVALID_HANDLE) { set_fail_state("Vault open failed"); return; }
    nvault_set(stats, "v1:STEAM_0:1:424242", "1 38 9");
    nvault_close(stats);
    server_print("UPGRADE_VAULT_ADVANCE=passed");
}
public Verify()
{
    new stats = nvault_open("goldsrcops-player-stats-v1");
    new prefs = nvault_open("goldsrcops-player-preferences-v1");
    if (stats == INVALID_HANDLE || prefs == INVALID_HANDLE) { set_fail_state("Vault open failed"); return; }
    new value[64], bool:valid = true;
    nvault_get(stats, "v1:STEAM_0:1:424242", value, charsmax(value));
    new mode[16];
    read_argv(1, mode, charsmax(mode));
    valid = valid && equal(value, equal(mode, "advanced") ? "1 38 9" : "1 37 9");
    nvault_get(prefs, "v1:STEAM_0:1:424242", value, charsmax(value));
    valid = valid && equal(value, "1 2 1 2");
    nvault_get(prefs, "hud:v1:STEAM_0:1:424242", value, charsmax(value));
    valid = valid && equal(value, "1 1");
    nvault_get(prefs, "streak:v1:STEAM_0:1:424242", value, charsmax(value));
    valid = valid && equal(value, "1 7");
    nvault_get(prefs, "name:v1:STEAM_0:1:424242", value, charsmax(value));
    valid = valid && equal(value, "1 Rehearsal");
    nvault_close(stats);
    nvault_close(prefs);
    server_print("UPGRADE_VAULT_VERIFY=%s", valid ? "passed" : "failed");
}
