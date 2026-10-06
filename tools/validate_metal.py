#!/usr/bin/env python3
"""Compile converted MSL. Sample the very large master variant set."""
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor
import sys,pickle,struct,hashlib,json,tempfile,subprocess,argparse
ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'third_party/casualties-port/tools/rewrap'))
from inject_metal import decompress_blob
from entrytools import parse_blob,parse_code_entry

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--dimension-fixes',action='store_true',help='Compile every shader program rebuilt for the texture-dimension fix.')
    parser.add_argument('--app',type=Path,help='Validate actual programs from this app and its shader inventory, instead of all historical caches.')
    parser.add_argument('--report',type=Path)
    args=parser.parse_args()
    changed={x['cache']:set(x['entries']) for x in json.loads((ROOT/'reports/texture-dimension-audit.json').read_text())} if args.dimension_fixes else {}
    sources={}
    # The build installs the official Mac default resources instead of these
    # converted Windows defaults. Only validate shaders shipped in the app.
    inventory=json.loads((ROOT/'reports/all-shaders.json').read_text())
    unused_defaults={hashlib.sha256((d['file']+str(d['path_id'])).encode()).hexdigest()[:20]
                     for d in inventory if d['file'].endswith('/Resources/unity default resources')}
    def shipped_shaders():
        import UnityPy, gc
        rows=json.loads((args.app/'Contents/Resources/shader-inventory.json').read_text())
        for file in sorted({r['file'] for r in rows}):
            env=UnityPy.load(str(args.app/'Contents/Resources/Data'/file))
            for obj in env.objects:
                if obj.type.name=='Shader':yield obj.read_typetree(),file
            del env;gc.collect()
    def cached_shaders():
        for path in (ROOT/'runtime/metal-shaders').glob('*.pkl'):
            if path.stem in unused_defaults:continue
            if args.dimension_fixes and path.name not in changed:continue
            yield pickle.loads(path.read_bytes()),path.name
    for d,filename in (shipped_shaders() if args.app else cached_shaders()):
        name=d['m_ParsedForm']['m_Name']
        if 14 not in d['platforms']:continue
        entries=[(i,e) for i,e in enumerate(parse_blob(decompress_blob(d,d['platforms'].index(14)))) if e['type'] in (23,24)]
        if args.dimension_fixes:
            entries=[(i,e) for i,e in entries if i in changed[filename]]
        elif name=='ULTRAKILL/Master':
            entries=entries[:8]+entries[-8:]+entries[::max(1,len(entries)//128)]
        for idx,e in entries:
            p=parse_code_entry(e['raw'])['payload'];start=struct.unpack_from('<I',p,12)[0];source=p[start:].split(b'\0',1)[0]
            sources.setdefault(hashlib.sha256(source).hexdigest(),(name,idx,source))
    print('Checking',len(sources),'unique programs',flush=True)
    def check(item):
        digest,(name,idx,source)=item
        with tempfile.NamedTemporaryFile(suffix='.metal',dir=ROOT/'runtime-state') as f:
            f.write(source);f.flush()
            result=subprocess.run(['xcrun','metal','-c',f.name,'-o','/dev/null','-std=macos-metal2.2','-Wno-unused-variable'],capture_output=True,text=True)
        if result.returncode:
            (ROOT/'reports/metal-sources'/(digest+'.metal')).write_bytes(source)
            return dict(shader=name,entry=idx,source=digest,error=result.stderr)
        return None
    with ThreadPoolExecutor(max_workers=8) as pool:
        errors=[r for r in pool.map(check,sources.items()) if r]
    report=dict(checked_unique_programs=len(sources),master_sampling=not args.dimension_fixes,
                excluded_windows_default_shader_caches=len(unused_defaults),failures=errors)
    report['app']=str(args.app) if args.app else None
    (args.report or ROOT/'reports'/('metal-dimensions-validation.json' if args.dimension_fixes else 'metal-validation.json')).write_text(json.dumps(report,indent=2))
    print('Checked',len(sources),'failed',len(errors),flush=True)
    for e in errors[:12]:print(e['shader'],e['entry'],e['error'][:250],flush=True)
    return bool(errors)

if __name__=='__main__':raise SystemExit(main())
