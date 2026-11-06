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
was replayed. Renderer acceptance remained open at that point. Production and
addon were unchanged.

Correction PR #481 handles native echo whitespace and a final extension-line
marker. Required PR/main CI and signed adoption passed. Bounded read-only
diagnostics were retrieved remotely through the existing status channel, with
the retained log hash and original result projection unchanged. No additional
game trial, log deletion, broker permission or session-policy change occurred.

On 2026-11-06, separately approved recovery PR #485 and required PR/main CI
passed. The exact retained log was archived without altering the failed result.
Signed sequence 29 passed independent nine-file readback, preserving seven
files. One new EN `/profile` trial passed five-frame UI/OK/alias/control review
and a separate process-bound OpenGL renderer receipt, with complete cleanup
and no manual window selection. The bounded native renderer gate is accepted;
hardware acceleration/performance, a new RU matrix and an actual Windows-session
transition are not claimed. Production and addon remain unchanged.

See [scope, review and evidence](v2.108-client-renderer-evidence.md).
