#!/usr/bin/env python3
"""Apply the reviewed nVault source overlay to an extracted pinned SDK tree."""
import argparse
import hashlib
from pathlib import Path
import shutil


def apply(source, overlay):
    module = source / 'modules/nvault'
    api = module / 'amxxapi.cpp'
    if hashlib.sha256(api.read_bytes()).hexdigest() != '8d20cedfcfe476a1bdb159d39ff1b75a249d0d6f3bdcd1f5b6b0c1daece10d2a':
        raise ValueError('Pinned upstream API digest mismatch')
    text = api.read_text(encoding='utf-8')
    for marker in ('IVaultMngr *GetVaultMngr()', 'AMX_NATIVE_INFO nVault_natives[] = {'):
        if text.count(marker) != 1:
            raise ValueError('Unexpected pinned SDK API structure')
    addition = '''#ifdef NVAULT_TESTING
#include <time.h>
static cell nvault_test_clock(AMX *, cell *) {
    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    return static_cast<cell>(static_cast<uint32_t>(now.tv_sec) * 1000000U + now.tv_nsec / 1000U);
}
static cell nvault_test_failure(AMX *, cell *params) {
    if (params[1] == 1) return setenv("NVAULT_FAIL", "journal_flush", 1) == 0;
    if (params[1] == 0) return unsetenv("NVAULT_FAIL") == 0;
    return 0;
}
#endif
static cell nvault_process_version(AMX *, cell *) { return 1; }

static cell nvault_process_commit(AMX *, cell *params)
{
    unsigned int id = params[1];
    if (id >= g_Vaults.length() || !g_Vaults.at(id)) return 0;
    return g_Vaults.at(id)->Commit() ? 1 : 0;
}

'''
    text = text.replace('IVaultMngr *GetVaultMngr()', addition + 'IVaultMngr *GetVaultMngr()')
    text = text.replace('AMX_NATIVE_INFO nVault_natives[] = {', 'AMX_NATIVE_INFO nVault_natives[] = {\n'
                        '    {"nvault_process_version", nvault_process_version},\n'
                        '    {"nvault_process_commit", nvault_process_commit},\n'
                        '#ifdef NVAULT_TESTING\n'
                        '    {"nvault_test_clock", nvault_test_clock},\n'
                        '    {"nvault_test_failure", nvault_test_failure},\n'
                        '#endif')
    with api.open('w', encoding='utf-8', newline='\n') as output:
        output.write(text)
    config = module / 'moduleconfig.h'
    config.write_text(config.read_text().replace('#define MODULE_VERSION AMXX_VERSION',
                                                '#define MODULE_VERSION "1.10.0.5481-process1"'))
    for name in ('ProcessIO.h', 'Journal.h', 'Journal.cpp', 'NVault.h', 'NVault.cpp'):
        shutil.copyfile(overlay / name, module / name)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path)
    parser.add_argument('overlay', type=Path)
    args = parser.parse_args()
    apply(args.source, args.overlay)
