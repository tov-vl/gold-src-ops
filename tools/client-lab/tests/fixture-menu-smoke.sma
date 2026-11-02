#include <amxmodx>
#include <reapi>
#include <nvault>

// Exercise the real observer and AMXX menu resources without a connected client.
// Client state, network writes and diagnostic log capture are substituted here.
#define plugin_init FixturePluginInit
#define plugin_precache FixturePluginPrecache
#define plugin_natives FixturePluginNatives
#define client_disconnected FixtureDisconnect
#define is_user_alive(%1) TestAlive(%1)
#define player_menu_info(%1,%2,%3,%4) TestMenuInfo(%1,%2,%3,%4)
#define message_begin(%1,%2,%3,%4) TestMessageBegin(%1,%2,%3,%4)
#define write_short(%1) TestShort(%1)
#define write_byte(%1) TestByte(%1)
#define message_end() TestMessageEnd()
#define get_user_ammo TestAmmo
#define rg_get_user_ammo TestDirectAmmo
#define rg_get_user_bpammo TestDirectReserve
#define user_has_weapon TestHasWeapon
#define get_entvar TestEntvar
#define log_amx TestLog
#include <fixture_vgui_guard.sma>
#undef plugin_init
#undef plugin_precache
#undef plugin_natives
#undef client_disconnected
#undef is_user_alive
#undef player_menu_info
#undef message_begin
#undef write_short
#undef write_byte
#undef message_end
#undef get_user_ammo
#undef rg_get_user_ammo
#undef rg_get_user_bpammo
#undef user_has_weapon
#undef get_entvar
#undef log_amx

new g_failures, g_checks, g_current = -1, g_page;
new bool:g_alive = true, bool:g_visible = true;
new g_sent, g_shorts[3], g_bytes[4], g_short_count, g_byte_count;
new g_loadout[8], g_cached_ammo[4], g_loadout_logs, g_cache_logs, g_respawn_logs, g_origin_reads;
new g_loadout_report[256], g_cache_report[160], g_respawn_report[80];

public plugin_init() { register_plugin("Fixture menu observer checks", "1.0.0", ""); register_srvcmd("gso_fixture_menu_smoke", "RunMenuSmoke"); }
public plugin_natives() { set_native_filter("SetupNativeFilter"); }
public TestMenuHandler(player, menu, item) { return PLUGIN_HANDLED; }
public TestItemCallback(player, menu, item) { return ITEM_ENABLED; }

bool:TestAlive(player) { return g_alive && player == 1; }
TestMenuInfo(player, &oldmenu, &menu, &page) { oldmenu = 0; menu = g_current; page = g_page; return player == 1 && g_visible; }
TestMessageBegin(destination, message, const origin[3] = {0,0,0}, player = 0)
{
    #pragma unused origin
    Check(destination == MSG_ONE && message == get_user_msgid("ScreenFade") && player == 1, "fixed reliable ScreenFade target");
    g_short_count = 0; g_byte_count = 0;
}
TestShort(value)
{
    if (g_short_count < sizeof g_shorts) { g_shorts[g_short_count] = value; }
    g_short_count++;
}
TestByte(value)
{
    if (g_byte_count < sizeof g_bytes) { g_bytes[g_byte_count] = value; }
    g_byte_count++;
}
TestMessageEnd() { g_sent++; Check(g_short_count == 3 && g_byte_count == 4, "ScreenFade wire shape"); }
Check(bool:valid, const label[]) { g_checks++; if (!valid) { g_failures++; server_print("FIXTURE_MENU_ASSERTION_FAILED %s", label); } }

TestAmmo(player, weapon, &clip, &ammo)
{
    Check(player == 1 && (weapon == CSW_AK47 || weapon == CSW_DEAGLE), "fixed loadout ammo reads");
    new offset = weapon == CSW_AK47 ? 0 : 2;
    clip = g_cached_ammo[offset]; ammo = g_cached_ammo[offset + 1];
    return 1;
}
TestDirectAmmo(player, WeaponIdType:weapon)
{
    Check(player == 1 && (weapon == WEAPON_AK47 || weapon == WEAPON_DEAGLE), "direct clip read target");
    return g_loadout[weapon == WEAPON_AK47 ? 4 : 6];
}
TestDirectReserve(player, WeaponIdType:weapon)
{
    Check(player == 1 && (weapon == WEAPON_AK47 || weapon == WEAPON_DEAGLE), "direct reserve read target");
    return g_loadout[weapon == WEAPON_AK47 ? 5 : 7];
}
TestHasWeapon(player, weapon)
{
    Check(player == 1, "fixed loadout presence target");
    switch (weapon)
    {
        case CSW_AK47: return g_loadout[0];
        case CSW_DEAGLE: return g_loadout[1];
        case CSW_MP5NAVY: return g_loadout[2];
        case CSW_USP: return g_loadout[3];
    }
    Check(false, "unexpected weapon presence read");
    return 0;
}
TestEntvar(player, EntVars:member, any:...)
{
    Check(player == 1 && member == var_origin, "read-only movement origin after report");
    for (new index = 0; index < 3; index++) { setarg(2, index, 0); }
    g_origin_reads++;
    return 0;
}
TestLog(const format[], any:...)
{
    new text[256]; vformat(text, charsmax(text), format, 2);
    if (containi(text, "FIXTURE_GUNS_LOADOUT ") == 0)
    {
        Check(!g_loadout_logs && !g_cache_logs && !g_respawn_logs, "one detailed report before cache and strict result");
        copy(g_loadout_report, charsmax(g_loadout_report), text); g_loadout_logs++;
    }
    if (containi(text, "FIXTURE_GUNS_CACHE ") == 0)
    {
        Check(g_loadout_logs == 1 && !g_cache_logs && !g_respawn_logs, "one cache report between details and result");
        copy(g_cache_report, charsmax(g_cache_report), text); g_cache_logs++;
    }
    if (containi(text, "FIXTURE_GUNS_RESPAWN ") == 0)
    {
        Check(g_loadout_logs == 1 && g_cache_logs == 1 && !g_respawn_logs, "one strict result after direct and cache details");
        copy(g_respawn_report, charsmax(g_respawn_report), text); g_respawn_logs++;
    }
    return 1;
}

RunLoadoutChecks()
{
    new const EXPECTED[] = { 1, 1, 0, 0, 30, 90, 7, 35 };
    for (new variant = -1; variant <= sizeof EXPECTED; variant++)
    {
        for (new index = 0; index < sizeof EXPECTED; index++) { g_loadout[index] = EXPECTED[index]; }
        if (variant >= 0 && variant < sizeof EXPECTED) { g_loadout[variant] = variant < 4 ? 1 - g_loadout[variant] : g_loadout[variant] - 1; }
        arrayset(g_cached_ammo, 0, sizeof g_cached_ammo);
        if (variant == sizeof EXPECTED)
        {
            g_loadout[6] = 0;
            g_cached_ammo[0] = 30; g_cached_ammo[1] = 90; g_cached_ammo[2] = 7; g_cached_ammo[3] = 35;
        }
        g_loadout_logs = 0; g_cache_logs = 0; g_respawn_logs = 0; g_origin_reads = 0;
        g_guns_respawn[1] = true;
        ObserveGunsLoadout(1);
        new expected[256];
        formatex(expected, charsmax(expected), "FIXTURE_GUNS_LOADOUT ak47=%d deagle=%d mp5=%d usp=%d ak_clip=%d ak_reserve=%d de_clip=%d de_reserve=%d",
            g_loadout[0], g_loadout[1], g_loadout[2], g_loadout[3], g_loadout[4], g_loadout[5], g_loadout[6], g_loadout[7]);
        Check(equal(g_loadout_report, expected) != 0, "exact presence and ammo diagnostics for each failed term");
        formatex(expected, charsmax(expected), "FIXTURE_GUNS_CACHE ak_ok=1 ak_clip=%d ak_reserve=%d de_ok=1 de_clip=%d de_reserve=%d",
            g_cached_ammo[0], g_cached_ammo[1], g_cached_ammo[2], g_cached_ammo[3]);
        Check(equal(g_cache_report, expected) != 0, "cache values retained without controlling acceptance");
        formatex(expected, charsmax(expected), "FIXTURE_GUNS_RESPAWN selected_loadout=%d", variant == -1);
        Check(equal(g_respawn_report, expected) != 0, "strict predicate rejects every individual mismatch");
        Check(g_loadout_logs == 1 && g_cache_logs == 1 && g_respawn_logs == 1 && g_origin_reads == 1, "single observation without retry");
        for (new index = 0; index < sizeof EXPECTED; index++)
        {
            new value = EXPECTED[index];
            if (variant == index) { value = index < 4 ? 1 - value : value - 1; }
            if (variant == sizeof EXPECTED && index == 6) { value = 0; }
            Check(g_loadout[index] == value, "observation does not change inventory or ammo");
        }
    }
    g_loadout_logs = 0; g_cache_logs = 0; g_respawn_logs = 0; g_origin_reads = 0;
    g_alive = false; ObserveGunsLoadout(1); g_alive = true;
    g_guns_respawn[1] = false; ObserveGunsLoadout(1);
    Check(!g_loadout_logs && !g_cache_logs && !g_respawn_logs && !g_origin_reads, "dead or non-respawn observation is inert");
}

MakeMenu(kind, selected = -1, language = 0)
{
    new const WEAPONS[][] = { "MP5", "AK-47", "M4A1", "USP", "Glock", "Desert Eagle" };
    new menu = menu_create("Test title", "TestMenuHandler"), text[128];
    for (new item = 0; item < 3; item++)
    {
        if (item == selected) { formatex(text, charsmax(text), "%s \y[%s]\w", WEAPONS[(kind - 1) * 3 + item], language ? "selected" : "выбрано"); }
        else { copy(text, charsmax(text), WEAPONS[(kind - 1) * 3 + item]); }
        menu_additem(menu, text);
    }
    return menu;
}

public RunMenuSmoke()
{
    if (g_checks) { return PLUGIN_HANDLED; }
    for (new kind = 1; kind <= 2; kind++)
    {
        for (new language = 0; language < 2; language++)
        {
            for (new selected = 0; selected < 3; selected++)
            {
                new menu = MakeMenu(kind, selected, language);
                Check(GunsMenuKind(menu) == kind, "all localized selected variants");
                menu_destroy(menu);
            }
        }
    }
    Check(!GunsWeaponLabel("MP5 impostor", "MP5") && !GunsWeaponLabel("XMP5", "MP5")
        && !GunsWeaponLabel("MP5 \y[]\w", "MP5") && !GunsWeaponLabel("MP5 \y[selected]\w extra", "MP5"), "strict item boundaries");
    new primary = MakeMenu(1), pistol = MakeMenu(2), wrong = MakeMenu(1);
    menu_additem(wrong, "Extra");
    Check(!GunsMenuKind(wrong), "reject fourth item"); menu_destroy(wrong);
    for (new variant = 0; variant < 5; variant++)
    {
        wrong = menu_create("Unrelated", "TestMenuHandler");
        menu_additem(wrong, variant == 0 ? "USP" : "MP5", variant == 1 ? "command" : "", variant == 2 ? ADMIN_KICK : 0,
            variant == 3 ? menu_makecallback("TestItemCallback") : -1);
        menu_additem(wrong, variant == 4 ? "M4A1" : "AK-47");
        menu_additem(wrong, variant == 4 ? "AK-47" : "M4A1");
        Check(!GunsMenuKind(wrong), "reject mixed order, metadata or callback"); menu_destroy(wrong);
    }
    g_current = primary;
    ObserveGunsMenu(1); Check(!g_sent, "unarmed observer is inert");
    g_guns[1] = 1; g_guns_resource[1] = -2;
    g_alive = false; ObserveGunsMenu(1); Check(!g_sent, "dead observer is inert"); g_alive = true;
    ObserveGunsMenu(0); ObserveGunsMenu(33); Check(!g_sent, "invalid slots are inert");
    ObserveGunsMenu(1);
    Check(g_guns_menu[1] == 1 && g_guns_backdrop[1] && g_sent == 1, "primary recognized without ShowMenu hook");
    Check(g_shorts[0] == 0 && g_shorts[1] == 0 && g_shorts[2] == 5
        && !g_bytes[0] && !g_bytes[1] && !g_bytes[2] && g_bytes[3] == 255, "opaque fade parameters unchanged");
    ObserveGunsMenu(1); Check(g_sent == 1, "unchanged resource sends once");
    g_current = pistol; ObserveGunsMenu(1); Check(g_guns_menu[1] == 2 && g_sent == 2, "pistol transition");
    g_visible = false; ObserveGunsMenu(1);
    Check(!g_guns_menu[1] && !g_guns_backdrop[1] && g_sent == 3 && !g_shorts[2] && !g_bytes[3], "close releases fade");
    ObserveGunsMenu(1); Check(g_sent == 3, "closed resource sends once");
    g_visible = true; g_current = primary; g_page = 1;
    ObserveGunsMenu(1); Check(!g_guns_menu[1] && g_sent == 3, "reject another page");
    g_page = 0; ObserveGunsMenu(1); Check(g_guns_menu[1] == 1 && g_sent == 4, "reopen primary");
    // Selection invalidates the cache, including when the same resource reopens.
    g_guns_resource[1] = -2; g_guns_menu[1] = 0;
    ObserveGunsMenu(1); Check(g_guns_menu[1] == 1 && g_sent == 5, "same resource after selection");
    g_current = -1; ObserveGunsMenu(1); Check(!g_guns_menu[1] && !g_guns_backdrop[1] && g_sent == 6, "no active menu releases fade");
    FixtureDisconnect(1);
    Check(!g_guns[1] && !g_guns_backdrop[1] && g_guns_resource[1] == -2, "disconnect clears observer");
    menu_destroy(primary); menu_destroy(pistol);
    RunLoadoutChecks();
    server_print("FIXTURE_MENU_SMOKE=%s failures=%d checks=%d", g_failures ? "failed" : "passed", g_failures, g_checks);
    return PLUGIN_HANDLED;
}
