"""Download a ZIP music library and install it transactionally, using only stdlib."""
import argparse
from datetime import datetime
import hashlib
from html.parser import HTMLParser
import http.cookiejar
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import sys
import tempfile
from urllib.parse import urlencode, urlparse, parse_qs, urljoin
from urllib.request import build_opener, HTTPCookieProcessor, Request
import zipfile

MAX_ZIP = 2 * 1024**3
MAX_EXPANDED = 4 * 1024**3


def unlinked(path):
    path = Path(path)
    for entry in (path, *path.parents):
        if entry.is_symlink() or (entry.exists() and getattr(entry.lstat(), 'st_file_attributes', 0) & 0x400):
            raise ValueError('Music path must not be a link: ' + str(entry))


def download_url(url):
    parsed = urlparse(url)
    if parsed.scheme not in ('https', 'http') or not parsed.hostname or parsed.username or parsed.password:
        raise ValueError('Specify an HTTP(S) ZIP URL or public Google Drive file link.')
    if parsed.hostname in ('drive.google.com', 'drive.usercontent.google.com'):
        match = re.search(r'/file/d/([\w-]+)', parsed.path)
        file_id = match[1] if match else parse_qs(parsed.query).get('id', [''])[0]
        if not re.fullmatch(r'[\w-]+', file_id):
            raise ValueError('Google Drive link must identify a file, not a folder.')
        return 'https://drive.usercontent.google.com/download?' + urlencode({'id': file_id, 'export': 'download', 'confirm': 't'})
    return url


class ConfirmForm(HTMLParser):
    def __init__(self):
        super().__init__()
        self.action = None
        self.fields = {}
        self.active = False

    def handle_starttag(self, tag, attrs):
        values = dict(attrs)
        if tag == 'form' and values.get('id') == 'download-form':
            self.action = values.get('action')
            self.active = True
        if tag == 'input' and self.active and values.get('name'):
            self.fields[values['name']] = values.get('value', '')

    def handle_endtag(self, tag):
        if tag == 'form':
            self.active = False


def download(url, destination):
    url = download_url(url)
    opener = build_opener(HTTPCookieProcessor(http.cookiejar.CookieJar()))
    for attempt in range(2):
        print('Downloading music ZIP...', flush=True)
        with opener.open(Request(url, headers={'User-Agent': 'ValheimModPack-Music/1.0'}), timeout=120) as response:
            first = response.read(4)
            if first == b'PK\x03\x04':
                size = len(first)
                with Path(destination).open('wb') as output:
                    output.write(first)
                    while True:
                        chunk = response.read(1024 * 1024)
                        if not chunk:
                            break
                        size += len(chunk)
                        if size > MAX_ZIP:
                            raise ValueError('Music ZIP exceeds 2 GiB.')
                        output.write(chunk)
                return
            html = (first + response.read(1024 * 1024)).decode('utf-8', errors='replace')
            form = ConfirmForm()
            form.feed(html)
            action = urljoin(response.url, form.action or '')
            parsed = urlparse(action)
            if attempt == 0 and form.action and parsed.scheme == 'https' and parsed.hostname in ('drive.google.com', 'drive.usercontent.google.com'):
                url = action + ('&' if '?' in action else '?') + urlencode(form.fields)
                continue
            raise ValueError('URL returned no ZIP. Check public access and Google Drive download quota.')


def extract_music(archive, stage):
    names = set()
    total = 0
    count = 0
    with zipfile.ZipFile(archive) as bundle:
        entries = bundle.infolist()
        if len(entries) > 10000:
            raise ValueError('Too many ZIP entries.')
        for entry in entries:
            raw = entry.filename.replace('\\', '/')
            path = PurePosixPath(raw)
            mode = entry.external_attr >> 16
            if path.is_absolute() or '..' in path.parts or ':' in raw or stat.S_ISLNK(mode):
                raise ValueError('Unsafe ZIP entry: ' + raw)
            total += entry.file_size
            if total > MAX_EXPANDED:
                raise ValueError('Expanded ZIP exceeds 4 GiB.')
            if entry.is_dir() or path.suffix.lower() != '.mp3' or '__MACOSX' in path.parts or path.name.startswith('._'):
                continue
            name = path.name
            if not name or any(c in name for c in '<>"|?*') or name.endswith((' ', '.')) or any(ord(c) < 32 for c in name):
                raise ValueError('Unsupported MP3 filename: ' + name)
            if name.casefold() in names:
                raise ValueError('Duplicate MP3 basename in ZIP: ' + name)
            if name.split('.')[0].upper() in {'CON', 'PRN', 'AUX', 'NUL', *('COM' + str(i) for i in range(1, 10)), *('LPT' + str(i) for i in range(1, 10))}:
                raise ValueError('Reserved filename: ' + name)
            if not 4 <= entry.file_size <= 64 * 1024**2:
                raise ValueError('Unsupported MP3 size: ' + name)
            names.add(name.casefold())
            # Replace names case-insensitively on Linux too, so both platforms agree.
            for old in stage.iterdir():
                if old.name.casefold() == name.casefold() and old.name != name:
                    if not old.is_file():
                        raise ValueError('MP3 destination is a directory: ' + name)
                    old.unlink()
            with bundle.open(entry) as source, (stage / name).open('wb') as output:
                shutil.copyfileobj(source, output)
            count += 1
        if count == 0:
            raise ValueError('ZIP contains no MP3 files.')
        if sum(p.suffix.lower() == '.mp3' for p in stage.iterdir() if p.is_file()) > 256:
            raise ValueError('NordicRadio supports at most 256 tracks. Remove excess tracks first.')
    return count


def install_music(game, url, ensure_closed, downloader=None):
    downloader = downloader or download
    radio = Path(game) / 'NordicRadio'
    unlinked(radio)
    ensure_closed()
    radio.mkdir(exist_ok=True)
    lock = radio / '.music-update.lock'
    try:
        lock.mkdir()
    except FileExistsError:
        raise ValueError('Another music update is running. Remove NordicRadio/.music-update.lock only if the previous update was interrupted.')
    try:
        return _install_music(game, url, ensure_closed, downloader)
    finally:
        lock.rmdir()


def _install_music(game, url, ensure_closed, downloader):
    game = Path(game)
    radio = game / 'NordicRadio'
    music = radio / 'Music'
    backups = radio / 'Music-backups'
    for path in (radio, music, backups):
        unlinked(path)
    radio.mkdir(exist_ok=True)
    backups.mkdir(exist_ok=True)
    job = Path(tempfile.mkdtemp(prefix=datetime.now().strftime('%Y%m%d-%H%M%S-'), dir=backups))
    stage = job / 'staged'
    archive = job / 'music.zip'
    downloader(url, archive)
    if music.exists():
        # Never follow linked entries while copying personal files.
        for path in music.rglob('*'):
            unlinked(path)
        shutil.copytree(music, stage)
    else:
        stage.mkdir()
    count = extract_music(archive, stage)
    ensure_closed()
    for path in (radio, music, backups):
        unlinked(path)
    original = job / 'original'
    moved = False
    try:
        if music.exists():
            music.rename(original)
            moved = True
        stage.rename(music)
    except Exception:
        if moved:
            original.rename(music)
        raise
    digest = hashlib.sha256()
    with archive.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(chunk)
    (job / 'INSTALL.txt').write_text('Source: ' + url + '\nZIP SHA256: ' + digest.hexdigest() + '\nTracks: ' + str(count) + '\nBackup: original/\n', encoding='utf-8')
    archive.unlink()
    print('Music installed:', count, 'tracks. Folder:', music, '\nBackup:', original if moved else job)
    return count


def main():
    from game_path import get_game_directory
    from install_linux import ensure_game_closed
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('game_directory', nargs='?')
    parser.add_argument('--url', help='Override music-source.txt with a public ZIP URL.')
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    if sys.platform != 'linux':
        raise ValueError('On Windows use Update-Music-Windows.cmd.')
    target = get_game_directory(root, args.game_directory)
    url = args.url or (root / 'music-source.txt').read_text(encoding='utf-8-sig').strip()
    install_music(target, url, ensure_game_closed)


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print('Music update failed:', error, file=sys.stderr)
        sys.exit(1)
