// AMX Mod X nVault, Copyright (C) The AMX Mod X Development Team.
// GPL version 3 or higher, with exceptions in upstream LICENSE.txt.
#include "Journal.h"
using namespace process_io;

bool Journal::Begin() {
    if (m_fp || m_failed || Fail("journal_open")) return false;
    m_fp = Create(m_File.chars());
    m_bytes = m_ops = 0;
    return m_fp != NULL;
}
bool Journal::End() {
    if (!m_fp) return !m_failed;
    bool ok = Commit();
    FILE *file = m_fp; m_fp = NULL;
    if (!process_io::Close(file, "journal_close")) ok = false;
    m_failed = m_failed || !ok;
    return ok;
}
bool Journal::Commit() {
    if (m_failed || !m_fp || !Flush(m_fp, "journal_flush")) { m_failed = true; return false; }
    Point("journal_committed");
    return true;
}
bool Journal::Erase() {
    struct stat s;
    if (lstat(m_File.chars(), &s)) return errno == ENOENT;
    return !m_fp && Regular(m_File.chars()) && !Fail("journal_erase") && unlink(m_File.chars()) == 0;
}
bool Journal::Append(const std::vector<unsigned char> &bytes) {
    if (m_failed || !m_fp || bytes.size() > MaxJournal - m_bytes || m_ops >= MaxEntries
        || !Write(m_fp, bytes, "journal_write")) { m_failed = true; return false; }
    m_bytes += bytes.size(); m_ops++;
    Point("journal_appended");
    return true;
}
bool Journal::Write_Clear() { return Append(std::vector<unsigned char>(1, Journal_Clear)); }
bool Journal::Write_Prune(time_t start, time_t end) {
    std::vector<unsigned char> b(1, Journal_Prune); U32(b, start); U32(b, end); return Append(b);
}
bool Journal::Write_Insert(const char *key, const char *value, time_t stamp) {
    if (strlen(key) > 255 || strlen(value) > 65535) { m_failed = true; return false; }
    std::vector<unsigned char> b(1, Journal_Insert);
    U32(b, stamp); U8(b, strlen(key)); Text(b, key); U16(b, strlen(value)); Text(b, value);
    return Append(b);
}
bool Journal::Write_Remove(const char *key) {
    if (strlen(key) > 255) { m_failed = true; return false; }
    std::vector<unsigned char> b(1, Journal_Remove); U8(b, strlen(key)); Text(b, key); return Append(b);
}
int Journal::Replay(VaultMap *map, bool *truncated) {
    if (truncated) *truncated = false;
    std::vector<unsigned char> bytes; bool missing;
    if (!Read(m_File.chars(), MaxJournal, bytes, missing)) return -1;
    Reader r(bytes); size_t ops = 0;
    while (r.offset < bytes.size()) {
        if (ops >= MaxEntries) return -1;
        uint32_t op, stamp, length, start, end; std::string key, value;
        r.Number(1, op);
        bool ok = true;
        switch (op) {
            case Journal_Clear: map->clear(); break;
            case Journal_Prune:
                ok = r.Number(4, start) && r.Number(4, end);
                if (ok) {
                    for (auto i = map->iter(); !i.empty(); i.next()) {
                        time_t t = i->value.stamp;
                        if (t && ((!start && !end) || (!start && t < static_cast<time_t>(end))
                            || (!end && t > static_cast<time_t>(start))
                            || (t > static_cast<time_t>(start) && t < static_cast<time_t>(end)))) i.erase();
                    }
                }
                break;
            case Journal_Insert:
                ok = r.Number(4, stamp) && r.Number(1, length) && r.Text(length, key)
                    && r.Number(2, length) && r.Text(length, value);
                if (ok) { ArrayInfo info; info.value = value.c_str(); info.stamp = static_cast<time_t>(stamp); ok = map->replace(key.c_str(), info); }
                break;
            case Journal_Remove:
                ok = r.Number(1, length) && r.Text(length, key);
                if (ok) map->remove(key.c_str());
                break;
            default: return -1;
        }
        if (!ok) {
            if (!r.truncated) return -1;
            if (truncated) *truncated = true;
            return static_cast<int>(ops);
        }
        if (map->elements() > MaxEntries) return -1;
        ops++;
    }
    return static_cast<int>(ops);
}
