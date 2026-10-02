"""Real local Git repositories plus ZIP/rollback tests, never touches the game."""
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / 'scripts'))
import sync_pack
from install_linux import verify_pack
from update_linux import expand_archive


def git(root, *args):
    return subprocess.check_output(['git', '-C', str(root), '-c', 'user.name=Test', '-c', 'user.email=test@invalid.local', *args], stderr=subprocess.STDOUT).decode('utf-8').strip()


def fixture(root, version, content):
    (root / 'Game/plugins').mkdir(parents=True, exist_ok=True)
    (root / 'Game/plugins/test.dll').write_bytes(content)
    (root / 'scripts').mkdir(exist_ok=True)
    for name in ('install_linux.py', 'Install-Windows.ps1'):
        (root / 'scripts' / name).write_text('# Fixture; no game installation\n')
    (root / 'VERSION').write_text(version + '\n')
    (root / 'mods.lock.json').write_text(json.dumps({'packVersion': version, 'packages': []}))
    (root / 'files.sha256.json').write_text(json.dumps([{'path': 'plugins/test.dll', 'sha256': hashlib.sha256(content).hexdigest()}]))
    (root / 'README.md').write_text('Release ' + version)
    (root / '.gitignore').write_text('.updates/\nlocal-settings.json\nNordicRadio/\n.cache/\nbackups/\n')
    (root / '.gitattributes').write_text('* -text\n')


class SyncTests(unittest.TestCase):
    windows = False

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix='sync-русский пробел-', dir=ROOT / '.cache')
        self.addCleanup(self.tmp.cleanup)
        self.base = Path(self.tmp.name)
        self.root, self.remote, self.snapshot = (self.base / name for name in ('pack', 'remote', 'snapshot'))
        fixture(self.root, '1.0.0', b'old')
        fixture(self.remote, '1.1.0', b'new')
        fixture(self.snapshot, '1.1.0', b'new')
        git(self.remote, 'init', '-b', 'main')
        git(self.remote, 'add', '.')
        git(self.remote, 'commit', '-m', 'initial fixture')
        (self.root / 'local-settings.json').write_text('{"game":"keep"}')
        (self.root / 'NordicRadio/Music').mkdir(parents=True)
        (self.root / 'NordicRadio/Music/personal.mp3').write_bytes(b'music')
        (self.root / 'ValheimModpack/WorldCharacters/characters').mkdir(parents=True)
        (self.root / 'ValheimModpack/WorldCharacters/characters/preserved.wchar').write_bytes(b'world character save')
        (self.root / 'notes.txt').write_text('unrelated')
        (self.root / 'backups/user').mkdir(parents=True)
        (self.root / 'backups/user/archive.zip').write_bytes(b'user backup')
        (self.root / '.updates/manual-old').mkdir(parents=True)
        (self.root / '.updates/manual-old/keep.txt').write_text('old user archive')

    def run_update(self, mode='git', fail=False):
        if self.windows:
            result = subprocess.run(['powershell.exe','-NoProfile','-ExecutionPolicy','Bypass','-File',
                str(ROOT / 'scripts/test_sync_pack.ps1'), '-Repository', str(ROOT), '-Root', str(self.root),
                '-Remote', str(self.remote), '-Snapshot', str(self.snapshot), '-Mode', mode], capture_output=True)
            if fail:
                self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
            else:
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            return
        action = lambda: sync_pack.sync_pack(self.root, lambda _: self.snapshot, verify_pack, expand_archive,
                git=None if mode in ('zip','rollback','rollback-failed','interrupt') else shutil.which('git'), repository=str(self.remote))
        if fail:
            with self.assertRaises((ValueError, OSError)):
                action()
        else:
            self.assertEqual(action(), self.root)

    def preserved(self):
        self.assertEqual((self.root / 'local-settings.json').read_text(), '{"game":"keep"}')
        self.assertEqual((self.root / 'NordicRadio/Music/personal.mp3').read_bytes(), b'music')
        self.assertEqual((self.root / 'ValheimModpack/WorldCharacters/characters/preserved.wchar').read_bytes(), b'world character save')
        self.assertEqual((self.root / 'notes.txt').read_text(), 'unrelated')
        self.assertEqual((self.root / 'backups/user/archive.zip').read_bytes(), b'user backup')
        self.assertEqual((self.root / '.updates/manual-old/keep.txt').read_text(), 'old user archive')
        self.assertFalse((self.root / '.updates/update.lock').exists())

    def cleaned(self):
        for prefix in ('replace-', 'pull-', 'clone-'):
            self.assertFalse(list((self.root / '.updates').glob(prefix + '*')))

    def test_zip_without_git_replaces_current_folder_and_cleans_up(self):
        (self.root / 'Game/plugins/obsolete.dll').write_bytes(b'old mod')
        self.run_update('zip')
        verify_pack(self.root)
        self.assertFalse((self.root / 'Game/plugins/obsolete.dll').exists())
        self.cleaned()
        self.assertFalse((self.root / '.git').exists())
        self.preserved()
        self.run_update('zip')
        self.preserved()
        self.cleaned()

    def test_zip_becomes_git_then_real_pull_updates_it(self):
        self.run_update()
        self.assertEqual(Path(git(self.root, 'remote', 'get-url', 'origin')), self.remote)
        self.assertEqual(git(self.root, 'status', '--porcelain', '--untracked-files=no'), '')
        fixture(self.remote, '1.2.0', b'newer')
        git(self.remote, 'add', '.')
        git(self.remote, 'commit', '-m', 'second fixture')
        self.run_update()
        self.assertEqual(git(self.root, 'rev-parse', 'HEAD'), git(self.remote, 'rev-parse', 'HEAD'))
        self.assertEqual((self.root / 'Game/plugins/test.dll').read_bytes(), b'newer')
        self.cleaned()
        self.preserved()

    def test_local_git_changes_are_not_overwritten(self):
        self.run_update()
        (self.root / 'README.md').write_text('my changes')
        self.run_update(fail=True)
        self.assertEqual((self.root / 'README.md').read_text(), 'my changes')
        self.preserved()

    def test_different_origin_is_not_replaced(self):
        self.run_update()
        git(self.root, 'remote', 'set-url', 'origin', 'https://example.org/another.git')
        self.run_update(fail=True)
        self.assertEqual(git(self.root, 'remote', 'get-url', 'origin'), 'https://example.org/another.git')

    def test_missing_origin_is_connected(self):
        self.run_update()
        git(self.root, 'remote', 'remove', 'origin')
        self.run_update()
        self.assertEqual(git(self.root, 'rev-parse', 'HEAD'), git(self.remote, 'rev-parse', 'HEAD'))

    def test_version_mismatch_rejected_before_replacement(self):
        (self.snapshot / 'VERSION').write_text('1.4.9\n')
        self.run_update('zip', fail=True)
        self.assertEqual((self.root / 'Game/plugins/test.dll').read_bytes(), b'old')
        self.preserved()

    def test_corrupt_git_remote_rejected_before_pull(self):
        self.run_update()
        before = git(self.root, 'rev-parse', 'HEAD')
        (self.remote / 'Game/plugins/test.dll').write_bytes(b'bad hash')
        git(self.remote, 'add', '.')
        git(self.remote, 'commit', '-m', 'bad fixture')
        self.run_update(fail=True)
        self.assertEqual(git(self.root, 'rev-parse', 'HEAD'), before)
        self.cleaned()

    def test_replacement_failure_rolls_back(self):
        if self.windows:
            self.run_update('rollback', fail=True)
        else:
            original = Path.rename
            def injected(path, target):
                if path.name == 'Game' and path.parent.name == 'staged':
                    raise OSError('Injected file replacement failure')
                return original(path, target)
            with patch.object(Path, 'rename', injected):
                self.run_update('rollback', fail=True)
        self.assertEqual((self.root / 'Game/plugins/test.dll').read_bytes(), b'old')
        self.assertEqual((self.root / 'README.md').read_text(), 'Release 1.0.0')
        self.preserved()
        self.cleaned()

    def test_replacement_rollback_failure_retains_originals_and_instructions(self):
        if self.windows:
            self.run_update('rollback-failed', fail=True)
        else:
            rename = Path.rename
            def failing(path, target):
                if path.name == 'Game' and path.parent.name in ('staged', 'original'):
                    raise OSError('Injected replacement and rollback failure')
                return rename(path, target)
            with patch.object(Path, 'rename', failing):
                self.run_update('rollback-failed', fail=True)
        job, = (self.root / '.updates').glob('replace-*')
        self.assertEqual((job / 'original/Game/plugins/test.dll').read_bytes(), b'old')
        self.assertTrue((job / 'RECOVERY.txt').is_file())
        self.preserved()

    def test_interrupted_replacement_restores_originals(self):
        if self.windows:
            self.run_update('interrupt', fail=True)
        else:
            rename = Path.rename
            def interrupted(path, target):
                if path.name == 'Game' and path.parent.name == 'staged':
                    raise KeyboardInterrupt()
                return rename(path, target)
            with patch.object(Path, 'rename', interrupted), self.assertRaises(KeyboardInterrupt):
                self.run_update('interrupt')
        self.assertEqual((self.root / 'Game/plugins/test.dll').read_bytes(), b'old')
        self.preserved()
        self.cleaned()

    def test_interrupt_after_original_move_restores_originals(self):
        if self.windows:
            self.skipTest('Python interruption injection')
        rename = Path.rename
        def interrupted(path, target):
            result = rename(path, target)
            if path == self.root / 'Game':
                raise KeyboardInterrupt()
            return result
        with patch.object(Path, 'rename', interrupted), self.assertRaises(KeyboardInterrupt):
            self.run_update('zip')
        self.assertEqual((self.root / 'Game/plugins/test.dll').read_bytes(), b'old')
        self.preserved()
        self.cleaned()

    def test_interrupt_during_rollback_keeps_originals(self):
        if self.windows:
            self.skipTest('Python interruption injection')
        rename = Path.rename
        def interrupted(path, target):
            if path.name == 'Game' and path.parent.name in ('staged', 'original'):
                raise KeyboardInterrupt()
            return rename(path, target)
        with patch.object(Path, 'rename', interrupted):
            self.run_update('zip', fail=True)
        job, = (self.root / '.updates').glob('replace-*')
        self.assertEqual((job / 'original/Game/plugins/test.dll').read_bytes(), b'old')
        self.assertTrue((job / 'RECOVERY.txt').is_file())
        self.preserved()

    def new_upstream(self):
        fixture(self.remote, '1.2.0', b'newer')
        git(self.remote, 'add', '.')
        git(self.remote, 'commit', '-m', 'new upstream')

    def test_git_post_pull_failure_restores_old_commit_without_archive(self):
        self.run_update()
        old = git(self.root, 'rev-parse', 'HEAD')
        self.new_upstream()
        if self.windows:
            self.run_update('pull-verify-failed', fail=True)
        else:
            original_verify = verify_pack
            def verify(path):
                if Path(path) == self.root:
                    raise ValueError('Injected verification failure after Git pull')
                return original_verify(path)
            with patch.object(sys.modules[__name__], 'verify_pack', verify):
                self.run_update('pull-verify-failed', fail=True)
        self.assertEqual(git(self.root, 'rev-parse', 'HEAD'), old)
        self.assertEqual((self.root / 'Game/plugins/test.dll').read_bytes(), b'new')
        self.preserved()
        self.cleaned()

    def test_git_rollback_failure_retains_only_recovery_job(self):
        self.run_update()
        old = git(self.root, 'rev-parse', 'HEAD')
        self.new_upstream()
        if self.windows:
            self.run_update('pull-rollback-failed', fail=True)
        else:
            original_git = sync_pack.git_run
            original_verify = verify_pack
            def verify(path):
                if Path(path) == self.root:
                    raise ValueError('Injected verification failure')
                return original_verify(path)
            def failing(executable, root, *arguments):
                if arguments[0] == 'reset':
                    raise OSError('Injected rollback failure')
                return original_git(executable, root, *arguments)
            with patch.object(sync_pack, 'git_run', failing), patch.object(sys.modules[__name__], 'verify_pack', verify):
                self.run_update('pull-rollback-failed', fail=True)
        job, = (self.root / '.updates').glob('pull-*')
        self.assertIn(old, (job / 'RECOVERY.txt').read_text(encoding='utf-8-sig'))
        self.assertFalse((job / 'original').exists())
        self.preserved()

    def test_another_update_lock_is_respected(self):
        (self.root / '.updates/update.lock').mkdir(parents=True)
        self.run_update('zip', fail=True)
        self.assertTrue((self.root / '.updates/update.lock').exists())
        self.assertEqual((self.root / 'Game/plugins/test.dll').read_bytes(), b'old')

    def test_personal_paths_in_snapshot_rejected(self):
        (self.snapshot / 'local-settings.json').write_text('overwrite attempt')
        self.run_update('zip', fail=True)
        self.preserved()

    def test_standalone_backups_in_snapshot_rejected(self):
        (self.snapshot / 'backups').mkdir()
        (self.snapshot / 'backups/overwrite.zip').write_bytes(b'overwrite attempt')
        self.run_update('zip', fail=True)
        self.preserved()
        self.cleaned()

    def test_diverged_history_is_not_reset(self):
        self.run_update()
        (self.root / 'README.md').write_text('local committed changes')
        git(self.root, 'add', 'README.md')
        git(self.root, 'commit', '-m', 'local commit')
        local = git(self.root, 'rev-parse', 'HEAD')
        fixture(self.remote, '1.2.0', b'new upstream')
        git(self.remote, 'add', '.')
        git(self.remote, 'commit', '-m', 'divergent upstream')
        self.run_update(fail=True)
        self.assertEqual(git(self.root, 'rev-parse', 'HEAD'), local)
        self.assertEqual((self.root / 'README.md').read_text(), 'local committed changes')

    def test_untracked_git_collision_is_preserved(self):
        self.run_update()
        (self.remote / 'notes.txt').write_text('upstream notes')
        git(self.remote, 'add', 'notes.txt')
        git(self.remote, 'commit', '-m', 'colliding filename')
        self.run_update(fail=True)
        self.assertEqual((self.root / 'notes.txt').read_text(), 'unrelated')

    def test_links_are_rejected_without_following_them(self):
        outside = self.base / 'outside.txt'
        outside.write_text('external contents')
        link = self.root / 'Game/plugins/external.dll'
        try:
            link.symlink_to(outside)
        except OSError:
            self.skipTest('Creating symlinks is not permitted on this host')
        self.run_update('zip', fail=True)
        self.assertEqual(outside.read_text(), 'external contents')
        self.assertTrue(link.is_symlink())


@unittest.skipUnless(sys.platform == 'win32', 'Requires Windows PowerShell 5.1')
class WindowsSyncTests(SyncTests):
    windows = True


if __name__ == '__main__':
    unittest.main()
