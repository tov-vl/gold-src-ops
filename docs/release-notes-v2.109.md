# v2.109 - Unified client acceptance evidence

- Require an independently parsed, result-bound renderer receipt in new client
  campaigns and include its hash in visual review evidence.
- Report UI acceptance, renderer identity, Windows-session binding and cleanup
  separately without changing raw worker results or historical receipts.
- Preserve UI-only legacy campaign inspection and existing no-retry guards.
- Recognize one preserved terminal renderer failure through existing read-only
  evidence after accepted log archival, without changing queue contents.

Controller-only implementation and verification are in progress. Required
PR/main CI and one new EN `/profile` native trial remain pending. Installed
runner, permissions, addon and production are unchanged. See
[scope and evidence](v2.109-campaign-renderer-evidence.md).
