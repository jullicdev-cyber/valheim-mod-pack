"""Run with Python 3. Uses temporary mock games, never the real installation."""
import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import sys

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location('installer', ROOT / 'scripts/install_linux.py')
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)


class InstallTests(unittest.TestCase):
    def make_symlink(self, link, target):
        try:
            link.symlink_to(target)
        except OSError as error:
            if getattr(error, 'winerror', None) == 1314:
                self.skipTest('Creating Windows symlinks requires Developer Mode or elevation.')
            raise

    def setUp(self):
        # Retain fixtures for inspecting backups and failure recovery.
        self.target = Path(tempfile.mkdtemp(prefix='valheim installer test '))
        (self.target / 'valheim.x86_64').write_bytes(b'mock game')
        (self.target / 'valheim.exe').write_bytes(b'mock game')
        (self.target / 'BepInEx').mkdir()
        (self.target / 'BepInEx/old-plugin.txt').write_text('old mod')
        (self.target / 'unrelated.txt').write_text('keep')

    def test_linux_install_and_reinstall(self):
        backup = installer.install(ROOT, self.target)
        self.assertEqual((backup / 'BepInEx/old-plugin.txt').read_text(), 'old mod')
        snapshot = backup.parent / 'full-backup'
        self.assertEqual((snapshot / 'unrelated.txt').read_text(), 'keep')
        self.assertEqual((snapshot / 'valheim.x86_64').read_bytes(), b'mock game')
        self.assertTrue((backup.parent / 'BACKUP-COMPLETE.txt').exists())
        self.assertFalse((snapshot / 'ValheimModpack-backups').exists())
        self.assertFalse((self.target / 'BepInEx/old-plugin.txt').exists())
        second = installer.install(ROOT, self.target)
        self.assertTrue((second / 'BepInEx/core/BepInEx.dll').is_file())
        self.assertEqual((self.target / 'unrelated.txt').read_text(), 'keep')
        self.assertNotIn(b'\r', (self.target / 'start_game_bepinex.sh').read_bytes())

    def test_linux_rollback(self):
        rename = Path.rename
        def fail_once(path, destination):
            if path.parent.name == 'staged' and path.name == 'doorstop_libs':
                raise OSError('simulated interrupted installation')
            return rename(path, destination)
        with patch.object(Path, 'rename', fail_once):
            with self.assertRaises(OSError):
                installer.install(ROOT, self.target)
        self.assertEqual((self.target / 'BepInEx/old-plugin.txt').read_text(), 'old mod')
        self.assertFalse((self.target / 'BepInEx/core').exists())

    def test_invalid_directory(self):
        invalid = self.target / 'wrong directory'
        invalid.mkdir()
        with self.assertRaises(ValueError):
            installer.install(ROOT, invalid)
        self.assertEqual(list(invalid.iterdir()), [])

    @unittest.skipUnless(sys.platform == 'win32', 'Windows PowerShell test')
    def test_windows_pasted_prompt(self):
        pasted = 'Valheim directory (contains valheim.exe): ' * 2 + '"' + str(self.target) + '"'
        result = subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass',
                                 '-File', str(ROOT / 'scripts/Install-Windows.ps1'),
                                 '-GameDirectory', pasted], capture_output=True)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue((self.target / 'BepInEx/core/BepInEx.dll').exists())

    @unittest.skipUnless(sys.platform == 'win32', 'Windows PowerShell test')
    def test_windows_vortex_file_links(self):
        outside = Path(tempfile.mkdtemp(prefix='vortex staging '))
        payload = outside / 'mod.dll'
        payload.write_bytes(b'previous mod contents')
        self.make_symlink(self.target / 'BepInEx/linked.dll', payload)
        self.make_symlink(self.target / 'BepInEx/relative.dll', 'old-plugin.txt')
        self.make_symlink(self.target / 'winhttp.dll', payload)
        result = subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass',
                                 '-File', str(ROOT / 'scripts/Install-Windows.ps1'),
                                 '-GameDirectory', str(self.target)], capture_output=True)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        backup = next((self.target / 'ValheimModpack-backups').iterdir())
        for name in ['BepInEx/linked.dll', 'winhttp.dll']:
            copy = backup / 'full-backup' / name
            self.assertFalse(copy.is_symlink())
            self.assertEqual(copy.read_bytes(), b'previous mod contents')
            self.assertTrue((backup / 'original' / name).is_symlink())
        self.assertEqual((backup / 'full-backup/BepInEx/relative.dll').read_text(), 'old mod')
        self.assertEqual(payload.read_bytes(), b'previous mod contents')
        self.assertFalse((self.target / 'winhttp.dll').is_symlink())

    @unittest.skipUnless(sys.platform == 'win32', 'Windows PowerShell test')
    def test_windows_dangling_link_preserves_game(self):
        self.make_symlink(self.target / 'BepInEx/missing.dll', self.target / 'not-present.dll')
        result = subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass',
                                 '-File', str(ROOT / 'scripts/Install-Windows.ps1'),
                                 '-GameDirectory', str(self.target)], capture_output=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual((self.target / 'BepInEx/old-plugin.txt').read_text(), 'old mod')
        self.assertFalse((self.target / 'ValheimModpack-backups').exists())

    @unittest.skipUnless(sys.platform == 'win32', 'Windows PowerShell test')
    def test_windows_rollback_on_locked_loader(self):
        import ctypes
        from ctypes import wintypes
        loader = self.target / 'winhttp.dll'
        loader.write_bytes(b'old loader')
        create = ctypes.windll.kernel32.CreateFileW
        create.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, wintypes.LPVOID,
                           wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
        create.restype = wintypes.HANDLE
        handle = create(str(loader), 0x80000000, 1, None, 3, 0, None)
        self.assertNotEqual(handle, wintypes.HANDLE(-1).value)
        try:
            result = subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass',
                                     '-File', str(ROOT / 'scripts/Install-Windows.ps1'),
                                     '-GameDirectory', str(self.target)], capture_output=True)
            self.assertNotEqual(result.returncode, 0)
        finally:
            close = ctypes.windll.kernel32.CloseHandle
            close.argtypes = [wintypes.HANDLE]
            close(handle)
        self.assertEqual((self.target / 'BepInEx/old-plugin.txt').read_text(), 'old mod')
        self.assertEqual(loader.read_bytes(), b'old loader')
        self.assertFalse((self.target / 'BepInEx/core').exists())

    def test_incomplete_backup_does_not_change_game(self):
        copy = installer.shutil.copy2
        def fail_backup(src, dst, **kwargs):
            if Path(src).name == 'unrelated.txt':
                raise OSError('simulated full disk during backup')
            return copy(src, dst, **kwargs)
        with patch.object(installer.shutil, 'copy2', fail_backup):
            with self.assertRaises(OSError):
                installer.install(ROOT, self.target)
        self.assertEqual((self.target / 'BepInEx/old-plugin.txt').read_text(), 'old mod')
        self.assertEqual(list((self.target / 'ValheimModpack-backups').glob('*/BACKUP-COMPLETE.txt')), [])

    @unittest.skipUnless(sys.platform == 'win32', 'Windows PowerShell test')
    def test_windows_install_and_reinstall(self):
        command = ['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                   str(ROOT / 'scripts/Install-Windows.ps1'), '-GameDirectory', str(self.target)]
        for _ in range(2):
            result = subprocess.run(command, capture_output=True)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue((self.target / 'BepInEx/core/BepInEx.dll').exists())
        self.assertFalse((self.target / 'BepInEx/old-plugin.txt').exists())
        originals = list((self.target / 'ValheimModpack-backups').glob('*/original/BepInEx/old-plugin.txt'))
        self.assertEqual(len(originals), 1)
        snapshots = list((self.target / 'ValheimModpack-backups').glob('*/full-backup'))
        self.assertEqual(len(snapshots), 2)
        for snapshot in snapshots:
            self.assertEqual((snapshot / 'unrelated.txt').read_text(), 'keep')
            self.assertEqual((snapshot / 'valheim.exe').read_bytes(), b'mock game')
            self.assertFalse((snapshot / 'ValheimModpack-backups').exists())
        self.assertEqual((self.target / 'unrelated.txt').read_text(), 'keep')
        invalid = self.target / 'invalid'
        invalid.mkdir()
        command[-1] = str(invalid)
        self.assertNotEqual(subprocess.run(command, capture_output=True).returncode, 0)
        self.assertEqual(list(invalid.iterdir()), [])


if __name__ == '__main__':
    unittest.main()
