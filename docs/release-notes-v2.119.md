# Release notes - v2.119

## Added

- Bounded source-completeness diagnostics for the existing 24-hour shadow audit:
  exact missing UTC minutes, precise neighboring source observations and
  multiple-observation counts.
- Separate schedule-only and schedule-plus-manual requested-window coverage
  from a bounded caller-reviewed receipt manifest. Missing input remains Unknown.
- Sanitized diagnostics survive a draft-target miss in the existing job log and
  summary; temporary raw evidence is still deleted.

## Preserved

Exporter pin, evaluator, SLI target and canonical selection, cron, independent
observer thresholds, keys, permissions, notifications and application/game
runtimes are unchanged. No Archive write is part of acceptance. Window coverage
does not establish object integrity or service availability during missing slots.

## Status

Released as [availability-diagnostics-v2.119.0](https://github.com/tov-vl/gold-src-ops/releases/tag/availability-diagnostics-v2.119.0) after protected PR/main CI,
signed same-byte package promotion and independent read-only acceptance.
All eight original source gaps and precise neighbors were reproduced; the
draft SLI target remains missed. API/Web stay on the accepted v2.118 release.
See [scope, limits and evidence](v2.119-archive-completeness.md).
