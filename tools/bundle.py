#!/usr/bin/env python3
"""Build a universal Metal app from a Windows installation or Steam depot."""
from pathlib import Path
import argparse, datetime, json, subprocess, sys
from game_source import resolve_source, describe_source
from make_universal import build as universal_build
from install_bundle import stage
ROOT=Path(__file__).resolve().parents[1]

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--source','--depot',type=Path,help='Windows game root, ULTRAKILL_Data, or DepotDownloader depot root')
    p.add_argument('--steam-api',type=Path,default=ROOT/'runtime/steamworks/libsteam_api.dylib')
    p.add_argument('--output',type=Path,help='Fresh output .app under mac/')
    p.add_argument('--install',action='store_true',help='Replace the playable app when stopped; otherwise prepare the next-launch update')
    a=p.parse_args();source=resolve_source(a.source)
    stamp=datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%d-%H%M%S')
    work=ROOT/'runtime-state'/f'bundle-{stamp}';work.mkdir(parents=True)
    arm=work/'arm64'/'ULTRAKILL.app';arm.parent.mkdir()
    output=(a.output or work/'universal'/'ULTRAKILL.app').resolve()
    if not output.is_relative_to(ROOT) or output.suffix!='.app' or output.exists():p.error('--output must be a fresh .app path under mac/')
    python=ROOT/'.venv/bin/python'
    def run(script,*args):subprocess.run([str(python),str(ROOT/'tools'/script),*map(str,args)],check=True)
    run('build.py','--arch','arm64','--graphics','metal','--background','--source',source,'--app',arm,'--steam-api',a.steam_api)
    run('convert_metal.py','--include-master','--source',source,'--app',arm)
    run('audit_metal.py','--app',arm,'--report',arm/'Contents/Resources/metal-audit.json')
    run('audit_clip_distances.py','--app',arm,'--report',arm/'Contents/Resources/clip-distance-audit.json')
    run('audit_inline_samplers.py','--app',arm,'--report',arm/'Contents/Resources/inline-sampler-audit.json')
    run('validate_metal.py','--app',arm,'--report',arm/'Contents/Resources/metal-compiler-validation.json')
    report={'source':describe_source(source),'universal':universal_build(arm,output)}
    report_path=ROOT/'reports'/f'bundle-{stamp}.json'
    if a.install:report['installation']=stage(output)
    report_path.write_text(json.dumps(report,indent=2)+'\n')
    print(json.dumps({'app':str(output),'report':str(report_path),'installation':report.get('installation')},indent=2),flush=True)

if __name__=='__main__':main()
