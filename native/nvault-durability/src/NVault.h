// AMX Mod X nVault, Copyright (C) The AMX Mod X Development Team.
// GPL version 3 or higher, with exceptions in upstream LICENSE.txt.
#ifndef _INCLUDE_NVAULT_H
#define _INCLUDE_NVAULT_H
#include "IVault.h"
#include "Journal.h"
#define VAULT_MAGIC 0x6E564C54
#define VAULT_VERSION 0x0200
enum VaultError { Vault_Ok=0, Vault_NoFile, Vault_BadFile, Vault_OldFile, Vault_Read };
class NVault : public IVault {
public:
    explicit NVault(const char *file);
    ~NVault();
    bool GetValue(const char *key, time_t &stamp, char buffer[], size_t len);
    const char *GetValue(const char *key);
    void SetValue(const char *key, const char *value);
    void SetValue(const char *key, const char *value, time_t stamp);
    void Touch(const char *key, time_t stamp);
    size_t Prune(time_t start, time_t end);
    void Clear();
    void Remove(const char *key);
    bool Open();
    bool Close();
    bool Commit();
    size_t Items();
    const char *GetFilename() { return m_File.chars(); }
    bool isValid() { return m_Valid; }
private:
    bool SaveCheckpoint();
    bool CanWrite();
    ke::AString m_File;
    VaultMap m_Hash;
    Journal *m_Journal;
    bool m_Open, m_Valid, m_failed;
};
class VaultMngr : public IVaultMngr {
public:
    IVault *OpenVault(const char *name);
};
#endif
