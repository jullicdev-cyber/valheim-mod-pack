#!/usr/bin/env python3
"""Offline installer for native Linux Valheim; only Python's standard library."""
import hashlib
import json
from pathlib import Path
import shutil
import sys
import tempfile


def install(root, target):
    root, target = Path(root).resolve(), Path(target).expanduser().resolve(strict=True)
    if not (target / 'valheim.x86_64').is_file():
        raise ValueError('valheim.x86_64 not found. Select the native Linux game directory (not Proton).')
    if target == root or root in target.parents:
        raise ValueError('Cannot install inside the pack repository.')
    source = root / 'Game'
    inventory = json.loads((root / 'files.sha256.json').read_text(encoding='utf-8-sig'))
    actual = {p.relative_to(source).as_posix() for p in source.rglob('*') if p.is_file()}
    if actual != {entry['path'] for entry in inventory}:
        raise ValueError('Pack file inventory mismatch.')
    for entry in inventory:
        path = source / entry['path']
        if path.is_symlink() or hashlib.sha256(path.read_bytes()).hexdigest() != entry['sha256']:
            raise ValueError('Pack checksum mismatch: ' + entry['path'])
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
    # Upstream shell files may have CRLF in Windows downloads.
    launcher = stage / 'start_game_bepinex.sh'
    launcher.write_bytes(launcher.read_bytes().replace(b'\r\n', b'\n'))
    launcher.chmod(0o755)
    wrapper = stage / 'valheim-modded.sh'
    wrapper.write_text('#!/bin/sh\ncd -- "$(dirname -- "$0")" || exit 1\nexec ./start_game_bepinex.sh "$@"\n', encoding='utf-8')
    wrapper.chmod(0o755)
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


def main():
    if sys.platform != 'linux':
        raise ValueError('Run this installer on Linux. On Windows use Install-Windows.cmd.')
    if len(sys.argv) > 2:
        raise ValueError('Usage: bash Install-Linux.sh [Valheim-directory]')
    # Check the current user's processes without requiring pgrep.
    for entry in Path('/proc').iterdir():
        if entry.name.isdigit():
            try:
                if (entry / 'comm').read_text().strip() in ('valheim.x86_64', 'valheim_server.'):
                    raise ValueError('Close Valheim and its server before installing.')
            except (OSError, UnicodeError):
                pass
    target = sys.argv[1] if len(sys.argv) == 2 else input('Valheim directory (contains valheim.x86_64): ').strip().strip('"')
    if not target:
        raise ValueError('No directory specified.')
    original = install(Path(__file__).resolve().parent.parent, target)
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
