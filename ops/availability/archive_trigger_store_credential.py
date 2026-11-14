"""Root-only exclusive token installation from stdin; never prints credential data."""
import os
from pathlib import Path
import stat
import sys

DESTINATION = Path('/etc/goldsrcops/archive-trigger')


def store(directory, raw, owner=0):
    if os.name != 'posix' or os.geteuid() != owner:
        raise ValueError('root-required')
    directory = Path(directory)
    if any(p.is_symlink() for p in (directory, *directory.parents)):
        raise ValueError('symlink-rejected')
    info=directory.stat()
    if not stat.S_ISDIR(info.st_mode) or info.st_uid != owner or info.st_mode & 0o022:
        raise ValueError('protected-directory-required')
    token=raw.decode('ascii').removesuffix('\n')
    if not 1 <= len(token) <= 512 or not all(c.isalnum() or c=='_' for c in token):
        raise ValueError('credential-format')
    fd=os.open(directory/'github-token',os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW|os.O_CLOEXEC,0o600)
    try:
        os.fchmod(fd,0o600)
        with os.fdopen(fd,'wb',closefd=False) as stream:
            stream.write(token.encode('ascii'));stream.flush();os.fsync(fd)
    finally:
        os.close(fd)
    parent=os.open(directory,os.O_RDONLY|os.O_DIRECTORY)
    try:os.fsync(parent)
    finally:os.close(parent)


if __name__=='__main__':
    os.umask(0o077)
    try:
        if len(sys.argv)!=1:raise ValueError('no-arguments')
        raw=sys.stdin.buffer.read(514)
        if len(raw)>513:raise ValueError('credential-bound')
        store(DESTINATION,raw)
        print('ARCHIVE_TRIGGER_CREDENTIAL=stored-exclusively')
    except Exception:
        print('Credential installation stopped; do not retry.',file=sys.stderr)
        sys.exit(1)
