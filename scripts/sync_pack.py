"""Transactional in-place pack updates, with optional Git. No game files touched."""
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import tempfile
from contextlib import contextmanager

REPOSITORY = 'https://github.com/jullicdev-cyber/valheim-mod-pack.git'
PROTECTED = {'.git', '.updates', '.cache', 'dist', 'backups', 'local-settings.json', 'NordicRadio', 'ValheimModpack'}
LEGACY_NAMES = set('''.gitattributes .gitignore CHANGELOG.md COMPATIBILITY.md Game INVENTORY-DIAGNOSTIC.md
Install-Linux.sh Install-Windows.cmd MOD-REVIEW.md README.md Set-GamePath-Linux.sh
Set-GamePath-Windows.cmd Update-Linux.sh Update-Windows.cmd VALIDATION.md VERSION audit config
files.sha256.json local-plugins mods.lock.json plugins reference scripts third-party world-settings.json'''.split())


def unlinked(path):
    path = Path(path)
    if path.is_symlink() or (path.exists() and getattr(path.lstat(), 'st_file_attributes', 0) & 0x400):
        raise ValueError('Linked update path is not supported: ' + str(path))
    if path.is_dir():
        for entry in path.iterdir():
            unlinked(entry)


@contextmanager
def update_lock(root):
    updates = root / '.updates'
    if updates.is_symlink() or (updates.exists() and getattr(updates.stat(), 'st_file_attributes', 0) & 0x400):
        raise ValueError('Update directory must not be a link.')
    updates.mkdir(exist_ok=True)
    lock = updates / 'update.lock'
    try:
        lock.mkdir()
    except FileExistsError:
        raise ValueError('Another update is running. If it was interrupted, remove .updates/update.lock after closing it.')
    try:
        yield updates
    finally:
        lock.rmdir()


def managed_names(root, pack):
    names = {p.name for p in pack.iterdir() if p.name != '.git'}
    reserved = {p.casefold() for p in PROTECTED}
    if any(name.casefold() in reserved for name in names):
        raise ValueError('Downloaded pack contains personal/reserved paths.')
    previous = root / '.updates/managed-names.json'
    unlinked(previous)
    old = json.loads(previous.read_text(encoding='utf-8-sig')) if previous.exists() else list(LEGACY_NAMES)
    if not isinstance(old, list) or any(not isinstance(n, str) or n in ('', '.', '..') or '/' in n or '\\' in n or ':' in n or n.casefold() in reserved for n in old):
        raise ValueError('Invalid previous managed-file list.')
    return names, set(old) | names


def replace_pack(root, pack, verify, include_git=False):
    """Keep originals only during replacement; retain them if rollback fails."""
    root, pack = Path(root).resolve(), Path(pack).resolve()
    verify(pack)
    names, affected = managed_names(root, pack)
    if include_git:
        if (root / '.git').exists():
            raise ValueError('Refusing to replace existing Git history.')
        names.add('.git')
        affected.add('.git')
    for name in affected:
        unlinked(root / name)
    for name in names:
        unlinked(pack / name)
    manifest = root / '.updates/managed-names.json'
    old_manifest = manifest.read_bytes() if manifest.exists() else None
    job = Path(tempfile.mkdtemp(prefix='replace-', dir=root / '.updates'))
    staged, original, failed = (job / n for n in ('staged', 'original', 'failed'))
    saved, installed = [], []
    recovery = False
    try:
        for directory in (staged, original, failed):
            directory.mkdir()
        for name in names:
            source = pack / name
            if source.is_dir():
                shutil.copytree(source, staged / name)
            else:
                shutil.copy2(source, staged / name)
        recovery = True
        try:
            for name in sorted(affected):
                if (root / name).exists():
                    saved.append(name)
                    (root / name).rename(original / name)
                if name in names:
                    installed.append(name)
                    (staged / name).rename(root / name)
            verify(root)
            manifest.write_text(json.dumps(sorted(names - {'.git'})) + '\n', encoding='utf-8')
        except BaseException as error:
            failures = []
            for name in reversed(installed):
                try:
                    if not (staged / name).exists() and (root / name).exists():
                        (root / name).rename(failed / name)
                except BaseException as failure:
                    failures.append(str(failure))
            for name in saved:
                try:
                    if (original / name).exists():
                        if (root / name).exists():
                            raise OSError('Rollback destination is occupied: ' + str(root / name))
                        (original / name).rename(root / name)
                except BaseException as failure:
                    failures.append(str(failure))
            try:
                if old_manifest is None:
                    manifest.unlink(missing_ok=True)
                else:
                    manifest.write_bytes(old_manifest)
            except BaseException as failure:
                failures.append(str(failure))
            if failures:
                recovery = True
                (job / 'RECOVERY.txt').write_text('Pack rollback failed. Previous replaced entries are in original/.\n'
                    'Close the updater, move conflicting new entries aside, and restore original/ to the pack root.\n'
                    'Personal root folders were not replaced.\n' + '\n'.join(failures) + '\n', encoding='utf-8')
                raise OSError('Pack rollback failed; recovery files retained at ' + str(job)) from error
            recovery = False
            raise
        recovery = False
        print('Pack folder updated.')
        return root
    finally:
        if recovery:
            instructions = job / 'RECOVERY.txt'
            if not instructions.exists():
                instructions.write_text('Pack transaction was interrupted. Previous replaced entries are in original/.\n'
                    'Close the updater and restore original/ entries after moving conflicting new entries aside.\n', encoding='utf-8')
        else:
            remove_job(root, job)


def remove_job(root, job):
    root, job = Path(root).resolve(), Path(job).resolve()
    if job.parent != root / '.updates':
        raise ValueError('Update cleanup outside its transaction refused.')
    unlinked(job)
    def writable_remove(function, path, error):
        # Git object files are read-only on Windows, including temporary clones.
        if not Path(path).is_relative_to(job) or function not in (os.unlink, os.remove, os.rmdir):
            raise error[1]
        os.chmod(path, stat.S_IWRITE | stat.S_IREAD)
        function(path)
    shutil.rmtree(job, onerror=writable_remove)


def git_run(git, root, *arguments):
    # No automatic stashing/rebasing or interactive credentials during an update.
    env = dict(os.environ, GIT_TERMINAL_PROMPT='0', GCM_INTERACTIVE='Never')
    result = subprocess.run([git, '-C', str(root), '-c', 'core.quotepath=false',
        '-c', 'merge.autoStash=false', '-c', 'rebase.autoStash=false', *arguments],
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding='utf-8', errors='replace', env=env)
    if result.returncode:
        raise ValueError('Git ' + arguments[0] + ' failed: ' + result.stderr.strip())
    return result.stdout.strip()


def same_origin(url, expected):
    def normalized(value):
        value = value.strip().rstrip('/').removesuffix('.git')
        for prefix in ('https://github.com/', 'ssh://git@github.com/', 'git@github.com:'):
            if value.startswith(prefix):
                return 'github:' + value[len(prefix):].casefold()
        return value
    return normalized(url) == normalized(expected)


def sync_pack(root, download, verify, expand, git='auto', repository=REPOSITORY):
    root = Path(root).resolve()
    executable = shutil.which('git') if git == 'auto' else git
    with update_lock(root) as updates:
        if not executable:
            if (root / '.git').exists():
                raise ValueError('This folder has Git history but Git is unavailable. Install Git to update it without desynchronizing its history.')
            print('Git unavailable: updating this folder from a verified ZIP.')
            return replace_pack(root, download(root), verify)
        if not (root / '.git').exists():
            print('ZIP folder detected: connecting this folder to the GitHub repository.')
            job = Path(tempfile.mkdtemp(prefix='clone-', dir=updates))
            pack = job / 'pack'
            try:
                git_run(executable, root, 'clone', '--branch', 'main', '--single-branch', '--depth', '1', repository, str(pack))
                verify(pack)
                return replace_pack(root, pack, verify, include_git=True)
            finally:
                remove_job(root, job)

        top = Path(git_run(executable, root, 'rev-parse', '--show-toplevel')).resolve()
        if top != root:
            raise ValueError('Run the updater from the root of the pack repository.')
        if git_run(executable, root, 'status', '--porcelain', '--untracked-files=no'):
            raise ValueError('Tracked files have local changes. Commit or save them separately before updating; no files were overwritten.')
        if git_run(executable, root, 'branch', '--show-current') != 'main':
            raise ValueError('Switch this repository to main before updating.')
        remotes = git_run(executable, root, 'remote').splitlines()
        if 'origin' not in remotes:
            git_run(executable, root, 'remote', 'add', 'origin', repository)
        origin = git_run(executable, root, 'remote', 'get-url', 'origin')
        if not same_origin(origin, repository):
            raise ValueError('origin points to another repository; it was not changed: ' + origin)
        git_run(executable, root, 'fetch', 'origin', 'main')
        commit = git_run(executable, root, 'rev-parse', 'FETCH_HEAD')
        old = git_run(executable, root, 'rev-parse', 'HEAD')
        git_run(executable, root, 'merge-base', '--is-ancestor', old, commit)
        job = Path(tempfile.mkdtemp(prefix='pull-', dir=updates))
        archive = job / 'pack.zip'
        recovery = attempted = False
        try:
            git_run(executable, root, 'archive', '--format=zip', '--prefix=valheim-mod-pack-' + commit + '/', '-o', str(archive), commit)
            pack = expand(archive, job / 'pack', commit)
            verify(pack)
            _, affected = managed_names(root, pack)
            for name in affected:
                unlinked(root / name)
            recovery = True
            try:
                # Git retains the old commit itself; no full filesystem copy is needed.
                attempted = True
                git_run(executable, root, 'pull', '--ff-only', '--no-rebase', 'origin', commit)
                if git_run(executable, root, 'rev-parse', 'HEAD') != commit:
                    raise ValueError('Git did not advance to the verified commit; installation stopped.')
                verify(root)
            except BaseException as error:
                if attempted:
                    try:
                        git_run(executable, root, 'reset', '--hard', old)
                    except BaseException as failure:
                        recovery = True
                        (job / 'RECOVERY.txt').write_text('Git rollback failed. Previous commit: ' + old
                            + '\nSave any new local changes before restoring that commit. Personal root folders were not replaced.\n'
                            + str(failure) + '\n', encoding='utf-8')
                        raise OSError('Git rollback failed; recovery instructions retained at ' + str(job)) from error
                recovery = False
                raise
            recovery = False
            print('Git pull completed.')
            return root
        finally:
            if recovery:
                instructions = job / 'RECOVERY.txt'
                if not instructions.exists():
                    instructions.write_text('Git update was interrupted. Previous commit: ' + old
                        + '\nClose the updater and save any new local changes before restoring that commit.\n', encoding='utf-8')
            else:
                remove_job(root, job)
