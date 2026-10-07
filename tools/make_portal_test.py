#!/usr/bin/env python3
"""Prepare the sole normal-gameplay portal test bundle without launching it."""
import json
import plistlib
from pathlib import Path
import shutil
import subprocess
import uuid

ROOT = Path(__file__).resolve().parent.parent
FINAL = ROOT / 'ULTRAKILL Portal Test.app'
STAGING = ROOT / 'test-builds/portal-test-build'


def obsolete_bundles():
    apps = [FINAL, ROOT / 'ULTRAKILL Fraud Replay.app']
    for name in ('test-builds', 'runtime-state', 'converter/build-work'):
        folder = ROOT / name
        apps.extend(p for p in folder.rglob('*.app')
                    if not any(part.endswith('.app') for part in p.relative_to(folder).parts[:-1]))
    return sorted(set(p for p in apps if p.exists()))


def prepare():
    apps = obsolete_bundles()
    commands = subprocess.check_output(['ps', '-axo', 'pid=,command='], text=True).splitlines()
    for app in apps + [STAGING]:
        if any(str(app / 'Contents/MacOS/') in line for line in commands):
            raise SystemExit(f'Refusing to replace or remove a running test bundle: {app}')
    config_file = FINAL / 'Contents/Resources/converter-profile.json'
    profile_id = json.loads(config_file.read_text())['profile_id'] if config_file.exists() else str(uuid.uuid4())
    for app in apps:
        shutil.rmtree(app)
    if STAGING.exists():
        shutil.rmtree(STAGING)
    STAGING.parent.mkdir(exist_ok=True)
    subprocess.run(['cp', '-cR', str(ROOT / 'ULTRAKILL.app'), str(STAGING)], check=True)
    resources = STAGING / 'Contents/Resources'
    managed = resources / 'Data/Managed'
    subprocess.run(['dotnet', 'build', str(ROOT / 'tools/PortProbe'), '-c', 'Release',
                    '-p:EnablePortalOptimization=true', '--nologo', '-v:q'], check=True)
    subprocess.run(['dotnet', 'build', str(ROOT / 'tools/FraudPatch'), '-c', 'Release', '--nologo', '-v:q'], check=True)
    patched = ROOT / 'test-builds/portal-optimized.dll'
    subprocess.run(['dotnet', str(ROOT / 'tools/FraudPatch/bin/Release/net8.0/FraudPatch.dll'),
                    str(ROOT / 'ULTRAKILL.app/Contents/Resources/Data/Managed/Assembly-CSharp.dll'),
                    str(patched), '--portal-sync-timing', '--portal-async-visibility', '--portal-visibility-cache'], check=True)
    shutil.copy2(patched, managed / 'Assembly-CSharp.dll')
    patched.unlink()
    shutil.copy2(ROOT / 'tools/PortProbe/bin/Release/netstandard2.1/PortProbe.dll', managed / 'PortProbe.dll')
    shutil.copy2(ROOT / 'converter/payload/GameLauncher', STAGING / 'Contents/MacOS/ULTRAKILL')
    (resources / 'converter-profile.json').write_text(json.dumps({
        'profile_id': profile_id, 'source': str(ROOT.parent / 'depots/depots/1229491/22957324')}, indent=2) + '\n')
    (resources / 'portal-cache-test.json').write_text('{"enabled_by_default": true}\n')
    seed = resources / 'ProfileSeed'
    seed.mkdir(exist_ok=True)
    for name in ('Saves', 'Preferences', 'Palettes', 'Cybergrind', 'Mods'):
        source = ROOT / 'user-data' / name
        if source.exists():
            shutil.copytree(source, seed / name, symlinks=True, dirs_exist_ok=True)
    info_file = STAGING / 'Contents/Info.plist'
    info = plistlib.loads(info_file.read_bytes())
    info.update(CFBundleName='ULTRAKILL Portal Test', CFBundleDisplayName='ULTRAKILL Portal Test',
                CFBundleExecutable='ULTRAKILL', CFBundleIdentifier='local.ultrakill.portal-test.' + profile_id)
    info_file.write_bytes(plistlib.dumps(info))
    subprocess.run(['codesign', '--force', '--deep', '--sign', '-', str(STAGING)], check=True)
    subprocess.run(['codesign', '--verify', '--deep', '--strict', str(STAGING)], check=True)
    report = ROOT / 'reports/portal-test-preparation.json'
    report.parent.mkdir(exist_ok=True)
    report.write_text(json.dumps({'staging': str(STAGING), 'final': str(FINAL), 'profile_id': profile_id,
                                  'optimizationEnabledByDefault': True, 'normalGameplay': True,
                                  'removedBundles': [str(p.relative_to(ROOT)) for p in apps]}, indent=2) + '\n')
    print(f'Prepared {STAGING}. Verify the muted startup before publishing it as {FINAL.name}.', flush=True)


if __name__ == '__main__':
    prepare()
