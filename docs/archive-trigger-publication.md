# Archive Trigger Component Publication

Prepared on 2026-11-14. This record covers the publication implementation and
its source review. No component tag, GitHub Release or production activation is
created by merging the implementation pull request. The overall runtime risk
remains R3 under the [rollout plan](archive-trigger-rollout.md).

## Separate Component Identity

The existing CI also accepts `archive-trigger-vX.Y.Z-rc.N` and
`archive-trigger-vX.Y.Z`. Numeric identifiers have no leading zeroes and the
candidate number is positive. These tags select full Quality Gate, Container
Smoke, Browser Smoke and Client Lab Policy, followed by Publish Archive Trigger. They do not
select API, Web or AlertReceiver image publication. Application image tags keep
their existing contract. API/Web compatibility still needs its own accepted
application images before runtime cutover.

Only an annotated tag at the checked-out revision, reachable from `origin/main`,
is eligible. The publisher independently checks GitHub's signature verification
and requires the remote tag object to match the local object. An unsigned,
lightweight, moved or unrelated tag fails before any release write. Operators
must also verify their signed tag locally before pushing it.

The publication job alone gets the existing built-in GITHUB_TOKEN with Contents
write. No personal token, environment secret, provider setting, public port or
service identity is added. Publication is serialized and never canceled by a
later run. It has no workflow_dispatch recovery entry point.

## Candidate Build And Verification

The publisher runs `tools/release/build-archive-trigger.py` only for a candidate,
from a clean checkout. The two assets are `archive-trigger-X.Y.Z.zip` and its
`.sha256` file. The ZIP manifest records the exact source revision, version,
file inventory/hashes and pending runtime acceptance. Review-only/dirty packages
are rejected. Unit files and configuration remain install-disabled/paused
preparation, without a credential.

The publisher validates bounded ZIP contents without extracting them, rejects
duplicate/extra paths and compares every source payload with the clean checkout.
It creates one draft release with both assets, verifies server sizes/digests,
downloads and verifies the exact package again, and publishes the draft once.
Neither candidate nor stable changes the repository's Latest release pointer.
Existing published or draft releases are rejected; no assets are replaced.

## Byte-Preserving Stable Promotion

After separate R3 acceptance, create a signed annotated stable tag at the exact
candidate revision. Its annotation must contain exactly one line of each form:

```text
Candidate: archive-trigger-v2.132.0-rc.1
SHA256: <64 lowercase hexadecimal characters from the accepted candidate ZIP>
```

Other human-readable annotation text is allowed. The selected candidate must
have the same base version and revision and a verified signed tag. The publisher
downloads its already published prerelease assets and checks the signed
annotation's checksum, server metadata, manifest and source bytes. It copies
those assets to the stable release through the same draft/download/publish
sequence. It never invokes the package builder on this path. The final receipt
records build/promote, source revision, candidate and exact ZIP SHA-256.

The stable package intentionally retains the candidate's pending-acceptance
manifest: target acceptance evidence belongs in the separate release record,
not a rebuilt or edited archive.

## Failure And Provider Boundary

One failed or uncertain create/upload/publish stops the publisher. A draft or
published release may remain. Preserve its state, assets and logs, inspect it
read-only and agree a recovery before another write. Rerunning a workflow does
not grant permission to delete or replace that release. The implementation has
no automatic cleanup, overwrite or retry path.

The inspected repository currently has provider immutability disabled. This
change preserves that setting. Write-once checks protect this publication path;
they cannot stop a repository administrator changing a mutable release. The
receipt records the actual `providerImmutable` value, rather than claiming a
provider lock. Signed tag identity and the accepted SHA-256 remain the install
and promotion inputs. Enabling provider immutability is a separate setting
decision. See [GitHub release management](https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository)
and [tag signature verification](https://docs.github.com/en/rest/git/tags).

## Verification And Remaining Gates

Offline tests cover strict tags/signatures/main reachability, provider read
errors, review-package rejection, altered payloads, exact candidate promotion,
existing releases and an uncertain publish without a retry. The CI routing smoke
preserves the application publication guards. Remote required CI and actual
candidate asset round-trip remain separate evidence; local mocks do not prove
GitHub publication.

The present delivery stops after implementation PR/CI/merge. Signed component
and application tags, credential/identity approval, target preflight, cutover,
exact-object Recovery and natural-slot acceptance remain in the rollout plan.
