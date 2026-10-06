"""Install and check the compatible macOS Steam redistributable and managed ABI."""
from pathlib import Path
import json, subprocess, tempfile, shutil
ROOT=Path(__file__).resolve().parents[1]

def install_steam(app, source, library):
    managed=app/'Contents/Resources/Data/Managed'
    wrapper=managed/'Facepunch.Steamworks.Win64.dll'
    if not wrapper.exists():raise RuntimeError('Input is missing its Steamworks wrapper')
    library=library.resolve()
    if not library.is_file():raise RuntimeError('Provide the compatible universal macOS Steam API dylib with --steam-api')
    with tempfile.TemporaryDirectory(prefix='steam-patch-',dir=ROOT/'runtime-state') as temporary:
        patched=Path(temporary)/wrapper.name
        report=app/'Contents/Resources/steam-bindings.json'
        subprocess.run(['dotnet','run','--project',str(ROOT/'tools/SteamPatcher'),'--configuration','Release','--','patch',str(source/'ULTRAKILL_Data/Managed'/wrapper.name),str(patched),str(ROOT/'runtime/steamworks/struct-packing.json'),str(report)],check=True)
        bindings=json.loads(report.read_text())
        for arch in ['arm64','x86_64']:
            exports={line.split()[-1].lstrip('_') for line in subprocess.check_output(['nm','-arch',arch,'-gU',str(library)],text=True).splitlines() if line.strip()}
            missing=set(bindings['nativeEntryPoints'])-exports
            if missing:raise RuntimeError(f'Steam API on {arch} is missing wrapper exports: '+', '.join(sorted(missing)))
        bindings['exports_verified_on_architectures']=['arm64','x86_64']
        report.write_text(json.dumps(bindings,indent=2)+'\n')
        if set(subprocess.check_output(['lipo','-archs',str(library)],text=True).split())!={'arm64','x86_64'}:raise RuntimeError('Steam API must contain ARM64 and Intel x86_64')
        shutil.copy2(patched,wrapper)
        Path(str(wrapper)+'.config').unlink(missing_ok=True)
        shutil.copy2(library,app/'Contents/Frameworks/libsteam_api.dylib')
    (app/'Contents/MacOS/steam_appid.txt').write_text('1229490\n')
