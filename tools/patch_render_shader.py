"""Temporary shader diagnostics applied only to the render probe app."""
from pathlib import Path
import sys,pickle,struct,lz4.block
root=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(root/'third_party/casualties-port/tools/rewrap'))
import UnityPy
from entrytools import parse_blob,parse_code_entry,build_code_entry,serialize_blob
from inject_metal import decompress_blob
from inject_custom_metal import build_metal_payload
sys.path.insert(0,str(root/'tools'))
from metal_support import parse_params
mode=sys.argv[1]
d=pickle.loads((root/'runtime/metal-shaders/831dce55e9d3a3e1e55c.pkl').read_bytes())
entries=parse_blob(decompress_blob(d,0))
for i,e in enumerate(entries):
    if mode == 'bindings':
        if e['type'] in (23,24): continue
        try: params = parse_params(e['raw'])
        except (ValueError,struct.error,AssertionError): continue
        raw = bytearray(e['raw'])
        for resource in params['resources']:
            if resource['kind'] == 2:
                struct.pack_into('<I',raw,resource['slot_offset'],resource['values'][0]+2)
        e['raw'] = bytes(raw)
        continue
    if e['type'] not in (23,24):continue
    p=parse_code_entry(e['raw']);payload=p['payload'];start=struct.unpack_from('<I',payload,12)[0]
    source=payload[start:].split(b'\0',1)[0].decode()
    if mode=='nolights':
        if e['type'] != 23: continue
        import re
        source=re.sub(r'(u_xlatu\d+ = )as_type<uint>\(input.TEXCOORD3\) >> 0x10u;',r'\g<1>0u;',source)
        e['raw']=build_code_entry(e['type'],p['stats'],p['keywords'],build_metal_payload(source,len(payload)),p['trailing'])
        continue
    if e['type'] != 24: continue
    if mode=='lighting': expr='float4(input.COLOR0.xyz, 1.0)'
    elif mode=='atlas':expr='float4(input.TEXCOORD7.zw - input.TEXCOORD7.xy, 0.0, 1.0)'
    elif mode=='blendatlas':expr='float4(input.TEXCOORD8.zw - input.TEXCOORD8.xy, 0.0, 1.0)'
    elif mode=='uv':expr='float4(fract(input.TEXCOORD0.xy / input.TEXCOORD1.ww), 0.0, 1.0)'
    elif mode=='texture':expr='_MainTex.sample(sampler0, fract(input.TEXCOORD0.xy / input.TEXCOORD1.ww) * (input.TEXCOORD7.zw - input.TEXCOORD7.xy) + input.TEXCOORD7.xy, level(0.0))'
    elif mode=='restore':continue
    else:raise ValueError(mode)
    source=source.replace('    return output;',f'    output.SV_Target0 = {expr};\n    return output;')
    e['raw']=build_code_entry(e['type'],p['stats'],p['keywords'],build_metal_payload(source,len(payload)),p['trailing'])
raw=serialize_blob(entries);comp=lz4.block.compress(raw,mode='high_compression',store_size=False)
d.update(offsets=[[0]],compressedLengths=[[len(comp)]],decompressedLengths=[[len(raw)]],compressedBlob=comp)
relative=Path('Contents/Resources/Data/StreamingAssets/aa/StandaloneWindows64/assets_assets_assets/shaders.bundle')
bundle=root/'test-builds/BlueProbe.app'/relative
env=UnityPy.load(str(root/'ULTRAKILL.app'/relative))
for obj in env.objects:
    if obj.type.name=='Shader' and obj.read_typetree()['m_ParsedForm']['m_Name']==d['m_ParsedForm']['m_Name']:obj.save_typetree(d)
new=bundle.with_suffix('.pending');new.write_bytes(env.file.save());new.replace(bundle)
print(mode)
