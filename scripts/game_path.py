"""Local game-path preference shared by the Linux entry points."""
import json
from pathlib import Path
import sys


def get_game_directory(root, value=None, ask_again=False, settings_root=None):
    root = Path(root).resolve()
    settings_file = Path(settings_root or root) / 'local-settings.json'
    if settings_file.is_symlink():
        raise ValueError('Settings file must not be a link.')
    settings = json.loads(settings_file.read_text(encoding='utf-8-sig')) if settings_file.exists() else {}
    if not isinstance(settings, dict):
        raise ValueError('Invalid local-settings.json. Fix or rename it before continuing.')
    if not value and not ask_again:
        saved = settings.get('linuxGameDirectory')
        if isinstance(saved, str) and saved:
            if (Path(saved) / 'valheim.x86_64').is_file():
                print('Using saved game directory:', saved)
                value = saved
            else:
                print('Saved game directory no longer exists. Enter its new location.')
    if not value:
        value = input('Valheim directory (contains valheim.x86_64): ')
    value = value.strip()
    prefix = 'Valheim directory (contains valheim.x86_64):'
    while value.startswith(prefix):
        value = value[len(prefix):].strip()
    value = value.strip('"')
    if not value:
        raise ValueError('No directory specified.')
    target = Path(value).expanduser().resolve(strict=True)
    if not (target / 'valheim.x86_64').is_file():
        raise ValueError('valheim.x86_64 not found. Select the native Linux game directory.')
    if target == root or root in target.parents or target in root.parents:
        raise ValueError('Keep the pack repository and game directory separate.')
    settings['linuxGameDirectory'] = str(target)
    settings_file.parent.mkdir(parents=True, exist_ok=True)
    settings_file.write_text(json.dumps(settings, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    return target


if __name__ == '__main__':
    try:
        if sys.platform != 'linux':
            raise ValueError('On Windows use Set-GamePath-Windows.cmd.')
        if len(sys.argv) > 2:
            raise ValueError('Usage: bash Set-GamePath-Linux.sh [Valheim-directory]')
        print('Saved:', get_game_directory(Path(__file__).resolve().parent.parent,
                                          sys.argv[1] if len(sys.argv) == 2 else None, ask_again=True))
    except (OSError, ValueError) as error:
        print('Cannot save game path:', error, file=sys.stderr)
        sys.exit(1)
