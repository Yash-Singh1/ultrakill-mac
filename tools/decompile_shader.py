#!/usr/bin/env python3
"""Native DXBC -> register-level HLSL. No Windows DLLs or GUI processes."""
from pathlib import Path
import json, re, struct, subprocess, argparse
from convert_metal import ROOT
from dxbc2msl import parse_container, parse_dcls, build_rdef, assemble_dxbc

COMPILER=ROOT/'third_party/vkd3d-2.0/vkd3d-compiler'
DECOMPILER=ROOT/'third_party/HLSLDecompiler/portable/hlsl-decompiler'

def signature(chunk, kind):
    out=[f'// {kind} signature:', '//', '// Name Index Mask Register SysValue Format Used', '// ---- ----- ---- -------- -------- ------ ----']
    if chunk:
        count=struct.unpack_from('<I',chunk)[0]
        for i in range(count):
            off,index,system,typ,reg,mask=struct.unpack_from('<6I',chunk,8+24*i)
            name=chunk[off:].split(b'\0',1)[0].decode()
            m=''.join(c for n,c in enumerate('xyzw') if mask&(1<<n)) or 'NONE'
            if reg==0xffffffff: reg='oDepth';m='N/A'
            out.append(f'// {name} {index} {m} {reg} NONE '+{1:'uint',2:'int',3:'float'}.get(typ,'float')+f' {m}')
    else: out.append(f'// no {kind} signature')
    return out+['//','']

def reflected_asm(dxbc, assembly, names=None):
    names=names or {}; chunks=parse_container(dxbc)
    d=parse_dcls(chunks.get(b'SHDR',chunks.get(b'SHEX')))
    lines=['// Buffer Definitions:','//']
    # Keep exact register packing. Serialized author names are recorded separately.
    # Register arrays avoid unreliable reconstruction of stripped structs/matrix layouts.
    for slot,count in sorted(d['cbs'].items()):
        lines += [f'// cbuffer cb{slot}', '// {',f'//   float4 cb{slot}_data[{count}]; // Offset: 0 Size: {count*16}', '// }','//']
    lines += ['// Resource Bindings:','//','// Name Type Format Dim Bind Count','// ---- ---- ------ --- ---- -----']
    for slot in sorted(d['cbs']): lines.append(f'// cb{slot} cbuffer NA NA cb{slot} 1')
    dimnames={1:'buf',2:'1d',3:'1darray',4:'2d',5:'2darray',6:'2dMS',7:'2dMSarray',8:'3d',9:'cube',10:'cubearray'}
    for slot,dim in sorted(d['textures'].items()): lines.append(f'// tex{slot} texture float4 {dimnames[dim]} t{slot} 1')
    for slot,stride in sorted(d['structured'].items()): lines.append(f'// structured{slot} texture float4 buf t{slot} 1')
    for slot in sorted(d['samplers']): lines.append(f'// sampler{slot} sampler NA NA s{slot} 1')
    lines+=['//','']+signature(chunks.get(b'ISGN'),'Input')+signature(chunks.get(b'OSGN'),'Output')
    assembly=re.sub(r'^dcl_sampler (s\d+)\s*$',r'dcl_sampler \1, mode_default',assembly,flags=re.M)
    assembly=assembly.replace('dcl_constantBuffer','dcl_constantbuffer').replace('dcl_immediateConstantBuffer','dcl_immediateConstantBuffer')
    return '\n'.join(lines)+'\n'+assembly

def decompile(dxbc, prefix):
    prefix.parent.mkdir(parents=True,exist_ok=True)
    original=prefix.with_suffix('.dxbc');original.write_bytes(dxbc)
    chunks=parse_container(dxbc);d=parse_dcls(chunks.get(b'SHDR',chunks.get(b'SHEX')))
    source=prefix.with_suffix('.full.dxbc')
    source.write_bytes(assemble_dxbc([(b'RDEF',build_rdef(d,{},d['stage'],d['major']))]+list(chunks.items())))
    asm=prefix.with_suffix('.asm');hlsl=prefix.with_suffix('.hlsl')
    dis=subprocess.run([str(COMPILER),'-x','dxbc-tpf','-b','d3d-asm','-o',str(asm),str(original)],capture_output=True,text=True,timeout=30)
    if dis.returncode: return {'status':'disassembly_failed','error':dis.stderr[-4000:]}
    asm.write_text(reflected_asm(dxbc,asm.read_text()))
    result=subprocess.run([str(DECOMPILER),str(source),str(asm),str(hlsl)],capture_output=True,text=True,timeout=30)
    if result.returncode: return {'status':'decompilation_failed','returncode':result.returncode,'error':result.stderr[-4000:]}
    text=hlsl.read_text().replace('#define cmp -', '\n'.join('float%s cmp(bool%s v) { return -float%s(v); }' % (n,n,n) for n in ['',2,3,4]))
    hlsl.write_text(text)
    profile=re.search(r'^([vp]s_\d_\d)$',asm.read_text(),re.M).group(1)
    rebuilt=prefix.with_suffix('.rebuilt.dxbc')
    result=subprocess.run([str(COMPILER),'-x','hlsl','-b','dxbc-tpf','-p',profile,'-e','main','-o',str(rebuilt),str(hlsl)],capture_output=True,text=True,timeout=30)
    return {'status':'recompiled' if result.returncode==0 else 'recompile_failed','profile':profile,'error':result.stderr[-4000:]}

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('dxbc',type=Path);p.add_argument('output',type=Path);a=p.parse_args()
    print(json.dumps(decompile(a.dxbc.read_bytes(),a.output),indent=2))
