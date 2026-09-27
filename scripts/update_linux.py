#!/usr/bin/env python3
"""Update this pack folder from GitHub, then run its offline game installer."""
import argparse
import json
from pathlib import Path
import re
import shutil
import stat
import subprocess
import sys
import tempfile
from urllib.request import Request, urlopen
import zipfile
from game_path import get_game_directory
from install_linux import ensure_game_closed, verify_pack
from sync_pack import sync_pack

REPO = 'jullicdev-cyber/valheim-mod-pack'


def expand_archive(archive, destination, commit):
    if not re.fullmatch(r'[0-9a-f]{40}', commit):
        raise ValueError('Invalid GitHub commit.')
    destination = Path(destination).resolve()
    if destination.exists():
        raise ValueError('Extraction directory must be new.')
    prefix = 'valheim-mod-pack-' + commit
    with zipfile.ZipFile(archive) as bundle:
        seen = set()
        for item in bundle.infolist():
            name = item.filename
            parts = name.rstrip('/').split('/')
            if (parts[0] != prefix or any(p in ('', '.', '..') for p in parts)
                    or any(c in name for c in '\\:<>|?*') or name.casefold() in seen
                    or stat.S_ISLNK(item.external_attr >> 16)):
                raise ValueError('Unsafe or duplicate archive entry: ' + name)
            seen.add(name.casefold())
        destination.mkdir(parents=True)
        # Strip GitHub's wrapper directory; this also keeps portable archives
        # usable from long Windows workspace paths during verification.
        for item in bundle.infolist():
            relative = item.filename[len(prefix):].lstrip('/')
            if not relative:
                continue
            target = destination / relative
            if item.is_dir():
                target.mkdir(parents=True, exist_ok=True)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                with bundle.open(item) as source, target.open('xb') as stream:
                    shutil.copyfileobj(source, stream)
    return destination


def fetch(url):
    return urlopen(Request(url, headers={'User-Agent': 'ValheimModPack-Updater',
                                       'Accept': 'application/vnd.github+json'}), timeout=300)


def download_pack(root):
    updates = Path(root) / '.updates'
    if updates.is_symlink():
        raise ValueError('Update directory must not be a link.')
    with fetch('https://api.github.com/repos/' + REPO + '/commits/main') as response:
        commit = json.load(response)['sha']
    if not isinstance(commit, str) or not re.fullmatch(r'[0-9a-f]{40}', commit):
        raise ValueError('GitHub returned an invalid commit.')
    updates.mkdir(exist_ok=True)
    job = Path(tempfile.mkdtemp(prefix=commit[:12] + '-', dir=updates))
    archive = job / 'pack.zip'
    print('Downloading main at', commit)
    with fetch('https://codeload.github.com/' + REPO + '/zip/' + commit) as response, archive.open('wb') as stream:
        shutil.copyfileobj(response, stream)
    pack = expand_archive(archive, job / 'pack', commit)
    if not (pack / 'scripts/install_linux.py').is_file():
        raise ValueError('Downloaded Linux installer is missing.')
    verify_pack(pack)
    version = (pack / 'VERSION').read_text(encoding='utf-8-sig').strip()
    (updates / 'latest.json').write_text(json.dumps({'commit': commit, 'version': version, 'directory': str(pack)},
                                                  ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print('Downloaded and verified pack', version, 'at', pack)
    return pack


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('game_directory', nargs='?')
    parser.add_argument('--download-only', action='store_true', help='Update this pack folder without installing into the game.')
    args = parser.parse_args()
    if sys.platform != 'linux':
        raise ValueError('On Windows use Update-Windows.cmd.')
    root = Path(__file__).resolve().parent.parent
    target = None
    if not args.download_only:
        target = get_game_directory(root, args.game_directory)
        ensure_game_closed()
    pack = sync_pack(root, download_pack, verify_pack, expand_archive)
    if args.download_only:
        print('Pack folder updated. Game files were not changed.')
        return
    subprocess.run([sys.executable, str(pack / 'scripts/install_linux.py'), str(target)], check=True)


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, KeyError, zipfile.BadZipFile, subprocess.CalledProcessError) as error:
        print('Update failed:', error, file=sys.stderr)
        sys.exit(1)
