// SPDX-License-Identifier: GPL-3.0-or-later
// Linking exceptions and corresponding module source: upstream LICENSE.txt.
#include "NVault.h"
#include <algorithm>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>
#include <sys/stat.h>
#include <dirent.h>
#include <fcntl.h>
#include <unistd.h>
#include <time.h>
#define CHECK(x) do { if (!(x)) { fprintf(stderr, "PLAYER_STORAGE_PROBE=failed line=%d; retain scratch\n", __LINE__); return 1; } } while (0)
static unsigned long long micros() {
    struct timespec t;
    if (clock_gettime(CLOCK_MONOTONIC, &t)) return 0;
    return static_cast<unsigned long long>(t.tv_sec) * 1000000 + t.tv_nsec / 1000;
}
static bool safe(const std::string &path, bool directory) {
    struct stat s;
    return !lstat(path.c_str(), &s) && s.st_uid == geteuid()
        && (s.st_mode & 0777) == (directory ? 0700 : 0600)
        && (directory ? S_ISDIR(s.st_mode) : S_ISREG(s.st_mode) && s.st_nlink == 1);
}
static bool private_path(const std::string &path, const char *prefix) {
    if (path.empty() || path[0] != '/' || path.find("/../") != std::string::npos
        || path.find("/./") != std::string::npos || path.find("//") != std::string::npos) return false;
    size_t slash = path.rfind('/');
    std::string name = path.substr(slash + 1);
    if (name.compare(0, strlen(prefix), prefix) || name.size() != strlen(prefix) + 32) return false;
    for (size_t i = strlen(prefix); i < name.size(); i++)
        if (!((name[i] >= '0' && name[i] <= '9') || (name[i] >= 'a' && name[i] <= 'f'))) return false;
    for (size_t pos = 1; (pos = path.find('/', pos)) != std::string::npos; pos++) {
        struct stat s;
        if (lstat(path.substr(0, pos).c_str(), &s) || !S_ISDIR(s.st_mode)) return false;
    }
    return true;
}
int main(int argc, char **argv) {
    CHECK(argc == 3);
    const std::string directory = argv[2];
    if (!strcmp(argv[1], "--reopen-copy")) {
        CHECK(private_path(directory, ".player-storage-copy-") && safe(directory, true));
        const char *names[] = {"goldsrcops-player-stats-v1", "goldsrcops-player-preferences-v1"};
        DIR *entries = opendir(directory.c_str());
        CHECK(entries);
        struct dirent *entry;
        unsigned files = 0;
        while ((entry = readdir(entries))) {
            if (!strcmp(entry->d_name, ".") || !strcmp(entry->d_name, "..")) continue;
            bool allowed = false;
            for (int i = 0; i < 2; i++) {
                std::string stem = names[i], name = entry->d_name;
                if (name == stem + ".vault" || name == stem + ".journal") allowed = true;
            }
            CHECK(allowed && ++files <= 4 && safe(directory + "/" + entry->d_name, false));
        }
        CHECK(!closedir(entries));
        for (int i = 0; i < 2; i++) {
            std::string path = directory + "/" + names[i] + ".vault";
            CHECK(safe(path, false));
            NVault vault(path.c_str());
            CHECK(vault.Open() && vault.Close());
        }
        puts("{\"schema\":1,\"state\":\"reopened\"}");
        return 0;
    }
#ifdef PLAYER_STORAGE_OLD_READER
    CHECK(false);
#else
    CHECK(!strcmp(argv[1], "--probe") && private_path(directory, ".player-storage-probe-"));
    // mkdir is exclusive: the probe never enters a pre-existing data directory.
    CHECK(!mkdir(directory.c_str(), 0700) && safe(directory, true));
    const std::string path = directory + "/synthetic.vault";
    std::vector<unsigned long long> times;
    times.reserve(10000);
    NVault vault(path.c_str());
    CHECK(vault.Open());
    for (int i = 0; i < 10000; i++) {
        char key[32], value[32];
        snprintf(key, sizeof key, "synthetic-%d", i % 32);
        snprintf(value, sizeof value, "%d", i);
        unsigned long long before = micros();
        vault.SetValue(key, value, 1700000000 + i);
        CHECK(vault.Commit());
        unsigned long long after = micros();
        CHECK(before && after >= before);
        times.push_back(after - before);
    }
    CHECK(vault.Close() && safe(path, false));
    // Exercise renameat2(RENAME_NOREPLACE) only on this synthetic orphan.
    const std::string orphan = path + ".checkpoint";
    int fd = open(orphan.c_str(), O_CREAT | O_EXCL | O_NOFOLLOW | O_WRONLY, 0600);
    CHECK(fd >= 0 && write(fd, "synthetic", 9) == 9 && !close(fd));
    NVault reopened(path.c_str());
    CHECK(reopened.Open());
    for (int i = 9968; i < 10000; i++) {
        char key[32], value[32], actual[32]; time_t stamp;
        snprintf(key, sizeof key, "synthetic-%d", i % 32);
        snprintf(value, sizeof value, "%d", i);
        CHECK(reopened.GetValue(key, stamp, actual, sizeof actual)
            && stamp == 1700000000 + i && !strcmp(value, actual));
    }
    CHECK(reopened.Items() == 32 && reopened.Close());
    const std::string retained = orphan + ".retained-0";
    CHECK(safe(path, false) && safe(retained, false));
    // Remove only known single-link files created by this invocation.
    CHECK(!unlink(path.c_str()) && !unlink(retained.c_str()) && !rmdir(directory.c_str()));
    std::sort(times.begin(), times.end());
    printf("{\"schema\":1,\"state\":\"verified\",\"samples\":10000,\"p99Us\":%llu,\"maxUs\":%llu}\n",
           times[9899], times.back());
    return times[9899] <= 2000 ? 0 : 1;
#endif
}
