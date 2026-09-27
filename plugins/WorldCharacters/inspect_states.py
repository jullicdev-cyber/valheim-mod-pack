"""Read World Characters state/checkpoints offline. Never writes active saves."""
import argparse
import hashlib
import io
import json
from pathlib import Path
import struct

MAX_STATE = 16 * 1024 * 1024


class Reader:
    def __init__(self, raw):
        self.stream = io.BytesIO(raw)

    def number(self, fmt):
        size = struct.calcsize('<' + fmt)
        raw = self.stream.read(size)
        if len(raw) != size:
            raise ValueError('Truncated file')
        return struct.unpack('<' + fmt, raw)[0]

    def block(self, maximum):
        count = self.number('i')
        if count < 0 or count > maximum:
            raise ValueError('Invalid field size')
        raw = self.stream.read(count)
        if len(raw) != count:
            raise ValueError('Truncated field')
        return raw

    def text(self, maximum):
        return self.block(maximum).decode('utf-8')


def state(raw):
    if not 72 <= len(raw) <= MAX_STATE or hashlib.sha256(raw[:-32]).digest() != raw[-32:]:
        raise ValueError('State checksum or size is invalid')
    r = Reader(raw[:-32])
    if r.number('i') != 0x57434831 or r.number('i') != 1:
        raise ValueError('Unsupported state version')
    world, character, revision = (r.number('q') for _ in range(3))
    owner, name, build = r.text(100), r.text(256), r.text(256)
    player, world_data = r.block(4 * 1024 * 1024), r.block(10 * 1024 * 1024)
    if r.stream.read(1) or not world or not character or revision < 0:
        raise ValueError('Invalid state identity or trailing data')
    if player and (len(player) < 4 or struct.unpack_from('<i', player)[0] != 33):
        raise ValueError('Unsupported Player.Save format')
    key = hashlib.sha256(f'{world}\n{owner}\n{character}'.encode()).hexdigest()
    return dict(key=key, world=world, owner=owner, character=character, name=name,
                revision=revision, build=build, player_bytes=len(player), world_bytes=len(world_data))


def checkpoint(raw):
    r = Reader(raw)
    if r.number('i') != 0x57434331:
        raise ValueError('Unsupported checkpoint')
    world, ticks, count = r.number('q'), r.number('q'), r.number('i')
    if not world or not 0 <= count <= 10000:
        raise ValueError('Invalid checkpoint header')
    result, keys = [], set()
    for _ in range(count):
        body = r.block(MAX_STATE)
        metadata = state(body)
        if metadata['world'] != world or metadata['key'] in keys:
            raise ValueError('Checkpoint mixes worlds or duplicate characters')
        keys.add(metadata['key'])
        result.append((metadata, body))
    if r.stream.read(1):
        raise ValueError('Trailing checkpoint bytes')
    return world, ticks, result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('file', type=Path)
    parser.add_argument('--extract', type=Path, help='Extract a checkpoint to a NEW directory for manual review')
    args = parser.parse_args()
    if args.file.stat().st_size > 512 * 1024 * 1024:
        raise ValueError('File exceeds offline inspection limit')
    raw = args.file.read_bytes()
    if raw[:4] == struct.pack('<i', 0x57434831):
        if args.extract:
            raise ValueError('--extract only applies to checkpoints')
        print(json.dumps(state(raw), ensure_ascii=False, indent=2))
        return
    world, ticks, records = checkpoint(raw)
    # Validate the ENTIRE checkpoint before creating any files. Do not restore into an active store.
    if args.extract:
        target = args.extract.absolute()
        if target.exists() or target.is_symlink() or any(p.is_symlink() for p in target.parents):
            raise ValueError('Extraction needs a new directory without symlink parents')
        target.mkdir(parents=True, exist_ok=False)
        for metadata, body in records:
            with (target / (metadata['key'] + '.wchar')).open('xb') as out:
                out.write(body)
        (target / 'READ-ME.txt').write_text('Review these states together with the matching world backup.\n'
            'Close Valheim before any manual restore. No active save was replaced by this tool.\n')
    print(json.dumps(dict(world=world, utc_dotnet_ticks=ticks, characters=[m for m, _ in records]), ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
