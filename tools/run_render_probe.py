#!/usr/bin/env python3
"""Launch the separate render probe with a copied Mac profile."""
from pathlib import Path
import os
import tempfile
import shutil
import subprocess
import json
from run import sandbox_policy

root = Path(__file__).resolve().parents[1]
app = root / 'test-builds/BlueProbe.app'
profile = Path(tempfile.mkdtemp(prefix='render-probe-', dir=root / 'test-data'))
shutil.copytree(root / 'user-data', profile, symlinks=True, dirs_exist_ok=True)
preferences = profile / 'Preferences/LocalPrefs.json'
settings = json.loads(preferences.read_text())
settings.update(fullscreen=False, resolutionWidth=1280, resolutionHeight=720)
preferences.write_text(json.dumps(settings))
link = profile / 'ULTRAKILL_Data'
link.unlink()
link.symlink_to(app / 'Contents/Resources/Data')
env = os.environ.copy()
env.update(ULTRAKILL_MAC_DATA_PATH=str(link), ULTRAKILL_MAC_START_SCENE='Level 0-1',
           ULTRAKILL_MAC_TEST_MUTE='1',
           ULTRAKILL_MAC_RENDER_PROBE='1',
           ULTRAKILL_MAC_RENDER_CONTROL=str(root / 'reports/render-control.txt'))
env.pop('ULTRAKILL_MAC_TRANSITION_PROBE', None)
with (root / 'logs/render-probe-launch.log').open('w') as log:
    p = subprocess.Popen(['/usr/bin/sandbox-exec', '-p', sandbox_policy(app),
                          str(app / 'Contents/MacOS/UnityPlayer'), '-force-metal',
                          '-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720',
                          '-logFile', str(root / 'logs/render-probe.log')],
                         env=env, cwd=app, stdout=log, stderr=log, start_new_session=True)
    print('Test PID', p.pid, 'profile', profile)
