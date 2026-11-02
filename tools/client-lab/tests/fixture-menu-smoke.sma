#include <amxmodx>
#include <reapi>
#include <nvault>

// Exercise the real observer and AMXX menu resources without a connected client.
// Only client state and network writes are substituted in this test binary.
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

new g_failures, g_checks, g_current = -1, g_page;
new bool:g_alive = true, bool:g_visible = true;
new g_sent, g_shorts[3], g_bytes[4], g_short_count, g_byte_count;

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
    server_print("FIXTURE_MENU_SMOKE=%s failures=%d checks=%d", g_failures ? "failed" : "passed", g_failures, g_checks);
    return PLUGIN_HANDLED;
}
