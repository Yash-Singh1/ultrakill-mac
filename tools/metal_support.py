"""ULTRAKILL parameter-blob support for the MIT-licensed reference converter."""
import struct
from paramblob import _rd_str
from inject_custom_metal2 import enrich_names as original_enrich

def parse_params(raw):
    def u32(off): return struct.unpack_from('<I',raw,off)[0]
    def bindings(off):
        n=u32(off); off+=4
        if n>128: raise ValueError('binding count')
        out=[]
        for _ in range(n):
            if u32(off)==0: name=''; off+=4
            else: name,off=_rd_str(raw,off)
            kind=u32(off); off+=4
            if kind not in (0,1,2,3,4): raise ValueError('binding kind')
            count=3 if kind==0 else 2
            slot_offset=off
            values=struct.unpack_from('<'+'I'*count,raw,off); off+=4*count
            out.append(dict(name=name,kind=kind,values=values,slot_offset=slot_offset))
        if off!=len(raw): raise ValueError('binding trailing bytes')
        return out
    off=24; cbs=[]
    while True:
        try:
            resources=bindings(off)
            break
        except (ValueError,struct.error): pass
        name,off=_rd_str(raw,off); size=u32(off);off+=4
        cb=dict(name=name,size=size,vec=[],mat=[])
        for group in range(2):
            count=u32(off);off+=4
            if count>512: raise ValueError('member count')
            for _ in range(count):
                nm,off=_rd_str(raw,off); fields=struct.unpack_from('<6I',raw,off);off+=24
                if group == 1:
                    # Struct arrays carry four header words, followed by members.
                    off -= 24
                    offset,array_size,stride,member_count=struct.unpack_from('<4I',raw,off);off+=16
                    for _ in range(member_count):
                        member_name,off=_rd_str(raw,off)
                        off+=24
                    # Engine-filled instancing buffers can remain opaque float4
                    # arrays. DXBC addresses their original byte offsets.
                    continue
                member=dict(name=nm,type=fields[0],rows=fields[1],dim=fields[2],arraySize=fields[4],f4=fields[4],offset=fields[5])
                cb['mat' if fields[1]>1 else 'vec'].append(member)
        cbs.append(cb)
    textures=[dict(name=r['name'],ms=0,index=r['values'][0],sampler=r['values'][1],dim=r['values'][2]) for r in resources if r['kind']==0]
    return dict(wtype=u32(4),head=raw[8:24],cbs=cbs,textures=textures,resources=resources)

def enrich_names(ps, prog, p):
    names,unused=original_enrich(ps,prog,p)
    byname={c['name']:c for c in p['cbs']}
    from inject_custom_metal import build_names
    # Common definitions can be partial and have only variant-specific bindings.
    augmented=dict(prog)
    common=dict(prog.get('m_CommonParameters',{}))
    idx2name={i:n for n,i in ps.get('m_NameIndices',[])}
    name2idx={n:i for i,n in idx2name.items()}
    bindings={b['m_NameIndex']:b for b in common.get('m_ConstantBufferBindings',[])}
    bindings.update({name2idx[r['name']]:dict(m_NameIndex=name2idx[r['name']],m_Index=r['values'][0]) for r in p['resources'] if r['kind']==1 and r['name'] in name2idx})
    if '$Globals' in name2idx and '$Globals' in byname:
        bindings.setdefault(name2idx['$Globals'],dict(m_NameIndex=name2idx['$Globals'],m_Index=0))
    common['m_ConstantBufferBindings']=list(bindings.values())
    augmented['m_CommonParameters']=common
    names=build_names(ps,augmented)
    for texture in p['textures']:
        names['textures'][texture['index']]=texture['name']
        if texture['sampler'] != 0xFFFFFFFF:
            names['samplers'].setdefault(texture['sampler'],'sampler'+texture['name'])
    for r in p['resources']:
        if r['kind']==1:
            cb=byname.get(r['name'])
            if cb: names['cbs'].setdefault(r['values'][0],dict(name=cb['name'],size=cb['size'],members=[]))
        elif r['kind']==2:
            names.setdefault('buffers',{})[r['values'][0]]=r['name']
    # $Globals is bound implicitly on D3D; other buffers have explicit bindings.
    if '$Globals' in byname and not any(c['name']=='$Globals' for c in names['cbs'].values()):
        names['cbs'][0]=dict(name='$Globals',size=byname['$Globals']['size'],members=[])
    for cbdef in names['cbs'].values():
        cb=byname.get(cbdef['name'])
        if not cb: continue
        have={m[0] for m in cbdef['members']}
        for v in cb['vec']+cb['mat']:
            if v['name'] in have: continue
            cls=3 if v['rows']>1 else 0 if v['dim']==1 else 1
            ty={0:3,1:2,2:1}.get(v['type'],3)
            cbdef['members'].append((v['name'],v['offset'],cls,ty,v['rows'],v['dim'],v['arraySize']))
    offsets={slot:({m[0]:m[1] for m in cb['members']},cb['size']) for slot,cb in names['cbs'].items()}
    return names,offsets
