#!/usr/bin/env python3
"""Freeze both workers and assemble the self-contained native converter app."""
from pathlib import Path
import argparse, plistlib, subprocess, shutil
ROOT=Path(__file__).resolve().parents[1];HERE=ROOT/'converter';WORK=HERE/'build-work'
APP=ROOT/'ULTRAKILL Converter.app'
def run(*args):subprocess.run(list(map(str,args)),check=True)
def clone(src,dst):dst.parent.mkdir(parents=True,exist_ok=True);run('/bin/cp','-cR',src,dst)
def main():
    p=argparse.ArgumentParser();p.add_argument('--skip-freeze',action='store_true');a=p.parse_args()
    if not (HERE/'payload/manifest.json').exists():raise SystemExit('Run prepare_payload.py first.')
    commands=subprocess.check_output(['ps','-axo','command='],text=True)
    if any(line.strip().startswith(str(APP/'Contents/MacOS/Converter')) or line.strip().startswith(str(APP/'Contents/Resources/worker-')) for line in commands.splitlines()):
        raise SystemExit('Close the converter and finish or cancel active conversions before rebuilding it.')
    for arch in ['arm64','x86_64']:
        run('swiftc','-O','-target',f'{arch}-apple-macos11.0',HERE/'Converter.swift','-o',WORK/f'Converter-{arch}')
        if not a.skip_freeze:
            python=ROOT/'.venv/bin/python' if arch=='arm64' else WORK/'venv-x86_64/bin/python'
            command=(['/usr/bin/arch','-x86_64'] if arch=='x86_64' else [])+[str(python),'-m','PyInstaller','--noconfirm','--onedir','--name','convert-worker','--distpath',str(WORK/f'dist-{arch}'),'--workpath',str(WORK/f'freeze-{arch}'),'--specpath',str(WORK/f'spec-{arch}'),'--collect-all','UnityPy','--exclude-module','tkinter',str(HERE/'worker.py')]
            with (HERE/f'freeze-{arch}.log').open('w') as log:subprocess.run(command,check=True,stdout=log,stderr=subprocess.STDOUT)
    run('lipo','-create',WORK/'Converter-arm64',WORK/'Converter-x86_64','-output',WORK/'Converter')
    if APP.exists():shutil.rmtree(APP)
    contents=APP/'Contents';res=contents/'Resources';(contents/'MacOS').mkdir(parents=True);res.mkdir()
    clone(WORK/'Converter',contents/'MacOS/Converter')
    for arch in ['arm64','x86_64']:clone(WORK/f'dist-{arch}/convert-worker',res/f'worker-{arch}')
    clone(HERE/'payload',res/'payload')
    shutil.copy2(ROOT/'ULTRAKILL.app/Contents/Resources/ULTRAKILL.icns',res/'Converter.icns')
    shutil.copy2(ROOT/'README.md',res/'README.md')
    info=dict(CFBundleExecutable='Converter',CFBundleIdentifier='local.ultrakill.converter',CFBundleName='ULTRAKILL Converter',CFBundleDisplayName='ULTRAKILL Converter',CFBundlePackageType='APPL',CFBundleInfoDictionaryVersion='6.0',CFBundleShortVersionString='1.0',CFBundleVersion='1',CFBundleIconFile='Converter.icns',LSMinimumSystemVersion='11.0',NSHighResolutionCapable=True,NSPrincipalClass='NSApplication')
    (contents/'Info.plist').write_bytes(plistlib.dumps(info));(contents/'PkgInfo').write_bytes(b'APPL????')
    # Unity runtime inside the patch pack is a template, not a runnable game.
    run('codesign','--force','--deep','--sign','-',APP)
    run('codesign','--verify','--deep','--strict',APP)
    print(APP)
if __name__=='__main__':main()
