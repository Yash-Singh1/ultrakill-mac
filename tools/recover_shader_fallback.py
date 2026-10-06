"""Recover difficult variants through Unity HLSLcc -> GLSL -> SPIR-V -> HLSL.
Bindings/semantics must be reconciled before these reference sources can replace a Unity program.
"""
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor,as_completed
from collections import Counter
import subprocess,json,re
from decompile_shader import ROOT,parse_container,parse_dcls,COMPILER
GLSL=ROOT/'third_party/casualties-port/tools/hlslcc/hlslcc-glsl'
CROSS=ROOT/'third_party/SPIRV-Cross/build/spirv-cross'
REPORT=ROOT/'reports/custom-shader-recovery.json'
def fallback(identity):
    prefix=ROOT/'shader-source/recovered'/identity
    dxbc=prefix.with_suffix('.full.dxbc')
    chunks=parse_container(prefix.with_suffix('.dxbc').read_bytes());d=parse_dcls(chunks.get(b'SHDR',chunks.get(b'SHEX')))
    stage={0:'frag',1:'vert'}[d['stage']]
    def call(args):
        r=subprocess.run([str(x) for x in args],capture_output=True,text=True,timeout=60)
        if r.returncode:raise RuntimeError(' '.join(str(x) for x in args[:2])+': '+(r.stderr+r.stdout)[-1800:])
        return r.stdout
    source=call([GLSL,dxbc]);source=re.sub(r'(?:layout\(location = \d+\)|UNITY_LOCATION\(\d+\)) (?=uniform)','',source)
    # HLSLcc's GLSL dependency map leaves some SSBO binding integers uninitialized.
    # Assign every block deterministically from its DXBC resource register.
    source=re.sub(r'layout\(std430, binding = -?\d+\)( readonly)? buffer (structured|uav)(\d+)',lambda m: 'layout(std430, binding = %d)%s buffer %s%s' % (int(m[3])+ (64 if m[2]=='uav' else 0),m[1] or '',m[2],m[3]),source)
    glsl=prefix.with_suffix('.'+stage);glsl.write_text(source)
    spv=prefix.with_suffix('.spv');call(['/opt/homebrew/bin/glslangValidator','-G','--auto-map-locations','--auto-map-bindings','-S',stage,'-o',spv,glsl])
    hlsl=prefix.with_suffix('.cross.hlsl');call([CROSS,spv,'--hlsl','--shader-model','50','--output',hlsl])
    profile={0:'ps_5_0',1:'vs_5_0'}[d['stage']]
    if identity=='c6d98d90748159204d44f6009c4f226fb09e01569de95d2e07d9bdb6addb1f68':
        call(['/opt/homebrew/bin/glslangValidator','-D','-V','-S',stage,'-e','main','--auto-map-bindings','--auto-map-locations','-o',prefix.with_suffix('.hlsl.spv'),hlsl])
        return {'status':'fallback_hlsl_spirv_compiled','fallback_profile':profile,'fallback_source':str(hlsl),'fallback_error':'','dxbc_recompile_limitation':'vkd3d optimizer timed out twice. Recovered HLSL compiles to SPIR-V with glslang.'}
    call([COMPILER,'-x','hlsl','-b','dxbc-tpf','-p',profile,'-e','main','-o',prefix.with_suffix('.cross.dxbc'),hlsl])
    return {'status':'fallback_recompiled','fallback_profile':profile,'fallback_source':str(hlsl),'fallback_error':''}
if __name__=='__main__':
    r=json.loads(REPORT.read_text());items=[(i,p) for i,p in r['programs'].items() if p['status'] not in ('recompiled','fallback_recompiled','fallback_hlsl_spirv_compiled')]
    def work(item):
        i,p=item
        try:result=fallback(i)
        except Exception as e:result={'fallback_error':str(e)}
        return i,{**p,**result}
    with ThreadPoolExecutor(max_workers=4) as pool:
        futures=[pool.submit(work,item) for item in items]
        for n,f in enumerate(as_completed(futures),1):
            i,p=f.result();r['programs'][i]=p
            if n%50==0:print(n,dict(Counter(p['status'] for p in r['programs'].values())),flush=True)
    r['summary']['status_counts']=dict(Counter(p['status'] for p in r['programs'].values()));REPORT.write_text(json.dumps(r,indent=2)+'\n');print(r['summary'])
