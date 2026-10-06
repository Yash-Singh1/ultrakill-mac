#!/usr/bin/env python3
"""Convert copied Unity shaders to Metal, preserving the source installation."""
from pathlib import Path
import sys,json,pickle,struct,hashlib,traceback,argparse,re
ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'third_party/casualties-port/tools/rewrap'))
import UnityPy,lz4.block
import inject_custom_metal2 as converter
from entrytools import serialize_blob,parse_blob,parse_code_entry,build_code_entry
from inject_metal import decompress_blob
from metal_support import parse_params,enrich_names
from metal_pilot import d3d_only
from game_source import resolve_source
from clip_distance import repair_clip_distances
import inline_samplers
converter.parse_params_entry=parse_params
converter.enrich_names=enrich_names

def normalize_segments(d):
    segments=[]
    blob=bytes(d['compressedBlob'])
    for off,c,rawsize in zip(d['offsets'][0],d['compressedLengths'][0],d['decompressedLengths'][0]):
        segments.append(lz4.block.decompress(blob[off:off+c],uncompressed_size=rawsize))
    count=struct.unpack_from('<I',segments[0])[0];entries=[]
    for i in range(count):
        off,n,seg=struct.unpack_from('<III',segments[0],4+12*i)
        raw=segments[seg][off:off+n]
        entries.append(dict(raw=raw,seg=0))
    raw=serialize_blob(entries);comp=lz4.block.compress(raw,store_size=False)
    d.update(offsets=[[0]],compressedLengths=[[len(comp)]],decompressedLengths=[[len(raw)]],compressedBlob=comp)
    return d

def upgrade_buffer_bindings(d):
    if d.get('_MacPortBufferABI') == 3: return d
    entries=parse_blob(decompress_blob(d,0));changed=False
    program_bindings={};patched={}
    for ss in d['m_ParsedForm'].get('m_SubShaders',[]):
        for ps in ss.get('m_Passes',[]):
            for key in ('progVertex','progFragment'):
                prog=ps.get(key)
                if not prog: continue
                pbi=prog.get('m_ParameterBlobIndices',[])
                for sp,t,i in converter.flat_subs(prog):
                    bi=sp['m_BlobIndex'];pi=pbi[t][i]
                    if bi not in program_bindings:
                        payload=parse_code_entry(entries[bi]['raw'])['payload']
                        start=struct.unpack_from('<I',payload,12)[0]
                        source=payload[start:].split(b'\0',1)[0].decode()
                        program_bindings[bi]={name:int(slot) for name,slot in re.findall(r'\*\s*(\w+)\s*\[\[\s*buffer\((\d+)\)',source)}
                    bindings=program_bindings[bi]
                    params=parse_params(entries[pi]['raw']);raw=bytearray(entries[pi]['raw'])
                    for resource in params['resources']:
                        if resource['kind'] != 2: continue
                        slot=bindings[resource['name']]
                        address=(pi,resource['slot_offset'])
                        if address in patched and patched[address] != slot:
                            raise ValueError('Conflicting shared buffer parameter bindings')
                        patched[address]=slot
                        if resource['values'][0] != slot:
                            struct.pack_into('<I',raw,resource['slot_offset'],slot);changed=True
                    entries[pi]['raw']=bytes(raw)
    if changed:
        raw=serialize_blob(entries);comp=lz4.block.compress(raw,mode='high_compression',store_size=False)
        d.update(offsets=[[0]],compressedLengths=[[len(comp)]],decompressedLengths=[[len(raw)]],compressedBlob=comp)
    d['_MacPortBufferABI']=3
    return d

def upgrade_color_outputs(d):
    if d.get('_MacPortColorABI') == 2: return d
    entries=parse_blob(decompress_blob(d,0));changed=False
    for e in entries:
        if e['type'] != 24: continue
        pe=parse_code_entry(e['raw']);p=pe['payload']
        start=struct.unpack_from('<I',p,12)[0]
        old=p[start:].split(b'\0',1)[0].decode()
        new=converter.widen_fragment_outputs(old)
        if new==old: continue
        payload=converter.build_metal_payload(new,len(p))
        e['raw']=build_code_entry(e['type'],pe['stats'],pe['keywords'],payload,pe['trailing']);changed=True
    if changed:
        raw=serialize_blob(entries);comp=lz4.block.compress(raw,mode='high_compression',store_size=False)
        d.update(offsets=[[0]],compressedLengths=[[len(comp)]],decompressedLengths=[[len(raw)]],compressedBlob=comp)
    d['_MacPortColorABI']=2
    return d

def upgrade_texture_dimensions(d, source_object, name):
    if d.get('_MacPortTextureDimensionsABI') == 2: return d
    entries=parse_blob(decompress_blob(d,0));affected=set()
    # This exact sequence is the broken Shader Model 4 resinfo translation.
    bad=re.compile(r'(\w+)\.x = (?:uint\(0\)|0\.0);\s*\1\.y = (?:uint\(0\)|0\.0);\s*\1\.z = (?:uint\(0\)|0\.0);\s*\1\.w = \w+\.get_num_mip_levels\(\);')
    for i,e in enumerate(entries):
        if e['type'] not in (23,24): continue
        p=parse_code_entry(e['raw'])['payload'];start=struct.unpack_from('<I',p,12)[0]
        if bad.search(p[start:].split(b'\0',1)[0].decode()): affected.add(i)
    if affected:
        print(f'{name}: rebuilding {len(affected)} texture-dimension programs',flush=True)
        source=normalize_segments(d3d_only(source_object.read_typetree()))
        converter.rewrap_shader_v2(source,name,False,only_entries=affected)
        rebuilt=parse_blob(decompress_blob(source,0))
        for i in affected: entries[i]=rebuilt[i]
        raw=serialize_blob(entries);comp=lz4.block.compress(raw,mode='high_compression',store_size=False)
        d.update(offsets=[[0]],compressedLengths=[[len(comp)]],decompressedLengths=[[len(raw)]],compressedBlob=comp)
    d['_MacPortTextureDimensionsABI']=2
    return d

def upgrade_transparent_sampler(d):
    """Keep Master's authored point/repeat sampler inside its Metal code.

    All D3D variants use the pass's inline sampler, rather than a sampler
    bound to _MainTex. HLSLcc emits a dynamic sampler argument. A constexpr
    sampler preserves the texture's point/repeat behavior without depending
    on a sampler left in that slot by a previous draw.
    """
    if d['m_ParsedForm']['m_Name'] != 'ULTRAKILL/Master': return d
    if d.get('_MacPortTransparentSamplerABI') == 2: return d
    entries=parse_blob(decompress_blob(d,0));affected=set()
    for ss in d['m_ParsedForm']['m_SubShaders']:
        for ps in ss['m_Passes']:
            prog=ps.get('progFragment',{})
            samplers=prog.get('m_CommonParameters',{}).get('m_Samplers',[])
            if not any(s['bindPoint']==0 and s['sampler']==0 for s in samplers): continue
            for sp,t,i in converter.flat_subs(prog):
                idx=sp['m_BlobIndex'];pe=parse_code_entry(entries[idx]['raw'])
                # The inline sampler belongs to the entire Master pass. Opaque
                # variants also use it and must not inherit a previous draw's
                # bound sampler, which can clamp away repeating wall textures.
                payload=pe['payload'];start=struct.unpack_from('<I',payload,12)[0]
                source=payload[start:].split(b'\0',1)[0].decode()
                declaration=r'\bsampler\s+sampler0\s*\[\[\s*sampler\s*\(0\)\s*\]\]\s*,'
                source,n=re.subn(declaration,'',source)
                if not n: continue
                source=source.replace('using namespace metal;',
                    'using namespace metal;\nconstexpr sampler sampler0(coord::normalized, address::repeat, filter::nearest);',1)
                entries[idx]['raw']=build_code_entry(entries[idx]['type'],pe['stats'],pe['keywords'],
                    converter.build_metal_payload(source,len(payload)),pe['trailing'])
                affected.add(idx)
    if affected:
        raw=serialize_blob(entries);comp=lz4.block.compress(raw,mode='high_compression',store_size=False)
        d.update(offsets=[[0]],compressedLengths=[[len(comp)]],decompressedLengths=[[len(raw)]],compressedBlob=comp)
    d['_MacPortTransparentSamplerABI']=2
    d['_MacPortTransparentSamplerEntries']=sorted(affected)
    return d

def upgrade_clip_distances(d, source_object):
    if d.get('_MacPortClipDistanceABI') == 1: return d
    entries=parse_blob(decompress_blob(d,0)); original=None; affected=[]
    for index, entry in enumerate(entries):
        if entry['type'] != 23: continue
        pe=parse_code_entry(entry['raw']);payload=pe['payload']
        start=struct.unpack_from('<I',payload,12)[0]
        old=payload[start:].split(b'\0',1)[0].decode()
        if '[[ clip_distance ]]' not in old: continue
        if original is None:
            source=normalize_segments(d3d_only(source_object.read_typetree()))
            original=parse_blob(decompress_blob(source,0))
        source_entry=parse_code_entry(original[index]['raw'])
        if source_entry['keywords'] != pe['keywords']:
            raise ValueError('Clip-distance source variant does not match converted variant')
        dxbc=source_entry['payload'];dxbc=dxbc[dxbc.index(b'DXBC'):]
        new=repair_clip_distances(old,dxbc)
        if new == old: continue
        entries[index]['raw']=build_code_entry(entry['type'],pe['stats'],pe['keywords'],
            converter.build_metal_payload(new,len(payload)),pe['trailing'])
        affected.append(index)
    if affected:
        raw=serialize_blob(entries);comp=lz4.block.compress(raw,mode='high_compression',store_size=False)
        d.update(offsets=[[0]],compressedLengths=[[len(comp)]],decompressedLengths=[[len(raw)]],compressedBlob=comp)
    d['_MacPortClipDistanceABI']=1
    d['_MacPortClipDistanceEntries']=affected
    return d

def upgrade_inline_samplers(d):
    if d.get('_MacPortInlineSamplersABI') == inline_samplers.ABI: return d
    entries=parse_blob(decompress_blob(d,0)); contexts={}; affected=[]
    for ss in d['m_ParsedForm'].get('m_SubShaders',[]):
        for ps in ss.get('m_Passes',[]):
            for key in ('progVertex','progFragment'):
                prog=ps.get(key,{})
                for sp,t,i in converter.flat_subs(prog):
                    index=sp['m_BlobIndex']
                    pi=prog['m_ParameterBlobIndices'][t][i]
                    states=inline_samplers.fixed_bindings(prog,parse_params(entries[pi]['raw']))
                    if index in contexts and contexts[index] != states:
                        raise ValueError(f'Conflicting fixed sampler states for shared program {index}')
                    contexts[index]=states
    for index,states in contexts.items():
        if not states: continue
        entry=entries[index];pe=parse_code_entry(entry['raw']);payload=pe['payload']
        start=struct.unpack_from('<I',payload,12)[0]
        old=payload[start:].split(b'\0',1)[0].decode()
        new=inline_samplers.repair(old,states)
        if new == old: continue
        entry['raw']=build_code_entry(entry['type'],pe['stats'],pe['keywords'],
            converter.build_metal_payload(new,len(payload)),pe['trailing'])
        affected.append(index)
    if affected:
        raw=serialize_blob(entries);comp=lz4.block.compress(raw,mode='high_compression',store_size=False)
        d.update(offsets=[[0]],compressedLengths=[[len(comp)]],decompressedLengths=[[len(raw)]],compressedBlob=comp)
    d['_MacPortInlineSamplersABI']=inline_samplers.ABI
    d['_MacPortInlineSamplersEntries']=affected
    return d

def upgrade_recovered_cage(d, source_object):
    """Use the verified readable source for the two base cage programs."""
    if d['m_ParsedForm']['m_Name'] != 'ULTRAKILL/Master': return d
    validation=ROOT/'reports/cage-source-validation.json'
    if not validation.exists(): return d
    verified=json.loads(validation.read_text())
    source_hash=hashlib.sha256((ROOT/'shader-source/Cage.hlsl').read_bytes()).hexdigest()
    if not verified.get('passed') or verified['source_sha256'] != source_hash:
        raise ValueError('Rebuild and validate the changed cage source before converting it')
    sources={stage:(ROOT/('reports/cage-reconstructed-'+stage+'.metal')).read_bytes() for stage in ('vertex','fragment')}
    hashes={stage:hashlib.sha256(source).hexdigest() for stage,source in sources.items()}
    if hashes != verified['metal_sha256']:
        raise ValueError('Recovered cage Metal differs from the verified programs')
    fingerprint=hashlib.sha256((source_hash+json.dumps(hashes,sort_keys=True)).encode()).hexdigest()
    if d.get('_MacPortRecoveredCageSource') == fingerprint: return d
    # The hand-recovered source was validated against the original installation.
    # A depot may contain a newer Master, even when its keywords are unchanged.
    # Only reuse it for byte-identical DXBC; otherwise keep the freshly translated
    # programs and the point/repeat sampler correction above.
    reference=ROOT/'reports/cage-reference-d3d.pkl'
    if not reference.exists():
        raise ValueError('Missing validated cage reference DXBC')
    original_entries=parse_blob(decompress_blob(pickle.loads(reference.read_bytes()),0))
    source_entries=parse_blob(decompress_blob(normalize_segments(d3d_only(source_object.read_typetree())),0))
    stages=[]
    for stage,old_index,kind in [('vertex',5083,23),('fragment',9483,24)]:
        original=parse_code_entry(original_entries[old_index]['raw'])
        matches=[i for i,e in enumerate(source_entries) if e['type']==kind and
                 parse_code_entry(e['raw'])['payload']==original['payload'] and
                 parse_code_entry(e['raw'])['keywords']==original['keywords']]
        if len(matches)!=1:
            d['_MacPortRecoveredCageSkipped']='Source DXBC differs from the validated cage; using its translated programs'
            return d
        stages.append((stage,matches[0],kind,original['keywords']))
    entries=parse_blob(decompress_blob(d,0))
    for stage,index,kind,keywords in stages:
        pe=parse_code_entry(entries[index]['raw'])
        if entries[index]['type'] != kind or pe['keywords'] != keywords:
            raise ValueError('Cage program layout changed; do not use hard-coded entry indices')
        entries[index]['raw']=build_code_entry(kind,pe['stats'],pe['keywords'],
            converter.build_metal_payload(sources[stage].decode(),len(pe['payload'])),pe['trailing'])
    raw=serialize_blob(entries);comp=lz4.block.compress(raw,mode='high_compression',store_size=False)
    d.update(offsets=[[0]],compressedLengths=[[len(comp)]],decompressedLengths=[[len(raw)]],compressedBlob=comp)
    d['_MacPortRecoveredCageSource']=fingerprint
    d['_MacPortRecoveredCageEntries']=[s[1] for s in stages]
    return d

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--include-master',action='store_true');ap.add_argument('--only',nargs='*')
    ap.add_argument('--app',type=Path,default=ROOT/'ULTRAKILL.app')
    ap.add_argument('--source',type=Path)
    ap.add_argument('--inventory',type=Path)
    args=ap.parse_args()
    source_root=resolve_source(args.source)
    args.app=args.app.resolve()
    if not args.app.is_relative_to(ROOT) or args.app.suffix != '.app': ap.error('--app must be an app copy under mac/')
    report=[];cache=ROOT/'runtime/metal-shaders';cache.mkdir(exist_ok=True)
    inventory=args.inventory or args.app/'Contents/Resources/shader-inventory.json'
    if not inventory.exists():
        from scan_source_shaders import scan
        scan(source_root,inventory)
    shaders=json.loads(inventory.read_text())
    originals={r['path']:r['sha256'] for r in json.loads((ROOT/'reports/original-files.json').read_text())}
    files={d['file'] for d in shaders if 4 in d['platforms']}
    for file in sorted(files):
        if file=='Resources/unity default resources' or file.endswith('/Resources/unity default resources'): continue
        targets={d['path_id']:d['name'] for d in shaders if d['file']==file and 4 in d['platforms'] and (args.include_master or d['name']!='ULTRAKILL/Master') and (not args.only or d['name'] in args.only)}
        if not targets:continue
        relative=Path(file)
        if relative.parts[:2]==('ULTRAKILL','ULTRAKILL_Data'):relative=relative.relative_to('ULTRAKILL/ULTRAKILL_Data')
        source_file=source_root/'ULTRAKILL_Data'/relative
        source_hash=hashlib.sha256(source_file.read_bytes()).hexdigest()
        same_original=originals.get(str(Path('ULTRAKILL_Data')/relative))==source_hash
        env=UnityPy.load(str(source_file));changed=0
        target=args.app/'Contents/Resources/Data'/relative
        dst=UnityPy.load(str(target))
        destination_objects={(o.assets_file.name,o.path_id):o for o in dst.objects}
        for obj in env.objects:
            if obj.type.name != 'Shader' or obj.path_id not in targets:continue
            name=targets[obj.path_id]
            legacy_file=str(Path('ULTRAKILL/ULTRAKILL_Data')/relative)
            identity=legacy_file+str(obj.path_id) if same_original else str(relative)+str(obj.path_id)+source_hash
            key=hashlib.sha256(identity.encode()).hexdigest()[:20];cached=cache/(key+'.pkl')
            print('Converting',name,flush=True)
            try:
                if cached.exists():
                    d=upgrade_inline_samplers(upgrade_clip_distances(upgrade_recovered_cage(upgrade_transparent_sampler(upgrade_texture_dimensions(upgrade_color_outputs(upgrade_buffer_bindings(pickle.loads(cached.read_bytes()))),obj,name)),obj),obj));result='cached'
                    cached.write_bytes(pickle.dumps(d))
                else:
                    original_cache=ROOT/'reports/master-original.pkl' if same_original else cache/(key+'-original.pkl')
                    normalized_cache=ROOT/'reports/master-d3d-original.pkl' if same_original else cache/(key+'-d3d.pkl')
                    if name=='ULTRAKILL/Master' and normalized_cache.exists():
                        d=pickle.loads(normalized_cache.read_bytes())
                    else:
                        source=pickle.loads(original_cache.read_bytes()) if name=='ULTRAKILL/Master' and original_cache.exists() else obj.read_typetree()
                        d=normalize_segments(d3d_only(source))
                        if name=='ULTRAKILL/Master': normalized_cache.write_bytes(pickle.dumps(d))
                    result=converter.rewrap_shader_v2(d,name,False)
                    d['_MacPortBufferABI']=3
                    d['_MacPortColorABI']=2
                    d['_MacPortTextureDimensionsABI']=2
                    d=upgrade_inline_samplers(upgrade_clip_distances(upgrade_recovered_cage(upgrade_transparent_sampler(d),obj),obj))
                    cached.write_bytes(pickle.dumps(d))
                cached.write_bytes(pickle.dumps(d))
                destination_objects[(obj.assets_file.name,obj.path_id)].save_typetree(d);changed+=1
                report.append(dict(file=file,name=name,status='converted',result=result))
                print(result,flush=True)
            except Exception as exc:
                report.append(dict(file=file,name=name,status='failed',error=str(exc)));traceback.print_exc()
        if changed:
            assert target.is_relative_to(args.app)
            pending=target.with_name(target.name+'.pending')
            print('Saving',relative,changed,'shaders',flush=True)
            pending.write_bytes(dst.file.save());pending.replace(target)
        (args.app/'Contents/Resources/metal-conversion.json').write_text(json.dumps(report,indent=2))
    print('Converted',sum(r['status']=='converted' for r in report),'failed',sum(r['status']=='failed' for r in report),flush=True)
    return int(any(r['status']=='failed' for r in report))

if __name__=='__main__':raise SystemExit(main())
