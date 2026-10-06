#!/usr/bin/env python3
"""Developer tool: produce portable, checksummed patches from verified builds."""
from pathlib import Path
import gc, hashlib, json, os, pickle, plistlib, shutil, struct, subprocess, sys, zlib
import bsdiff4, UnityPy
ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'tools'))
from game_source import resolve_source
from scan_source_shaders import scan
from make_universal import native_files
import convert_metal as cm
from burst_support import install_burst
from steam_support import install_steam
sys.path.insert(0,str(ROOT/'third_party/casualties-port/tools/rewrap'))
from patch_ggm_bool import patch as bool_patch
OUT=ROOT/'converter/payload'
WORK=ROOT/'converter/build-work'
PYTHON=ROOT/'.venv/bin/python'

def sha(raw):return hashlib.sha256(raw).hexdigest()
def filehash(p):
    h=hashlib.sha256()
    with p.open('rb') as f:
        for b in iter(lambda:f.read(8*1024*1024),b''):h.update(b)
    return h.hexdigest()
def run(*args):subprocess.run(list(map(str,args)),check=True)
def delta(old,new,force_delta=False):
    codec='zlib' if not force_delta and max(len(old),len(new))>1024*1024 else 'bsdiff'
    key=sha(old)+sha(new);path=OUT/'patches'/f'{key}.{codec}'
    if not path.exists():path.write_bytes(zlib.compress(new,6) if codec=='zlib' else bsdiff4.diff(old,new))
    return dict(before=sha(old),after=sha(new),codec=codec,patch=str(path.relative_to(OUT)),patch_sha256=filehash(path))
def clone(source,dest):
    dest.parent.mkdir(parents=True,exist_ok=True)
    run('/bin/cp','-cR',source,dest)

def template():
    src=ROOT/'ULTRAKILL.app';dst=OUT/'template.app'
    if dst.exists():return
    (dst/'Contents').mkdir(parents=True)
    for name in ['Frameworks','MacOS','MonoBleedingEdge']:clone(src/'Contents'/name,dst/'Contents'/name)
    resources=dst/'Contents/Resources';resources.mkdir()
    for name in ['MainMenu.nib','ULTRAKILL.icns','unity default resources']:clone(src/'Contents/Resources'/name,resources/name)
    for name in ['Info.plist','PkgInfo']:shutil.copy2(src/'Contents'/name,dst/'Contents'/name)
    shutil.copy2(src/'Contents/Resources/Data/Resources/unity default resources',OUT/'unity default resources')
    shutil.copytree(ROOT/'runtime/mac-bcl',OUT/'bcl',symlinks=True)
    for arch in ['arm64','x86_64']:
        run('swiftc','-O','-target',f'{arch}-apple-macos11.0',ROOT/'converter/GameLauncher.swift','-o',WORK/f'launcher-{arch}')
    run('lipo','-create',WORK/'launcher-arm64',WORK/'launcher-x86_64','-output',OUT/'GameLauncher')


def profile(source,label):
    source=resolve_source(source);data=source/'ULTRAKILL_Data'
    identity=filehash(data/'Managed/Assembly-CSharp.dll');cache=WORK/(identity+'.json')
    if cache.exists():return json.loads(cache.read_text())
    print('Preparing',label,flush=True)
    app=WORK/identity/'ULTRAKILL.app';managed=app/'Contents/Resources/Data/Managed'
    if app.exists():shutil.rmtree(app)
    managed.parent.mkdir(parents=True);clone(data/'Managed',managed)
    (app/'Contents/Resources').mkdir(exist_ok=True)
    (app/'Contents/Frameworks').mkdir();(app/'Contents/MacOS').mkdir()
    helpers=OUT/'helpers'/identity;helpers.mkdir(parents=True,exist_ok=True)
    prop='-p:GameManagedPath='+str(managed)
    for project,dll in [('PortProbe','PortProbe.dll'),('BloodRenderer','MacBloodRenderer.dll')]:
        run('dotnet','build',ROOT/'tools'/project,'--configuration','Release','--verbosity','quiet',prop)
        shutil.copy2(ROOT/'tools'/project/'bin/Release/netstandard2.1'/dll,helpers/dll)
        shutil.copy2(helpers/dll,managed/dll)
    run('dotnet','run','--project',ROOT/'tools/AssemblyPatcher','--configuration','Release','--',data/'Managed/Assembly-CSharp.dll',managed/'Assembly-CSharp.dll')
    run('dotnet','run','--project',ROOT/'tools/BloodLifetimePatch','--configuration','Release','--',managed/'Assembly-CSharp.dll',managed/'lifetime.dll')
    (managed/'lifetime.dll').replace(managed/'Assembly-CSharp.dll')
    install_burst(app)
    shutil.copy2(app/'Contents/Plugins/lib_burst_generated.bundle',helpers/'lib_burst_generated.bundle')
    install_steam(app,source,ROOT/'runtime/steamworks/libsteam_api.dylib')
    # Windows metadata changes have fixed-size replacements. Preserve the file layout.
    raw=bytearray((data/'globalgamemanagers').read_bytes())
    for old,new in [(b'Hakita',b'MacLab'),(b'ULTRAKILL',b'ULTRAMACX')]:
        marker=struct.pack('<I',len(old))+old;replacement=struct.pack('<I',len(new))+new
        assert marker in raw;raw=raw.replace(marker,replacement)
    old=struct.pack('<4i',3,2,17,21);new=struct.pack('<4i',3,16,16,16)
    assert raw.count(old)==1;raw=raw.replace(old,new)
    ggm=app/'Contents/Resources/Data/globalgamemanagers';ggm.write_bytes(raw)
    bool_patch(str(ggm),'runInBackground',False)
    binary={}
    for rel in ['Managed/Assembly-CSharp.dll','Managed/Facepunch.Steamworks.Win64.dll','globalgamemanagers']:
        binary[rel]=delta((data/rel).read_bytes(),(app/'Contents/Resources/Data'/rel).read_bytes(),force_delta=True)
    inventory=WORK/(identity+'-shaders.json')
    if not inventory.exists():scan(source,inventory)
    rows=json.loads(inventory.read_text())
    originals={r['path']:r['sha256'] for r in json.loads((ROOT/'reports/original-files.json').read_text())}
    shader_files={};names=[]
    for filename in sorted({r['file'] for r in rows if 4 in r['platforms']}):
        if filename=='Resources/unity default resources':continue
        relative=Path(filename);source_file=data/relative;source_hash=filehash(source_file)
        same_original=originals.get(str(Path('ULTRAKILL_Data')/relative))==source_hash
        env=UnityPy.load(str(source_file));specs=[]
        for obj in env.objects:
            if obj.type.name!='Shader':continue
            tree=obj.read_typetree()
            if 4 not in tree['platforms']:continue
            name=tree['m_ParsedForm']['m_Name']
            legacy=str(Path('ULTRAKILL/ULTRAKILL_Data')/relative)
            cachekey=legacy+str(obj.path_id) if same_original else str(relative)+str(obj.path_id)+source_hash
            key=sha(cachekey.encode())[:20];cached=ROOT/'runtime/metal-shaders'/(key+'.pkl')
            if not cached.exists():raise ValueError('No verified shader cache for '+name+' in '+filename)
            d=cm.upgrade_inline_samplers(cm.upgrade_clip_distances(cm.upgrade_recovered_cage(cm.upgrade_transparent_sampler(cm.upgrade_texture_dimensions(cm.upgrade_color_outputs(cm.upgrade_buffer_bindings(pickle.loads(cached.read_bytes()))),obj,name)),obj),obj))
            if 14 not in d['platforms']:raise ValueError('Missing Metal platform in '+name)
            old=obj.get_raw_data();obj.save_typetree(d);new=obj.data
            specs.append(dict(delta(old,new),asset=obj.assets_file.name,id=str(obj.path_id),name=name))
            names.append(name)
        shader_files[filename]=specs
        print(label,filename,len(specs),'shaders',flush=True)
        del env;gc.collect()
    files={str(p.relative_to(data)):filehash(p) for p in sorted(data.rglob('*')) if p.is_file()}
    p=dict(name=label,assembly_sha256=identity,files=files,binary_patches=binary,helpers=str(helpers.relative_to(OUT)),shader_files=shader_files,shader_count=len(names),universal_binaries=[str(p) for p in native_files(OUT/'template.app')]+['Contents/Plugins/lib_burst_generated.bundle'])
    cache.write_text(json.dumps(p,indent=2)+'\n')
    return p

def main():
    (OUT/'patches').mkdir(parents=True,exist_ok=True);WORK.mkdir(exist_ok=True)
    template()
    profiles=[profile(ROOT.parent/'ULTRAKILL','Original Windows folder'),profile(ROOT.parent/'depots/depots/1229491/22957324','Steam depot 22957324')]
    (OUT/'manifest.json').write_text(json.dumps(dict(format=1,unity='2022.3.29f1',profiles=profiles),indent=2)+'\n')
    print('Payload ready:',OUT,flush=True)
if __name__=='__main__':main()
