// AMX Mod X nVault, Copyright (C) The AMX Mod X Development Team.
// GPL version 3 or higher, with exceptions in upstream LICENSE.txt.
#include "amxxmodule.h"
#include "NVault.h"
#include <sys/syscall.h>
using namespace process_io;

namespace {
bool ReadVault(const char *path, VaultMap &map, bool &missing) {
    std::vector<unsigned char> bytes;
    if (!Read(path, MaxVault, bytes, missing)) return false;
    if (missing) return true;
    Reader r(bytes); uint32_t magic, version, count;
    if (!r.Number(4, magic) || magic != VAULT_MAGIC || !r.Number(2, version)
        || version != VAULT_VERSION || !r.Number(4, count) || count > MaxEntries) return false;
    for (uint32_t i = 0; i < count; i++) {
        uint32_t stamp, keylen, vallen; std::string key, value;
        if (!r.Number(4, stamp) || !r.Number(1, keylen) || !r.Number(2, vallen)
            || !r.Text(keylen, key) || !r.Text(vallen, value) || map.contains(key.c_str())) return false;
        ArrayInfo info; info.value = value.c_str(); info.stamp = static_cast<time_t>(stamp);
        if (!map.replace(key.c_str(), info)) return false;
    }
    return r.offset == bytes.size();
}
bool Equal(VaultMap &a, VaultMap &b) {
    if (a.elements() != b.elements()) return false;
    for (auto i = a.iter(); !i.empty(); i.next()) {
        ArrayInfo value;
        if (!b.retrieve(i->key.chars(), &value) || value.stamp != i->value.stamp
            || strcmp(value.value.chars(), i->value.value.chars())) return false;
    }
    return true;
}
bool RetainTemporary(const std::string &path) {
    struct stat s;
    if (lstat(path.c_str(), &s)) return errno == ENOENT;
    if (!Regular(path.c_str()) || s.st_size < 0 || static_cast<size_t>(s.st_size) > MaxVault) return false;
    for (unsigned i = 0; i < 8; i++) {
        std::string target = path + ".retained-" + std::to_string(i);
        if (!syscall(SYS_renameat2, AT_FDCWD, path.c_str(), AT_FDCWD, target.c_str(), 1)) return true;
        if (errno != EEXIST) return false;
    }
    return false;
}
}

NVault::NVault(const char *file) : m_File(file), m_Journal(NULL), m_Open(false), m_Valid(true), m_failed(false) {}
NVault::~NVault() { Close(); delete m_Journal; }
bool NVault::SaveCheckpoint() {
    if (m_Hash.elements() > MaxEntries) return false;
    std::vector<unsigned char> bytes;
    U32(bytes, VAULT_MAGIC); U16(bytes, VAULT_VERSION); U32(bytes, m_Hash.elements());
    for (auto i = m_Hash.iter(); !i.empty(); i.next()) {
        const char *key = i->key.chars(), *value = i->value.value.chars();
        if (strlen(key) > 255 || strlen(value) > 65535) return false;
        if (bytes.size() + 7 + strlen(key) + strlen(value) > MaxVault) return false;
        U32(bytes, i->value.stamp); U8(bytes, strlen(key)); U16(bytes, strlen(value)); Text(bytes, key); Text(bytes, value);
    }
    std::string temporary = std::string(m_File.chars()) + ".checkpoint";
    if (!RetainTemporary(temporary) || Fail("checkpoint_open")) return false;
    FILE *file = Create(temporary.c_str());
    if (!file) return false;
    Point("checkpoint_created");
    bool ok = Write(file, bytes, "checkpoint_write");
    Point("checkpoint_written");
    if (!Flush(file, "checkpoint_flush")) ok = false;
    Point("checkpoint_flushed");
    if (!process_io::Close(file, "checkpoint_close")) ok = false;
    if (!ok) return false;
    Point("checkpoint_closed");
    VaultMap checked; bool missing;
    if (Fail("checkpoint_verify") || !ReadVault(temporary.c_str(), checked, missing) || missing || !Equal(m_Hash, checked)) return false;
    Point("checkpoint_verified");
    struct stat current;
    if (!lstat(m_File.chars(), &current) && !Regular(m_File.chars())) return false;
    if (Fail("checkpoint_rename") || rename(temporary.c_str(), m_File.chars())) return false;
    Point("checkpoint_published");
    return true;
}
bool NVault::Open() {
    if (m_Open || m_failed) return false;
    bool missing, journalMissing; std::vector<unsigned char> journalBytes;
    if (!ReadVault(m_File.chars(), m_Hash, missing)) return false;
    std::string journal = m_File.chars(); journal.replace(journal.size()-6, 6, ".journal");
    if (!Read(journal.c_str(), MaxJournal, journalBytes, journalMissing) || (missing && !journalMissing)) return false;
    delete m_Journal; m_Journal = new Journal(journal.c_str());
    bool truncated;
    if (m_Journal->Replay(&m_Hash, &truncated) < 0) return false;
    Point("replay_complete");
    if (truncated && !Retain(journal.c_str(), (journal + ".partial-retained").c_str(), MaxJournal)) return false;
    if (!SaveCheckpoint() || !m_Journal->Erase()) return false;
    Point("journal_erased");
    if (!m_Journal->Begin()) return false;
    m_Open = true;
    Point("journal_ready");
    return true;
}
bool NVault::Commit() {
    if (!m_Open || m_failed || !m_Journal || !m_Journal->Commit()) { m_failed = true; return false; }
    return true;
}
bool NVault::Close() {
    if (!m_Open) return false;
    bool ok = Commit(); m_Open = false;
    if (!m_Journal->End()) ok = false;
    if (!ok) return false;
    if (!SaveCheckpoint() || !m_Journal->Erase()) { m_failed = true; return false; }
    return true;
}
bool NVault::CanWrite() { return m_Open && !m_failed; }
const char *NVault::GetValue(const char *key) {
    auto r = m_Hash.find(key); return r.found() ? r->value.value.chars() : "";
}
bool NVault::GetValue(const char *key, time_t &stamp, char buffer[], size_t len) {
    ArrayInfo value;
    if (!m_Hash.retrieve(key, &value)) { if (len) buffer[0] = 0; return false; }
    stamp = value.stamp; ke::SafeSprintf(buffer, len, "%s", value.value.chars()); return true;
}
void NVault::SetValue(const char *key, const char *value) { SetValue(key, value, time(NULL)); }
void NVault::SetValue(const char *key, const char *value, time_t stamp) {
    if (!CanWrite()) return;
    if ((!m_Hash.contains(key) && m_Hash.elements() >= MaxEntries) || !m_Journal->Write_Insert(key, value, stamp)) { m_failed = true; return; }
    ArrayInfo info; info.value = value; info.stamp = stamp;
    if (!m_Hash.replace(key, info)) m_failed = true;
}
void NVault::Touch(const char *key, time_t stamp) {
    if (stamp == static_cast<time_t>(-1)) stamp = time(NULL);
    std::string value = GetValue(key); SetValue(key, value.c_str(), stamp);
}
void NVault::Remove(const char *key) {
    if (!CanWrite()) return;
    if (!m_Journal->Write_Remove(key)) { m_failed = true; return; }
    m_Hash.remove(key);
}
void NVault::Clear() {
    if (!CanWrite()) return;
    if (!m_Journal->Write_Clear()) { m_failed = true; return; }
    m_Hash.clear();
}
size_t NVault::Prune(time_t start, time_t end) {
    if (!CanWrite()) return 0;
    if (!m_Journal->Write_Prune(start, end)) { m_failed = true; return 0; }
    size_t removed = 0;
    for (auto i = m_Hash.iter(); !i.empty(); i.next()) {
        time_t t = i->value.stamp;
        if (t && ((!start && !end) || (!start && t < end) || (!end && t > start) || (t > start && t < end))) { i.erase(); removed++; }
    }
    return removed;
}
size_t NVault::Items() { return m_Hash.elements(); }
IVault *VaultMngr::OpenVault(const char *name) { return new NVault(name); }
