#!/usr/bin/env python3
"""Inventory the actual input build, including shaders embedded in scene bundles."""
from pathlib import Path
import argparse, json, gc, re
import UnityPy
from game_source import resolve_source

def scan(source, output):
    data=source/'ULTRAKILL_Data'
    files=sorted(set(data.rglob('*.bundle')) | set(data.glob('*.assets')) | {data/'globalgamemanagers'} | {p for p in data.glob('level*') if re.fullmatch(r'level\d+',p.name)} | {p for p in (data/'Resources').glob('*') if p.is_file()})
    rows=[]
    for file in files:
        env=UnityPy.load(str(file));found=0
        for obj in env.objects:
            if obj.type.name!='Shader':continue
            tree=obj.read_typetree()
            rows.append({'file':str(file.relative_to(data)),'path_id':obj.path_id,'name':tree['m_ParsedForm']['m_Name'],'platforms':tree.get('platforms',[])})
            found+=1
        print(str(file.relative_to(data)),found,'shaders',flush=True)
        del env;gc.collect()
    output.write_text(json.dumps(rows,indent=2)+'\n')
    return rows

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--source',type=Path,required=True);p.add_argument('--output',type=Path,required=True);a=p.parse_args()
    scan(resolve_source(a.source),a.output)
