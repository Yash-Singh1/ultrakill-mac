"""Recover all shipped custom compute kernels from their original SPIR-V backend."""
from pathlib import Path
import pickle,json,subprocess,hashlib
from decompile_shader import ROOT,COMPILER
CROSS=ROOT/'third_party/SPIRV-Cross/build/spirv-cross';OUT=ROOT/'shader-source/recovered/compute'
items=json.loads((ROOT/'reports/compute-shaders.json').read_text());results=[]
for item in items:
    data=pickle.loads((ROOT/item['tree']).read_bytes());byrenderer={v['targetRenderer']:v for v in data['variants']}
    for kernel in byrenderer[21]['kernels']:
        for i,variant in enumerate(kernel['uniqueVariants']):
            prefix=OUT/data['m_Name']/(kernel['name']+'-'+str(i));prefix.parent.mkdir(parents=True,exist_ok=True)
            spv=prefix.with_suffix('.spv');spv.write_bytes(bytes(variant['code']))
            for renderer,suffix in [(2,'.dxbc'),(17,'.glsl')]:
                k=next(k for k in byrenderer[renderer]['kernels'] if k['name']==kernel['name']);prefix.with_suffix(suffix).write_bytes(bytes(k['uniqueVariants'][i]['code']))
            for backend,suffix in [('hlsl','.hlsl'),('msl','.metal')]:
                args=[str(CROSS),str(spv),'--'+backend,'--output',str(prefix.with_suffix(suffix))]
                if backend=='hlsl':args+=['--shader-model','50']
                subprocess.run(args,check=True,capture_output=True)
            hlsl=prefix.with_suffix('.hlsl');dxbc=prefix.with_suffix('.rebuilt.dxbc')
            subprocess.run([str(COMPILER),'-x','hlsl','-b','dxbc-tpf','-p','cs_5_0','-e','main','-o',str(dxbc),str(hlsl)],check=True,capture_output=True)
            subprocess.run(['xcrun','metal','-c',str(prefix.with_suffix('.metal')),'-o',str(prefix.with_suffix('.air'))],check=True,capture_output=True)
            metadata={k:v for k,v in variant.items() if k!='code'}
            metadata['constantBuffers']=byrenderer[21]['constantBuffers']
            prefix.with_suffix('.json').write_text(json.dumps(metadata,indent=2)+'\n')
            results.append({'shader':data['m_Name'],'kernel':kernel['name'],'variant':i,'source':str(hlsl.relative_to(ROOT)), 'thread_group_size':variant['threadGroupSize'],'original_spirv_sha256':hashlib.sha256(spv.read_bytes()).hexdigest(),'hlsl_dxbc_compiled':True,'metal_compiled':True})
report={'complete':True,'shaders':len(items),'kernels':len(results),'source_backend':'Original shipped Vulkan SPIR-V. HLSL and Metal from SPIRV-Cross. Separate original D3D/OpenGL reference files.','runtime_installed':False,'results':results}
(ROOT/'reports/compute-shader-recovery.json').write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(report,indent=2))
