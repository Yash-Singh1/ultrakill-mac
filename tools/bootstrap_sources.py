#!/usr/bin/env python3
"""Fetch pinned upstream sources and apply the port's source-only changes."""
import json
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def git(repo, *args, check=True):
    return subprocess.run(['git', '-C', str(repo), *args], check=check,
                          stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)


def main():
    specs = json.loads((ROOT / 'patches/dependencies.json').read_text())
    for spec in specs:
        destination = ROOT / spec['destination']
        if not (destination / '.git').exists():
            if destination.exists():
                raise SystemExit(f'Refusing to overwrite {destination}')
            destination.parent.mkdir(parents=True, exist_ok=True)
            subprocess.run(['git', 'clone', '--no-checkout', spec['url'], str(destination)], check=True)
            git(destination, 'checkout', '--detach', spec['revision'])
        actual = git(destination, 'rev-parse', 'HEAD').stdout.strip()
        if actual != spec['revision']:
            raise SystemExit(f'{destination} is at {actual}; expected {spec["revision"]}. Leave existing work intact and use a fresh checkout.')
        if spec.get('patch'):
            patch = ROOT / 'patches' / spec['patch']
            if git(destination, 'apply', '--reverse', '--check', str(patch), check=False).returncode == 0:
                print(f'{spec["name"]}: patch already applied')
            else:
                result = git(destination, 'apply', '--check', str(patch), check=False)
                if result.returncode:
                    raise SystemExit(f'Cannot safely apply {patch.name}:\n{result.stderr}')
                git(destination, 'apply', str(patch))
                print(f'{spec["name"]}: patched')
        else:
            print(f'{spec["name"]}: pinned source ready')


if __name__ == '__main__':
    main()
