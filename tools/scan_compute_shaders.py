from pathlib import Path
from collections import Counter
import json,pickle,gc
from convert_metal import ROOT,UnityPy
source=ROOT.parent/'ULTRAKILL/ULTRAKILL_Data';items=[]
files=list(source.rglob('*.assets'))+list(source.rglob('*.bundle'))
for path in files:
    env=UnityPy.load(str(path))
    for obj in env.objects:
        if obj.type.name!='ComputeShader':continue
        d=obj.read_typetree();identity=str(path.relative_to(ROOT.parent))+':'+str(obj.path_id)
        safe=str(obj.path_id).replace('-','n')
        out=ROOT/('reports/compute-'+safe+'.pkl');out.write_bytes(pickle.dumps(d))
        items.append({'file':str(path.relative_to(ROOT.parent)),'path_id':obj.path_id,'name':d.get('m_Name'),'tree':str(out.relative_to(ROOT)),'keys':list(d)})
        print(items[-1],flush=True)
    del env;gc.collect()
(ROOT/'reports/compute-shaders.json').write_text(json.dumps(items,indent=2)+'\n')
print('compute shader objects',len(items),flush=True)
