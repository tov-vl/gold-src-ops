#include <amxmodx>
#include <nvault>

// Standalone seed plugin for a disposable engine. Never loaded with the product.
public plugin_init() { register_plugin("Player Search Fixture", "1.0", "GoldSrcOps"); }
public plugin_cfg()
{
    new stats = nvault_open("goldsrcops-player-stats-v1");
    new prefs = nvault_open("goldsrcops-player-preferences-v1");
    if (stats == INVALID_HANDLE || prefs == INVALID_HANDLE) { set_fail_state("Fixture vault unavailable"); return; }
    new key[48], namekey[64], value[64], name[32];
    for (new i = 0; i < 16; i++)
    {
        formatex(key, charsmax(key), "v1:STEAM_0:1:%d", 900000 + i);
        formatex(value, charsmax(value), "1 %d %d", 120 - i, i + 1);
        nvault_set(stats, key, value);
        if (i < 2) copy(name, charsmax(name), "Alex");
        else if (i < 12) formatex(name, charsmax(name), "[TAG]Alex-%02d", i);
        else if (i == 12) copy(name, charsmax(name), "Ab");
        else if (i == 13) copy(name, charsmax(name), "КИРИЛЛ");
        else if (i == 14) copy(name, charsmax(name), "😀😀😀Tail");
        else copy(name, charsmax(name), "<b>Alex</b>");
        formatex(namekey, charsmax(namekey), "name:%s", key);
        formatex(value, charsmax(value), "1 %s", name);
        nvault_set(prefs, namekey, value);
        nvault_set(prefs, key, "1 1 2 1");
        formatex(namekey, charsmax(namekey), "hud:%s", key);
        nvault_set(prefs, namekey, "1 1");
        formatex(namekey, charsmax(namekey), "streak:%s", key);
        nvault_set(prefs, namekey, "1 7");
    }
    nvault_close(stats);
    nvault_close(prefs);
    server_print("LOOKUP_INTEGRATION_SEED=passed records=80");
}
