"""Credential metadata regressions and an isolated Linux real-ACL rehearsal."""
import errno
import importlib.util
import os
from pathlib import Path
import stat
import struct
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('credential_service', ROOT/'ops/availability/archive_trigger_service.py')
service = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(service)


def acl(uid, permission):
    return struct.pack('<I', 2) + b''.join(struct.pack('<HHI', tag, access, identity)
        for tag, access, identity in ((1, permission, 0xffffffff), (2, permission, uid),
            (4, 0, 0xffffffff), (16, permission, 0xffffffff), (32, 0, 0xffffffff)))


class MetadataTests(unittest.TestCase):
    def check(self, uid=1234, owner=0, gid=0, mode=0o440, links=1, size=24,
              access=None, readonly=True, directory=False, acl_error=None, kind=None):
        permission = 5 if directory else 4
        info = SimpleNamespace(st_uid=owner, st_gid=gid, st_mode=(kind if kind is not None else
                               stat.S_IFDIR if directory else stat.S_IFREG)|mode,
                               st_nlink=links, st_size=size)
        kwargs = dict(side_effect=acl_error) if acl_error else dict(return_value=access if access is not None else acl(uid, permission))
        with patch.object(service.os, 'fstat', return_value=info), \
             patch.object(service.os, 'fstatvfs', return_value=SimpleNamespace(f_flag=int(readonly)), create=True), \
             patch.object(service.os, 'ST_RDONLY', 1, create=True), \
             patch.object(service.os, 'getxattr', create=True, **kwargs):
            service._systemd_access(12, uid, permission)

    def test_root_acl_and_directory_are_accepted(self):
        self.check()
        self.check(directory=True, mode=0o550, links=2)

    def test_service_owned_private_fallback_is_accepted(self):
        for permission in (4, 5):
            for code in (errno.ENODATA, errno.ENOTSUP):
                self.check(owner=1234, mode=permission<<6, directory=permission==5,
                           acl_error=OSError(code, 'No credential ACL'))

    def test_other_owners_group_or_other_access_are_rejected(self):
        for change in (dict(owner=77),dict(gid=1),dict(mode=0o444),dict(mode=0o640),dict(mode=0o400),
                       dict(owner=1234,mode=0o600),dict(owner=1234,mode=0o440),dict(uid=0)):
            with self.subTest(change=change),self.assertRaises(ValueError): self.check(**change)

    def test_writable_mount_and_link_or_size_errors_are_rejected(self):
        for change in (dict(readonly=False),dict(links=2),dict(size=0),dict(size=514),dict(kind=stat.S_IFIFO)):
            with self.subTest(change=change),self.assertRaises(ValueError): self.check(**change)

    def test_acl_identity_permissions_and_structure_are_exact(self):
        original = acl(1234, 4)
        altered = [b'', original[:-1], original+b'extra', acl(1235,4), acl(1234,5),
                   struct.pack('<I',3)+original[4:]]
        for index in (0,1,2,3,4):
            value=bytearray(original)
            struct.pack_into('<H',value,4+index*8+2,7)
            altered.append(bytes(value))
        for index in range(5):
            altered.append(original+original[4+index*8:12+index*8])
        for value in altered:
            with self.subTest(aclBytes=len(value)),self.assertRaises(ValueError): self.check(access=value)

    def test_missing_or_unreadable_acl_is_not_root_access(self):
        for code in (errno.ENODATA,errno.ENOTSUP,errno.EACCES):
            with self.subTest(code=code),self.assertRaises(ValueError):
                self.check(acl_error=OSError(code,'ACL unavailable'))

    def test_private_fallback_does_not_accept_an_acl(self):
        with self.assertRaises(ValueError):self.check(owner=1234,mode=0o400)

    def test_wrong_directory_is_rejected_before_open(self):
        with patch.object(service.os,'open',side_effect=AssertionError('Opened an unapproved path')):
            with self.assertRaises(ValueError):service.load_systemd_token('/unapproved')


@unittest.skipUnless(os.name=='posix' and getattr(os,'geteuid',lambda:-1)()==0, 'root Linux ACL rehearsal')
class RealAclTests(unittest.TestCase):
    def test_real_acl_loads_under_an_unprivileged_uid(self):
        uid=65534
        with tempfile.TemporaryDirectory(prefix='archive-credential-acl-') as temporary:
            directory=Path(temporary).resolve()
            self.assertTrue(directory.is_relative_to(Path(tempfile.gettempdir()).resolve()))
            path=directory/'github-token'
            path.write_bytes(b'synthetic_credential_only\n')
            os.chmod(path,0o400);os.chmod(directory,0o500)
            os.setxattr(path,'system.posix_acl_access',acl(uid,4))
            os.setxattr(directory,'system.posix_acl_access',acl(uid,5))
            self.assertEqual(path.stat().st_uid,0)
            self.assertEqual(stat.S_IMODE(path.stat().st_mode),0o440)
            def read_as_user(expected):
                read_fd,write_fd=os.pipe()
                child=os.fork()
                if child==0:
                    os.close(read_fd)
                    try:
                        os.setgroups([]);os.setgid(uid);os.setuid(uid)
                        # Real ACL and UID transition; only the separate read-only
                        # mount flag is simulated on this temporary filesystem.
                        with patch.object(service,'SYSTEMD_CREDENTIALS',directory), \
                             patch.object(service.os,'fstatvfs',return_value=SimpleNamespace(f_flag=os.ST_RDONLY)):
                            if expected is None:
                                with self.assertRaises((ValueError,OSError)):
                                    service.load_systemd_token(directory)
                            else:
                                self.assertTrue(os.access(path,os.R_OK))
                                with self.assertRaises(ValueError):service.load_token(directory)
                                self.assertEqual(service.load_systemd_token(directory),expected)
                        os.write(write_fd,b'P')
                    except BaseException:
                        os.write(write_fd,b'F')
                    finally:os._exit(0)
                os.close(write_fd)
                try:
                    result=os.read(read_fd,1)
                    _,status=os.waitpid(child,0)
                    self.assertEqual(result,b'P','Real ACL reader regression under non-root UID')
                    self.assertEqual(status,0)
                finally:os.close(read_fd)

            read_as_user('synthetic_credential_only')
            for invalid in (b'',b'x'*514,b'invalid token',b'\xff'):
                with self.subTest(invalidBytes=len(invalid)):
                    path.write_bytes(invalid)
                    read_as_user(None)
            path.write_bytes(b'synthetic_credential_only')
            alias=directory/'hardlink'
            os.link(path,alias)
            read_as_user(None)
            alias.unlink()
            path.unlink()
            path.symlink_to(alias)
            read_as_user(None)


if __name__=='__main__':
    if '--real-acl' in sys.argv:
        sys.argv.remove('--real-acl')
        if os.name!='posix' or os.geteuid()!=0:
            raise SystemExit('Real ACL rehearsal requires root Linux; it must not be silently skipped.')
    unittest.main()
