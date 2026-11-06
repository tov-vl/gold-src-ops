# v2.109 - Unified client acceptance evidence

- Require an independently parsed, result-bound renderer receipt in new client
  campaigns and include its hash in visual review evidence.
- Report UI acceptance, renderer identity, Windows-session binding and cleanup
  separately without changing raw worker results or historical receipts.
- Preserve UI-only legacy campaign inspection and existing no-retry guards.
- Recognize one preserved terminal renderer failure through existing read-only
  evidence after accepted log archival, without changing queue contents.

Accepted on 2026-11-06: product PR #488 and all six required PR/main checks
passed. One new EN `/profile` trial passed five-frame UI/OK/alias/control,
independent OpenGL identity, session binding and cleanup through the ordinary
controller. An initial unconfirmed collection was reconciled by the same ID
without replay. Runner, permissions, addon and production are unchanged.
Subsequent approved maintenance archived two completed request records and
freed two queue slots while preserving results/images and unchanged runner
bytes. One separately authorized RU `/profile` trial then passed five-frame
UI/OK/alias/control, independent OpenGL, session and cleanup checks without
replay. After bounded queue maintenance, the four remaining RU/EN ranks/topall
cases passed the same gate with 20 new frames and no replay. The complete
six-case MOTD matrix is accepted with 30 native frames. Prior results, profile
receipts, failed evidence/archive and runner hashes are preserved; the held
campaign has one free queue slot. See
[scope and evidence](v2.109-campaign-renderer-evidence.md).
