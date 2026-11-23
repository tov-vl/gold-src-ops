# Gameplay Pilot Result Template

Copy this template to a new owner-controlled session record. Follow the
[protocol](../gameplay-pilot.md). All cases start `Not tested`; this empty
template is not acceptance evidence. Publish only a sanitized summary.
The [participant brief](../gameplay-pilot-participants-ru.md) is available in Russian.

## Session And Baseline

- Session reference: <neutral identifier>
- UTC start/end and elapsed minutes: <UTC-start / UTC-end / elapsed minutes>
- Result: <Not run / Core passed with limits / Partial / Failed>
- Actor A / B: <pseudonyms; separate accounts confirmed, no Steam IDs here>
- Consent/readiness and account availability: <not checked>
- Repository/release reference: <reference, not an installed-version claim>
- Fresh installed weapon addon/version and receipt/payload hash: <not checked>
- Fresh installed map addon/version and receipt/payload hash: <not checked>
- Engine/build and managed profile: <not checked>
- Actual maps, start time-left and round/map boundaries: <not checked>
- A/B renderer, client language and menu language: <not checked>
- Effective respawn/protection/removal/freeze/team/round/C4/map-time values: <not checked>
- Effective healing amount and ammo refill state: <not checked>
- Saved stats/preferences availability and initial state: <not checked>
- Private baseline/evidence reference: <owner-only record; no endpoint/credentials>
- Baseline matches accepted target: <not checked; mismatch blocks start>

Paste the saved preflight's `pilotHeader` here as its own `## Server Preflight`
section. The report checker compares that section to the original JSON.

## Scenario Results

Evidence references must identify the actor, map/round and observation window.
Keep real identities, raw screenshots/chat and exact host data outside Git.

| ID | Result | Actual steps / preconditions / actor | Observed result and comparison | Evidence or untested reason |
| --- | --- | --- | --- | --- |
| P01 | Not tested | | | Not run |
| P02 | Not tested | | | Not run |
| P03 | Not tested | | | Not run |
| P04 | Not tested | | | Not run |
| P05 | Not tested | | | Not run |
| P06 | Not tested | | | Not run |
| P07 | Not tested | | | Not run |
| P08 | Not tested | | | Not run |
| P09 | Not tested | | | Not run |
| P10 | Not tested | | | Not run |
| P11 | Not tested | | | Not run |
| P12 | Not tested | | | Not run |
| P13 | Not tested | | | Not run |
| P14 | Not tested | | | Not run |
| P15 | Not tested | | | Not run |
| P16 | Not tested | | | Not run |
| P17 | Not tested | | | Not run |

## Timing, Friction And State

| Observation | Actor / map / repeat | Measurement boundaries | Actual value or description | Evidence |
| --- | --- | --- | --- | --- |
| Death to new spawn / first controllable frame | | | Not measured | |
| Time from spawn to next encounter | | | Not measured | |
| Repeated deaths near spawn | | | Not observed | |
| Protection expiration / early removal | | | Not observed | |
| HP, active weapon, clip and reserves before/after eligible kill | | | Not observed | |
| Saved versus map/session counters before/after reconnect | | | Not observed | |
| Natural map transition or native vote refusal | | | Not observed | |
| Unclear rule or menu wording, A/B comments | | | Not collected | |

For timestamps use one consistent clock and record uncertainty. Do not infer
frame-accurate timings from recollection. Separate ordinary shot consumption,
reloads and new-spawn inventory from kill rewards.

## Decision And Limits

- Core IDs P01-P06, P10-P16: <not completed>
- Conditional IDs P07-P09, P17 and remaining claim limits: <not completed>
- Failed cases and retained repeats: <not assessed>
- Stop/interruption reason, if any: <not assessed>
- Human observations versus optional operator telemetry: <separate evidence>
- Selected follow-up: <None selected / exactly one proposed change>
- Reproducible problem, frequency, affected actors and confidence: <not assessed>
- Player impact and priority rationale: <not assessed>
- Smallest proposed change, acceptance criterion and rollback: <not proposed>
- Remaining ideas explicitly deferred: <none recorded>
- Owner review of sanitized summary: <pending>

An untested case remains untested. Preserve earlier failures, do not edit saved
state to produce a pass, and do not claim busy-server balance or retention from
this session. The Archive assessment remains an independent operational gate.
