#!/usr/bin/env python3
"""Add matching Intel Unity slices to the working Metal app without rebuilding its data."""
from pathlib import Path
import argparse
import datetime
import hashlib
import json
import plistlib
import os
import struct
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]
INTEL = ROOT / 'runtime/Variations/macos_x64_player_development_mono/UnityPlayer.app'
MACH_MAGICS = {bytes.fromhex(s) for s in ('cffaedfe', 'feedfacf', 'cafebabe', 'bebafeca', 'cafebabf', 'bfbafeca', 'cefaedfe', 'feedface')}


def sha(path):
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(8 * 1024 * 1024), b''):
            digest.update(chunk)
    return digest.hexdigest()


def native_files(app):
    result = []
    for path in sorted(app.rglob('*')):
        if path.is_symlink() or not path.is_file():
            continue
        with path.open('rb') as stream:
            magic = stream.read(4)
        if magic in MACH_MAGICS:
            result.append(path.relative_to(app))
    return result


def architectures(path):
    return subprocess.check_output(['lipo', '-archs', str(path)], text=True).split()


def thin(path, arch, destination):
    if architectures(path) == [arch]:
        return path
    subprocess.run(['lipo', str(path), '-thin', arch, '-output', str(destination)], check=True)
    return destination


def section_hash(path):
    """Hash file-backed Mach-O sections, excluding headers and code signatures."""
    data = path.read_bytes()
    if data[:4] != bytes.fromhex('cffaedfe'):
        raise ValueError('Expected a little-endian 64-bit Mach-O slice')
    commands = struct.unpack_from('<I', data, 16)[0]
    position = 32
    digest = hashlib.sha256()
    for _ in range(commands):
        command, size = struct.unpack_from('<II', data, position)
        if command == 0x19:  # LC_SEGMENT_64
            sections = struct.unpack_from('<I', data, position + 64)[0]
            for i in range(sections):
                section = position + 72 + i * 80
                name = data[section:section + 32]
                length, offset = struct.unpack_from('<QI', data, section + 40)
                flags = struct.unpack_from('<I', data, section + 64)[0]
                if flags & 0xff in (1, 12, 18):  # zero-fill sections have no file bytes
                    continue
                digest.update(name)
                digest.update(data[offset:offset + length])
        position += size
    return digest.hexdigest()


def minimum_os(path, arch):
    lines = subprocess.check_output(['otool', '-arch', arch, '-l', str(path)], text=True).splitlines()
    for i, line in enumerate(lines):
        if line.strip() in ('cmd LC_BUILD_VERSION', 'cmd LC_VERSION_MIN_MACOSX'):
            for item in lines[i + 1:i + 7]:
                fields = item.split()
                if fields and fields[0] in ('minos', 'version'):
                    return fields[1]
    raise ValueError(f'No macOS deployment target in {path}: {arch}')


def audit(app):
    info = plistlib.loads((app / 'Contents/Info.plist').read_bytes())
    if info.get('LSMinimumSystemVersion') != '11.0':
        raise ValueError('Bundle must require macOS 11.0')
    if not os.access(app / 'Contents/MacOS' / info['CFBundleExecutable'], os.X_OK):
        raise ValueError('App executable is not executable')
    results = []
    for relative in native_files(app):
        path = app / relative
        archs = architectures(path)
        if set(archs) != {'arm64', 'x86_64'}:
            raise ValueError(f'Native binary is not universal: {relative}: {archs}')
        minimums = {arch: minimum_os(path, arch) for arch in archs}
        for arch, version in minimums.items():
            if tuple((list(map(int, version.split('.'))) + [0, 0])[:3]) > (11, 0, 0):
                raise ValueError(f'{relative} requires {version} on {arch}')
        results.append({'path': str(relative), 'architectures': archs, 'native_minimum_os': minimums})
    subprocess.run(['codesign', '--verify', '--deep', '--strict', str(app)], check=True)
    return {'app': str(app), 'bundle_minimum_os': '11.0', 'native_binaries': results, 'code_signature_valid': True}


def build(source, output):
    for app in (source, output):
        if not app.resolve().is_relative_to(ROOT) or app.suffix != '.app':
            raise ValueError('App paths must stay under mac/')
    if output.exists():
        raise ValueError('Output app already exists; choose a fresh output path')
    natives = native_files(source)
    runtime_natives = set(native_files(INTEL))
    if not runtime_natives.issubset(natives):
        raise ValueError('Working app is missing native Unity dependencies')
    for relative in set(natives) - runtime_natives:
        if set(architectures(source / relative)) != {'arm64', 'x86_64'}:
            raise ValueError(f'Additional native plugin must support both architectures: {relative}')
    for app in (source, INTEL):
        if b'2022.3.29f1' not in (app / 'Contents/Frameworks/UnityPlayer.dylib').read_bytes():
            raise ValueError('Unity runtime version does not match 2022.3.29f1')
    output.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run(['/bin/cp', '-cR', str(source), str(output)], check=True)
    preserved = []
    with tempfile.TemporaryDirectory(prefix='universal-slices-', dir=output.parent) as temporary:
        temporary = Path(temporary)
        for index, relative in enumerate(natives):
            arm = thin(source / relative, 'arm64', temporary / f'{index}-arm64')
            if relative in runtime_natives:
                intel = thin(INTEL / relative, 'x86_64', temporary / f'{index}-x86_64')
                subprocess.run(['lipo', '-create', str(arm), str(intel), '-output', str(output / relative)], check=True)
            (output / relative).chmod((source / relative).stat().st_mode & 0o777)
            arm_after = thin(output / relative, 'arm64', temporary / f'{index}-arm-after')
            if section_hash(arm) != section_hash(arm_after):
                raise ValueError(f'ARM64 code or data changed: {relative}')
            preserved.append(str(relative))
        info_path = output / 'Contents/Info.plist'
        info = plistlib.loads(info_path.read_bytes())
        info['LSMinimumSystemVersion'] = '11.0'
        info['LSMinimumSystemVersionByArchitecture'] = {'arm64': '11.0', 'x86_64': '11.0'}
        info_path.write_bytes(plistlib.dumps(info))
        subprocess.run(['codesign', '--force', '--deep', '--sign', '-', str(output)], check=True)
        # Signing changes the signatures, so compare actual sections after signing too.
        for index, relative in enumerate(natives):
            arm = thin(source / relative, 'arm64', temporary / f'{index}-original-final')
            after = thin(output / relative, 'arm64', temporary / f'{index}-signed-final')
            if section_hash(arm) != section_hash(after):
                raise ValueError(f'ARM64 sections changed during signing: {relative}')
    changed = set(natives) | {Path('Contents/Info.plist'), Path('Contents/_CodeSignature/CodeResources')}
    shared_files = 0
    for path in source.rglob('*'):
        relative = path.relative_to(source)
        target = output / relative
        if path.is_symlink():
            if not target.is_symlink() or path.readlink() != target.readlink():
                raise ValueError(f'App link changed: {relative}')
        elif path.is_file() and relative not in changed:
            if sha(path) != sha(target):
                raise ValueError(f'Shared game data changed: {relative}')
            shared_files += 1
    report = audit(output)
    report.update(arm64_code_and_data_sections_preserved=preserved, shared_files_unchanged=shared_files, source_app=str(source), intel_gpu_rendering_tested=False)
    return report


def stage_install(source, candidate, name):
    from apply_pending import MANIFEST, apply_pending
    if MANIFEST.exists():
        raise ValueError('Another update is pending')
    files = []
    for relative in native_files(candidate) + [Path('Contents/Info.plist')]:
        files.append({'source': str((candidate / relative).relative_to(ROOT)), 'target': str(relative), 'sha256': sha(candidate / relative), 'previous_sha256': sha(source / relative)})
    MANIFEST.write_text(json.dumps({'name': name, 'app': str(source), 'files': files}, indent=2) + '\n')
    apply_pending(source)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-app', type=Path, default=ROOT / 'ULTRAKILL.app')
    parser.add_argument('--output-app', type=Path)
    parser.add_argument('--audit', type=Path, help='Audit an existing universal app without changing it')
    parser.add_argument('--install', action='store_true', help='Stage and install when the game is stopped')
    args = parser.parse_args()
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%d-%H%M%S')
    if args.audit:
        print(json.dumps(audit(args.audit.resolve()), indent=2))
        return
    source = args.source_app.resolve()
    output = (args.output_app or ROOT / 'runtime-state' / f'universal-{stamp}' / 'ULTRAKILL.app').resolve()
    report = build(source, output)
    report_path = ROOT / 'reports' / f'universal-build-{stamp}.json'
    report_path.write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps({'app': str(output), 'report': str(report_path), 'shared_files_unchanged': report['shared_files_unchanged']}, indent=2), flush=True)
    if args.install:
        stage_install(source, output, f'universal-runtime-{stamp}')


if __name__ == '__main__':
    main()
