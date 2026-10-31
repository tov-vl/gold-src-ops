# Client campaign controller

`tools/client-lab/Invoke-LabCampaign.ps1` is the PowerShell 7 entry point for a
private six-case campaign: `ranks`, `profile`, `topall`, each in RU and EN. It
orchestrates the existing generated command handler and disposable LAN fixture;
it does not install software or extend the target's permissions. Read the
[lab runbook](client-lab.md) for prerequisites and
[panel acceptance scope](client-panel-acceptance.md) for the pending live gate.

## Preparation

Use the accepted nine-file bundle, matching revision-16 activation, pinned
addon/dictionary/helper and six independently reviewed native baseline body
hashes. The baseline JSON has `baselineProfiles`, with one `panel`, `language`
and uppercase `bodySha256` per fixed case. Baselines must belong to the same
scenario and accepted inputs; an arbitrary hash is not a reviewed baseline.
Keep all these files and the campaign directory outside Git, or under ignored
`artifacts/`. Private paths, licensed assets, keys and raw screenshots are not
publication inputs.

```powershell
$controller = './tools/client-lab/Invoke-LabCampaign.ps1'
& $controller -Operation prepare -CampaignDirectory $campaign `
    -BundleDirectory $bundle -ActivationPath $activation -AddonDirectory $addon `
    -HelperBinaryPath $helper -BaselinePath $baselines -PythonPath $python
& $controller -Operation status -CampaignDirectory $campaign
```

Preparation only copies and validates local inputs. It creates six fresh run
directories, six permanent job IDs, input/source hashes and an initial Steam
hold. It never contacts Docker, SSH, Steam or the target. A partial preparation
is retained for inspection and cannot be reused. Changing pinned inputs or the
controller source requires a fresh reviewed campaign; do not edit old claims.

## One attempt and same-ID reconciliation

Steam login and usage on another computer are not discoverable with sufficient
confidence through this channel. The hold is an explicit operator statement,
has no expiry and is never cleared automatically. Only clear it after confirming
the account is free:

```powershell
& $controller -Operation steam-free -CampaignDirectory $campaign -Execute
& $controller -Operation run -CampaignDirectory $campaign -Panel ranks -Language en -Execute
& $controller -Operation hold -CampaignDirectory $campaign
```

Run one case at a time. There is deliberately no unattended six-case batch or
automatic repeat. Before claiming an attempt, preflight checks the fixed local
Docker Desktop Linux endpoint, an unoccupied fixture/listener, current worker
readiness, queue capacity, target identity/capabilities and all nine installed
runner hashes. It does not change firewall, access, updates or queue contents.
Readiness does not prove Steam entitlement or exclusive account use.
If queue capacity is exhausted, use the existing separately reviewed signed
archive workflow for exact completed requests; never remove claims or drain
pending work to fit the campaign. The controller does not archive automatically.

The controller claims fixture start, dispatch, cancellation and fixture stop
before their effects. Each dispatch has one fixed ID. A lost acknowledgement
leaves `needs_reconciliation`; neither a restart nor `resume` can dispatch again:

```powershell
& $controller -Operation resume -CampaignDirectory $campaign -Panel ranks -Language en -Execute
& $controller -Operation cancel -CampaignDirectory $campaign -Panel ranks -Language en -Execute
```

Resume only reads the same request, retrieves missing images and reconciles its
cleanup. Existing images are never overwritten. Cancellation has a permanent
send claim; an uncertain cancellation is not sent again. `hold` requests one
cancellation for the active case when its polling loop next checks availability.
Use `cancel` to send it explicitly if the controller is no longer polling.
A connection/read failure alone does not authorize cancellation or a new run.

Collection has a ten-minute wall-clock deadline and bounded child commands.
The `finally` path attempts cleanup of the identified owned fixture. An already
claimed stop is never retried: resume checks actual absence only. Missing,
contradictory or incomplete cleanup receipts require operator reconciliation;
do not erase a claim to make the tool proceed. A process kill, power loss or
provider failure can interrupt cleanup; the finite relay/fixture leases are a
fallback, not independently verified cleanup.

Per-case file locks and a controller-session mutex reject overlapping work.
This is a single operator/session tool, not a distributed scheduler or a
security boundary against an administrator modifying its private files. State
replacement is atomic on the local filesystem. The separate availability lock
serializes the final dispatch check; if it is busy, the command fails rather
than silently dropping the requested state change. Repeat only that local
availability update after the active command returns.

Without `-Execute`, `run`, `resume`, `cancel`, `review` and `steam-free` print
an inert operation plan. `status` reads existing files only. Preparation and
`hold` have local file effects. There is no general shell or arbitrary remote
operation parameter.

## Evidence and review

`captured_unreviewed` requires five complete 1920x1200 BMPs with matching hashes,
fixed labels, ordered interaction phases, the reviewed body baseline, two
alias/cooldown and movement epochs, zero fixture errors, configuration cleanup,
fixture/network/relay absence and a fresh post-result game-free worker heartbeat.
A terminal cancelled/failed result stays unaccepted even when cleanup passes.

Review all five frames and bind the decision to the exact reported digest:

```powershell
& $controller -Operation review -CampaignDirectory $campaign -Panel ranks -Language en `
    -EvidenceSha256 $reviewedDigest -ImagesReviewed -Execute
```

Review rehashes evidence and writes a separate immutable review receipt. It does
not change raw runner results or their `acceptancePassed=false` flag. A crash
between a complete evidence/review receipt and state replacement can be repaired
with resume without runtime effects. Status rechecks accepted evidence and
reports `integrity_unconfirmed` if it changed. The sanitized matrix contains
case IDs, phases, safe reason codes and evidence digests, without private paths
or topology.

Completed evidence can also be inspected without runtime access or state writes:

```powershell
& $controller -Operation verify-evidence -CampaignDirectory $campaign -Panel ranks -Language en
```

This read-only operation verifies all prepared input pins and the full evidence
package with the current verifier. It reports the verifier hash and whether the
recorded controller source pins still match. It never changes those pins,
claims, results, campaign phase or acceptance. `run`, `resume` and `review`
continue to require the original source pins. After a verifier correction, keep
an independent review record for old evidence and prepare a fresh campaign for
future runtime work rather than silently migrating an earlier attempt.

Frame labels are panel-specific: `<panel>-first`, `after-first-ok`,
`after-controls`, `<panel>-alias`, `after-last-ok`. Generic or cross-panel labels
are rejected. The controller tests consume the actual runner label functions so
their synthetic results cannot silently invent a different protocol.

This controller does not prove renderer identity, production gameplay, PvP,
saved personal statistics or a 24-hour activity window. Its offline tests use
synthetic frames and effect ports; live controller acceptance remains a separate
gate. See [verification and limits](client-controller-readiness.md).
