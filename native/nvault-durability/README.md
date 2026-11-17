# nVault Process Durability

Linux i386 source overlay for pinned AMXX 1.10.0.5481. The public AMXX module
interface remains version 4 and the existing vault/journal formats remain
unchanged. This is a separate component, not a replacement of an upstream
release archive. Production installation is a separate reviewed operation.

`nvault_process_version()` returns 1. `nvault_process_commit(handle)` returns
true only after complete append and a successful journal flush, without an
earlier sticky write failure. This protects acknowledged changes against loss
of the process. It does not provide fsync, host-power-loss protection or a
transaction across vaults. A failed commit has an uncertain write outcome and
must not be blindly replayed.

Open and close write a bounded checkpoint to an exclusive temporary file,
verify its parsed content, atomically publish it, then reset the old journal.
A subsequent crash can replay the retained old journal idempotently. Complete
valid records before an incomplete final append are recovered; the incomplete
journal is retained before replacement. Unknown operations and malformed
snapshots fail closed. The legacy format has no checksum: arbitrary bit rot
that still forms valid records cannot be detected by this mechanism.

Limits: 16 MiB checkpoint, 4 MiB journal, 65,536 records/operations, eight
retained orphan checkpoints and one exact retained partial journal per vault.
Exhaustion refuses new storage work and preserves existing inputs; operator
reconciliation is required rather than automatic history deletion. This is
a single-writer design. Verify all module consumers and file ownership before
installation. The checkpoint path requires Linux `renameat2` with no-replace
support for retaining orphan temporary files.

Build on a host with Python 3.12+ and Docker:

```sh
python3 native/nvault-durability/build.py --build-builder \
  --cache /operator/nvault-source-cache --output /operator/nvault-build
python3 native/nvault-durability/build.py --verify /operator/nvault-build/package
python3 tools/smoke/test-nvault-compatibility.py \
  --build-output /operator/nvault-build --builder-image sha256:...
```

Alternatively pass `--builder-image sha256:...` for an already verified local
builder. The recipe uses the pinned engine's glibc 2.28 generation and exact
compiler packages from the signed historical Debian archive. Network is used
only to prepare sources/the builder. Compilation, fault tests and sanitizers
run with no network. Both source and patched builds are repeated and compared
byte for byte. C++ runtime is statically linked to avoid changing the engine's
bundled `libstdc++`.
The 32-bit build selects 64-bit file metadata (`_FILE_OFFSET_BITS=64`), so valid
files on filesystems with inode numbers above 32 bits are not rejected by stat.

The package carries the production binary, source/binary identity manifest and
the corresponding upstream source archives, overlay, build/test scripts and
upstream license. Derived nVault files retain the upstream GPL-3.0-or-later
terms and AMXX linking exceptions. AMTL and GCC runtime retain their respective
upstream licenses/exceptions. The repository's root license does not replace
these component-specific terms.

Fault-injection and timing natives exist only when explicitly built with
`NVAULT_TESTING`. The production verifier rejects those knobs/native names.
`tests/run.sh` covers actual repeated SIGKILL, I/O failures, a real full tmpfs,
readonly paths, replay algebra, retention/size limits, ASan/UBSan and commit
timing. The compatibility helper exercises both old-to-new and new-to-old
closed-vault restore through the existing backup transport, preserving timestamps,
absent journals and original input files. Game-engine acceptance must separately verify the production binary
and the addon's handling of commit failures. Do not infer target-host timing,
deployment or human gameplay acceptance from these isolated checks.
