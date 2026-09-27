"""Offline updater tests. Fixtures remain in .cache; never touches the installed game."""
import hashlib
import io
import json
from pathlib import Path
import shutil
import stat
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / 'scripts'))
import game_path
import update_linux

SHA = 'a' * 40
PREFIX = 'valheim-mod-pack-' + SHA + '/'


class UpdateTests(unittest.TestCase):
    def setUp(self):
        self.folder = Path(tempfile.mkdtemp(prefix='update-tests-', dir=ROOT / '.cache'))
        self.launcher = self.folder / 'pack'
        self.launcher.mkdir()
        self.game = self.folder / 'Игра с пробелами'
        self.game.mkdir()
        (self.game / 'valheim.x86_64').write_bytes(b'game')
        (self.game / 'valheim.exe').write_bytes(b'game')

    def archive(self, extra=None, corrupt=False):
        stream = io.BytesIO()
        payload = b'new plugin'
        with zipfile.ZipFile(stream, 'w') as bundle:
            files = {
                'Game/BepInEx/plugin.dll': payload,
                'VERSION': b'1.4.9\n',
                'mods.lock.json': json.dumps({'packVersion':'1.4.9','packages':[]}).encode(),
                'files.sha256.json': json.dumps([{'path':'BepInEx/plugin.dll','sha256':hashlib.sha256(b'wrong' if corrupt else payload).hexdigest()}]).encode(),
                'scripts/install_linux.py': b'# mock installer\n',
                'scripts/Install-Windows.ps1': b'# mock installer\n',
            }
            for name, data in files.items():
                bundle.writestr(PREFIX + name, data)
            if extra:
                bundle.writestr(*extra)
        return stream.getvalue()

    def fake_fetch(self, archive):
        def fetch(url):
            if url.endswith('/commits/main'):
                return io.BytesIO(json.dumps({'sha':SHA}).encode())
            self.assertTrue(url.endswith('/zip/' + SHA))
            return io.BytesIO(archive)
        return fetch

    def test_save_reuse_and_replace_path(self):
        (self.launcher / 'local-settings.json').write_text(json.dumps({'windowsGameDirectory':'C:/games/Valheim'}))
        value = game_path.get_game_directory(self.launcher, 'Valheim directory (contains valheim.x86_64): "' + str(self.game) + '"')
        self.assertEqual(value, self.game)
        with patch('builtins.input', side_effect=AssertionError('Must use saved path')):
            self.assertEqual(game_path.get_game_directory(self.launcher), self.game)
        settings = json.loads((self.launcher / 'local-settings.json').read_text(encoding='utf-8'))
        self.assertEqual(settings['windowsGameDirectory'], 'C:/games/Valheim')
        with patch('builtins.input', return_value=str(self.game)) as prompt:
            game_path.get_game_directory(self.launcher, ask_again=True)
            prompt.assert_called_once()

    def test_invalid_path_does_not_replace_saved_path(self):
        game_path.get_game_directory(self.launcher, str(self.game))
        before = (self.launcher / 'local-settings.json').read_bytes()
        with self.assertRaises(ValueError):
            game_path.get_game_directory(self.launcher, str(self.launcher))
        self.assertEqual(before, (self.launcher / 'local-settings.json').read_bytes())

    def test_stale_path_prompts_again(self):
        (self.launcher / 'local-settings.json').write_text('{"linuxGameDirectory":"/not/a/game"}')
        with patch('builtins.input', return_value=str(self.game)) as prompt:
            self.assertEqual(game_path.get_game_directory(self.launcher), self.game)
            prompt.assert_called_once()

    def test_download_pins_commit_verifies_and_preserves_checkout(self):
        (self.launcher / 'README.md').write_text('local changes')
        with patch.object(update_linux, 'fetch', self.fake_fetch(self.archive())):
            pack = update_linux.download_pack(self.launcher)
        self.assertEqual((pack / 'Game/BepInEx/plugin.dll').read_bytes(), b'new plugin')
        self.assertEqual((self.launcher / 'README.md').read_text(), 'local changes')
        self.assertEqual(json.loads((self.launcher / '.updates/latest.json').read_text())['commit'], SHA)
        self.assertFalse((self.game / 'BepInEx').exists())

    def test_bad_download_preserves_last_verified_snapshot(self):
        with patch.object(update_linux, 'fetch', self.fake_fetch(self.archive())):
            update_linux.download_pack(self.launcher)
        last = (self.launcher / '.updates/latest.json').read_bytes()
        for data in [self.archive(corrupt=True), b'incomplete zip']:
            with patch.object(update_linux, 'fetch', self.fake_fetch(data)):
                with self.assertRaises((ValueError, zipfile.BadZipFile)):
                    update_linux.download_pack(self.launcher)
            self.assertEqual((self.launcher / '.updates/latest.json').read_bytes(), last)
        with patch.object(update_linux, 'fetch', side_effect=OSError('network unavailable')):
            with self.assertRaises(OSError):
                update_linux.download_pack(self.launcher)
        self.assertEqual((self.launcher / '.updates/latest.json').read_bytes(), last)

    def test_archive_traversal_duplicate_and_link_rejected(self):
        link = zipfile.ZipInfo(PREFIX + 'linked')
        link.create_system = 3
        link.external_attr = (stat.S_IFLNK | 0o777) << 16
        for entry in [(PREFIX+'../escape', b'bad'), ('/absolute', b'bad'),
                      (PREFIX+'Game/BepInEx/plugin.dll', b'duplicate'), (link, b'../../escape')]:
            with self.assertRaises(ValueError):
                update_linux.expand_archive(io.BytesIO(self.archive(extra=entry)), self.folder / 'extracted', SHA)
            self.assertFalse((self.folder / 'extracted').exists())

    @unittest.skipUnless(sys.platform == 'win32', 'Requires Windows PowerShell 5.1')
    def test_windows_path_and_archive(self):
        (self.folder / 'good.zip').write_bytes(self.archive())
        (self.folder / 'bad.zip').write_bytes(self.archive(corrupt=True))
        (self.folder / 'escape.zip').write_bytes(self.archive(extra=(PREFIX+'../escape', b'bad')))
        result = subprocess.run(['powershell.exe','-NoProfile','-ExecutionPolicy','Bypass','-File',
                                 str(ROOT / 'scripts/test_updates.ps1'), '-Repository',str(ROOT),
                                 '-Fixture',str(self.folder),'-Commit',SHA],capture_output=True)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)


if __name__ == '__main__':
    unittest.main()
