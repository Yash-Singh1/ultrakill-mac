"""Check complete recovery coverage and rerun representative decompiler regressions."""
from pathlib import Path
from collections import Counter
from concurrent.futures import ThreadPoolExecutor
import hashlib,json,subprocess,re
from decompile_shader import ROOT,decompile
from recover_shader_fallback import fallback
report=json.loads((ROOT/'reports/custom-shader-recovery.json').read_text());index=json.loads((ROOT/'shader-source/recovered/index.json').read_text())
valid={'recompiled','fallback_recompiled','fallback_hlsl_spirv_compiled'}
assert len(report['programs'])==index['unique_programs']
assert all(p['status'] in valid for p in report['programs'].values())
refs=0
for s in index['shaders']:
    for use in s['programs']:
        i=use['program'];p=report['programs'][i];prefix=ROOT/'shader-source/recovered'/i
        assert hashlib.sha256(prefix.with_suffix('.dxbc').read_bytes()).hexdigest()==i
        hlsl=prefix.with_suffix('.hlsl' if p['status']=='recompiled' else '.cross.hlsl')
        assert hlsl.is_file() and re.search(r'\bmain\s*\(',hlsl.read_text());refs+=1
ids=[
 '07f2f87d567c15074b0fdf2765fce54c558b92eb460ca1f5f5dc297e4ca54fbb', # 2D resinfo
 'ecdccfc5242eda6ac22dd18c4aee8925d210e45c0d566243b6e4d9d79cb082dd', # cube resinfo
 '8dc2c81abf29c14b28acdb4fab3069096787238c56c4ba8ae028dfb6f6632798', # no resources
 'b5cbe368cc406f5754dba666153e8cd67eb759a02e4aded8bfc3d29e2d726c8d', # depth output
 'af0bbaad7129938161fa9064f7e0eb6a67624911450d43de44ae4ce40288180d', # structured + UAV
 '4449a70a264cbc0c1f63b0de61248214fcb53aec5ba54f6db2fe0f8be9621420', # packed vertex outputs
 '40908c4bd24a4bac07ca2206f280fc4ffca42c52dff53cb0d0ffbdbbebeefb6d', # packed fragment inputs
 'c6d98d90748159204d44f6009c4f226fb09e01569de95d2e07d9bdb6addb1f68', # palette, glslang
]
results=[]
for i in ids:
    p=report['programs'][i]
    if p['status']=='recompiled':
        out=ROOT/'reports/decompiler-regressions'/i;result=decompile((ROOT/'shader-source/recovered'/i).with_suffix('.dxbc').read_bytes(),out)
    else:result=fallback(i)
    assert result['status'] in valid,(i,result)
    results.append({'program':i,'shader':p['first_shader'],'status':result['status']})
compute=json.loads((ROOT/'reports/compute-shader-recovery.json').read_text());assert compute['complete'] and all(k['hlsl_dxbc_compiled'] and k['metal_compiled'] for k in compute['results'])
result={'passed':True,'graphics_programs':len(report['programs']),'variant_references_checked':refs,'compute_kernels':compute['kernels'],'regressions':results,'scope':'Full archive hash/coverage checks, all recorded compilation results, representative recompilation regressions. GPU equivalence is tested separately for the cage only.'}
(ROOT/'reports/shader-recovery-validation.json').write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result,indent=2))
