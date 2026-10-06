#!/usr/bin/env python3
"""Check copied asset files for Metal programs using their input inventory."""
from pathlib import Path
import argparse, gc, json
import UnityPy
ROOT=Path(__file__).resolve().parents[1]

def audit(app, inventory=None):
    inventory=inventory or app/'Contents/Resources/shader-inventory.json'
    if not inventory.exists():inventory=ROOT/'reports/all-shaders.json'
    rows=json.loads(inventory.read_text());count=0;missing=[]
    data=app/'Contents/Resources/Data'
    for file in sorted({r['file'] for r in rows}):
        relative=Path(file)
        if relative.parts[:2]==('ULTRAKILL','ULTRAKILL_Data'):relative=relative.relative_to('ULTRAKILL/ULTRAKILL_Data')
        env=UnityPy.load(str(data/relative))
        for obj in env.objects:
            if obj.type.name!='Shader':continue
            tree=obj.read_typetree();count+=1
            if 14 not in tree.get('platforms',[]):missing.append({'file':str(relative),'path_id':obj.path_id,'name':tree['m_ParsedForm']['m_Name']})
        del env;gc.collect()
    return {'app':str(app),'shaders':count,'missing_metal':missing}

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--app',type=Path,default=ROOT/'ULTRAKILL.app');p.add_argument('--inventory',type=Path);p.add_argument('--report',type=Path,default=ROOT/'reports/metal-build-audit.json');a=p.parse_args()
    report=audit(a.app.resolve(),a.inventory);a.report.write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(report,indent=2));raise SystemExit(bool(report['missing_metal']))
