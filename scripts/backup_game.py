#!/usr/bin/env python3
"""Create an explicit, verified backup of the selected Linux Valheim directory."""
import argparse
from datetime import datetime
import hashlib
import json
from pathlib import Path
import shutil
import stat
import sys
import tempfile

from game_path import get_game_directory
from install_linux import ensure_game_closed


EXCLUDED_DIRECTORIES = frozenset(('ValheimModpack-backups', 'Music-backups', 'backups'))
TRANSACTION_PREFIXES = ('.valheim-modpack-install-', '.music-update-')


def excluded(path):
    return path.name in EXCLUDED_DIRECTORIES or path.name.startswith(TRANSACTION_PREFIXES)


def sha256(path):
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(chunk)
    return digest.hexdigest()


def signature(path):
    details = path.lstat()
    return (details.st_mode, details.st_size, details.st_mtime_ns, details.st_ino)


def inventory(directory):
    """Do not traverse directory links, old backup directories, or transactions."""
    entries = {}

    def visit(parent):
        for path in sorted(parent.iterdir(), key=lambda entry: entry.name):
            relative = path.relative_to(directory).as_posix()
            details = path.lstat()
            if excluded(path) and (stat.S_ISDIR(details.st_mode) or stat.S_ISLNK(details.st_mode)):
                continue
            if stat.S_ISLNK(details.st_mode):
                entries[relative] = ('link', str(path.readlink()))
            elif stat.S_ISDIR(details.st_mode):
                entries[relative] = ('directory',)
                visit(path)
            elif stat.S_ISREG(details.st_mode):
                entries[relative] = ('file', signature(path))
            else:
                raise ValueError('Cannot back up a special filesystem entry: ' + str(path))

    visit(directory)
    return entries


def backup_game(root, target, backup_directory=None):
    root = Path(root).resolve()
    target = Path(target).expanduser().resolve(strict=True)
    if not (target / 'valheim.x86_64').is_file():
        raise ValueError('valheim.x86_64 not found. Select the native Linux game directory.')
    if target == root or root in target.parents or target in root.parents:
        raise ValueError('Keep the pack repository and game directory separate.')
    destination = Path(backup_directory or root / 'backups').expanduser().absolute()
    if any(path.is_symlink() for path in (destination, *destination.parents)):
        raise ValueError('Backup directory and its parents must not be links.')
    destination = destination.resolve()
    if destination == target or target in destination.parents:
        raise ValueError('Choose a backup directory outside the Valheim game directory.')
    if sys.platform == 'linux':
        ensure_game_closed()
    before = inventory(target)
    destination.mkdir(parents=True, exist_ok=True)
    job = Path(tempfile.mkdtemp(prefix=datetime.now().strftime('%Y%m%d-%H%M%S-'), dir=destination))
    snapshot = job / 'full-game-backup'
    try:
        snapshot.mkdir()
        files, links, directories = [], [], []
        for relative, entry in before.items():
            source, copy = target / relative, snapshot / relative
            if entry[0] == 'directory':
                copy.mkdir()
                directories.append(relative)
            elif entry[0] == 'link':
                # Preserve the link itself. Its target is never copied or traversed.
                copy.symlink_to(entry[1], target_is_directory=source.is_dir())
                if str(copy.readlink()) != entry[1]:
                    raise ValueError('Backup link verification failed: ' + relative)
                links.append({'path': relative, 'target': entry[1]})
            else:
                if source.is_symlink() or signature(source) != entry[1]:
                    raise ValueError('Game file changed during backup: ' + relative)
                digest = sha256(source)
                shutil.copy2(source, copy, follow_symlinks=False)
                if signature(source) != entry[1] or copy.is_symlink() or sha256(copy) != digest:
                    raise ValueError('Backup checksum verification failed: ' + relative)
                files.append({'path': relative, 'sha256': digest})
        for relative in reversed(directories):
            shutil.copystat(target / relative, snapshot / relative, follow_symlinks=False)
        if inventory(target) != before:
            raise ValueError('Game directory changed during backup; close the game and try again.')
        metadata = {'format': 1, 'gameDirectory': str(target),
                    'createdAt': datetime.now().astimezone().isoformat(),
                    'files': files, 'links': links, 'directories': directories,
                    'excludedDirectoryNames': sorted(EXCLUDED_DIRECTORIES),
                    'excludedTransactionPrefixes': list(TRANSACTION_PREFIXES)}
        (job / 'backup.json').write_text(json.dumps(metadata, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
        (job / 'BACKUP-COMPLETE.txt').write_text('Full game backup completed and verified.\n'
                                               'File SHA-256 and preserved links: backup.json\n', encoding='utf-8')
        return snapshot
    except BaseException:
        # Only this fresh job is removed. The game and previous backups are untouched.
        if job.is_symlink() or job.resolve().parent != destination:
            raise ValueError('Unsafe incomplete backup directory; retained: ' + str(job))
        try:
            shutil.rmtree(job)
        except OSError:
            print('Incomplete backup retained (no completion marker):', job, file=sys.stderr)
        raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('game_directory', nargs='?')
    parser.add_argument('--game-directory', dest='explicit_game_directory')
    parser.add_argument('--backup-directory', help='Destination outside the game; default: this pack folder/backups.')
    args = parser.parse_args()
    if sys.platform != 'linux':
        raise ValueError('On Windows use Backup-Windows.cmd.')
    if args.game_directory and args.explicit_game_directory:
        raise ValueError('Specify the game directory once.')
    root = Path(__file__).resolve().parent.parent
    target = get_game_directory(root, args.explicit_game_directory or args.game_directory)
    print('Creating full game backup. Files will be verified before marking it complete.')
    snapshot = backup_game(root, target, args.backup_directory)
    print('Backup completed:', snapshot)
    print('This copies the game folder, including mod data and music. Native saves outside it are not included.')


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError) as error:
        print('Backup failed:', error, file=sys.stderr)
        sys.exit(1)
