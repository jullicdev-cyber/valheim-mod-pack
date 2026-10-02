"""Music ZIP download/install tests. Temporary games only; no real saves touched."""
from functools import partial
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import shutil
import stat
import subprocess
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch
import zipfile

import update_music as music

ROOT = Path(__file__).resolve().parent.parent


class MusicTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix='music tests ')
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.game = self.root / 'game'
        self.folder = self.game / 'NordicRadio/Music'
        self.folder.mkdir(parents=True)
        (self.folder / 'личная.mp3').write_bytes(b'keep personal')
        (self.folder / 'replace.mp3').write_bytes(b'old track')
        self.archive = self.root / 'fixture.zip'

    def make_zip(self, entries):
        with zipfile.ZipFile(self.archive, 'w') as bundle:
            for name, content in entries:
                bundle.writestr(name, content)

    def install(self):
        return music.install_music(self.game, 'http://example.test/music.zip', lambda: None,
                                   lambda url, out: shutil.copyfile(self.archive, out))

    def clean_transaction(self):
        self.assertFalse(list((self.game / 'NordicRadio').glob('.music-update-*')))
        self.assertFalse((self.game / 'NordicRadio/.music-update.lock').exists())

    def test_nested_zip_merge_and_cleanup(self):
        self.make_zip([('music/новая.mp3', b'new track'), ('replace.mp3', b'replaced'), ('readme.txt', b'ignored')])
        self.assertEqual(self.install(), 2)
        self.assertEqual((self.folder / 'новая.mp3').read_bytes(), b'new track')
        self.assertEqual((self.folder / 'личная.mp3').read_bytes(), b'keep personal')
        self.assertEqual((self.folder / 'replace.mp3').read_bytes(), b'replaced')
        self.assertFalse((self.game / 'NordicRadio/Music-backups').exists())
        self.assertFalse((self.folder / 'readme.txt').exists())
        self.clean_transaction()
        record = self.game / 'NordicRadio/last-music-install.txt'
        self.assertIn('ZIP SHA256: ', record.read_text())
        self.make_zip([('second.mp3', b'second track')])
        self.install()
        self.assertIn('Tracks: 1', record.read_text())
        self.clean_transaction()

    def test_existing_user_backups_are_untouched(self):
        old = self.game / 'NordicRadio/Music-backups/saved/original.mp3'
        old.parent.mkdir(parents=True)
        old.write_bytes(b'user backup')
        self.make_zip([('new.mp3', b'new track')])
        self.install()
        self.assertEqual(old.read_bytes(), b'user backup')
        self.clean_transaction()

    def test_read_only_personal_track_does_not_leak_transaction(self):
        personal = self.folder / 'личная.mp3'
        personal.chmod(stat.S_IREAD)
        self.make_zip([('new.mp3', b'new track')])
        self.install()
        self.assertEqual(personal.read_bytes(), b'keep personal')
        self.clean_transaction()

    def test_invalid_archives_preserve_library(self):
        for entries in [[('../evil.mp3', b'evil')], [('x/a.mp3', b'aaaa'), ('y/A.mp3', b'bbbb')], [('README.txt', b'empty')], [('CON.mp3', b'aaaa')], [('/absolute.mp3', b'aaaa')]]:
            with self.subTest(entries=entries):
                self.make_zip(entries)
                with self.assertRaises(ValueError):
                    self.install()
                self.assertEqual((self.folder / 'replace.mp3').read_bytes(), b'old track')
                self.assertEqual(len(list(self.folder.iterdir())), 2)
                self.clean_transaction()

    def test_failed_download_preserves_library(self):
        def fail(url, destination):
            raise OSError('interrupted transfer')
        with self.assertRaises(OSError):
            music.install_music(self.game, 'https://example.test/a.zip', lambda: None, fail)
        self.assertEqual((self.folder / 'replace.mp3').read_bytes(), b'old track')
        self.clean_transaction()

    def test_concurrent_update_rejected(self):
        (self.game / 'NordicRadio/.music-update.lock').mkdir()
        self.make_zip([('new.mp3', b'new track')])
        with self.assertRaises(ValueError):
            self.install()
        self.assertFalse((self.folder / 'new.mp3').exists())
        self.assertTrue((self.game / 'NordicRadio/.music-update.lock').exists())

    def test_game_started_during_download(self):
        self.make_zip([('new.mp3', b'new track')])
        calls = []
        def check():
            calls.append(1)
            if len(calls) == 2:
                raise ValueError('game started')
        with self.assertRaises(ValueError):
            music.install_music(self.game, 'https://example.test/a.zip', check, lambda url, out: shutil.copyfile(self.archive, out))
        self.assertFalse((self.folder / 'new.mp3').exists())
        self.clean_transaction()

    def test_rename_failure_restores_original(self):
        self.make_zip([('new.mp3', b'new track')])
        rename = Path.rename
        def failing(path, destination):
            if path.name == 'staged':
                raise OSError('locked directory')
            return rename(path, destination)
        with patch.object(Path, 'rename', failing), self.assertRaises(OSError):
            self.install()
        self.assertEqual((self.folder / 'replace.mp3').read_bytes(), b'old track')
        self.clean_transaction()

    def test_record_failure_restores_music_and_previous_record(self):
        record = self.game / 'NordicRadio/last-music-install.txt'
        record.write_text('previous install')
        self.make_zip([('new.mp3', b'new track')])
        rename = Path.rename
        def failing(path, target):
            if path.name == 'install.txt':
                raise OSError('record locked')
            return rename(path, target)
        with patch.object(Path, 'rename', failing), self.assertRaises(OSError):
            self.install()
        self.assertEqual(record.read_text(), 'previous install')
        self.assertEqual((self.folder / 'replace.mp3').read_bytes(), b'old track')
        self.assertFalse((self.folder / 'new.mp3').exists())
        self.clean_transaction()

    def test_interrupted_replacement_rolls_back(self):
        self.make_zip([('new.mp3', b'new track')])
        rename = Path.rename
        def interrupted(path, target):
            if path.name == 'staged':
                raise KeyboardInterrupt()
            return rename(path, target)
        with patch.object(Path, 'rename', interrupted), self.assertRaises(KeyboardInterrupt):
            self.install()
        self.assertEqual((self.folder / 'replace.mp3').read_bytes(), b'old track')
        self.clean_transaction()

    def test_failed_rollback_retains_original_and_recovery(self):
        self.make_zip([('new.mp3', b'new track')])
        rename = Path.rename
        def failing(path, target):
            if path.name in ('staged', 'original'):
                raise OSError('locked replacement and rollback')
            return rename(path, target)
        with patch.object(Path, 'rename', failing), self.assertRaisesRegex(OSError, 'recovery files retained'):
            self.install()
        job, = (self.game / 'NordicRadio').glob('.music-update-*')
        self.assertEqual((job / 'original/replace.mp3').read_bytes(), b'old track')
        self.assertTrue((job / 'RECOVERY.txt').is_file())
        self.assertFalse((self.game / 'NordicRadio/.music-update.lock').exists())

    def test_interrupt_after_original_move_restores_music(self):
        self.make_zip([('new.mp3', b'new track')])
        rename = Path.rename
        def interrupted(path, target):
            result = rename(path, target)
            if path == self.folder:
                raise KeyboardInterrupt()
            return result
        with patch.object(Path, 'rename', interrupted), self.assertRaises(KeyboardInterrupt):
            self.install()
        self.assertEqual((self.folder / 'replace.mp3').read_bytes(), b'old track')
        self.clean_transaction()

    def test_interrupt_during_rollback_keeps_original_music(self):
        self.make_zip([('new.mp3', b'new track')])
        rename = Path.rename
        def interrupted(path, target):
            if path.name in ('staged', 'original'):
                raise KeyboardInterrupt()
            return rename(path, target)
        with patch.object(Path, 'rename', interrupted), self.assertRaisesRegex(OSError, 'recovery files retained'):
            self.install()
        job, = (self.game / 'NordicRadio').glob('.music-update-*')
        self.assertEqual((job / 'original/replace.mp3').read_bytes(), b'old track')
        self.assertTrue((job / 'RECOVERY.txt').is_file())

    def test_linked_music_rejected(self):
        try:
            (self.folder / 'linked.mp3').symlink_to(self.folder / 'replace.mp3')
        except OSError:
            self.skipTest('Symlink creation unavailable')
        self.make_zip([('new.mp3', b'new track')])
        with self.assertRaises(ValueError):
            self.install()

    def test_drive_url_and_confirmation(self):
        url = music.download_url((ROOT / 'music-source.txt').read_text().strip())
        self.assertIn('drive.usercontent.google.com/download?', url)
        self.assertIn('11n7No1iYRDXkNbcf3gI7faicaOeSFeF1', url)
        form = music.ConfirmForm()
        form.feed('<form id="download-form" action="https://drive.usercontent.google.com/download"><input name="uuid" value="a&amp;b"><input name="confirm" value="t"></form>')
        self.assertEqual(form.fields, {'uuid': 'a&b', 'confirm': 't'})

    def test_linux_install_cli_also_installs_music(self):
        import install_linux
        (self.game / 'valheim.x86_64').write_bytes(b'mock game')
        self.make_zip([('album/new.mp3', b'new track')])
        with patch.object(sys, 'platform', 'linux'), patch.object(sys, 'argv', ['install_linux.py', str(self.game), '--music-url', 'https://example.test/music.zip']), patch.object(install_linux, 'ensure_game_closed'), patch.object(music, 'download', lambda url, out: shutil.copyfile(self.archive, out)), patch('builtins.print'):
            install_linux.main()
        self.assertTrue((self.game / 'BepInEx/core/BepInEx.dll').is_file())
        self.assertEqual((self.folder / 'new.mp3').read_bytes(), b'new track')
        self.assertEqual((self.folder / 'личная.mp3').read_bytes(), b'keep personal')

    def test_http_download_and_html_error(self):
        self.make_zip([('new.mp3', b'new track')])
        (self.root / 'error.html').write_text('<html>Access denied</html>')
        class Quiet(SimpleHTTPRequestHandler):
            def log_message(self, *args):
                pass
        server = ThreadingHTTPServer(('127.0.0.1', 0), partial(Quiet, directory=str(self.root)))
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            base = 'http://127.0.0.1:' + str(server.server_port)
            out = self.root / 'download.zip'
            music.download(base + '/fixture.zip', out)
            self.assertEqual(out.read_bytes(), self.archive.read_bytes())
            with self.assertRaises(ValueError):
                music.download(base + '/error.html', out)
            if sys.platform == 'win32':
                result = subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', str(ROOT / 'scripts/test_music.ps1'), str(self.game), str(self.archive), base], capture_output=True)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        finally:
            server.shutdown()
            server.server_close()
            thread.join()


if __name__ == '__main__':
    unittest.main()
