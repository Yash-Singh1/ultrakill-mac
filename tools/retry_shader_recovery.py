from concurrent.futures import ThreadPoolExecutor,as_completed
from collections import Counter
from decompile_shader import ROOT,decompile
import json
report=ROOT/'reports/custom-shader-recovery.json';r=json.loads(report.read_text());items=[(i,p) for i,p in r['programs'].items() if p['status']!='recompiled' or '.GetDimensions(' in (ROOT/'shader-source/recovered'/ (i+'.hlsl')).read_text()]
def work(item):
    identity,meta=item;prefix=ROOT/'shader-source/recovered'/identity
    try: result=decompile(prefix.with_suffix('.dxbc').read_bytes(),prefix)
    except Exception as e: result={'status':'exception','error':str(e)}
    return identity,{**meta,**result}
with ThreadPoolExecutor(max_workers=4) as pool:
    futures=[pool.submit(work,item) for item in items]
    for n,future in enumerate(as_completed(futures),1):
        identity,result=future.result();r['programs'][identity]=result
        if n%100==0: print(n,dict(Counter(p['status'] for p in r['programs'].values())),flush=True)
r['summary']['status_counts']=dict(Counter(p['status'] for p in r['programs'].values()));report.write_text(json.dumps(r,indent=2)+'\n');print(r['summary'])
