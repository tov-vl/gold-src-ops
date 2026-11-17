// AMX Mod X nVault, Copyright (C) The AMX Mod X Development Team.
// GPL version 3 or higher, with exceptions in upstream LICENSE.txt.
#ifndef _INCLUDE_JOURNAL_H
#define _INCLUDE_JOURNAL_H
#include "Binary.h"
#include <sm_stringhashmap.h>
#include <amtl/am-string.h>
#include "ProcessIO.h"
enum JOp { Journal_Nop=0, Journal_Clear, Journal_Prune, Journal_Insert, Journal_Remove, Journal_TotalOps };
enum Encode { Encode_Small, Encode_Medium };
struct ArrayInfo { ke::AString value; time_t stamp; };
typedef StringHashMap<ArrayInfo> VaultMap;
class Journal {
public:
    explicit Journal(const char *file) : m_File(file), m_fp(NULL), m_failed(false), m_bytes(0), m_ops(0) {}
    ~Journal() { if (m_fp) End(); }
    bool Begin();
    bool End();
    bool Commit();
    int Replay(VaultMap *map, bool *truncated = NULL);
    bool Erase();
    bool Write_Clear();
    bool Write_Prune(time_t start, time_t end);
    bool Write_Insert(const char *key, const char *value, time_t stamp);
    bool Write_Remove(const char *key);
private:
    bool Append(const std::vector<unsigned char> &bytes);
    ke::AString m_File;
    FILE *m_fp;
    bool m_failed;
    size_t m_bytes, m_ops;
};
#endif
