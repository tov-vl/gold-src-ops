"""One hidden input to an approved existing SSH alias; no local credential file."""
import argparse
import getpass
import re
import subprocess


def transfer(alias, token, run=subprocess.run):
    if not re.fullmatch('[A-Za-z0-9][A-Za-z0-9_.-]{0,79}',alias):
        raise ValueError('Existing SSH alias required')
    if not 1 <= len(token)<=512 or not all(c.isascii() and (c.isalnum() or c=='_') for c in token):
        raise ValueError('Invalid credential format')
    result=run(['ssh','-T','-o','BatchMode=yes','-o','ClearAllForwardings=yes','-o','StrictHostKeyChecking=yes',
        '-o','ConnectTimeout=10',alias,'/usr/bin/python3',
        '/opt/goldsrcops-archive-trigger/ops/availability/archive_trigger_store_credential.py'],
        input=token.encode('ascii'),stdout=subprocess.PIPE,stderr=subprocess.DEVNULL,
        timeout=30,check=False)
    if result.returncode!=0 or result.stdout.strip()!=b'ARCHIVE_TRIGGER_CREDENTIAL=stored-exclusively':
        raise ValueError('Credential transfer uncertain; inspect metadata without retry')


if __name__=='__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('--ssh-alias',required=True,help='Exact existing alias frozen in the private rollout plan')
    args=parser.parse_args()
    token=None
    try:
        token=getpass.getpass('New Archive Actions token (hidden; memory only): ')
        transfer(args.ssh_alias,token)
        print('ARCHIVE_TRIGGER_CREDENTIAL=confirmed')
    except Exception:
        print('Credential transfer stopped; do not retry.')
        raise SystemExit(1) from None
    finally:token=None
