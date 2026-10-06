#!/usr/bin/env python3
"""Attach a bounded CPU-only capture to the user's existing game, without launching it."""
from pathlib import Path
import argparse, datetime, json, subprocess, time
ROOT=Path(__file__).resolve().parents[1]
EXE=str(ROOT/'ULTRAKILL.app/Contents/MacOS/UnityPlayer')
p=argparse.ArgumentParser();p.add_argument('--wait-seconds',type=int,default=180);p.add_argument('--seconds',type=int,default=60);args=p.parse_args()
def game_pid():
    for line in subprocess.check_output(['ps','-axo','pid=,command='],text=True).splitlines():
        fields=line.strip().split(None,1)
        if len(fields)==2 and fields[1].startswith(EXE):return int(fields[0])
    return None
deadline=time.monotonic()+args.wait_seconds
pid=game_pid()
while pid is None and time.monotonic()<deadline:
    time.sleep(1);pid=game_pid()
if pid is None:
    print('No game launched during the capture window. No recording made.',flush=True);raise SystemExit(0)
stamp=datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%d-%H%M%S');trace=ROOT/'diagnostics'/('cpu-gameplay-'+stamp+'.trace')
record={'pid':pid,'utc_requested':datetime.datetime.now(datetime.timezone.utc).isoformat(),'seconds':args.seconds,'template':'Time Profiler','additional_instruments':[],'trace':str(trace)}
meta=ROOT/'reports'/('capture-'+stamp+'.json');meta.write_text(json.dumps(record,indent=2)+'\n')
print('Capturing existing game PID '+str(pid)+' for '+str(args.seconds)+' seconds. CPU only.',flush=True)
result=subprocess.run(['xcrun','xctrace','record','--template','Time Profiler','--attach',str(pid),'--time-limit',str(args.seconds)+'s','--no-prompt','--output',str(trace)])
record['record_exit_code']=result.returncode;record['utc_finished']=datetime.datetime.now(datetime.timezone.utc).isoformat();meta.write_text(json.dumps(record,indent=2)+'\n')
if result.returncode!=0:raise SystemExit(result.returncode)
toc=ROOT/'reports'/('capture-'+stamp+'-toc.xml');cpu=ROOT/'reports'/('capture-'+stamp+'-cpu.xml')
subprocess.run(['xcrun','xctrace','export','--input',str(trace),'--toc','--output',str(toc)],check=True)
subprocess.run(['xcrun','xctrace','export','--input',str(trace),'--xpath','/trace-toc/run[@number="1"]/data/table[@schema="time-profile"]','--output',str(cpu)],check=True)
print('Saved '+str(meta),flush=True)
