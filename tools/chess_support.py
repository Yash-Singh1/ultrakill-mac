#!/usr/bin/env python3
"""Build the shipped Stockfish source for macOS without changing the input."""
from pathlib import Path
import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import tarfile
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
ENGINE_NAME = 'stockfish-macos.exe'


def source_archive(source, net, output):
    """Ship Stockfish's corresponding source, weights and native build recipe."""
    recipe = '''#!/bin/sh
set -eu
cd "$(dirname "$0")"
for pair in arm64:apple-silicon x86_64:x86-64; do
    slice=${pair%%:*}
    target=${pair#*:}
    rm -rf "build-$slice"
    cp -R src "build-$slice"
    (cd "build-$slice" && MACOSX_DEPLOYMENT_TARGET=11.0 make -j6 build COMP=clang ARCH="$target" dotprod=no EXTRACXXFLAGS=-mmacosx-version-min=11.0 EXTRALDFLAGS=-mmacosx-version-min=11.0)
done
lipo -create build-arm64/stockfish build-x86_64/stockfish -output stockfish-macos.exe
chmod 755 stockfish-macos.exe
codesign --force --sign - stockfish-macos.exe
'''
    recipe_path = output.parent/'build-macos.sh'
    recipe_path.write_text(recipe)
    with tarfile.open(output, 'w:gz') as archive:
        def stable(info):
            info.uid = info.gid = 0
            info.uname = info.gname = ''
            info.mtime = 0
            return info
        archive.add(source/'src', arcname='Stockfish/src', filter=stable)
        archive.add(net, arcname='Stockfish/src/'+net.name, filter=stable)
        for name in ['AUTHORS', 'Copying.txt', 'README.md']:
            archive.add(source/name, arcname='Stockfish/'+name, filter=stable)
        archive.add(recipe_path, arcname='Stockfish/build-macos.sh', filter=stable)


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def build_engine(data):
    source = Path(data)/'StreamingAssets/ChessEngine'
    if not source.is_dir():
        return None
    h = hashlib.sha256(Path(__file__).read_bytes())
    for path in sorted((source/'src').rglob('*')):
        if path.is_file():
            h.update(str(path.relative_to(source)).encode())
            h.update(path.read_bytes())
    work = ROOT/'runtime/chess'/h.hexdigest()
    output = work/ENGINE_NAME
    if output.exists():
        subprocess.run(['codesign', '--verify', '--strict', str(output)], check=True)
        return output
    work.mkdir(parents=True, exist_ok=True)
    name = re.search(r'"(nn-[a-f0-9]{12}\.nnue)"', (source/'src/evaluate.h').read_text()).group(1)
    net = ROOT/'runtime/chess/networks'/name
    net.parent.mkdir(parents=True, exist_ok=True)
    if not net.exists():
        partial = net.with_suffix('.download')
        try:
            urllib.request.urlretrieve('https://raw.githubusercontent.com/official-stockfish/networks/master/'+name, partial)
            if digest(partial)[:12] != name[3:15]:
                raise ValueError('Stockfish network checksum does not match its source.')
            partial.replace(net)
        finally:
            partial.unlink(missing_ok=True)
    if digest(net)[:12] != name[3:15]:
        raise ValueError('Stockfish network checksum does not match its source.')
    environment = dict(os.environ, MACOSX_DEPLOYMENT_TARGET='11.0')
    binaries = []
    for arch, target in [('arm64', 'apple-silicon'), ('x86_64', 'x86-64')]:
        tree = work/arch
        if tree.exists():
            shutil.rmtree(tree)
        shutil.copytree(source/'src', tree)
        shutil.copy2(net, tree/name)
        # Intel uses baseline SSE2. ARM uses NEON without newer dot-product instructions.
        # ARM's minimum is 11.0. Upstream's Intel minimum of 10.14 also works
        # under the app bundle's macOS 11.0 minimum.
        with (work/(arch+'.log')).open('w') as log:
            subprocess.run(['make', '-j6', 'build', 'COMP=clang', 'ARCH='+target,
                            'dotprod=no', 'EXTRACXXFLAGS=-mmacosx-version-min=11.0',
                            'EXTRALDFLAGS=-mmacosx-version-min=11.0'],
                           cwd=tree, env=environment, stdout=log, stderr=subprocess.STDOUT, check=True)
        binaries.append(tree/'stockfish')
    subprocess.run(['lipo', '-create', *map(str, binaries), '-output', str(output)], check=True)
    output.chmod(0o755)
    subprocess.run(['codesign', '--force', '--sign', '-', str(output)], check=True)
    subprocess.run(['codesign', '--verify', '--strict', str(output)], check=True)
    source_archive(source, net, output.with_suffix('.source.tar.gz'))
    (work/'build.json').write_text(json.dumps(dict(source_sha256=h.hexdigest(), network=name,
        network_sha256=digest(net), executable_sha256=digest(output), minimum_macos='11.0'), indent=2)+'\n')
    return output


def install_engine(app, engine):
    directory = Path(app)/'Contents/Resources/Data/StreamingAssets/ChessEngine'
    if engine is None:
        return
    # UciChessEngine discovers *.exe on every platform. Keep that suffix on the
    # native Mach-O binary, so the game's chess logic needs no assembly patch.
    for old in directory.glob('*.exe'):
        old.unlink()
    shutil.copy2(engine, directory/ENGINE_NAME)
    (directory/ENGINE_NAME).chmod(0o755)
    shutil.copy2(engine.with_suffix('.source.tar.gz'), directory/'stockfish-macos-source.tar.gz')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--data', type=Path, required=True)
    parser.add_argument('--app', type=Path)
    args = parser.parse_args()
    engine = build_engine(args.data)
    if args.app:
        install_engine(args.app, engine)
    print(engine)
