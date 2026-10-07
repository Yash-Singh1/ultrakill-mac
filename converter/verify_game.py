from pathlib import Path
import argparse,json,os,subprocess,time
HERE=Path(__file__).resolve().parent
p=argparse.ArgumentParser();p.add_argument('app',type=Path);p.add_argument('--arch',choices=['arm64','x86_64'],default='arm64');p.add_argument('--tag',default='depot');p.add_argument('--render',action='store_true');a=p.parse_args()
app=a.app.resolve();profile=HERE/'verification/profiles'/(a.tag+'-'+a.arch);profile.mkdir(parents=True,exist_ok=True)
log=HERE/'verification'/(a.tag+'-'+a.arch+('-rendered' if a.render else '')+'.log')
report_file=app/'Contents/Resources/conversion-report.json'
expected_cache=json.loads(report_file.read_text()).get('portal_visibility_cache',False)
env=os.environ.copy();env.update(ULTRAKILL_MAC_TEST_MUTE='1',ULTRAKILL_MAC_DATA_PATH=str(profile/'ULTRAKILL_Data'),ULTRAKILL_MAC_START_SCENE='Main Menu')
command=['/usr/bin/arch','-'+a.arch,str(app/'Contents/MacOS/ULTRAKILL'),'-batchmode','-logFile',str(log),'-screen-width','640','-screen-height','480','-screen-fullscreen','0']
if not a.render:command += ['-nographics']
proc=subprocess.Popen(command,env=env)
deadline=time.monotonic()+40
found=False
while time.monotonic()<deadline:
 if log.exists():
  content=log.read_text(errors='replace')
  if '[Presence] Scene loaded: Main Menu' in content and '[MacRuntime] Native Burst enabled=True' in content and '[MacTest] Audio muted' in content and (not expected_cache or '[MacPortals] Portal visibility cache enabled.' in content):found=True;break
 if proc.poll() is not None:break
 time.sleep(.5)
if proc.poll() is None:
 time.sleep(3);proc.terminate()
 try:proc.wait(timeout=15)
 except subprocess.TimeoutExpired:proc.kill();proc.wait()
text=log.read_text(errors='replace') if log.exists() else ''
failures=[s for s in ['DllNotFoundException','EntryPointNotFoundException','BadImageFormatException','Could not load file or assembly','Desired shader compiler platform 14 is not available','Unable to open archive file','Shader is not supported on this GPU'] if s in text]
report=dict(app=str(app),arch=a.arch,portal_optimization='[MacPortals] Portal visibility cache enabled.' in text,muted='[MacTest] Audio muted' in text,native_burst='[MacRuntime] Native Burst enabled=True' in text,menu_loaded=found,exit=proc.returncode,failures=failures,diagnostics=list(str(x.relative_to(profile)) for x in profile.rglob('*.csv')),rendered=a.render)
(HERE/'verification'/(a.tag+'-'+a.arch+('-rendered' if a.render else '')+'.json')).write_text(json.dumps(report,indent=2)+'\n')
print(json.dumps(report,indent=2))
if not found or failures:raise SystemExit(1)
