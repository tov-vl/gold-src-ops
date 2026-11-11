"""Use the runner's existing official Ubuntu fallback mirrors for browser dependencies."""
import hashlib
import os
from pathlib import Path
import stat

MIRRORS = Path('/etc/apt/apt-mirrors.txt')
SOURCES = Path('/etc/apt/sources.list.d/ubuntu.sources')
AZURE = b'http://azure.archive.ubuntu.com/ubuntu/\tpriority:1\n'
OFFICIAL = (b'https://archive.ubuntu.com/ubuntu/\tpriority:2\n'
            b'https://security.ubuntu.com/ubuntu/\tpriority:3\n')


def plan(mirrors, sources):
    if mirrors not in (AZURE + OFFICIAL, OFFICIAL):
        raise ValueError('Unexpected hosted-runner mirrorlist; no change made')
    if (b'mirror+file:/etc/apt/apt-mirrors.txt' not in sources or
            b'Signed-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg' not in sources):
        raise ValueError('Expected Ubuntu mirrorlist and signing key configuration missing')
    return OFFICIAL


def main():
    if (os.environ.get('GITHUB_ACTIONS') != 'true' or os.environ.get('RUNNER_OS') != 'Linux' or
            os.environ.get('RUNNER_ARCH') != 'X64' or os.environ.get('RUNNER_ENVIRONMENT') != 'github-hosted' or os.geteuid() != 0):
        raise RuntimeError('Only the privileged disposable Linux x64 CI runner is supported')
    release = Path('/etc/os-release').read_text(encoding='utf-8')
    if 'ID=ubuntu\n' not in release or 'VERSION_ID="24.04"\n' not in release:
        raise RuntimeError('Only the reviewed Ubuntu 24.04 runner is supported')
    sources = SOURCES.read_bytes()
    if len(sources) > 16384:
        raise ValueError('Unexpected Ubuntu sources size')
    with os.fdopen(os.open(MIRRORS, os.O_RDWR | os.O_NOFOLLOW), 'r+b') as stream:
        info = os.fstat(stream.fileno())
        if not stat.S_ISREG(info.st_mode) or info.st_uid != 0 or info.st_nlink != 1 or info.st_size > 4096:
            raise ValueError('Unexpected mirrorlist file metadata')
        original = stream.read(4097)
        proposed = plan(original, sources)
        if original != proposed:
            stream.seek(0)
            stream.write(proposed)
            stream.truncate()
            stream.flush()
            os.fsync(stream.fileno())
        stream.seek(0)
        if stream.read() != OFFICIAL or SOURCES.read_bytes() != sources:
            raise RuntimeError('APT mirror preparation verification failed')
    print('Existing official HTTPS mirrors selected; Ubuntu sources/signing keys preserved; sources SHA256=' +
          hashlib.sha256(sources).hexdigest())


if __name__ == '__main__':
    main()
