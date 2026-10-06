"""Reject fixed Unity sampler slots emitted as runtime-bound Metal arguments."""
from pathlib import Path
import argparse, gc, json, struct
from convert_metal import UnityPy, parse_blob, decompress_blob, parse_code_entry, parse_params, converter
from inline_samplers import ARGUMENT, fixed_bindings, repair

ROOT=Path(__file__).resolve().parents[1]

def audit(app):
    rows=json.loads((app/'Contents/Resources/shader-inventory.json').read_text())
    checked=0;failures=[];states=set()
    for filename in sorted({r['file'] for r in rows}):
        if filename.endswith('Resources/unity default resources'):continue
        relative=Path(filename)
        if relative.parts[:2]==('ULTRAKILL','ULTRAKILL_Data'):
            relative=relative.relative_to('ULTRAKILL/ULTRAKILL_Data')
        env=UnityPy.load(str(app/'Contents/Resources/Data'/relative))
        for obj in env.objects:
            if obj.type.name!='Shader':continue
            d=obj.read_typetree()
            if 14 not in d.get('platforms',[]):continue
            entries=parse_blob(decompress_blob(d,d['platforms'].index(14)))
            for ss in d['m_ParsedForm'].get('m_SubShaders',[]):
                for ps in ss.get('m_Passes',[]):
                    for key in ('progVertex','progFragment'):
                        prog=ps.get(key,{})
                        for sp,t,i in converter.flat_subs(prog):
                            bindings=fixed_bindings(prog,parse_params(entries[prog['m_ParameterBlobIndices'][t][i]]['raw']))
                            if not bindings:continue
                            checked+=1;states.update(bindings.values())
                            idx=sp['m_BlobIndex'];p=parse_code_entry(entries[idx]['raw'])['payload'];offset=struct.unpack_from('<I',p,12)[0]
                            source=p[offset:].split(b'\0',1)[0].decode()
                            if repair(source,bindings)!=source:
                                failures.append(dict(file=str(relative),shader=d['m_ParsedForm']['m_Name'],entry=idx,states=bindings))
        del env;gc.collect()
    return dict(program_references_checked=checked,encodings=sorted(states),failures=failures,passed=not failures,
                scope='Fixed sampler binding and descriptor validation, not full shader parity.')

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--app',type=Path,default=ROOT/'ULTRAKILL.app');p.add_argument('--report',type=Path,default=ROOT/'reports/inline-sampler-audit.json');a=p.parse_args()
    result=audit(a.app.resolve());a.report.write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps({k:v for k,v in result.items() if k!='failures'},indent=2));print('Failures:',len(result['failures']))
    raise SystemExit(not result['passed'])
