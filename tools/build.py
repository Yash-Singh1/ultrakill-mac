#!/usr/bin/env python3
"""Build a macOS Mono transplant using Unity's official player template."""
from pathlib import Path
import argparse
import json
import os
import plistlib
import shutil
import struct
import subprocess
import datetime
from game_source import resolve_source, describe_source
from portal_support import supports_portal_cache
from steam_support import install_steam
from burst_support import install_burst
from chess_support import build_engine, install_engine

ROOT = Path(__file__).resolve().parents[1]
ORIGINAL = ROOT.parent / "ULTRAKILL"


def copy(source, destination):
    if destination.exists():
        return
    destination.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run(["/bin/cp", "-cR", str(source), str(destination)], check=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--arch", choices=["universal", "arm64"], default="universal")
    parser.add_argument("--background", action="store_true", help="Keep loading while UI automation changes focus.")
    parser.add_argument("--graphics", choices=["glcore", "metal"])
    parser.add_argument('--source', '--depot', type=Path, help='Windows installation or Steam depot root')
    parser.add_argument('--app', type=Path, default=ROOT/'ULTRAKILL.app', help='Output app under mac/')
    parser.add_argument('--steam-api', type=Path, default=ROOT/'runtime/steamworks/libsteam_api.dylib')
    args = parser.parse_args()
    args.graphics = args.graphics or 'metal'
    original = resolve_source(args.source)
    source_info = describe_source(original)
    app = args.app.resolve()
    if not app.is_relative_to(ROOT) or app.suffix!='.app':parser.error('--app must stay under mac/')
    source_marker=app/'Contents/Resources/port-source.json'
    if app.exists():
        previous=json.loads(source_marker.read_text()) if source_marker.exists() else describe_source(ROOT.parent/'ULTRAKILL')
        if any(previous[k]!=source_info[k] for k in ['source','assembly_sha256','catalog_sha256']):
            parser.error('Source changed; use bundle.py or a fresh --app to prevent mixing old and new assets')
    executable = str(app / 'Contents/MacOS/UnityPlayer')
    processes = subprocess.check_output(['ps', '-axo', 'command='], text=True)
    if any(line.strip().startswith(executable) for line in processes.splitlines()):
        raise SystemExit('Quit ULTRAKILL before rebuilding its app.')
    if (ROOT / 'runtime-state/pending-update.json').exists():
        raise SystemExit('Apply the pending update before rebuilding the app.')
    template = ROOT / "runtime" / "Variations" / "macos_arm64_player_development_mono" / "UnityPlayer.app"
    copy(template, app)
    contents = app / "Contents"
    data = contents / "Resources" / "Data"
    copy(original / "ULTRAKILL_Data", data)
    # Windows depots can enable native graphics jobs. Retain the ordinary
    # threaded Metal renderer used by the verified Mac build instead.
    boot = data / 'boot.config'
    if boot.exists():
        settings = boot.read_text().splitlines()
        disabled = {'gfx-enable-gfx-jobs', 'gfx-enable-native-gfx-jobs'}
        settings = [line for line in settings if line.split('=', 1)[0] not in disabled]
        boot.write_text('\n'.join(['gfx-enable-gfx-jobs=0', 'gfx-enable-native-gfx-jobs=0'] + settings) + '\n')
    shutil.copy2(template / 'Contents/Resources/unity default resources',
                 data / 'Resources/unity default resources')
    resources = ROOT / "runtime" / "Source" / "Player" / "MacPlayer" / "MacPlayerEntryPoint" / "Resources"
    copy(resources / "MainMenu.nib", contents / "Resources" / "MainMenu.nib")
    copy(ROOT / "runtime" / "MonoBleedingEdge" / "etc", contents / "MonoBleedingEdge" / "etc")
    bcl = ROOT / "runtime" / "mac-bcl"
    if not (bcl / "mscorlib.dll").exists():
        raise RuntimeError("Fetch the matching macOS .NET libraries with tools/fetch_mac_bcl.py first.")
    replaced = []
    for destination in (data / "Managed").glob("*.dll"):
        source = bcl / destination.name
        if not source.exists():
            source = bcl / "Facades" / destination.name
        if source.exists():
            shutil.copy2(source, destination)
            config = source.with_name(source.name + ".config")
            if config.exists():
                shutil.copy2(config, destination.with_name(destination.name + ".config"))
            replaced.append(destination.name)
    print("Replaced Windows .NET libraries:", ", ".join(replaced), flush=True)
    catalog_file = data / "StreamingAssets" / "aa" / "catalog.json"
    catalog = json.loads(catalog_file.read_text())
    catalog["m_InternalIds"] = [path.replace("\\", "/") for path in catalog["m_InternalIds"]]
    catalog_file.write_text(json.dumps(catalog, separators=(",", ":")))
    user_data = ROOT / "user-data"
    user_data.mkdir(exist_ok=True)
    for name in ["Cybergrind", "Palettes"]:
        copy(original / name, user_data / name)
    # Saves and preferences are fresh, physically separate directories in mac/.
    for name in ["Saves", "Preferences", "Mods"]:
        (user_data / name).mkdir(exist_ok=True)
    preferences = user_data / "Preferences" / "Prefs.json"
    if not preferences.exists():
        preferences.write_text(json.dumps({"discordIntegration": False, "levelLeaderboards": True,
                                          "fullscreen": False, "vSync": True, "frameRateLimit": 1,
                                          "disabledComputeShaders": True}))
    data_link = user_data / "ULTRAKILL_Data"
    if not data_link.exists():
        data_link.symlink_to(os.path.relpath(data, user_data))
    # Remove directories created by the initial unsigned layout, only inside our copy.
    for name in ["Saves", "Preferences", "Mods", "Cybergrind", "Palettes"]:
        if (app / name).exists():
            shutil.rmtree(app / name)
    # Application.dataPath is Contents on macOS; keep StreamingAssets reachable there.
    link = contents / "StreamingAssets"
    if not link.exists():
        link.symlink_to("Resources/Data/StreamingAssets")
    # No Windows native code may be loaded by the Mac player.
    plugins = data / "Plugins"
    if plugins.exists():
        shutil.rmtree(plugins)
    raw = bytearray((data / "globalgamemanagers").read_bytes())
    for old, new in [(b"Hakita", b"MacLab"), (b"ULTRAKILL", b"ULTRAMACX")]:
        marker = struct.pack("<I", len(old)) + old
        replacement = struct.pack("<I", len(new)) + new
        assert marker in raw or replacement in raw, old
        raw = raw.replace(marker, replacement)
    (data / "globalgamemanagers").write_bytes(raw)
    if args.graphics:
        # Keep object lengths unchanged while selecting the Mac backend.
        windows_apis = struct.pack("<4i", 3, 2, 17, 21)
        opengl_apis = struct.pack("<4i", 3, 17, 17, 17)
        metal_apis = struct.pack("<4i", 3, 16, 16, 16)
        mac_apis = metal_apis if args.graphics == 'metal' else opengl_apis
        assert sum(raw.count(v) for v in (windows_apis,opengl_apis,metal_apis)) == 1
        for old in (windows_apis,opengl_apis,metal_apis): raw = raw.replace(old, mac_apis)
        (data / "globalgamemanagers").write_bytes(raw)
    info = {
        "CFBundleExecutable": "UnityPlayer", "CFBundleIdentifier": "local.ultrakill.mac-experiment",
        "CFBundleName": "ULTRAKILL Mac Experiment", "CFBundleDisplayName": "ULTRAKILL",
        "CFBundlePackageType": "APPL", "CFBundleInfoDictionaryVersion": "6.0",
        "CFBundleShortVersionString": "0.1", "CFBundleVersion": "1",
        "CFBundleSupportedPlatforms": ["MacOSX"], "LSMinimumSystemVersion": "11.0",
        "NSPrincipalClass": "PlayerApplication", "NSMainNibFile": "MainMenu",
        "NSHighResolutionCapable": False,
        "NSAppTransportSecurity": {"NSAllowsArbitraryLoads": True},
    }
    (contents / "Info.plist").write_bytes(plistlib.dumps(info))
    (contents / "PkgInfo").write_bytes(b"APPL????")
    subprocess.run([str(ROOT / ".venv/bin/python"), str(ROOT / "tools/create_icon.py"),
                    "--app", str(app), '--source', str(original/'ULTRAKILL.exe')], check=True)
    subprocess.run([str(ROOT / ".venv" / "bin" / "python"),
                    str(ROOT / "third_party" / "casualties-port" / "tools" / "rewrap" / "patch_ggm_bool.py"),
                    str(data / "globalgamemanagers"), "runInBackground", "on" if args.background else "off"], check=True)
    managed_property='-p:GameManagedPath='+str(data/'Managed')
    portal_cache=supports_portal_cache(original/'ULTRAKILL_Data/Managed')
    subprocess.run(["dotnet", "build", str(ROOT / "tools/PortProbe"), "--configuration", "Release", "--verbosity", "quiet", managed_property, "-p:EnablePortalOptimization="+str(portal_cache).lower()], check=True)
    shutil.copy2(ROOT / "tools/PortProbe/bin/Release/netstandard2.1/PortProbe.dll", data / "Managed/PortProbe.dll")
    subprocess.run(["dotnet", "build", str(ROOT / "tools/BloodRenderer"), "--configuration", "Release", "--verbosity", "quiet", managed_property], check=True)
    shutil.copy2(ROOT / "tools/BloodRenderer/bin/Release/netstandard2.1/MacBloodRenderer.dll", data / "Managed/MacBloodRenderer.dll")
    subprocess.run(["dotnet", "run", "--project", str(ROOT / "tools" / "AssemblyPatcher"), "--configuration", "Release",
                    "--", str(original / "ULTRAKILL_Data" / "Managed" / "Assembly-CSharp.dll"),
                    str(data / "Managed" / "Assembly-CSharp.dll")], check=True)
    blood_lifetime=data/'Managed/Assembly-CSharp.lifetime.dll'
    subprocess.run(['dotnet','run','--project',str(ROOT/'tools/BloodLifetimePatch'),'--configuration','Release',
                    '--',str(data/'Managed/Assembly-CSharp.dll'),str(blood_lifetime)],check=True)
    blood_lifetime.replace(data/'Managed/Assembly-CSharp.dll')
    if portal_cache:
        portal_optimized=data/'Managed/Assembly-CSharp.portals.dll'
        subprocess.run(['dotnet','run','--project',str(ROOT/'tools/FraudPatch'),'--configuration','Release',
                        '--',str(data/'Managed/Assembly-CSharp.dll'),str(portal_optimized),
                        '--portal-sync-timing','--portal-async-visibility','--portal-visibility-cache'],check=True)
        portal_optimized.replace(data/'Managed/Assembly-CSharp.dll')
    install_burst(app)
    install_steam(app, original, args.steam_api)
    install_engine(app, build_engine(original/'ULTRAKILL_Data'))
    source_info['steam_api_source']=str(args.steam_api.resolve())
    source_marker.write_text(json.dumps(source_info,indent=2)+'\n')
    executable = contents / "MacOS" / "UnityPlayer"
    executable.chmod(0o755)
    subprocess.run(["codesign", "--force", "--deep", "--sign", "-", str(app)], check=True)
    if args.arch == 'universal':
        from make_universal import build, stage_install
        stamp = datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%d-%H%M%S')
        candidate = ROOT / 'runtime-state' / f'universal-rebuild-{stamp}' / 'ULTRAKILL.app'
        report = build(app, candidate)
        (ROOT / 'reports' / f'universal-rebuild-{stamp}.json').write_text(json.dumps(report, indent=2) + '\n')
        stage_install(app, candidate, f'universal-rebuild-{stamp}')
    print(app)


if __name__ == "__main__":
    main()
