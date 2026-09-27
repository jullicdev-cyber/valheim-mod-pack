#!/usr/bin/env python3
"""Offline installer for native Linux Valheim; only Python's standard library."""
import hashlib
import json
import re
from pathlib import Path
import shutil
import sys
import tempfile
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
    for name in names + ['ValheimModpack-backups']:
        if (target / name).is_symlink():
            raise ValueError('Refusing linked target: ' + name)
    backups = target / 'ValheimModpack-backups'
    backups.mkdir(exist_ok=True)
    backup = Path(tempfile.mkdtemp(prefix='install-', dir=str(backups)))
    stage, original = backup / 'staged', backup / 'original'
    stage.mkdir()
    original.mkdir()
    snapshot = backup / 'full-backup'
    snapshot.mkdir()
    print('Creating full backup:', snapshot)
    for src in target.iterdir():
        if src.name == 'ValheimModpack-backups':
            continue
        destination = snapshot / src.name
        if src.is_symlink():
            # Preserve the link itself; never traverse outside the selected game.
            destination.symlink_to(src.readlink(), target_is_directory=src.is_dir())
        elif src.is_dir():
            shutil.copytree(src, destination, symlinks=True)
        else:
            shutil.copy2(src, destination)
    (backup / 'BACKUP-COMPLETE.txt').write_text('Full backup completed before installation.\n', encoding='utf-8')
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
    if sys.platform == 'linux':
        ensure_game_closed()
    saved, installed = [], []
    try:
        for name in names:
            path = target / name
            if path.exists():
                path.rename(original / name)
                saved.append(name)
            (stage / name).rename(path)
            installed.append(name)
    except Exception:
        failed = backup / 'failed-install'
        failed.mkdir()
        for name in installed:
            (target / name).rename(failed / name)
        for name in saved:
            (original / name).rename(target / name)
        raise
    (backup / 'INSTALL.txt').write_text(
        'Target: ' + str(target) + '\nReplaced entries: ' + ', '.join(saved) +
        '\nRollback: close the game, move installed entries aside, then copy original/* back.\n', encoding='utf-8')
    return original


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
    if len(sys.argv) > 2:
        raise ValueError('Usage: bash Install-Linux.sh [Valheim-directory]')
    root = Path(__file__).resolve().parent.parent
    target = get_game_directory(root, sys.argv[1] if len(sys.argv) == 2 else None)
    ensure_game_closed()
    original = install(root, target)
    print('Installed successfully. Backup:', original)
    print('Full game backup:', original.parent / 'full-backup')
    print('Steam launch options: ./valheim-modded.sh %command%')
    print('Set world Resources to x2 and Portals to Casual.')


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError) as error:
        print('Installation failed:', error, file=sys.stderr)
        sys.exit(1)
