"""Local fork dependencies and resources, without a real game or network."""
import copy
import hashlib
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
from install_linux import verify_locked_plugins


class LocalPluginLockTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='valheim-local-lock-')
        self.addCleanup(self.temporary.cleanup)
        self.pack = Path(self.temporary.name)
        self.dll = self.pack / 'BepInEx/plugins/XPortal/XPortal.dll'
        self.resource = self.pack / 'BepInEx/plugins/XPortal/LICENSE'
        self.dll.parent.mkdir(parents=True)
        self.dll.write_bytes(b'fork DLL fixture')
        self.resource.write_bytes(b'GPL fixture')
        self.local = {'id': 'AnyPortalPlus', 'version': '1.3.0',
                      'destination': self.dll.relative_to(self.pack).as_posix(),
                      'sha256': hashlib.sha256(self.dll.read_bytes()).hexdigest(),
                      'dependencies': ['Jotunn-2.30.0'],
                      'resourceFiles': [{'destination': self.resource.relative_to(self.pack).as_posix(),
                                         'sha256': hashlib.sha256(self.resource.read_bytes()).hexdigest()}]}
        self.lock = {'packages': [{'id': 'Jotunn', 'version': '2.30.2', 'dependencies': []}],
                     'localPlugins': [self.local, {'id': 'PortalFinder', 'version': '1.0.1',
                        'destination': self.local['destination'], 'sha256': self.local['sha256'],
                        'dependencies': ['AnyPortalPlus-1.3.0']}]}

    def test_local_dependency_and_resources_are_supported(self):
        verify_locked_plugins(self.pack, self.lock)

    def test_absent_fork_is_rejected(self):
        self.lock['localPlugins'].pop(0)
        with self.assertRaisesRegex(ValueError, 'Missing/incompatible dependency'):
            verify_locked_plugins(self.pack, self.lock)

    def test_older_fork_is_rejected(self):
        self.local['version'] = '1.2.25'
        with self.assertRaisesRegex(ValueError, 'Missing/incompatible dependency'):
            verify_locked_plugins(self.pack, self.lock)

    def test_modified_dll_is_rejected(self):
        self.dll.write_bytes(b'old XPortal')
        with self.assertRaisesRegex(ValueError, 'checksum mismatch'):
            verify_locked_plugins(self.pack, self.lock)

    def test_missing_and_changed_resources_are_rejected(self):
        for action in ('missing', 'changed'):
            with self.subTest(action=action):
                if action == 'missing':
                    self.resource.unlink()
                else:
                    self.resource.write_bytes(b'different license')
                with self.assertRaisesRegex(ValueError, 'checksum mismatch'):
                    verify_locked_plugins(self.pack, self.lock)

    def test_duplicate_ids_are_rejected(self):
        self.lock['packages'].append(copy.deepcopy(self.local))
        with self.assertRaisesRegex(ValueError, 'Duplicate locked plugin'):
            verify_locked_plugins(self.pack, self.lock)

    def test_malformed_dependency_is_rejected(self):
        self.local['dependencies'] = ['Jotunn']
        with self.assertRaisesRegex(ValueError, 'Unknown dependency format'):
            verify_locked_plugins(self.pack, self.lock)

    def test_empty_fixture_lock_is_allowed(self):
        verify_locked_plugins(self.pack, {})


if __name__ == '__main__':
    unittest.main()
