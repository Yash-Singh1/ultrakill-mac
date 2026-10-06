#!/usr/bin/env python3
"""Benchmark a copied game profile at the user's saved resolution."""
from pathlib import Path
import os, tempfile, shutil, subprocess, json, argparse
from run import sandbox_policy

root = Path(__file__).resolve().parents[1]
ap = argparse.ArgumentParser()
ap.add_argument('--name', required=True)
ap.add_argument('--scene', default='Level 0-3')
ap.add_argument('--particles', action='store_true')
args = ap.parse_args()
app = root / 'test-builds/BlueProbe.app'
profile = Path(tempfile.mkdtemp(prefix='perf-probe-', dir=root / 'test-data'))
shutil.copytree(root / 'user-data', profile, symlinks=True, dirs_exist_ok=True)
prefs = profile / 'Preferences/LocalPrefs.json'
settings = json.loads(prefs.read_text())
settings.update(fullscreen=False, vSync=False, frameRateLimit=0)
prefs.write_text(json.dumps(settings))
prefs2 = profile / 'Preferences/Prefs.json'
settings2 = json.loads(prefs2.read_text())
settings2.update(vSync=False, frameRateLimit=0)
prefs2.write_text(json.dumps(settings2))
link = profile / 'ULTRAKILL_Data'
link.unlink()
link.symlink_to(app / 'Contents/Resources/Data')
env = os.environ.copy()
for key in ('ULTRAKILL_MAC_TRANSITION_PROBE', 'ULTRAKILL_MAC_RENDER_PROBE', 'ULTRAKILL_MAC_BLOOD_PROBE'):
    env.pop(key, None)
env.update(ULTRAKILL_MAC_DATA_PATH=str(link), ULTRAKILL_MAC_START_SCENE=args.scene,
           ULTRAKILL_MAC_TEST_MUTE='1',
           ULTRAKILL_MAC_PERF_PROBE='1',
           ULTRAKILL_MAC_PERF_MARKERS=str(root / f'reports/perf-{args.name}-markers.txt'))
if args.particles:
    env['ULTRAKILL_MAC_PERF_PARTICLES'] = '1'
else:
    env.pop('ULTRAKILL_MAC_PERF_PARTICLES', None)
with (root / f'logs/perf-{args.name}-launch.log').open('w') as log:
    p = subprocess.Popen(['/usr/bin/sandbox-exec', '-p', sandbox_policy(app),
                          str(app / 'Contents/MacOS/UnityPlayer'), '-force-metal',
                          '-screen-fullscreen', '0', '-screen-width', str(settings['resolutionWidth']),
                          '-screen-height', str(settings['resolutionHeight']),
                          '-logFile', str(root / f'logs/perf-{args.name}.log')],
                         env=env, cwd=app, stdout=log, stderr=log, start_new_session=True)
    print('Test PID', p.pid, 'profile', profile)
