#!/usr/bin/env python3
"""Reject non-source files in the index and every reachable Git commit."""
from pathlib import Path, PurePosixPath
import subprocess

ROOT = Path(__file__).resolve().parents[1]


def git(*args):
    return subprocess.check_output(['git', '-C', str(ROOT), *args])


def allowed(name):
    path = PurePosixPath(name)
    if name in {'.gitignore', '.gitattributes', 'README.md',
                'requirements-dev.txt', 'shader-source/Cage.hlsl'}:
        return True
    if path.parts[:1] == ('tools',):
        return len(path.parts) >= 2 and not any(part in {'bin', 'obj', 'ilspy', '__pycache__'} for part in path.parts) and path.suffix in {'.py', '.cs', '.csproj', '.mm'}
    if path.parts[:1] == ('converter',):
        return len(path.parts) == 2 and path.suffix in {'.py', '.swift'}
    if path.parts[:1] == ('patches',):
        return len(path.parts) == 2 and (path.suffix == '.patch' or name == 'patches/dependencies.json')
    return False


def main():
    entries = {}
    failures = []
    for record in git('ls-files', '--stage', '-z').split(b'\0'):
        if not record:
            continue
        meta, name = record.split(b'\t', 1)
        mode, oid, stage = meta.split()
        if stage != b'0':
            failures.append('Unmerged index entry: ' + name.decode())
        entries[(name.decode(), oid.decode())] = mode.decode()
    commits = git('rev-list', '--all').decode().splitlines()
    for commit in commits:
        for record in git('ls-tree', '-r', '-z', commit).split(b'\0'):
            if not record:
                continue
            meta, name = record.split(b'\t', 1)
            mode, kind, oid = meta.split()
            entries[(name.decode(), oid.decode())] = mode.decode()
    total = 0
    for (name, oid), mode in entries.items():
        if not allowed(name) or mode not in {'100644', '100755'}:
            failures.append('Excluded path or file type: ' + name)
            continue
        raw = git('cat-file', 'blob', oid)
        total += len(raw)
        try:
            raw.decode('utf-8-sig')
        except UnicodeDecodeError:
            # Preserve upstream Latin-1 comments in byte-exact source patches.
            if PurePosixPath(name).suffix != '.patch':
                failures.append('Non-text contents: ' + name)
        if b'\0' in raw or len(raw) > 1024 * 1024:
            failures.append('Binary or oversized contents: ' + name)
        if any((b'-----BEGIN ' + kind + b' PRIVATE KEY-----') in raw for kind in (b'OPENSSH', b'RSA')):
            failures.append('Private key contents: ' + name)
    if failures:
        raise SystemExit('\n'.join(failures))
    print(f'Publication audit passed: {len(entries)} file versions, {len(commits)} commits, {total:,} text bytes. No game files or generated output.')


if __name__ == '__main__':
    main()
