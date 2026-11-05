# v2.108 - Client renderer evidence

- Add bounded, request-specific `gl_dump` evidence from the owned game process.
- Preserve pre-existing logs and keep unavailable or ambiguous identity unknown.
- Add an independent, source-hash-bound renderer review receipt without changing
  historical results or visual acceptance flags.
- Keep the existing nine-file runner, signed updater and access boundaries.

Local client policy/native compilation and fixture tests passed. PR #480 and
required PR/main CI passed. One native EN trial passed its five-frame UI checks,
but renderer identity and diagnostic-log cleanup were not proved. The candidate
was rolled back to the exact prior runner. Raw results are retained, no trial
was replayed, and renderer acceptance remains open. Production and addon are
unchanged; this is not an accepted native renderer release.

See [scope, review and evidence](v2.108-client-renderer-evidence.md).
