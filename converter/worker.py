#!/usr/bin/env python3
"""Offline, hash-checked converter for the supported Windows ULTRAKILL builds."""
from pathlib import Path
import argparse, ctypes, datetime, gc, hashlib, json, os, plistlib, shutil, signal, struct, subprocess, sys, tempfile, uuid, zlib
import bsdiff4
import UnityPy

PAYLOAD = Path(__file__).resolve().parent / 'payload'

def digest(path):
    h = hashlib.sha256()
    with path.open('rb') as f:
        for chunk in iter(lambda: f.read(8*1024*1024), b''): h.update(chunk)
    return h.hexdigest()

def emit(progress, message, **extra):
    print(json.dumps(dict(event='progress', progress=progress, message=message, **extra)), flush=True)

def source_root(path):
    root = Path(path).expanduser().resolve(strict=True)
    if root.name == 'ULTRAKILL_Data': root = root.parent
    choices = [root, root/'ULTRAKILL', root/'steamapps/common/ULTRAKILL', root/'common/ULTRAKILL']
    matches = [p for p in choices if (p/'ULTRAKILL_Data/Managed/Assembly-CSharp.dll').is_file()]
    if not matches: raise ValueError('Choose the game folder containing ULTRAKILL_Data, the Steam library, or the downloaded depot version folder.')
    root = matches[0]
    if root.suffix == '.app': raise ValueError('Choose the original Windows files, not a converted Mac app.')
    return root

def validate(source, payload=PAYLOAD):
    manifest = json.loads((payload/'manifest.json').read_text())
    identity = digest(source/'ULTRAKILL_Data/Managed/Assembly-CSharp.dll')
    profile = next((p for p in manifest['profiles'] if p['assembly_sha256'] == identity), None)
    if not profile: raise ValueError('This game build is not supported by the packaged patches. Supported builds: '+', '.join(p['name'] for p in manifest['profiles'])+'. New builds need a new verified patch pack.')
    root = source/'ULTRAKILL_Data'
    actual = {str(p.relative_to(root)) for p in root.rglob('*') if p.is_file()}
    expected = set(profile['files'])
    # Unrecognized DLLs or asset bundles must not enter a native transplant.
    if actual != expected:
        raise ValueError('Game files differ from the supported build. Missing: '+', '.join(sorted(expected-actual)[:5])+'; extra: '+', '.join(sorted(actual-expected)[:5]))
    for i, (name, checksum) in enumerate(profile['files'].items()):
        file = root/name
        if file.is_symlink() or any(p.is_symlink() for p in file.parents if p != root.parent):
            raise ValueError('Game data may not contain symlinks: '+name)
        if digest(file) != checksum: raise ValueError('Game file does not match this supported build: '+name+'. Verify the Windows installation in Steam first.')
        if i % 20 == 0: emit(2+10*i/len(expected), 'Checking input files', detail=name)
    return profile

def clone(source, destination):
    destination.parent.mkdir(parents=True, exist_ok=True)
    result = subprocess.run(['/bin/cp', '-cR', str(source), str(destination)], capture_output=True, text=True)
    if result.returncode:
        if destination.exists(): shutil.rmtree(destination) if destination.is_dir() else destination.unlink()
        if source.is_dir(): shutil.copytree(source, destination, symlinks=True)
        else: shutil.copy2(source, destination)

def patched_bytes(old, spec, payload):
    if hashlib.sha256(old).hexdigest() != spec['before']: raise ValueError('Patch input checksum changed.')
    patch = payload/spec['patch']
    if digest(patch) != spec['patch_sha256']: raise ValueError('The converter patch pack is damaged.')
    result = zlib.decompress(patch.read_bytes()) if spec.get('codec') == 'zlib' else bsdiff4.patch(old, patch.read_bytes())
    if hashlib.sha256(result).hexdigest() != spec['after']: raise ValueError('Patch output checksum failed.')
    return result

def publish(source, destination):
    # RENAME_EXCL atomically rejects a destination created while conversion ran.
    libc = ctypes.CDLL('/usr/lib/libSystem.B.dylib', use_errno=True)
    fn = libc.renamex_np
    fn.argtypes = [ctypes.c_char_p, ctypes.c_char_p, ctypes.c_uint]
    if fn(os.fsencode(source), os.fsencode(destination), 4):
        error = ctypes.get_errno()
        raise OSError(error, os.strerror(error), str(destination))

def architectures(path):
    with path.open('rb') as f: header=f.read(4096)
    formats={b'\xca\xfe\xba\xbe':('>',20),b'\xbe\xba\xfe\xca':('<',20),b'\xca\xfe\xba\xbf':('>',32),b'\xbf\xba\xfe\xca':('<',32)}
    if header[:4] not in formats: raise ValueError('Expected a universal Mach-O binary: '+str(path))
    endian,stride=formats[header[:4]];count=struct.unpack_from(endian+'I',header,4)[0]
    cpus={0x0100000c:'arm64',0x01000007:'x86_64'}
    return {cpus.get(struct.unpack_from(endian+'I',header,8+i*stride)[0],'unknown') for i in range(count)}

def convert(source, output, import_saves=False, payload=PAYLOAD):
    source = source_root(source)
    requested = Path(output).expanduser().absolute()
    if requested.is_symlink(): raise ValueError('The output already exists as a symbolic link. Choose a new app path.')
    output = requested.parent.resolve()/requested.name
    if output.suffix.lower() != '.app': raise ValueError('The output filename must end in .app.')
    if output.is_relative_to(source) or source.is_relative_to(output): raise ValueError('Choose an output outside the original game directory.')
    if output.exists() or output.is_symlink(): raise ValueError('The output already exists. Choose a new output folder or app name.')
    emit(1, 'Recognizing the Windows build')
    profile = validate(source, payload)
    emit(13, 'Input verified', build=profile['name'])
    # Require enough space for non-cloning filesystems and asset rewrites.
    ancestor = output.parent
    while not ancestor.exists(): ancestor = ancestor.parent
    clone_supported = False
    with tempfile.TemporaryDirectory(prefix='.ultrakill-space-check-',dir=ancestor) as probe:
        libc=ctypes.CDLL('/usr/lib/libSystem.B.dylib',use_errno=True)
        clonefile=libc.clonefile
        clonefile.argtypes=[ctypes.c_char_p,ctypes.c_char_p,ctypes.c_int]
        clone_supported=clonefile(os.fsencode(source/'ULTRAKILL_Data/Managed/UnityEngine.CoreModule.dll'),os.fsencode(Path(probe)/'probe'),0)==0
    written_files = profile['shader_files'] if clone_supported else profile['files']
    seed_names=['Cybergrind','Palettes']+(['Saves'] if import_saves else [])
    seed_bytes=sum(p.stat().st_size for name in seed_names if (source/name).is_dir() for p in (source/name).rglob('*') if p.is_file())
    required = sum((source/'ULTRAKILL_Data'/name).stat().st_size for name in written_files) + 300*1024*1024 + seed_bytes
    if shutil.disk_usage(ancestor).free < required:
        raise ValueError(f'Conversion needs at least {required/(1024**3):.1f} GB free at the output location, including room for rewritten assets.')
    output.parent.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix='.ultrakill-convert-', dir=output.parent))
    try:
        app = work/output.name
        emit(15, 'Copying the universal Mac runtime')
        clone(payload/'template.app', app)
        contents = app/'Contents'; data = contents/'Resources/Data'
        emit(20, 'Copying game assets')
        clone(source/'ULTRAKILL_Data', data)
        (contents/'StreamingAssets').symlink_to('Resources/Data/StreamingAssets')
        # Do not change source permissions or attributes, including read-only sources.
        for file in data.rglob('*'):
            if file.is_file() and not file.is_symlink(): file.chmod(file.stat().st_mode | 0o200)
        emit(30, 'Installing native runtime libraries and game fixes')
        for file in (data/'Managed').glob('*.dll'):
            replacement = payload/'bcl'/file.name
            if not replacement.exists(): replacement = payload/'bcl/Facades'/file.name
            if replacement.exists():
                shutil.copy2(replacement, file)
                config = replacement.with_name(replacement.name+'.config')
                if config.exists(): shutil.copy2(config, file.with_name(file.name+'.config'))
        for name, spec in profile['binary_patches'].items():
            (data/name).write_bytes(patched_bytes((source/'ULTRAKILL_Data'/name).read_bytes(), spec, payload))
        for name in ['PortProbe.dll', 'MacBloodRenderer.dll']:
            shutil.copy2(payload/profile['helpers']/name, data/'Managed'/name)
        if (data/'Plugins').exists(): shutil.rmtree(data/'Plugins')
        (contents/'Plugins').mkdir(exist_ok=True)
        shutil.copy2(payload/profile['helpers']/'lib_burst_generated.bundle', contents/'Plugins/lib_burst_generated.bundle')
        shutil.copy2(payload/'unity default resources', data/'Resources/unity default resources')
        boot = data/'boot.config'
        lines = boot.read_text().splitlines() if boot.exists() else []
        lines = [s for s in lines if s.split('=',1)[0] not in ('gfx-enable-gfx-jobs','gfx-enable-native-gfx-jobs')]
        boot.write_text('gfx-enable-gfx-jobs=0\ngfx-enable-native-gfx-jobs=0\n'+'\n'.join(lines)+'\n')
        catalog_file = data/'StreamingAssets/aa/catalog.json'
        catalog = json.loads(catalog_file.read_text())
        catalog['m_InternalIds'] = [p.replace('\\','/') for p in catalog['m_InternalIds']]
        catalog_file.write_text(json.dumps(catalog, separators=(',',':')))
        emit(36, 'Applying verified Metal shaders')
        shaders = profile['shader_files']
        converted_count = 0
        for i, (filename, specs) in enumerate(shaders.items()):
            env = UnityPy.load(str(data/filename))
            objects = {(o.assets_file.name,str(o.path_id)):o for o in env.objects}
            for spec in specs:
                obj = objects[(spec['asset'],spec['id'])]
                if obj.type.name != 'Shader': raise ValueError('Shader object layout changed.')
                obj.set_raw_data(patched_bytes(obj.get_raw_data(),spec,payload))
                converted_count += 1
            # Retain compression for UnityFS bundles rather than expanding them.
            saved=env.file.save(packer='lz4') if getattr(env.file,'signature',None)=='UnityFS' else env.file.save()
            (data/filename).write_bytes(saved)
            del env, objects, saved; gc.collect()
            verified = UnityPy.load(str(data/filename))
            checks = {(s['asset'],s['id']):s['after'] for s in specs}
            for obj in verified.objects:
                key=(obj.assets_file.name,str(obj.path_id))
                if key in checks and hashlib.sha256(obj.get_raw_data()).hexdigest()!=checks.pop(key):
                    raise ValueError('The saved shader bundle failed verification: '+filename)
            if checks: raise ValueError('A shader is missing from the saved bundle: '+filename)
            del verified; gc.collect()
            emit(36+48*(i+1)/len(shaders),'Applying verified Metal shaders', detail=filename)
        emit(86, 'Preparing an isolated Mac save profile')
        profile_id = str(uuid.uuid4())
        config = {'profile_id':profile_id, 'source':str(source), 'build':profile['name'], 'imported_windows_saves':import_saves}
        (contents/'Resources/converter-profile.json').write_text(json.dumps(config,indent=2)+'\n')
        seed = contents/'Resources/ProfileSeed'; seed.mkdir()
        for name in ['Cybergrind','Palettes'] + (['Saves'] if import_saves else []):
            original = source/name
            if original.is_dir():
                # Only regular files, never follow links outside the chosen input.
                for p in original.rglob('*'):
                    if p.is_symlink(): raise ValueError('Save/custom content contains a symlink: '+str(p))
                shutil.copytree(original,seed/name)
        for name in ['Saves','Preferences','Mods']: (seed/name).mkdir(exist_ok=True)
        (seed/'Preferences/Prefs.json').write_text(json.dumps({'discordIntegration':False,'levelLeaderboards':True,'fullscreen':False,'vSync':True,'frameRateLimit':1,'disabledComputeShaders':True}))
        info_path = contents/'Info.plist'; info = plistlib.loads(info_path.read_bytes())
        info['CFBundleExecutable'] = 'ULTRAKILL'
        info['CFBundleIdentifier'] = 'local.ultrakill.converted.'+profile_id
        info['CFBundleName'] = 'ULTRAKILL'; info['CFBundleDisplayName'] = 'ULTRAKILL'
        info_path.write_bytes(plistlib.dumps(info))
        shutil.copy2(payload/'GameLauncher',contents/'MacOS/ULTRAKILL')
        (contents/'MacOS/ULTRAKILL').chmod(0o755)
        report = dict(config, unity_version='2022.3.29f1', architectures=['arm64','x86_64'], minimum_macos='11.0', shaders_converted=converted_count, input_verified=True, patch_pack_version=1)
        (contents/'Resources/conversion-report.json').write_text(json.dumps(report,indent=2)+'\n')
        emit(93, 'Signing the app bundle')
        subprocess.run(['/usr/bin/codesign','--force','--deep','--sign','-',str(app)], check=True, stdout=sys.stderr)
        subprocess.run(['/usr/bin/codesign','--verify','--deep','--strict',str(app)],check=True,stdout=sys.stderr)
        for rel in profile['universal_binaries'] + ['Contents/MacOS/ULTRAKILL']:
            if architectures(app/rel) != {'arm64','x86_64'}:
                raise ValueError('A native library is not universal: '+rel)
        emit(98, 'Saving the finished app')
        publish(app,output)
        print(json.dumps(dict(event='complete',progress=100,message='Conversion complete',app=str(output),report=report)),flush=True)
        return output
    finally:
        shutil.rmtree(work,ignore_errors=True)

def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--source',type=Path,required=True);p.add_argument('--output',type=Path)
    p.add_argument('--import-saves',action='store_true');p.add_argument('--inspect',action='store_true')
    p.add_argument('--payload',type=Path,default=PAYLOAD)
    a=p.parse_args()
    def cancelled(*_): raise KeyboardInterrupt()
    signal.signal(signal.SIGTERM,cancelled)
    try:
        if a.inspect:
            profile=validate(source_root(a.source),a.payload)
            print(json.dumps(dict(event='inspection',build=profile['name'],supported=True)),flush=True)
        else:
            if a.output is None: p.error('--output is required for conversion')
            convert(a.source,a.output,a.import_saves,a.payload)
    except KeyboardInterrupt:
        print(json.dumps(dict(event='cancelled',message='Conversion cancelled. Partial output removed.')),flush=True);return 130
    except Exception as e:
        print(json.dumps(dict(event='error',message=str(e))),flush=True);return 1
    return 0

if __name__=='__main__': raise SystemExit(main())
