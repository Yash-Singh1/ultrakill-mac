#!/usr/bin/env python3
"""Launch the experimental Mac build with original game files write-protected."""
from pathlib import Path
import argparse
import json
import os
import platform
import subprocess
import shutil
import tempfile
from apply_pending import apply_pending

ROOT = Path(__file__).resolve().parents[1]


def sandbox_policy(app):
    """Protect the input installation without a separate sandbox profile file."""
    protected = {str(ROOT.parent / 'ULTRAKILL')}
    marker = app / 'Contents/Resources/port-source.json'
    if marker.exists():
        protected.add(json.loads(marker.read_text())['source'])
    quoted = [p.replace('\\', '\\\\').replace('"', '\\"') for p in sorted(protected)]
    return '(version 1)\n(allow default)\n' + ''.join(f'(deny file-write* (subpath "{p}"))\n' for p in quoted)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--arch", choices=["auto", "arm64", "x64"], default="auto", help="Use the host architecture by default; x64 forces Intel through Rosetta on Apple silicon.")
    parser.add_argument("--app", type=Path, help="Optional separate app copy under mac/ for isolated verification.")
    parser.add_argument("--seconds", type=int, default=0)
    parser.add_argument("--headless", action="store_true")
    parser.add_argument("--offscreen", action="store_true", help="Render a muted batch-mode test with the GPU and a copied profile, without a game window.")
    parser.add_argument("--graphics", choices=["glcore", "metal"])
    parser.add_argument("--scene", help="Optional Addressables scene key for port testing, e.g. 'Level 0-1'.")
    parser.add_argument("--transition-probe", action="store_true", help="Test particle effects and scene transitions with a copied save profile.")
    parser.add_argument("--isolated-profile", action="store_true", help="Use a copied profile without scripted gameplay.")
    parser.add_argument("--steam-check", action="store_true", help="Run the read-only Steam binding and callback check.")
    parser.add_argument("--mute", action="store_true", help="Mute audio for this launch. Headless and isolated tests always mute audio.")
    parser.add_argument("--detach", action="store_true", help="Leave the game running independently of this terminal or T3 session.")
    args = parser.parse_args()
    if args.offscreen:
        if args.headless:
            parser.error('--offscreen cannot be combined with --headless')
        args.isolated_profile = True
        args.mute = True
    if args.detach and args.seconds:
        parser.error("--detach cannot be combined with --seconds")
    # Prefer the hardware's native slice even if this Python runs through Rosetta.
    arm_support = subprocess.run(['/usr/sbin/sysctl', '-n', 'hw.optional.arm64'],
                                 capture_output=True, text=True)
    host_arch = 'arm64' if arm_support.stdout.strip() == '1' or platform.machine() == 'arm64' else 'x64'
    args.arch = host_arch if args.arch == 'auto' else args.arch
    args.graphics = args.graphics or 'metal'
    app = (args.app or ROOT / 'ULTRAKILL.app').resolve()
    if not app.is_relative_to(ROOT) or app.suffix != '.app':
        parser.error('--app must be an app copy under mac/')
    apply_pending(app)
    subprocess.run(["codesign", "--verify", "--deep", str(app)], check=True)
    app_suffix = "-" + app.stem.lower().replace(" ", "-") if args.app else ""
    log = ROOT / "logs" / f"player-{args.arch}-{args.graphics}{app_suffix}{'-headless' if args.headless else ''}.log"
    command = ["/usr/bin/sandbox-exec", "-p", sandbox_policy(app),
               "/usr/bin/arch", "-arm64" if args.arch == 'arm64' else "-x86_64",
               str(app / "Contents" / "MacOS" / "UnityPlayer"),
               f"-force-{args.graphics}", "-logFile", str(log)]
    if args.headless:
        command += ["-batchmode", "-nographics"]
    elif args.offscreen:
        command += ["-batchmode"]
        if os.environ.get("ULTRAKILL_MAC_FRAUD_BENCH") == "1":
            command += ["-force-gfx-mt"]
    environment = os.environ.copy()
    environment.pop("ULTRAKILL_MAC_TEST_MUTE", None)
    if args.mute or args.headless or args.isolated_profile or args.transition_probe:
        environment["ULTRAKILL_MAC_TEST_MUTE"] = "1"
    state = ROOT / "runtime-state"
    (state / "tmp").mkdir(parents=True, exist_ok=True)
    environment["TMPDIR"] = str(state / "tmp") + "/"
    environment["ULTRAKILL_MAC_DATA_PATH"] = str(ROOT / "user-data" / "ULTRAKILL_Data")
    environment.pop("ULTRAKILL_MAC_TRANSITION_PROBE", None)
    environment.pop("ULTRAKILL_MAC_STEAM_CHECK", None)
    if args.steam_check:
        if not args.isolated_profile or args.transition_probe:
            parser.error('--steam-check requires --isolated-profile without --transition-probe')
        environment["ULTRAKILL_MAC_STEAM_CHECK"] = "1"
    if args.transition_probe or args.isolated_profile:
        test_root = ROOT / "test-data"
        test_root.mkdir(exist_ok=True)
        test_data = Path(tempfile.mkdtemp(prefix="transition-probe-", dir=test_root))
        shutil.copytree(ROOT / "user-data", test_data, symlinks=True, dirs_exist_ok=True)
        link = test_data / "ULTRAKILL_Data"
        link.unlink()
        link.symlink_to(app / "Contents/Resources/Data")
        environment["ULTRAKILL_MAC_DATA_PATH"] = str(link)
        if args.transition_probe:
            environment["ULTRAKILL_MAC_TRANSITION_PROBE"] = "1"
            args.scene = "Tutorial"
        print(f"Test profile {test_data}", flush=True)
    environment.pop("ULTRAKILL_MAC_START_SCENE", None)
    if args.scene:
        environment["ULTRAKILL_MAC_START_SCENE"] = args.scene
    # Direct scene launches can skip InitGame's resolution restore. Start the
    # player with this profile's saved settings instead of fixed test dimensions.
    preferences = Path(environment["ULTRAKILL_MAC_DATA_PATH"]).parent / "Preferences" / "LocalPrefs.json"
    if preferences.exists():
        settings = json.loads(preferences.read_text())
        width, height = settings.get("resolutionWidth"), settings.get("resolutionHeight")
        if type(width) is int and type(height) is int and width > 0 and height > 0:
            command += ["-screen-width", str(width), "-screen-height", str(height)]
        fullscreen = settings.get("fullscreen")
        if type(fullscreen) is bool:
            command += ["-screen-fullscreen", "1" if fullscreen else "0"]
    if args.offscreen:
        command += ["-screen-fullscreen", "0"]
    with (ROOT / "logs" / "launch.log").open("w") as output:
        process = subprocess.Popen(command, cwd=app, env=environment, stdout=output, stderr=subprocess.STDOUT,
                                   start_new_session=args.detach)
        print(f"PID {process.pid}; log {log}", flush=True)
        if args.detach:
            return
        timed_out = False
        try:
            process.wait(timeout=args.seconds or None)
        except subprocess.TimeoutExpired:
            timed_out = True
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()
        print(f"Exit {process.returncode}")
        if not timed_out and process.returncode:
            raise SystemExit(process.returncode if process.returncode > 0 else 128 - process.returncode)


if __name__ == "__main__":
    main()
