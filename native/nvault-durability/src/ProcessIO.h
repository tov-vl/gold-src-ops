// SPDX-License-Identifier: GPL-3.0-or-later
// AMX Mod X linking exceptions: see the corresponding upstream LICENSE.txt.
#pragma once
#include <cstdio>
#include <cstring>
#include <cerrno>
#include <stdint.h>
#include <string>
#include <vector>
#include <sys/stat.h>
#include <fcntl.h>
#include <unistd.h>
#ifdef NVAULT_TESTING
#include <cstdlib>
#include <signal.h>
#endif

namespace process_io {
static const size_t MaxVault = 16 * 1024 * 1024;
static const size_t MaxJournal = 4 * 1024 * 1024;
static const size_t MaxEntries = 65536;

inline bool Fail(const char *point) {
#ifdef NVAULT_TESTING
    const char *value = getenv("NVAULT_FAIL");
    if (value && !strcmp(value, point)) { errno = EIO; return true; }
#else
    (void)point;
#endif
    return false;
}
inline void Point(const char *point) {
#ifdef NVAULT_TESTING
    const char *value = getenv("NVAULT_KILL");
    if (value && !strcmp(value, point)) { kill(getpid(), SIGKILL); _exit(99); }
    if (!strcmp(point, "journal_committed") && getenv("NVAULT_SLOW_IO")) usleep(5000);
#else
    (void)point;
#endif
}
inline bool Regular(const char *path) {
    struct stat s;
    return lstat(path, &s) == 0 && S_ISREG(s.st_mode) && s.st_nlink == 1;
}
inline bool Read(const char *path, size_t maximum, std::vector<unsigned char> &data, bool &missing) {
    missing = false;
    int fd = open(path, O_RDONLY | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK);
    if (fd == -1) { missing = errno == ENOENT; return missing; }
    struct stat before, after;
    if (fstat(fd, &before) || !S_ISREG(before.st_mode) || before.st_nlink != 1
        || before.st_size < 0 || static_cast<size_t>(before.st_size) > maximum) { close(fd); return false; }
    data.resize(static_cast<size_t>(before.st_size));
    size_t position = 0;
    while (position < data.size()) {
        ssize_t count = read(fd, data.data() + position, data.size() - position);
        if (count < 0 && errno == EINTR) continue;
        if (count <= 0) { close(fd); return false; }
        position += static_cast<size_t>(count);
    }
    bool valid = !fstat(fd, &after) && before.st_size == after.st_size
        && before.st_mtim.tv_sec == after.st_mtim.tv_sec && before.st_mtim.tv_nsec == after.st_mtim.tv_nsec
        && before.st_ctim.tv_sec == after.st_ctim.tv_sec && before.st_ctim.tv_nsec == after.st_ctim.tv_nsec;
    return close(fd) == 0 && valid;
}
inline FILE *Create(const char *path) {
    int fd = open(path, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0600);
    if (fd == -1) return NULL;
    FILE *file = fdopen(fd, "wb");
    if (!file) close(fd);
    return file;
}
inline bool Write(FILE *file, const std::vector<unsigned char> &bytes, const char *point) {
    if (Fail(point)) {
        if (!bytes.empty()) fwrite(bytes.data(), 1, bytes.size() / 2, file);
        return false;
    }
    return bytes.empty() || fwrite(bytes.data(), 1, bytes.size(), file) == bytes.size();
}
inline bool Flush(FILE *file, const char *point) {
    return file && !Fail(point) && fflush(file) == 0 && !ferror(file);
}
inline bool Close(FILE *file, const char *point) {
    bool injected = Fail(point);
    return fclose(file) == 0 && !injected;
}
inline void U8(std::vector<unsigned char> &b, unsigned value) { b.push_back(static_cast<unsigned char>(value)); }
inline void U16(std::vector<unsigned char> &b, unsigned value) { U8(b, value); U8(b, value >> 8); }
inline void U32(std::vector<unsigned char> &b, uint32_t value) { U16(b, value); U16(b, value >> 16); }
inline void Text(std::vector<unsigned char> &b, const char *text) { b.insert(b.end(), text, text + strlen(text)); }
struct Reader {
    const std::vector<unsigned char> &bytes;
    size_t offset;
    bool truncated;
    explicit Reader(const std::vector<unsigned char> &value) : bytes(value), offset(0), truncated(false) {}
    bool Number(size_t count, uint32_t &value) {
        if (count > bytes.size() - offset) { truncated = true; return false; }
        value = 0;
        for (size_t i = 0; i < count; i++) value |= static_cast<uint32_t>(bytes[offset++]) << (i * 8);
        return true;
    }
    bool Text(size_t count, std::string &value) {
        if (count > bytes.size() - offset) { truncated = true; return false; }
        value.assign(reinterpret_cast<const char *>(bytes.data() + offset), count);
        offset += count;
        return value.find('\0') == std::string::npos;
    }
};
inline bool Retain(const char *source, const char *target, size_t maximum) {
    std::vector<unsigned char> input, existing; bool missing;
    if (!Read(source, maximum, input, missing) || missing) return false;
    if (!Read(target, maximum, existing, missing)) return false;
    if (!missing) return input == existing;
    FILE *file = Create(target);
    if (!file) return false;
    bool ok = Write(file, input, "retained_write") && Flush(file, "retained_flush");
    return Close(file, "retained_close") && ok;
}
}
