#!/usr/bin/env python3
"""Install the Valheim pack and music on Linux; --skip-music supports offline installs."""
import hashlib
import json
import re
from pathlib import Path
import shutil
import sys
import tempfile
import argparse
from game_path import get_game_directory


def verify_pack(root):
    root = Path(root).resolve()
    version = (root / 'VERSION').read_text(encoding='utf-8-sig').strip()
    lock = json.loads((root / 'mods.lock.json').read_text(encoding='utf-8-sig'))
    if not re.fullmatch(r'\d+\.\d+\.\d+', version) or lock.get('packVersion') != version:
        raise ValueError('Pack version mismatch: VERSION=' + version + ', mods.lock.json=' + str(lock.get('packVersion')))
    source = root / 'Game'
    inventory = json.loads((root / 'files.sha256.json').read_text(encoding='utf-8-sig'))
    actual = {p.relative_to(source).as_posix() for p in source.rglob('*') if p.is_file()}
    if actual != {entry['path'] for entry in inventory}:
        raise ValueError('Pack file inventory mismatch.')
    for entry in inventory:
        path = source / entry['path']
        if path.is_symlink() or hashlib.sha256(path.read_bytes()).hexdigest() != entry['sha256']:
            raise ValueError('Pack checksum mismatch: ' + entry['path'])


def install(root, target):
    root, target = Path(root).resolve(), Path(target).expanduser().resolve(strict=True)
    if not (target / 'valheim.x86_64').is_file():
        raise ValueError('valheim.x86_64 not found. Select the native Linux game directory (not Proton).')
    if target == root or root in target.parents or target in root.parents:
        raise ValueError('Keep the pack repository and game directory separate.')
    if sys.platform == 'linux':
        ensure_game_closed()
    source = root / 'Game'
    verify_pack(root)
    names = ['BepInEx', 'doorstop_libs', 'start_game_bepinex.sh', '.doorstop_version', 'valheim-modded.sh']
    for name in names:
        if (target / name).is_symlink():
            raise ValueError('Refusing linked target: ' + name)
    # Same-volume renames allow rollback without making a permanent game backup.
    transaction = Path(tempfile.mkdtemp(prefix='.valheim-modpack-install-', dir=str(target)))
    stage, original = transaction / 'staged', transaction / 'original'
    saved, installed = [], []
    try:
        stage.mkdir()
        original.mkdir()
        prepare_install(source, target, stage, names)
        if sys.platform == 'linux':
            ensure_game_closed()
        for name in names:
            path = target / name
            if path.exists():
                saved.append(name)
                path.rename(original / name)
            installed.append(name)
            (stage / name).rename(path)
    except BaseException as error:
        rollback_errors = []
        failed = transaction / 'failed-install'
        if installed:
            try:
                failed.mkdir(exist_ok=True)
            except OSError as rollback_error:
                rollback_errors.append(str(rollback_error))
        for name in reversed(installed):
            try:
                # A signal can arrive after rename succeeds but before Python
                # resumes. Track intent first and inspect where the entry is.
                path = target / name
                if not (stage / name).exists() and (path.exists() or path.is_symlink()):
                    path.rename(failed / name)
            except OSError as rollback_error:
                rollback_errors.append(name + ': ' + str(rollback_error))
        for name in reversed(saved):
            try:
                previous = original / name
                if previous.exists() or previous.is_symlink():
                    if (target / name).exists() or (target / name).is_symlink():
                        raise OSError('Target is occupied; original retained.')
                    previous.rename(target / name)
            except OSError as rollback_error:
                rollback_errors.append(name + ': ' + str(rollback_error))
        try:
            if original.exists() and any(original.iterdir()):
                rollback_errors.append('Previous entries remain in original/; recovery files retained.')
        except OSError as rollback_error:
            rollback_errors.append('Cannot verify restored originals: ' + str(rollback_error))
        if rollback_errors:
            instructions = ('Installation failed and automatic rollback was incomplete.\n'
                            'Target: ' + str(target) + '\n'
                            'Close the game. Recover the previous entries from original/.\n'
                            'New entries moved aside are in failed-install/.\n'
                            'Do not delete this directory until recovery is complete.\n'
                            'Errors: ' + '\n'.join(rollback_errors) + '\n')
            try:
                (transaction / 'RECOVERY.txt').write_text(instructions, encoding='utf-8')
            except OSError:
                pass
            raise OSError('Installation failed; rollback incomplete. Recovery files retained: ' + str(transaction)) from error
        remove_transaction(transaction, target)
        raise
    try:
        remove_transaction(transaction, target)
    except OSError as error:
        raise OSError('Mods installed, but temporary installation files could not be removed: ' + str(transaction)) from error


def remove_transaction(transaction, target):
    """Only remove the fresh transaction directly inside this selected game."""
    transaction, target = Path(transaction), Path(target)
    if transaction.is_symlink() or transaction.resolve().parent != target.resolve():
        raise ValueError('Unsafe installation transaction path.')
    if not transaction.name.startswith('.valheim-modpack-install-'):
        raise ValueError('Invalid installation transaction directory.')
    shutil.rmtree(transaction)


def prepare_install(source, target, stage, names):
    for name in names[:-1]:
        src = source / name
        if src.is_dir():
            shutil.copytree(src, stage / name)
        else:
            shutil.copy2(src, stage / name)
    # Keep Quick Stack's per-character protection flags across pack updates.
    # Shared mod configs still come from the verified package.
    previous_config = target / 'BepInEx/config'
    if previous_config.is_symlink():
        raise ValueError('Personal-data config directory must not be a link.')
    if previous_config.is_dir():
        for personal in previous_config.iterdir():
            if re.fullmatch(r'(QuickStackStore|AzuAutoStore|AzuExtendedPlayerInventory)_player_-?\d+\.dat', personal.name):
                if personal.is_symlink() or not personal.is_file():
                    raise ValueError('Personal Quick Stack data must be a regular file: ' + personal.name)
                shutil.copy2(personal, stage / 'BepInEx/config' / personal.name)
    # These are personal Bindrune state, not shared modpack settings.
    for relative in ('BepInEx/bindrune.keys', 'BepInEx/bindrune.spare', 'BepInEx/config/Bindrune/situations.txt', 'BepInEx/config/isimp.Bindrune.cfg'):
        personal = target / relative
        if personal.is_symlink():
            raise ValueError('Personal bindings must be a regular file: ' + relative)
        if personal.exists():
            if not personal.is_file() or any(parent.is_symlink() for parent in personal.parents if parent != target):
                raise ValueError('Personal bindings must be an unlinked regular file: ' + relative)
            destination = stage / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(personal, destination)
    # Upstream shell files may have CRLF in Windows downloads.
    launcher = stage / 'start_game_bepinex.sh'
    launcher.write_bytes(launcher.read_bytes().replace(b'\r\n', b'\n'))
    launcher.chmod(0o755)
    wrapper = stage / 'valheim-modded.sh'
    wrapper.write_text('#!/bin/sh\ncd -- "$(dirname -- "$0")" || exit 1\nexec ./start_game_bepinex.sh "$@"\n', encoding='utf-8')
    wrapper.chmod(0o755)


def ensure_game_closed():
    # Check the current user's processes without requiring pgrep.
    for entry in Path('/proc').iterdir():
        if entry.name.isdigit():
            try:
                if (entry / 'comm').read_text().strip() in ('valheim.x86_64', 'valheim_server.'):
                    raise ValueError('Close Valheim and its server before installing.')
            except (OSError, UnicodeError):
                pass

def main():
    if sys.platform != 'linux':
        raise ValueError('Run this installer on Linux. On Windows use Install-Windows.cmd.')
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('game_directory', nargs='?')
    parser.add_argument('--music-url')
    parser.add_argument('--skip-music', action='store_true')
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    target = get_game_directory(root, args.game_directory)
    ensure_game_closed()
    install(root, target)
    print('Installed successfully. Temporary rollback files removed.')
    print('Steam launch options: ./valheim-modded.sh %command%')
    print('Set world Resources to x2 and Portals to Casual.')
    if not args.skip_music:
        from update_music import install_music
        url = args.music_url or (root / 'music-source.txt').read_text(encoding='utf-8-sig').strip()
        try:
            install_music(target, url, ensure_game_closed)
        except Exception as error:
            raise ValueError('Mods installed successfully, but music update failed: ' + str(error) + '. Retry with Update-Music-Linux.sh.') from error


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError) as error:
        print('Installation failed:', error, file=sys.stderr)
        sys.exit(1)
