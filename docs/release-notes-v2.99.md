# v2.99 - Respawn and protection in `/rules`

Addon 0.38.0 explains the current automatic respawn delay, spawn protection
duration and early-removal conditions in RU/EN. Values come from live engine
settings; disabled and unavailable states are explicit. Weapon/reward/rank
rules and the five-second private command cooldown remain.

Gameplay settings, balance, storage and application services are unchanged.
Focused engine and dictionary checks cover timing, removal modes, UTF-8 and
complete MOTD contents. Product PR #419 and complete required/post-merge CI
passed. The immutable package uses the verified CI binary without rebuilding.

One guarded empty-server apply installed 0.38.0 on 2026-10-29 and reached
`accepted`; independent technical acceptance passed. Vaults, queue, agent,
workflow, backup baseline invariants and all ten control-plane containers were
preserved. No rollback was needed. A read-only predecessor check first rejected
a subsecond cross-host clock difference; the coordinator was corrected before
any apply, retaining server-side freshness checks.

Real-client RU/EN readability and rules-window interaction passed on 2026-10-31
using fresh isolated LAN fixtures with the exact accepted plugin/dictionary.
Ten frames confirmed complete localized text, both OK closes and alternate-alias
reopening; fixture markers confirmed cooldown and returned movement. No manual
window selection was needed, and client/fixture cleanup passed. OpenGL was
requested but not independently verified. PvP and human activity remain separate.
No application images/tags, new provider configuration or gameplay settings were
changed. See
[scope and gates](v2.99-respawn-rules.md).
