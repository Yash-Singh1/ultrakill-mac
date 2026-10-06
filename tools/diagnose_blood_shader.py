from pathlib import Path
import sys,pickle,struct,re
import convert_metal as c
mode=sys.argv[1];d=pickle.loads((c.ROOT/'runtime/metal-shaders/a51f2eda601ec3f7734d.pkl').read_bytes())
entries=c.parse_blob(c.decompress_blob(d,0))
for e in entries:
 if e['type'] not in (23,24):continue
 p=c.parse_code_entry(e['raw']);old=p['payload'];start=struct.unpack_from('<I',old,12)[0];s=old[start:].split(b'\0',1)[0].decode()
 if e['type']==23 and 'PROCEDURAL_INSTANCING_ON' in p['keywords']:
  if mode=='constant':
   for var,value in [('u_xlat1','float4(39.7,-2.0,344.72,0)'),('u_xlat2','float4(0,1,0,1)'),('u_xlat3','float4(1,0,0,0)'),('u_xlat4','float4(0,1,0,0)'),('u_xlat5','float4(0,0,1,0)'),('u_xlat6','float4(0,0,0,1)')]:
    s=re.sub(r'    '+var+r' = float4\(as_type<float>\((?:instance|parent)Buffer.*?;','    '+var+' = '+value+';',s)
  elif mode=='base':s=s.replace('u_xlatu0.x = mtl_InstanceID + uint(UnityDrawCallInfo.unity_BaseInstanceID);','u_xlatu0.x = mtl_InstanceID;')
  elif mode=='slots':
   pass
 if e['type']==24 and mode=='constant':
  at=s.index('Mtl_FragmentOut output');at=s.index(';',at)+1;s=s[:at]+'\n    output.SV_Target0 = float4(1,0,0,1); return output;\n'+s[at:]
 e['raw']=c.build_code_entry(e['type'],p['stats'],p['keywords'],c.converter.build_metal_payload(s,len(old)),p['trailing'])
if mode=='forcevariant':
 for ss in d['m_ParsedForm']['m_SubShaders']:
  for ps in ss['m_Passes']:
   prog=ps['progVertex']
   for sp,t,i in c.converter.flat_subs(prog):
    sp['m_BlobIndex']=5;prog['m_ParameterBlobIndices'][t][i]=1
raw=c.serialize_blob(entries);comp=c.lz4.block.compress(raw,store_size=False);d.update(offsets=[[0]],compressedLengths=[[len(comp)]],decompressedLengths=[[len(raw)]],compressedBlob=comp)
b=c.ROOT/'test-builds/BlueProbe.app/Contents/Resources/Data/StreamingAssets/aa/StandaloneWindows64/assets_assets_assets/shaders.bundle';env=c.UnityPy.load(str(b))
for o in env.objects:
 if o.type.name=='Shader' and o.read_typetree()['m_ParsedForm']['m_Name']==d['m_ParsedForm']['m_Name']:o.save_typetree(d)
b.write_bytes(env.file.save());print(mode)
