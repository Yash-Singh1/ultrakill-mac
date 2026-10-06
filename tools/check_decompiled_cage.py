from pathlib import Path
import sys,subprocess,json,re
from cage_source_check import ROOT,namespace
import convert_metal
from dxbc2msl import dxbc_to_msl
from inject_custom_metal2 import widen_fragment_outputs

def prepare():
    source='#include <metal_stdlib>\n#include <metal_texture>\nusing namespace metal;\nconstexpr sampler fixture_repeat(filter::nearest,address::repeat);\n'
    for stage in ['vertex','fragment']:
        rebuilt=ROOT/('reports/decompiler-test/cage-'+stage+'.metal')
        msl,_=dxbc_to_msl(rebuilt.with_suffix('.rebuilt.dxbc').read_bytes(),{})
        if stage=='fragment':msl=widen_fragment_outputs(msl)
        rebuilt.write_text(msl)
        original=ROOT/('reports/cage-vertex.metal' if stage=='vertex' else 'reports/cage-fragment-9483.metal')
        for label,path in [('original',original),('rebuilt',rebuilt)]:
            name=label+'_'+stage;source+=namespace(path,name)
            source+=f'kernel void {name}_check(constant float4* fixtures [[buffer(0)]], device float4* results [[buffer(1)]], texture2d<float> texture [[texture(0)]], uint id [[thread_position_in_grid]]) {{\nusing namespace {name};\nconstant float4* row=fixtures+id*512;\n'
            if stage=='vertex':
                source+='Mtl_VertexIn input;input.POSITION0=row[256];input.COLOR0=row[257];input.TEXCOORD0=row[258].xy;input.NORMAL0=row[259].xyz;\n'
                types=['VGlobals_Type','UnityPerCamera_Type','UnityLighting_Type','UnityPerDraw_Type','UnityPerFrame_Type','UnityFog_Type','StandardProperties_Type'] if label=='original' else [f'cb{i}_Type' for i in range(7)]
                args=[f'*reinterpret_cast<constant {ty}*>(row+{offset})' for ty,offset in zip(types,[0,32,64,128,160,192,224])]
                source+='auto output=xlatMtlMain('+','.join(args+['input'])+');\n'
                for i,field in enumerate(['mtl_Position','COLOR0','COLOR2','TEXCOORD0','TEXCOORD1','TEXCOORD2']):
                    value='float4(output.TEXCOORD2,0)' if field=='TEXCOORD2' else 'output.'+field
                    source+=f'results[id*6+{i}]={value};\n'
            else:
                source+='Mtl_FragmentIn input;input.COLOR0=row[260];input.COLOR2=row[261];input.TEXCOORD0=row[262];input.TEXCOORD1=row[263];\n'
                ty='FGlobals_Type' if label=='original' else 'cb0_Type'
                source+='auto output=xlatMtlMain('+f'*reinterpret_cast<constant {ty}*>(row),fixture_repeat,texture,input'+');\n'
                source+='results[id*2]=output.SV_Target0;results[id*2+1]=output.SV_Target1;\n'
            source+='}\n'
    path=ROOT/'reports/decompiler-test/cage-comparison.metal';path.write_text(source)
    reports=[]
    for texture in sorted((ROOT/'reports').glob('tile_fancypanel*.rgba')):
        out=ROOT/('reports/decompiler-test/'+texture.stem+'.json')
        result=subprocess.run([str(ROOT/'runtime-state/cage_source_check'),str(path),str(texture),str(out)],capture_output=True,text=True)
        if not out.exists():raise RuntimeError(result.stderr)
        data=json.loads(out.read_text());reports.append(data)
    summary={'passed':all(r['passed'] for r in reports),'textures':len(reports),'fixtures_per_stage':len(reports)*4096,'exact_components':sum(f['exact_components'] for r in reports for stage in ['vertex','fragment'] for f in r[stage]),'components':sum(f['components'] for r in reports for stage in ['vertex','fragment'] for f in r[stage]),'failures':sum(f['outside_tolerance'] for r in reports for stage in ['vertex','fragment'] for f in r[stage]),'scope':'Original DXBC-derived Metal versus automatically decompiled HLSL rebuilt to DXBC and translated to Metal. No Windows GPU execution. Does not validate the full shader collection.'}
    (ROOT/'reports/decompiled-cage-validation.json').write_text(json.dumps(summary,indent=2)+'\n');print(json.dumps(summary))
    if not summary['passed']:sys.exit(1)
if __name__=='__main__':prepare()
