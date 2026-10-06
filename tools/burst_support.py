"""Compile the copied game's existing Burst routines for both Mac architectures."""
from pathlib import Path
import hashlib
import json
import re
import subprocess
import tarfile
import urllib.request

ROOT = Path(__file__).resolve().parents[1]


def compiler_for(managed):
    matches = re.findall(rb'com\.unity\.burst@([0-9]+\.[0-9]+\.[0-9]+)', (managed / 'Unity.Burst.dll').read_bytes())
    if not matches:
        raise ValueError('Cannot identify the source Burst package version')
    versions = {v.decode() for v in matches}
    if len(versions) != 1:
        raise ValueError(f'Ambiguous Burst versions: {versions}')
    version = versions.pop()
    package = ROOT / 'third_party' / f'burst-{version}' / 'package'
    archive = ROOT / 'downloads' / f'com.unity.burst-{version}.tgz'
    metadata = ROOT / 'reports' / f'burst-package-{version}.json'
    if not metadata.exists():
        with urllib.request.urlopen('https://packages.unity.com/com.unity.burst') as response:
            info = json.load(response)['versions'][version]
        metadata.write_text(json.dumps(info, indent=2) + '\n')
    else:
        info = json.loads(metadata.read_text())
    if not archive.exists():
        temporary = archive.with_suffix('.download')
        urllib.request.urlretrieve(info['dist']['tarball'], temporary)
        temporary.replace(archive)
    if hashlib.sha1(archive.read_bytes()).hexdigest() != info['dist']['shasum']:
        raise ValueError('Unity Burst package checksum does not match its registry entry')
    runtime = package / '.Runtime'
    # Package archives contain all platform backends. Extract the Mac toolchain
    # and managed compiler dependencies, retaining the official package bytes.
    if not (runtime / 'bcl.exe').exists() or not (runtime / 'libs/burstRTL_m64.a').exists():
        with tarfile.open(archive) as tar:
            for member in tar:
                parts = Path(member.name).parts
                if member.name.startswith('/') or '..' in parts or member.issym() or member.islnk():
                    raise ValueError(f'Unsafe package entry: {member.name}')
                if not member.name.startswith('package/.Runtime/'):
                    continue
                name = member.name
                if any(part in name for part in ('hostlinux', 'hostwin', '.so', '-arm64.dll', '-llvm-43', '-llvm-B4')):
                    continue
                if name.endswith('.a') and not '/libs/burstRTL_m' in name:
                    continue
                tar.extract(member, package.parent)
    linkers = list(runtime.glob('burst-lld-*-hostmac'))
    if not linkers:
        raise ValueError('Burst package has no Mac linker')
    # Burst constructs Mono-style single-quoted process arguments. The .NET
    # build host leaves those quotes in argv, so normalize argv for the linker.
    for linker in linkers:
        native = linker.with_name(linker.name + '.native')
        if not native.exists():
            linker.rename(native)
            native.chmod(0o755)
            linker.write_text('#!/usr/bin/env python3\nimport os,sys,shlex\n'
                              'args=shlex.split(" ".join(sys.argv[1:]))\n'
                              'os.execv(os.path.realpath(__file__)+".native",[sys.argv[0]+".native"]+args)\n')
            linker.chmod(0o755)
    return runtime / 'bcl.exe', version, info['dist']['shasum']


def install_burst(app):
    app = app.resolve()
    if not app.is_relative_to(ROOT) or app.suffix != '.app':
        raise ValueError('Burst destination must be an app under mac/')
    managed = app / 'Contents/Resources/Data/Managed'
    compiler, version, checksum = compiler_for(managed)
    subprocess.run(['dotnet', 'build', str(ROOT / 'tools/BurstHost'), '--configuration', 'Release', '--verbosity', 'quiet'], check=True)
    output = ROOT / 'runtime-state' / 'burst-builds' / app.parent.name
    output.mkdir(parents=True, exist_ok=True)
    libraries = []
    logs = []
    for architecture, target in [('arm64', 'ARMV8A_AARCH64_HALFFP'), ('x86_64', 'X64_SSE2')]:
        folder = output / architecture
        folder.mkdir(exist_ok=True)
        command = ['dotnet', str(ROOT / 'tools/BurstHost/bin/Release/net8.0/BurstHost.dll'), str(compiler),
                   '--platform=macOS', '--target=' + target, '--minimum-os-version=11.0',
                   '--assembly-folder=' + str(managed), '--root-assembly=' + str(managed / 'Assembly-CSharp.dll'),
                   '--include-root-assembly-references=True', '--threads=4',
                   '--output=' + str(folder / 'lib_burst_generated'), '--temp-folder=' + str(folder / 'tmp'), '--log-timings']
        log = folder / 'compile.log'
        with log.open('w') as stream:
            subprocess.run(command, stdout=stream, stderr=subprocess.STDOUT, check=True, cwd=ROOT)
        library = folder / 'lib_burst_generated.bundle'
        if not library.exists():
            raise ValueError(f'Burst produced no {architecture} library; inspect {log}')
        libraries.append(library)
        logs.append(str(log))
    plugins = app / 'Contents/Plugins'
    plugins.mkdir(exist_ok=True)
    destination = plugins / 'lib_burst_generated.bundle'
    subprocess.run(['lipo', '-create', *(str(p) for p in libraries), '-output', str(destination)], check=True)
    destination.chmod(0o755)
    report = {'compiler_version': version, 'compiler_package_sha1': checksum, 'architectures': ['arm64', 'x86_64'],
              'minimum_macos': '11.0', 'assembly_sha256': hashlib.sha256((managed / 'Assembly-CSharp.dll').read_bytes()).hexdigest(),
              'library_sha256_before_signing': hashlib.sha256(destination.read_bytes()).hexdigest(), 'compile_logs': logs}
    (app / 'Contents/Resources/port-burst.json').write_text(json.dumps(report, indent=2) + '\n')
    return report
