# Player Storage 0.40.0 Release

The paired addon 0.40.0 and Linux i386 nVault passed production R3
acceptance under the schema-2 plan on 2026-11-18. The signed stable release
promotes the exact accepted candidate archive.

| Evidence | Result |
| --- | --- |
| Product PRs | [Paired installer #567](https://github.com/tov-vl/gold-src-ops/pull/567), [persistent guard fix #568](https://github.com/tov-vl/gold-src-ops/pull/568) |
| Accepted main | [353f67a](https://github.com/tov-vl/gold-src-ops/commit/353f67ab887e0184974ee7ae18d849c45da80350), identical Git tree to the tested PR |
| Full CI | [PR CI](https://github.com/tov-vl/gold-src-ops/actions/runs/44302757358) and [main CI](https://github.com/tov-vl/gold-src-ops/actions/runs/44307640552) passed |
| Published assets | [Accepted rc.2](https://github.com/tov-vl/gold-src-ops/releases/tag/player-storage-v0.40.0-rc.2) and [stable 0.40.0](https://github.com/tov-vl/gold-src-ops/releases/tag/player-storage-v0.40.0) |
| ZIP SHA-256 | `cf3515f7990b509de2210b59e208b4d53df80a20235dd6a9bf00d9a562e3d59a` |

The stable ZIP and checksum retain the candidate filenames and bytes.
Independent downloads matched each other byte for byte. Both signed
annotated tags resolve to the accepted revision and bind the same ZIP
digest. Verification also checked GitHub asset digests and all 28 ZIP
entries. GitHub reports the release as mutable, so the signed tag is the
integrity reference. This component release uses the same accepted addon,
nVault and readers and requires no image promotion.

## Rollout and recovery evidence

The first candidate reached a known partial transition and was rolled back.
The active pilot manifest pinned stock nVault, and the original preflight
missed the conflict. The verified closed backup and the installed originals
allowed restoration of the previous module. Independent acceptance passed
on the restored 0.39.0 pair.

Candidate rc.2 retains the pilot manifest and binds the exact addon and
nVault hashes through the protected persistent marker. The schema-2
coordinator covers both copies of the guard and checks startup and the
installed backup preflight. Full hosted rehearsal passed 38 cases with
the real shell guards and i386 tools, including rollback after each of
the seven writes and after candidate startup. See the
[installer contract](paired-player-storage-installer.md).

Production preflight confirmed the predecessor, 26 baseline rows, four
canonical vault files, an empty game, and clear pending state. The rc.2
transition finished `accepted` and cleared its matching pending marker.
Five of 26 baseline rows changed. Both guards, the schema-3 receipt, and
the loaded nVault inode matched the plan. The event agent retained its
identity and restart count.

The closures before and after the transition each produced an encrypted
off-host archive and a separate Recover. Both recovered TAR hashes matched
their originals. The old and new i386 readers agreed on the player-record
digest, including values and timestamps, across both closures. Independent
production acceptance passed A2S, public status, RCON, player lookup and
baseline verification. The host storage probe accepted 10,000 operations
with p99 of 4 microseconds and a recorded maximum of 51 microseconds.

The durability claim covers acknowledged individual saves across a process
crash. Power-loss durability, cross-vault transactions, a maximum-latency
commitment, and acceptance by human players remain separate. Weekly backup
policy and the Archive trigger retain their own evidence.
