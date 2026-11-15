"""Build a bounded review or clean-revision Archive trigger package; no publication."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import zipfile

ROOT = Path(__file__).resolve().parents[2]
FILES = (
    '.github/workflows/availability-evidence.yml',
    'ops/availability/archive_trigger_state.py', 'ops/availability/archive_trigger_workflow.py',
    'ops/availability/archive_trigger_github.py', 'ops/availability/archive_trigger_service.py',
    'ops/availability/archive_trigger_store_credential.py',
    'ops/availability/archive-monitor.py', 'ops/production/agent-monitor.py',
    'ops/gameserver/agent_diagnostics.py',
    'ops/production/systemd/goldsrcops-archive-trigger.service',
    'ops/production/systemd/goldsrcops-archive-trigger.timer',
    'ops/production/systemd/goldsrcops-archive-schedule-monitor-external.conf',
    'tools/smoke/archive-trigger-state.py', 'tools/smoke/archive-trigger-integration.py',
    'tools/smoke/archive-trigger-service.py',
    'tools/smoke/archive-trigger-credential.py',
    'tools/release/set-archive-trigger-credential.py',
    'tools/release/build-archive-trigger.py',
    'tools/release/publish-archive-trigger.py',
    'tools/smoke/archive-trigger-publication.py', 'docs/archive-trigger-rollout.md')


def build(output, version, review=False):
    if not re.fullmatch(r'(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)', version):
        raise ValueError('Invalid package version')
    revision = subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip()
    dirty = bool(subprocess.check_output(['git','status','--porcelain'],cwd=ROOT))
    if not re.fullmatch('[a-f0-9]{40}',revision) or (dirty and not review):
        raise ValueError('Release packaging requires a clean reviewed revision')
    payload = {}
    entries = []
    for name in FILES:
        path=ROOT/name
        if path.is_symlink():
            raise ValueError('Symlink payload rejected')
        raw=path.read_bytes().replace(b'\r\n',b'\n')
        if not 0<len(raw)<=524288:
            raise ValueError('Payload outside bounds')
        payload[name]=raw
        entries.append(dict(path=name,bytes=len(raw),sha256=hashlib.sha256(raw).hexdigest()))
    config=dict(schema=1,mode='paused',generation='SET_AT_ROLLOUT',exporter='SET_REVIEWED_EXPORTER_PIN',
        manifest={p:hashlib.sha256(payload[p]).hexdigest() for p in FILES[:3]},
        activation='SET_AT_ROLLOUT',coverage_end='SET_FROM_VERIFIED_ARCHIVE_RECEIPT',assessment_closed=False)
    # FILES[:3] is the workflow and two admission helpers, matching the on-host validator.
    payload['config.template.json']=(json.dumps(config,indent=2)+'\n').encode()
    entries.append(dict(path='config.template.json',bytes=len(payload['config.template.json']),
                        sha256=hashlib.sha256(payload['config.template.json']).hexdigest()))
    manifest=dict(schema=1,purpose='archive-external-trigger',version=version,sourceRevision=revision,
        reviewOnly=review,workingTree=dirty,risk='R3',files=entries,
        runtimeAcceptance='pending',credentialIncluded=False,automaticInstallation=False)
    payload['manifest.json']=(json.dumps(manifest,indent=2)+'\n').encode()
    output=Path(output)
    output.mkdir(parents=True,exist_ok=True)
    target=output/f'archive-trigger-{version}{"-review" if review else ""}.zip'
    if target.exists() or Path(str(target)+'.sha256').exists():
        raise ValueError('Immutable output exists')
    with zipfile.ZipFile(target,'x',compression=zipfile.ZIP_DEFLATED) as archive:
        for name,raw in payload.items():
            info=zipfile.ZipInfo(name,(2026,1,1,0,0,0));info.external_attr=0o100644<<16
            archive.writestr(info,raw)
    with zipfile.ZipFile(target) as archive:
        if archive.testzip() is not None:
            raise ValueError('Archive verification failed')
        for name,raw in payload.items():
            if archive.read(name)!=raw:
                raise ValueError('Payload verification failed')
    digest=hashlib.sha256(target.read_bytes()).hexdigest()
    with Path(str(target)+'.sha256').open('x',encoding='ascii') as stream:
        stream.write(digest+'  '+target.name+'\n')
    return target,digest


if __name__=='__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('--version',required=True)
    parser.add_argument('--output',required=True)
    parser.add_argument('--review',action='store_true')
    args=parser.parse_args()
    target,digest=build(args.output,args.version,args.review)
    print(target)
    print(digest)
