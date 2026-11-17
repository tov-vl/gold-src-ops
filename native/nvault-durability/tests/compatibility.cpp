// SPDX-License-Identifier: GPL-3.0-or-later
#include "NVault.h"
#include <cstdio>
#include <cstring>
#include <string>
#define REQUIRE(x) do { if (!(x)) { fprintf(stderr, "COMPATIBILITY_FAILED line=%d\n", __LINE__); return 1; } } while (0)
int main(int argc, char **argv) {
    REQUIRE(argc == 3 && (!strcmp(argv[1], "seed") || !strcmp(argv[1], "read")));
    const char *stems[] = {"goldsrcops-player-stats-v1", "goldsrcops-player-preferences-v1"};
    for (int vault = 0; vault < 2; vault++) {
        std::string path = std::string(argv[2]) + "/" + stems[vault] + ".vault";
        NVault data(path.c_str()); REQUIRE(data.Open());
        for (int player = 0; player < 2; player++) {
            for (int field = 0; field < 4; field++) {
                char key[64], value[128], actual[128];
                snprintf(key, sizeof key, "synthetic-%d-field-%d", player, field);
                snprintf(value, sizeof value, "vault=%d player=%d field=%d RU/EN 25 4 HUD streak", vault, player, field);
                time_t stamp = field == 0 ? 0 : 1700000000 + player * 10 + field;
                if (!strcmp(argv[1], "seed")) data.SetValue(key, value, stamp);
                else {
                    time_t found = -1;
                    REQUIRE(data.GetValue(key, found, actual, sizeof actual));
                    REQUIRE(found == stamp && !strcmp(value, actual));
                }
            }
        }
        REQUIRE(data.Items() == 8 && data.Close());
    }
    puts("NVAULT_CLOSED_FORMAT=passed");
    return 0;
}
