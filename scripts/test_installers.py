"""Run with Python 3. Uses temporary mock games, never the real installation."""
import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import sys
import configparser
import os

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / 'scripts'))
spec = importlib.util.spec_from_file_location('installer', ROOT / 'scripts/install_linux.py')
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)

RADIO_DEFAULTS = '''## Current pack audio settings
[Audio]
## The preferred volume is local to this client.
PersonalVolume = 0.8
PersonalMuted = false
NearDistance = 15
Amplification = 1
BackgroundMusicVolume = 0.2
[Network]
UploadKiBPerSecond = 1024
[Controls]
OpenPersonalAudio = F8 + LeftControl
'''

PORTAL_CONFIG = ('## Настройки прежнего XPortal\r\n[General]\r\nNexusID = 2239\r\n'
                 'DefaultPortal = {"x":123.5,"y":2.0,"z":-321.25}\r\n'
                 'DisplayPortalColour = true\r\nHidePortalDistance = true\r\n'
                 'DoublePortalCosts = true\r\n[Controls]\r\nNextPortal = PageDown\r\n').encode('utf-8-sig')

PERSONAL_SAVED_DATA = {
    'Recycle_N_Reclaim_player_123.dat': b'\x00\xfftrash-slot fixture',
    'Recycle_N_Reclaim_player_-456.dat': b'\x00\xfetrash-slot second fixture',
    'EpicLoot/BountySaves/randyknapp.mods.epicloot.BountyLedger.123.dat': b'\x00\xffbounty fixture',
    'EpicLoot/BountySaves/randyknapp.mods.epicloot.BountyLedger.-456.dat': b'\x00\xfebounty second fixture',
}


def radio_values(path):
    config = configparser.RawConfigParser()
    config.read_string(path.read_text(encoding='utf-8-sig'))
    return config


class PersonalAudioMergeTests(unittest.TestCase):
    """Small stage-only cases exercise the same merge code as both installers."""
    def run_cases(self, windows=False):
        with tempfile.TemporaryDirectory(prefix='vh-audio-merge-') as directory:
            fixture = Path(directory)
            cases = [
                ('preserve', '[Audio]\nPersonalVolume = 0.37\nPersonalMuted = TRUE\nNearDistance = 999\nAmplification = 6\n[Network]\nUploadKiBPerSecond = 64\n', .37, True),
                ('zero', '[Audio]\nPersonalVolume = 0\nPersonalMuted = false\n', 0, False),
                ('unity', '[Audio]\nPersonalVolume = 1\nPersonalMuted = true\n', 1, True),
                ('exponent', '[Audio]\nPersonalVolume = 2.5e-1\nPersonalMuted = false\n', .25, False),
                ('old-version', '[Audio]\nPersonalVolume = 0.42\n', .42, False),
                ('wrong-section', '[Network]\nPersonalVolume = 0.31\nPersonalMuted = true\n', .8, False),
                ('duplicate', '[Audio]\nPersonalVolume = 0.2\nPersonalVolume = 0.4\nPersonalMuted = true\nPersonalMuted = false\n', .8, False),
                ('invalid-mute', '[Audio]\nPersonalVolume = .5\nPersonalMuted = yes\n', .5, False),
                ('commented', '[Audio]\n# PersonalVolume = .25\n; PersonalMuted = true\n', .8, False),
            ]
            for value in ('nan', 'inf', '-inf', '1.01', '-0.1', '0,5', '0_0.5', 'abc', '1e999', '٠.٥'):
                cases.append(('invalid-' + str(len(cases)), '[Audio]\nPersonalVolume = ' + value + '\nPersonalMuted = true\n', .8, True))
            folders = []
            for name, previous, _, _ in cases:
                folder = fixture / name
                folder.mkdir()
                (folder / 'old.cfg').write_text(previous, encoding='utf-8-sig')
                (folder / 'new.cfg').write_text(RADIO_DEFAULTS, encoding='utf-8')
                folders.append(folder)
            # A new config section/key is inserted inside Audio rather than Network.
            extra = fixture / 'missing-new-key'
            extra.mkdir()
            (extra / 'old.cfg').write_text('[Audio]\nPersonalVolume=.65\nPersonalMuted=true\n', encoding='utf-8')
            (extra / 'new.cfg').write_text(RADIO_DEFAULTS.replace('PersonalMuted = false\n', ''), encoding='utf-8')
            folders.append(extra)
            if windows:
                script = '''
$ErrorActionPreference = 'Stop'
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($env:RADIO_INSTALLER, [ref]$null, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Installer syntax errors' }
$definition = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Merge-RadioPersonalAudio' }, $true)
if (-not $definition) { throw 'Personal audio merge function missing' }
. ([scriptblock]::Create($definition.Extent.Text))
foreach ($case in Get-ChildItem -LiteralPath $env:RADIO_FIXTURE -Directory) {
    Merge-RadioPersonalAudio (Join-Path $case.FullName 'old.cfg') (Join-Path $case.FullName 'new.cfg')
}
'''
                env = dict(os.environ, RADIO_INSTALLER=str(ROOT / 'scripts/Install-Windows.ps1'), RADIO_FIXTURE=str(fixture))
                result = subprocess.run(['powershell.exe', '-NoProfile', '-Command', script], env=env, capture_output=True)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            else:
                for folder in folders:
                    installer.merge_radio_personal_audio(folder / 'old.cfg', folder / 'new.cfg')
            for name, _, volume, muted in cases:
                with self.subTest(name=name, windows=windows):
                    path = fixture / name / 'new.cfg'
                    config = radio_values(path)
                    self.assertAlmostEqual(config.getfloat('Audio', 'PersonalVolume'), volume)
                    self.assertEqual(config.getboolean('Audio', 'PersonalMuted'), muted)
                    self.assertEqual(config.getfloat('Audio', 'NearDistance'), 15)
                    self.assertEqual(config.getfloat('Audio', 'Amplification'), 1)
                    self.assertEqual(config.getint('Network', 'UploadKiBPerSecond'), 1024)
                    self.assertEqual(config.get('Controls', 'OpenPersonalAudio'), 'F8 + LeftControl')
                    self.assertIn('## The preferred volume is local to this client.', path.read_text())
            config = radio_values(extra / 'new.cfg')
            self.assertAlmostEqual(config.getfloat('Audio', 'PersonalVolume'), .65)
            self.assertTrue(config.getboolean('Audio', 'PersonalMuted'))
            self.assertFalse(config.has_option('Network', 'PersonalMuted'))

    def test_linux_merge_personal_audio_and_validation(self):
        self.run_cases()

    @unittest.skipUnless(sys.platform == 'win32', 'Windows PowerShell test')
    def test_windows_merge_personal_audio_and_validation(self):
        self.run_cases(windows=True)


class InstallTests(unittest.TestCase):
    def make_symlink(self, link, target):
        try:
            link.symlink_to(target)
        except OSError as error:
            if getattr(error, 'winerror', None) == 1314:
                self.skipTest('Creating Windows symlinks requires Developer Mode or elevation.')
            raise

    def setUp(self):
        fixture = tempfile.TemporaryDirectory(prefix='vh-install-')
        self.addCleanup(fixture.cleanup)
        self.target = Path(fixture.name).resolve() / 'game'
        self.settings = Path(fixture.name).resolve() / 'settings'
        self.target.mkdir()
        self.settings.mkdir()
        (self.target / 'valheim.x86_64').write_bytes(b'mock game')
        (self.target / 'valheim.exe').write_bytes(b'mock game')
        (self.target / 'BepInEx').mkdir()
        (self.target / 'BepInEx/old-plugin.txt').write_text('old mod')
        (self.target / 'unrelated.txt').write_text('keep')
        (self.target / 'NordicRadio/Music').mkdir(parents=True)
        (self.target / 'NordicRadio/Music/скальд.mp3').write_bytes(b'personal music fixture')
        (self.target / 'NordicRadio/Cache').mkdir()
        (self.target / 'NordicRadio/Cache/fixture.mp3').write_bytes(b'cached music fixture')
        (self.target / 'BepInEx/config').mkdir()
        (self.target / 'BepInEx/config/QuickStackStore_player_123.dat').write_bytes(b'personal favorite slots fixture')
        (self.target / 'BepInEx/config/QuickStackStore_player_-456.dat').write_bytes(b'personal favorite item types fixture')
        (self.target / 'BepInEx/config/AzuAutoStore_player_123.dat').write_bytes(b'legacy Azu favorites fixture')
        (self.target / 'BepInEx/config/AzuExtendedPlayerInventory_player_-456.dat').write_bytes(b'legacy Azu EPI favorites fixture')
        (self.target / 'BepInEx/config/unknown-old-mod.cfg').write_bytes(b'old config must not survive')
        (self.target / 'BepInEx/config/yay.spikehimself.xportal.cfg').write_bytes(PORTAL_CONFIG)
        for relative, content in PERSONAL_SAVED_DATA.items():
            path = self.target / 'BepInEx/config' / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(content)
        for relative in ('Recycle_N_Reclaim_player_invalid.dat',
                         'EpicLoot/BountySaves/randyknapp.mods.epicloot.BountyLedger.invalid.dat',
                         'EpicLoot/BountySaves/unrelated.dat',
                         'EpicLoot/personal-old-config.json'):
            (self.target / 'BepInEx/config' / relative).write_bytes(b'unmatched old data')
        (self.target / 'ValheimModpack/ExpeditionLoadouts').mkdir(parents=True)
        (self.target / 'ValheimModpack/ExpeditionLoadouts/character.json').write_bytes(b'personal loadout fixture')
        (self.target / 'ValheimModpack/MapPinHistory').mkdir()
        (self.target / 'ValheimModpack/MapPinHistory/world-character.bin').write_bytes(b'personal map history fixture')
        (self.target / 'ValheimModpack/WorldCharacters/characters').mkdir(parents=True)
        (self.target / 'ValheimModpack/WorldCharacters/characters/world-account-character.wchar').write_bytes(b'authoritative character fixture')

        for relative in ('BepInEx/bindrune.keys', 'BepInEx/bindrune.spare', 'BepInEx/config/Bindrune/situations.txt', 'BepInEx/config/isimp.Bindrune.cfg'):
            path = self.target / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b'personal Bindrune fixture')

    def assert_personal_data(self, location):
        for relative in ('BepInEx/bindrune.keys', 'BepInEx/bindrune.spare', 'BepInEx/config/Bindrune/situations.txt', 'BepInEx/config/isimp.Bindrune.cfg'):
            self.assertEqual((location / relative).read_bytes(), b'personal Bindrune fixture')
        for name, content in [('123', b'personal favorite slots fixture'), ('-456', b'personal favorite item types fixture')]:
            self.assertEqual((location / ('BepInEx/config/QuickStackStore_player_' + name + '.dat')).read_bytes(), content)
        self.assertEqual((location / 'ValheimModpack/ExpeditionLoadouts/character.json').read_bytes(), b'personal loadout fixture')
        self.assertEqual((location / 'ValheimModpack/MapPinHistory/world-character.bin').read_bytes(), b'personal map history fixture')
        self.assertEqual((location / 'ValheimModpack/WorldCharacters/characters/world-account-character.wchar').read_bytes(), b'authoritative character fixture')
        self.assertEqual((location / 'BepInEx/config/AzuAutoStore_player_123.dat').read_bytes(), b'legacy Azu favorites fixture')
        self.assertEqual((location / 'BepInEx/config/AzuExtendedPlayerInventory_player_-456.dat').read_bytes(), b'legacy Azu EPI favorites fixture')
        self.assertEqual((location / 'BepInEx/config/yay.spikehimself.xportal.cfg').read_bytes(), PORTAL_CONFIG)
        for relative, content in PERSONAL_SAVED_DATA.items():
            self.assertEqual((location / 'BepInEx/config' / relative).read_bytes(), content)
        for relative in ('Recycle_N_Reclaim_player_invalid.dat',
                         'EpicLoot/BountySaves/randyknapp.mods.epicloot.BountyLedger.invalid.dat',
                         'EpicLoot/BountySaves/unrelated.dat',
                         'EpicLoot/personal-old-config.json'):
            self.assertFalse((location / 'BepInEx/config' / relative).exists())

    def test_linux_install_and_reinstall(self):
        installer.install(ROOT, self.target)
        self.assertFalse((self.target / 'BepInEx/old-plugin.txt').exists())
        installer.install(ROOT, self.target)
        self.assertTrue((self.target / 'BepInEx/core/BepInEx.dll').is_file())
        self.assert_no_automatic_backups()
        self.assertEqual((self.target / 'unrelated.txt').read_text(), 'keep')
        self.assertNotIn(b'\r', (self.target / 'start_game_bepinex.sh').read_bytes())
        self.assert_personal_data(self.target)
        self.assertFalse((self.target / 'BepInEx/config/unknown-old-mod.cfg').exists())
        self.assertEqual((self.target / 'NordicRadio/Music/скальд.mp3').read_bytes(), b'personal music fixture')
        self.assertEqual((self.target / 'NordicRadio/Cache/fixture.mp3').read_bytes(), b'cached music fixture')

    def assert_no_automatic_backups(self):
        self.assertFalse((self.target / 'ValheimModpack-backups').exists())
        self.assertEqual(list(self.target.glob('.valheim-modpack-install-*')), [])
        self.assertEqual(list(self.target.glob('.valheim-modpack-txn-*')), [])

    def reject_invalid_saved_data(self, windows=False, linked=False):
        # Exercise both exact file patterns, before any game entry is replaced.
        for relative in ('Recycle_N_Reclaim_player_123.dat',
                         'EpicLoot/BountySaves/randyknapp.mods.epicloot.BountyLedger.123.dat'):
            with self.subTest(relative=relative, windows=windows, linked=linked):
                personal = self.target / 'BepInEx/config' / relative
                content = personal.read_bytes()
                personal.unlink()
                external = self.target.parent / 'external-personal.dat'
                external.write_bytes(content)
                if linked:
                    self.make_symlink(personal, external)
                else:
                    personal.mkdir()
                if windows:
                    result = subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                                             str(ROOT / 'scripts/Install-Windows.ps1'), '-SkipMusic',
                                             '-SettingsDirectory', str(self.settings), '-GameDirectory', str(self.target)], capture_output=True)
                    self.assertNotEqual(result.returncode, 0)
                else:
                    with self.assertRaisesRegex(ValueError, 'unlinked regular file'):
                        installer.install(ROOT, self.target)
                self.assertEqual((self.target / 'BepInEx/old-plugin.txt').read_text(), 'old mod')
                self.assertEqual(external.read_bytes(), content)
                self.assert_no_automatic_backups()
                if linked:
                    personal.unlink()
                else:
                    personal.rmdir()
                personal.write_bytes(content)

    def test_linux_saved_data_directories_rejected_without_changes(self):
        self.reject_invalid_saved_data()

    def test_linux_saved_data_links_rejected_without_changes(self):
        self.reject_invalid_saved_data(linked=True)

    @unittest.skipUnless(sys.platform == 'win32', 'Windows PowerShell test')
    def test_windows_saved_data_directories_rejected_without_changes(self):
        self.reject_invalid_saved_data(windows=True)

    @unittest.skipUnless(sys.platform == 'win32', 'Windows PowerShell test')
    def test_windows_saved_data_links_rejected_without_changes(self):
        self.reject_invalid_saved_data(windows=True, linked=True)

    def set_radio_preferences(self):
        (self.target / 'BepInEx/config/valheimmodpack.nordicradio.cfg').write_text(
            '[Audio]\nPersonalVolume = .37\nPersonalMuted = true\nNearDistance = 999\nAmplification = 6\n[Network]\nUploadKiBPerSecond = 64\n', encoding='utf-8')

    def assert_radio_preferences(self):
        config = radio_values(self.target / 'BepInEx/config/valheimmodpack.nordicradio.cfg')
        source = radio_values(ROOT / 'Game/BepInEx/config/valheimmodpack.nordicradio.cfg')
        self.assertAlmostEqual(config.getfloat('Audio', 'PersonalVolume'), .37)
        self.assertTrue(config.getboolean('Audio', 'PersonalMuted'))
        for section, key in [('Audio', 'NearDistance'), ('Audio', 'Amplification'), ('Network', 'UploadKiBPerSecond')]:
            self.assertEqual(config.get(section, key), source.get(section, key))

    def test_linux_personal_audio_survives_reinstall(self):
        self.set_radio_preferences()
        for _ in range(2):
            installer.install(ROOT, self.target)
            self.assert_radio_preferences()
        self.assert_no_automatic_backups()

    def test_linux_portal_config_survives_install_and_reinstall_byte_for_byte(self):
        for _ in range(2):
            installer.install(ROOT, self.target)
            self.assertEqual((self.target / 'BepInEx/config/yay.spikehimself.xportal.cfg').read_bytes(), PORTAL_CONFIG)
        self.assert_no_automatic_backups()

    @unittest.skipUnless(sys.platform == 'win32', 'Windows PowerShell test')
    def test_windows_portal_config_survives_install_and_reinstall_byte_for_byte(self):
        command = ['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                   str(ROOT / 'scripts/Install-Windows.ps1'), '-SkipMusic', '-SettingsDirectory', str(self.settings), '-GameDirectory', str(self.target)]
        for _ in range(2):
            result = subprocess.run(command, capture_output=True)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertEqual((self.target / 'BepInEx/config/yay.spikehimself.xportal.cfg').read_bytes(), PORTAL_CONFIG)
        self.assert_no_automatic_backups()

    def test_linux_linked_portal_config_rejected_without_changes(self):
        external = self.target.parent / 'external-portal.cfg'
        external.write_bytes(PORTAL_CONFIG)
        portal = self.target / 'BepInEx/config/yay.spikehimself.xportal.cfg'
        portal.unlink()
        self.make_symlink(portal, external)
        with self.assertRaisesRegex(ValueError, 'unlinked regular file'):
            installer.install(ROOT, self.target)
        self.assertEqual((self.target / 'BepInEx/old-plugin.txt').read_text(), 'old mod')
        self.assertEqual(external.read_bytes(), PORTAL_CONFIG)
        self.assert_no_automatic_backups()

    @unittest.skipUnless(sys.platform == 'win32', 'Windows PowerShell test')
    def test_windows_linked_portal_config_rejected_without_changes(self):
        external = self.target.parent / 'external-portal.cfg'
        external.write_bytes(PORTAL_CONFIG)
        portal = self.target / 'BepInEx/config/yay.spikehimself.xportal.cfg'
        portal.unlink()
        self.make_symlink(portal, external)
        result = subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                                 str(ROOT / 'scripts/Install-Windows.ps1'), '-SkipMusic', '-SettingsDirectory', str(self.settings), '-GameDirectory', str(self.target)], capture_output=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual((self.target / 'BepInEx/old-plugin.txt').read_text(), 'old mod')
        self.assertEqual(external.read_bytes(), PORTAL_CONFIG)
        self.assert_no_automatic_backups()

    def test_linux_directory_portal_config_rejected_without_changes(self):
        portal = self.target / 'BepInEx/config/yay.spikehimself.xportal.cfg'
        portal.unlink()
        portal.mkdir()
        with self.assertRaisesRegex(ValueError, 'unlinked regular file'):
            installer.install(ROOT, self.target)
        self.assertEqual((self.target / 'BepInEx/old-plugin.txt').read_text(), 'old mod')
        self.assert_no_automatic_backups()

    @unittest.skipUnless(sys.platform == 'win32', 'Windows PowerShell test')
    def test_windows_directory_portal_config_rejected_without_changes(self):
        portal = self.target / 'BepInEx/config/yay.spikehimself.xportal.cfg'
        portal.unlink()
        portal.mkdir()
        result = subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                                 str(ROOT / 'scripts/Install-Windows.ps1'), '-SkipMusic', '-SettingsDirectory', str(self.settings), '-GameDirectory', str(self.target)], capture_output=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual((self.target / 'BepInEx/old-plugin.txt').read_text(), 'old mod')
        self.assert_no_automatic_backups()

    @unittest.skipUnless(sys.platform == 'win32', 'Windows PowerShell test')
    def test_windows_personal_audio_survives_reinstall(self):
        self.set_radio_preferences()
        command = ['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                   str(ROOT / 'scripts/Install-Windows.ps1'), '-SkipMusic', '-SettingsDirectory', str(self.settings), '-GameDirectory', str(self.target)]
        for _ in range(2):
            result = subprocess.run(command, capture_output=True)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assert_radio_preferences()
        self.assert_no_automatic_backups()

    def test_linux_linked_radio_config_rejected_without_changes(self):
        external = self.target.parent / 'external-radio.cfg'
        external.write_text('[Audio]\nPersonalVolume=.2\n', encoding='utf-8')
        self.make_symlink(self.target / 'BepInEx/config/valheimmodpack.nordicradio.cfg', external)
        with self.assertRaisesRegex(ValueError, 'unlinked regular file'):
            installer.install(ROOT, self.target)
        self.assertEqual((self.target / 'BepInEx/old-plugin.txt').read_text(), 'old mod')
        self.assertEqual(external.read_text(), '[Audio]\nPersonalVolume=.2\n')
        self.assert_no_automatic_backups()

    @unittest.skipUnless(sys.platform == 'win32', 'Windows PowerShell test')
    def test_windows_linked_radio_config_rejected_without_changes(self):
        external = self.target.parent / 'external-radio.cfg'
        external.write_text('[Audio]\nPersonalVolume=.2\n', encoding='utf-8')
        self.make_symlink(self.target / 'BepInEx/config/valheimmodpack.nordicradio.cfg', external)
        result = subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                                 str(ROOT / 'scripts/Install-Windows.ps1'), '-SkipMusic', '-SettingsDirectory', str(self.settings), '-GameDirectory', str(self.target)], capture_output=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual((self.target / 'BepInEx/old-plugin.txt').read_text(), 'old mod')
        self.assertEqual(external.read_text(), '[Audio]\nPersonalVolume=.2\n')
        self.assert_no_automatic_backups()

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
        self.assert_no_automatic_backups()

    def test_linux_interrupted_swap_rolls_back_and_cleans(self):
        rename = Path.rename
        def interrupt_once(path, destination):
            if path.parent.name == 'staged' and path.name == 'doorstop_libs':
                raise KeyboardInterrupt('simulated cancellation')
            return rename(path, destination)
        with patch.object(Path, 'rename', interrupt_once):
            with self.assertRaises(KeyboardInterrupt):
                installer.install(ROOT, self.target)
        self.assertEqual((self.target / 'BepInEx/old-plugin.txt').read_text(), 'old mod')
        self.assert_no_automatic_backups()

    def test_linux_incomplete_rollback_keeps_recovery_originals(self):
        rename = Path.rename
        def fail_swap_and_rollback(path, destination):
            if path.parent.name == 'staged' and path.name == 'doorstop_libs':
                raise OSError('simulated swap failure')
            if path.parent.name == 'original' and path.name == 'BepInEx':
                raise OSError('simulated rollback failure')
            return rename(path, destination)
        with patch.object(Path, 'rename', fail_swap_and_rollback):
            with self.assertRaisesRegex(OSError, 'rollback incomplete'):
                installer.install(ROOT, self.target)
        jobs = list(self.target.glob('.valheim-modpack-install-*'))
        self.assertEqual(len(jobs), 1)
        self.assertTrue((jobs[0] / 'RECOVERY.txt').is_file())
        self.assertEqual((jobs[0] / 'original/BepInEx/old-plugin.txt').read_text(), 'old mod')

    def test_linux_interruption_after_original_rename_restores_it(self):
        rename = Path.rename
        def interrupt_after_rename(path, destination):
            result = rename(path, destination)
            if path.parent == self.target and path.name == 'BepInEx':
                raise KeyboardInterrupt('cancelled immediately after saving original')
            return result
        with patch.object(Path, 'rename', interrupt_after_rename):
            with self.assertRaises(KeyboardInterrupt):
                installer.install(ROOT, self.target)
        self.assertEqual((self.target / 'BepInEx/old-plugin.txt').read_text(), 'old mod')
        self.assert_no_automatic_backups()

    def test_linux_interruption_after_staged_rename_restores_original(self):
        rename = Path.rename
        def interrupt_after_rename(path, destination):
            result = rename(path, destination)
            if path.parent.name == 'staged' and path.name == 'BepInEx':
                raise KeyboardInterrupt('cancelled immediately after installing new entry')
            return result
        with patch.object(Path, 'rename', interrupt_after_rename):
            with self.assertRaises(KeyboardInterrupt):
                installer.install(ROOT, self.target)
        self.assertEqual((self.target / 'BepInEx/old-plugin.txt').read_text(), 'old mod')
        self.assert_no_automatic_backups()

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
                                 '-File', str(ROOT / 'scripts/Install-Windows.ps1'), '-SkipMusic',
                                 '-SettingsDirectory', str(self.settings), '-GameDirectory', pasted], capture_output=True)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue((self.target / 'BepInEx/core/BepInEx.dll').exists())
        self.assert_personal_data(self.target)
        self.assertFalse((self.target / 'BepInEx/config/unknown-old-mod.cfg').exists())
        self.assert_no_automatic_backups()

    @unittest.skipUnless(sys.platform == 'win32', 'Windows PowerShell test')
    def test_windows_vortex_file_links(self):
        fixture = tempfile.TemporaryDirectory(prefix='vortex staging ')
        self.addCleanup(fixture.cleanup)
        outside = Path(fixture.name)
        payload = outside / 'mod.dll'
        payload.write_bytes(b'previous mod contents')
        self.make_symlink(self.target / 'BepInEx/linked.dll', payload)
        self.make_symlink(self.target / 'BepInEx/relative.dll', 'old-plugin.txt')
        self.make_symlink(self.target / 'winhttp.dll', payload)
        result = subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass',
                                 '-File', str(ROOT / 'scripts/Install-Windows.ps1'), '-SkipMusic',
                                 '-SettingsDirectory', str(self.settings), '-GameDirectory', str(self.target)], capture_output=True)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(payload.read_bytes(), b'previous mod contents')
        self.assertFalse((self.target / 'winhttp.dll').is_symlink())
        self.assert_no_automatic_backups()

    @unittest.skipUnless(sys.platform == 'win32', 'Windows PowerShell test')
    def test_windows_dangling_link_preserves_game(self):
        self.make_symlink(self.target / 'BepInEx/missing.dll', self.target / 'not-present.dll')
        result = subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass',
                                 '-File', str(ROOT / 'scripts/Install-Windows.ps1'), '-SkipMusic',
                                 '-SettingsDirectory', str(self.settings), '-GameDirectory', str(self.target)], capture_output=True)
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
                                     '-File', str(ROOT / 'scripts/Install-Windows.ps1'), '-SkipMusic',
                                     '-SettingsDirectory', str(self.settings), '-GameDirectory', str(self.target)], capture_output=True)
            self.assertNotEqual(result.returncode, 0)
        finally:
            close = ctypes.windll.kernel32.CloseHandle
            close.argtypes = [wintypes.HANDLE]
            close(handle)
        self.assertEqual((self.target / 'BepInEx/old-plugin.txt').read_text(), 'old mod')
        self.assertEqual(loader.read_bytes(), b'old loader')
        self.assertFalse((self.target / 'BepInEx/core').exists())
        self.assert_no_automatic_backups()

    def test_incomplete_staging_does_not_change_game(self):
        copy = installer.shutil.copy2
        def fail_staging(src, dst, **kwargs):
            if Path(src).name == 'start_game_bepinex.sh':
                raise OSError('simulated full disk during staging')
            return copy(src, dst, **kwargs)
        with patch.object(installer.shutil, 'copy2', fail_staging):
            with self.assertRaises(OSError):
                installer.install(ROOT, self.target)
        self.assertEqual((self.target / 'BepInEx/old-plugin.txt').read_text(), 'old mod')
        self.assert_no_automatic_backups()

    def test_linux_preserves_existing_backups_without_copying_game(self):
        old = self.target / 'ValheimModpack-backups/old/full-backup'
        old.mkdir(parents=True)
        (old / 'keep.txt').write_text('existing archive')
        copy = installer.shutil.copy2
        def forbid_copy(src, dst, **kwargs):
            if Path(src) == self.target / 'unrelated.txt' or self.target / 'ValheimModpack-backups' in Path(src).parents:
                raise AssertionError('Installer must not copy the game or old backups')
            return copy(src, dst, **kwargs)
        with patch.object(installer.shutil, 'copy2', forbid_copy):
            installer.install(ROOT, self.target)
        self.assertEqual((old / 'keep.txt').read_text(), 'existing archive')
        self.assertEqual(list((self.target / 'ValheimModpack-backups').iterdir()), [old.parent])

    @unittest.skipUnless(sys.platform == 'win32', 'Windows PowerShell test')
    def test_windows_install_and_reinstall(self):
        command = ['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                   str(ROOT / 'scripts/Install-Windows.ps1'), '-SkipMusic', '-SettingsDirectory', str(self.settings), '-GameDirectory', str(self.target)]
        for _ in range(2):
            result = subprocess.run(command, capture_output=True)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue((self.target / 'BepInEx/core/BepInEx.dll').exists())
        self.assertFalse((self.target / 'BepInEx/old-plugin.txt').exists())
        self.assert_no_automatic_backups()
        self.assertEqual((self.target / 'ValheimModpack/WorldCharacters/characters/world-account-character.wchar').read_bytes(), b'authoritative character fixture')
        self.assertEqual((self.target / 'NordicRadio/Music/скальд.mp3').read_bytes(), b'personal music fixture')
        self.assertEqual((self.target / 'NordicRadio/Cache/fixture.mp3').read_bytes(), b'cached music fixture')
        self.assertEqual((self.target / 'unrelated.txt').read_text(), 'keep')
        self.assertEqual((self.target / 'BepInEx/config/yay.spikehimself.xportal.cfg').read_bytes(), PORTAL_CONFIG)
        self.assert_personal_data(self.target)
        invalid = self.target / 'invalid'
        invalid.mkdir()
        command[-1] = str(invalid)
        self.assertNotEqual(subprocess.run(command, capture_output=True).returncode, 0)
        self.assertEqual(list(invalid.iterdir()), [])


if __name__ == '__main__':
    unittest.main()
