# v2.108 - Client renderer evidence

- Add bounded, request-specific `gl_dump` evidence from the owned game process.
- Preserve pre-existing logs and keep unavailable or ambiguous identity unknown.
- Add an independent, source-hash-bound renderer review receipt without changing
  historical results or visual acceptance flags.
- Keep the existing nine-file runner, signed updater and access boundaries.

Local client policy/native compilation and fixture tests passed. Required CI,
target adoption and the native renderer check remain pending; the current
Windows session binding prevents dispatch. Production and addon are unchanged.

See [scope, review and evidence](v2.108-client-renderer-evidence.md).
