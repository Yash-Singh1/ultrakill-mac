#!/usr/bin/env python3
"""Test DXBC-to-Metal conversion without changing either game build."""
from pathlib import Path
import sys, pickle, json, copy, traceback

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'third_party/casualties-port/tools/rewrap'))
import UnityPy
import inject_custom_metal2 as converter
from metal_support import parse_params, enrich_names
converter.parse_params_entry = parse_params
converter.enrich_names = enrich_names

def compile_and_save(msl, tag):
    import re
    filename = re.sub(r'[^a-zA-Z0-9_-]', '_', tag) + '.metal'
    target = ROOT / 'reports/metal-sources'
    target.mkdir(exist_ok=True)
    (target / filename).write_text(msl)
    converter.compile_check_original(msl, tag)

converter.compile_check_original = converter.compile_check
converter.compile_check = compile_and_save

def d3d_only(d):
    # UnityPy exposes this byte array as millions of Python integers. Keep the
    # immutable bytes shared rather than deep-copying each element.
    d = dict(d)
    d['compressedBlob'] = bytes(d['compressedBlob'])
    d = copy.deepcopy(d)
    idx = d['platforms'].index(4)
    for key in ('platforms', 'offsets', 'compressedLengths', 'decompressedLengths'):
        d[key] = [d[key][idx]]
    for ss in d['m_ParsedForm']['m_SubShaders']:
        for ps in ss['m_Passes']:
            for key, prog in ps.items():
                if not key.startswith('prog') or not isinstance(prog, dict): continue
                for t, tier in enumerate(prog.get('m_PlayerSubPrograms', [])):
                    keep = [i for i, sp in enumerate(tier) if sp['m_GpuProgramType'] in (15,16,17,18)]
                    prog['m_PlayerSubPrograms'][t] = [tier[i] for i in keep]
                    for field in ('m_ParameterBlobIndices',):
                        if prog.get(field): prog[field][t] = [prog[field][t][i] for i in keep]
    return d

def main():
    names = sys.argv[1:] or ['Hidden/ULTRAKILL/ULTRAKILL-Stationary', 'ULTRAKILL/PostProcessV2']
    cache = ROOT / 'reports/shader-pilot-cache.pkl'
    if cache.exists():
        data = pickle.loads(cache.read_bytes())
    else:
        env = UnityPy.load(str(ROOT.parent / 'ULTRAKILL/ULTRAKILL_Data/StreamingAssets/aa/StandaloneWindows64/assets_assets_assets/shaders.bundle'))
        data = {}
        targets = {x['path_id']: x['name'] for x in json.loads((ROOT/'reports/shaders.json').read_text()) if x['name'] in names}
        for obj in env.objects:
            if obj.path_id in targets: data[targets[obj.path_id]] = obj.read_typetree()
        cache.write_bytes(pickle.dumps(data))
    for name in names:
        print('Converting', name, flush=True)
        try:
            d = d3d_only(data[name])
            result = converter.rewrap_shader_v2(d, name, True)
            (ROOT / 'reports' / (name.rsplit('/',1)[-1]+'-metal.pkl')).write_bytes(pickle.dumps(d))
            print(result, flush=True)
        except Exception:
            traceback.print_exc()

if __name__ == '__main__': main()
