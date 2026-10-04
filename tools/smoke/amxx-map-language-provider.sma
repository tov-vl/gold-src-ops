#include <amxmodx>
#include <reapi>
new g_provider_client;
#define is_user_bot(%1) FixtureProviderIsBot(%1)
#include <goldsrcops_weapon_selection.sma>
#undef is_user_bot

bool:FixtureProviderIsBot(id) { return id != g_provider_client && is_user_bot(id); }

// Fixture-only control; the consumer still calls the real product native.
public FixtureChooseMapLanguage(id, language)
{
    g_provider_client = id;
    set_pcvar_num(g_enabled, language >= 0);
    if (language >= 0) { OnLanguageSelected(id, g_language_menu, language); }
}
