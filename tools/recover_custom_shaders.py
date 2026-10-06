#!/usr/bin/env python3
"""Recover every D3D shader program for game-specific shader objects, deduplicated by DXBC."""
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor, as_completed
from collections import defaultdict, Counter
import json, hashlib, struct, gc, time
from convert_metal import ROOT, UnityPy, normalize_segments, d3d_only, parse_blob, parse_code_entry, converter, parse_params, enrich_names, decompress_blob
from decompile_shader import decompile
OUT=ROOT/'shader-source/recovered'
REPORT=ROOT/'reports/custom-shader-recovery.json'

def custom(name):
    return name.startswith(('ULTRAKILL/','Hidden/ULTRAKILL/','psx/','ddShaders/','smileOS/','PostProcess/')) or name in {
        'Hidden/HeatWave_PP','Hidden/PaletteCalc','Hidden/PortalOcclusionDownample','UI/Default_UK','UI/Slider_UK',
        'Unlit/BleedSurface','Unlit/CopyCompositePortal','Unlit/DebugVertexLights','Unlit/NoSignalGlitch','Unlit/PortalShader','Unlit/Test','Unlit/TestBakedLights','Unlit/Volumetric','Unlit/ZeroStencilBuffer'}

def main():
    OUT.mkdir(parents=True,exist_ok=True)
    inventory=json.loads((ROOT/'reports/all-shaders.json').read_text())
    grouped=defaultdict(list)
    for s in inventory:
        if custom(s['name']):grouped[s['file']].append(s)
    shaders=[];programs={};aliases=0
    for source,records in grouped.items():
        env=UnityPy.load(str(ROOT.parent/source));lookup={o.path_id:o for o in env.objects if o.type.name=='Shader'}
        for record in records:
            data=normalize_segments(d3d_only(lookup[record['path_id']].read_typetree()))
            entries=parse_blob(decompress_blob(data,0));uses=[];visited=set()
            for ssidx,ss in enumerate(data['m_ParsedForm']['m_SubShaders']):
                for pidx,ps in enumerate(ss['m_Passes']):
                    for stage in ('progVertex','progFragment','progGeometry','progHull','progDomain'):
                        prog=ps.get(stage)
                        if not prog:continue
                        pbi=prog.get('m_ParameterBlobIndices',[])
                        for sp,t,i in converter.flat_subs(prog):
                            bi=sp['m_BlobIndex'];pi=pbi[t][i]
                            key=(ssidx,pidx,stage,bi,pi)
                            if key in visited:continue
                            visited.add(key)
                            e=parse_code_entry(entries[bi]['raw']);payload=e['payload'];start=payload.find(b'DXBC')
                            if start<0:raise ValueError(f'No DXBC: {source} {record["name"]} {bi}')
                            dxbc=payload[start:];size=struct.unpack_from('<I',dxbc,24)[0];dxbc=dxbc[:size]
                            identity=hashlib.sha256(dxbc).hexdigest()
                            path=OUT/identity
                            if identity not in programs:
                                path.with_suffix('.dxbc').write_bytes(dxbc)
                                programs[identity]={'sha256':identity,'bytes':len(dxbc),'first_shader':record['name'],'stage':stage}
                            names,offsets=enrich_names(ps,prog,parse_params(entries[pi]['raw']))
                            uses.append({'program':identity,'stage':stage,'subshader':ssidx,'pass':pidx,'entry':bi,'parameters':pi,'keywords':e['keywords'],'bindings':names})
                            aliases+=1
            shaders.append({**record,'programs':uses})
            print(f'Extracted {record["name"]}: {len(uses)} variants; {len(programs)} unique programs total',flush=True)
        del env,lookup,data,entries;gc.collect()
    catalog={'scope':'Game-specific prefixes plus explicit custom Hidden/Unlit/UI shaders. Stock Unity and third-party UI shaders excluded.','shader_objects':len(shaders),'shader_names':len(set(s['name'] for s in shaders)),'variant_references':aliases,'unique_programs':len(programs),'shaders':shaders}
    (OUT/'index.json').write_text(json.dumps(catalog,separators=(',',':'))+'\n')
    results={};old=json.loads(REPORT.read_text()) if REPORT.exists() else {};old=old.get('programs',{})
    def process(item):
        identity,meta=item;prefix=OUT/identity
        if identity in old and old[identity].get('status')=='recompiled':return identity,old[identity]
        try:result=decompile(prefix.with_suffix('.dxbc').read_bytes(),prefix)
        except Exception as e:result={'status':'exception','error':str(e)}
        return identity,{**meta,**result}
    with ThreadPoolExecutor(max_workers=4) as pool:
        futures=[pool.submit(process,item) for item in programs.items()]
        for future in as_completed(futures):
            identity,result=future.result();results[identity]=result
            if len(results)%100==0:
                print(f'Decompiled {len(results)}/{len(programs)} {dict(Counter(r["status"] for r in results.values()))}',flush=True)
                REPORT.write_text(json.dumps({'catalog':str(OUT/'index.json'),'programs':results,'complete':False},indent=2)+'\n')
    summary={**{k:v for k,v in catalog.items() if k!='shaders'},'status_counts':dict(Counter(r['status'] for r in results.values()))}
    REPORT.write_text(json.dumps({'summary':summary,'catalog':str(OUT/'index.json'),'programs':results,'complete':True},indent=2)+'\n')
    print(json.dumps(summary,indent=2))
if __name__=='__main__':main()
