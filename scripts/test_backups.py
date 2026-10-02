"""Verify explicit Linux backups using temporary mock games only."""
import hashlib
import json
from pathlib import Path
import sys
import subprocess
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent))
import backup_game


class BackupTests(unittest.TestCase):
    def setUp(self):
        self.fixture = tempfile.TemporaryDirectory(prefix='vh-backup-')
        self.addCleanup(self.fixture.cleanup)
        self.base = Path(self.fixture.name).resolve()
        self.root, self.game = self.base / 'pack', self.base / 'game'
        self.root.mkdir()
        self.game.mkdir()
        (self.game / 'valheim.x86_64').write_bytes(b'game fixture')
        for name, contents in (
                ('BepInEx/plugins/old.dll', b'previous mod'),
                ('BepInEx/config/personal.cfg', b'personal config'),
                ('NordicRadio/Music/скальд.mp3', b'personal music'),
                ('ValheimModpack/WorldCharacters/characters/saved.wchar', b'authoritative character'),
                ('unrelated.txt', b'unrelated root file')):
            path = self.game / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(contents)
        (self.game / 'empty-directory').mkdir()

    def make_link(self, path, target, directory=False):
        try:
            path.symlink_to(target, target_is_directory=directory)
        except OSError as error:
            if getattr(error, 'winerror', None) == 1314:
                self.skipTest('Windows symlinks require Developer Mode or elevation.')
            raise

    def test_explicit_backup_is_complete_verified_and_preserves_source(self):
        before = backup_game.inventory(self.game)
        snapshot = backup_game.backup_game(self.root, self.game)
        self.assertEqual(snapshot.parent.parent, self.root / 'backups')
        self.assertEqual(snapshot.name, 'full-game-backup')
        self.assertTrue((snapshot.parent / 'BACKUP-COMPLETE.txt').is_file())
        self.assertTrue((snapshot / 'empty-directory').is_dir())
        metadata = json.loads((snapshot.parent / 'backup.json').read_text(encoding='utf-8'))
        self.assertEqual(metadata['gameDirectory'], str(self.game.resolve()))
        self.assertEqual(len(metadata['files']), 6)
        for entry in metadata['files']:
            copy = snapshot / entry['path']
            self.assertEqual(copy.read_bytes(), (self.game / entry['path']).read_bytes())
            self.assertEqual(entry['sha256'], hashlib.sha256(copy.read_bytes()).hexdigest())
        self.assertEqual(backup_game.inventory(self.game), before)

    def test_custom_destination_and_second_backup_keep_previous_copy(self):
        output = self.base / 'custom' / 'archive'
        first = backup_game.backup_game(self.root, self.game, output)
        (self.game / 'unrelated.txt').write_bytes(b'new content')
        second = backup_game.backup_game(self.root, self.game, output)
        self.assertNotEqual(first, second)
        self.assertEqual((first / 'unrelated.txt').read_bytes(), b'unrelated root file')
        self.assertEqual((second / 'unrelated.txt').read_bytes(), b'new content')

    def test_old_backups_and_transactions_are_excluded_at_every_depth(self):
        for name in ('ValheimModpack-backups/old/full-game-backup/data',
                     'NordicRadio/Music-backups/old/Music/song.mp3',
                     'BepInEx/backups/data',
                     '.valheim-modpack-install-old/original/old.dll',
                     'NordicRadio/.music-update-old/original/old.mp3',
                     'nested/.valheim-modpack-install-failed/RECOVERY.txt'):
            path = self.game / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b'old backup or unfinished transaction')
        snapshot = backup_game.backup_game(self.root, self.game)
        self.assertFalse((snapshot / 'ValheimModpack-backups').exists())
        self.assertFalse((snapshot / 'NordicRadio/Music-backups').exists())
        self.assertFalse((snapshot / 'BepInEx/backups').exists())
        self.assertFalse((snapshot / '.valheim-modpack-install-old').exists())
        self.assertFalse((snapshot / 'NordicRadio/.music-update-old').exists())
        self.assertFalse((snapshot / 'nested/.valheim-modpack-install-failed').exists())
        self.assertTrue((self.game / '.valheim-modpack-install-old/original/old.dll').is_file())

    def test_cannot_back_up_inside_game_directory(self):
        for destination in (self.game, self.game / 'backup-output'):
            with self.assertRaisesRegex(ValueError, 'outside'):
                backup_game.backup_game(self.root, self.game, destination)
        self.assertFalse((self.game / 'backup-output').exists())

    def test_invalid_game_and_repository_overlap_are_rejected(self):
        invalid = self.base / 'invalid'
        invalid.mkdir()
        with self.assertRaises(ValueError):
            backup_game.backup_game(self.root, invalid)
        with self.assertRaises(ValueError):
            backup_game.backup_game(self.game, self.game)
        self.assertFalse((self.root / 'backups').exists())

    def test_copy_failure_removes_incomplete_job_and_keeps_previous_backup(self):
        previous = backup_game.backup_game(self.root, self.game)
        before = backup_game.inventory(self.game)
        with patch.object(backup_game.shutil, 'copy2', side_effect=OSError('disk full')):
            with self.assertRaisesRegex(OSError, 'disk full'):
                backup_game.backup_game(self.root, self.game)
        self.assertEqual(list((self.root / 'backups').iterdir()), [previous.parent])
        self.assertTrue((previous.parent / 'BACKUP-COMPLETE.txt').is_file())
        self.assertEqual(backup_game.inventory(self.game), before)

    def test_changed_source_is_not_marked_complete(self):
        copy2 = backup_game.shutil.copy2
        def change_source(source, destination, **kwargs):
            result = copy2(source, destination, **kwargs)
            if Path(source).name == 'unrelated.txt':
                Path(source).write_bytes(b'changed while backing up')
            return result
        with patch.object(backup_game.shutil, 'copy2', change_source):
            with self.assertRaisesRegex(ValueError, 'verification'):
                backup_game.backup_game(self.root, self.game)
        self.assertEqual(list((self.root / 'backups').iterdir()), [])

    def test_links_are_preserved_without_following_external_directories(self):
        external = self.base / 'external'
        external.mkdir()
        (external / 'secret.txt').write_bytes(b'must not be copied')
        self.make_link(self.game / 'linked-directory', external, directory=True)
        self.make_link(self.game / 'linked-file', 'unrelated.txt')
        self.make_link(self.game / 'dangling-link', 'does-not-exist')
        snapshot = backup_game.backup_game(self.root, self.game)
        for name in ('linked-directory', 'linked-file', 'dangling-link'):
            self.assertTrue((snapshot / name).is_symlink())
            self.assertEqual((snapshot / name).readlink(), (self.game / name).readlink())
        metadata = json.loads((snapshot.parent / 'backup.json').read_text(encoding='utf-8'))
        self.assertEqual(len(metadata['links']), 3)
        self.assertFalse(any('secret.txt' in entry['path'] for entry in metadata['files']))
        self.assertEqual((external / 'secret.txt').read_bytes(), b'must not be copied')

    def test_linked_destination_is_rejected(self):
        external = self.base / 'external'
        external.mkdir()
        output = self.root / 'linked-output'
        self.make_link(output, external, directory=True)
        with self.assertRaisesRegex(ValueError, 'links'):
            backup_game.backup_game(self.root, self.game, output)
        self.assertEqual(list(external.iterdir()), [])

    def test_running_game_blocks_backup_before_creation(self):
        with patch.object(backup_game.sys, 'platform', 'linux'), \
             patch.object(backup_game, 'ensure_game_closed', side_effect=ValueError('Close Valheim')):
            with self.assertRaisesRegex(ValueError, 'Close Valheim'):
                backup_game.backup_game(self.root, self.game)
        self.assertFalse((self.root / 'backups').exists())

    @unittest.skipUnless(sys.platform == 'win32', 'Requires Windows PowerShell')
    def test_windows_manual_backup_and_exclusions(self):
        (self.game / 'valheim.exe').write_bytes(b'game fixture')
        old = self.game / 'NordicRadio/Music-backups/old/data'
        old.parent.mkdir(parents=True)
        old.write_bytes(b'keep old archive')
        output = self.base / 'windows-backups'
        script = Path(__file__).resolve().parent / 'Backup-Windows.ps1'
        result = subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass',
                                 '-File', str(script), '-GameDirectory', str(self.game),
                                 '-SettingsDirectory', str(self.root), '-BackupDirectory', str(output)],
                                capture_output=True)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        jobs = list(output.iterdir())
        self.assertEqual(len(jobs), 1)
        snapshot = jobs[0] / 'full-game-backup'
        self.assertTrue((jobs[0] / 'BACKUP-COMPLETE.txt').is_file())
        self.assertFalse((snapshot / 'NordicRadio/Music-backups').exists())
        self.assertEqual(old.read_bytes(), b'keep old archive')
        record = json.loads((jobs[0] / 'backup.json').read_text(encoding='utf-8-sig'))
        self.assertEqual(len(record['files']), 7)
        for entry in record['files']:
            self.assertEqual(hashlib.sha256((snapshot / entry['path']).read_bytes()).hexdigest(), entry['sha256'])

    @unittest.skipUnless(sys.platform == 'win32', 'Requires Windows PowerShell')
    def test_windows_rejects_destination_inside_game(self):
        (self.game / 'valheim.exe').write_bytes(b'game fixture')
        script = Path(__file__).resolve().parent / 'Backup-Windows.ps1'
        result = subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass',
                                 '-File', str(script), '-GameDirectory', str(self.game),
                                 '-SettingsDirectory', str(self.root), '-BackupDirectory', str(self.game / 'new-backup')],
                                capture_output=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse((self.game / 'new-backup').exists())


if __name__ == '__main__':
    unittest.main()
