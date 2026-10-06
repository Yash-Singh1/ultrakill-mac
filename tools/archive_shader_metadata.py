"""Retain the original ShaderLab pass/state/property/sampler metadata for recovered programs."""
from pathlib import Path
from collections import defaultdict
import gzip,json,hashlib,gc
from convert_metal import ROOT,UnityPy
out=ROOT/'shader-source/recovered';index=json.loads((out/'index.json').read_text());groups=defaultdict(list)
for item in index['shaders']:groups[item['file']].append(item)
metadata=out/'original-metadata';metadata.mkdir(exist_ok=True)
for file,items in groups.items():
    env=UnityPy.load(str(ROOT.parent/file));lookup={o.path_id:o for o in env.objects if o.type.name=='Shader'}
    for item in items:
        identity=hashlib.sha256((file+':'+str(item['path_id'])).encode()).hexdigest()[:20]
        destination=metadata/(identity+'.json.gz')
        if not destination.exists():
            tree=lookup[item['path_id']].read_typetree()
            record={'file':file,'path_id':item['path_id'],'platforms':tree['platforms'],'parsed_form':tree['m_ParsedForm']}
            with gzip.GzipFile(filename=str(destination),mode='wb',mtime=0) as f:f.write(json.dumps(record,separators=(',',':')).encode())
        item['metadata']=str(destination.relative_to(out))
    print('Archived',file,len(items),flush=True)
    del env,lookup;gc.collect()
(out/'index.json').write_text(json.dumps(index,separators=(',',':'))+'\n')
byname=defaultdict(list)
for item in index['shaders']:byname[item['name']].append({k:item[k] for k in ('file','path_id','metadata')})
for name,records in byname.items():
    path=out/'shaders'/name/'manifest.json';d=json.loads(path.read_text());d['shader_objects']=records;path.write_text(json.dumps(d,separators=(',',':'))+'\n')
print('Archived metadata for',len(index['shaders']),'objects')
