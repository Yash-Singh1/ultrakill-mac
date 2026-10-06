#!/usr/bin/env python3
"""Check structured buffer indices against the actual installed Metal programs."""
from pathlib import Path
import argparse,json,sys
root=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(root/'third_party/casualties-port/tools/rewrap'))
import UnityPy
from convert_metal import upgrade_buffer_bindings
from entrytools import parse_blob
from inject_metal import decompress_blob
from metal_support import parse_params
ap=argparse.ArgumentParser();ap.add_argument('--app',type=Path,default=root/'ULTRAKILL.app');args=ap.parse_args()
app=args.app.resolve()
inventory=json.loads((root/'reports/structured-buffer-shaders.json').read_text())
files=sorted(set(x['file'] for x in inventory));targets={x['shader'] for x in inventory}
checked=[];failures=[];resources=0
for file in files:
 relative=Path(file).relative_to('ULTRAKILL/ULTRAKILL_Data')
 env=UnityPy.load(str(app/'Contents/Resources/Data'/relative))
 for obj in env.objects:
  if obj.type.name!='Shader':continue
  d=obj.read_typetree();name=d['m_ParsedForm']['m_Name']
  if name not in targets:continue
  before=bytes(d['compressedBlob'])
  try:
   after=upgrade_buffer_bindings(d)
   if bytes(after['compressedBlob'])!=before:failures.append(dict(shader=name,error='Serialized buffer indices differ from MSL'))
   for entry in parse_blob(decompress_blob(d,0)):
    if entry['type'] in (23,24):continue
    try:params=parse_params(entry['raw'])
    except (ValueError,AssertionError):continue
    resources+=sum(r['kind']==2 for r in params['resources'])
   checked.append(name)
  except Exception as exc:failures.append(dict(shader=name,error=str(exc)))
missing=sorted(targets-set(checked))
report=dict(app=str(app),checked_shaders=len(checked),parameter_buffer_bindings=resources,failures=failures,missing_shaders=missing)
(root/'reports/metal-buffer-bindings-validation.json').write_text(json.dumps(report,indent=2));print(json.dumps(report,indent=2))
raise SystemExit(bool(failures or missing))
