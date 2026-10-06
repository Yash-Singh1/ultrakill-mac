#!/usr/bin/env python3
"""Read two original Windows cage programs; write reference files under mac/."""
from pathlib import Path
import hashlib
import json
import subprocess

from convert_metal import ROOT, UnityPy, d3d_only, normalize_segments, parse_blob, decompress_blob, parse_code_entry
from dxbc2msl import parse_container, parse_dcls
from inject_custom_metal import parse_isgn


def extract():
    source = ROOT.parent / 'ULTRAKILL/ULTRAKILL_Data/StreamingAssets/aa/StandaloneWindows64/assets_assets_assets/shaders.bundle'
    env = UnityPy.load(str(source))
    obj = next(o for o in env.objects if o.path_id == 6390567462476448578 and o.type.name == 'Shader')
    data = normalize_segments(d3d_only(obj.read_typetree()))
    entries = parse_blob(decompress_blob(data, 0))
    report = {'bundle_sha256': hashlib.sha256(source.read_bytes()).hexdigest(),
              'shader': data['m_ParsedForm']['m_Name'], 'stages': {}}
    compiler = ROOT / 'third_party/vkd3d-2.0/vkd3d-compiler'
    for stage, index in [('vertex', 5083), ('fragment', 9483)]:
        entry = parse_code_entry(entries[index]['raw'])
        payload = entry['payload']
        original = payload[payload.index(b'DXBC'):]
        prefix = ROOT / ('reports/cage-original-' + stage)
        prefix.with_suffix('.dxbc').write_bytes(original)
        subprocess.run([str(compiler), '-x', 'dxbc-tpf', '-b', 'd3d-asm',
                        '-o', str(prefix.with_suffix('.asm')), str(prefix.with_suffix('.dxbc'))], check=True)
        rebuilt = (ROOT / ('reports/cage-reconstructed-' + stage + '.dxbc')).read_bytes()
        old_chunks = parse_container(original)
        new_chunks = parse_container(rebuilt)
        code = lambda chunks: chunks.get(b'SHDR', chunks.get(b'SHEX'))
        signatures = lambda chunks: {tag.decode(): [{'semantic': name, 'index': index, 'register': register}
                                      for (name, index), register in parse_isgn(chunks[tag]).items()]
                                      for tag in [b'ISGN', b'OSGN'] if tag in chunks}
        old_signatures, new_signatures = signatures(old_chunks), signatures(new_chunks)
        report['stages'][stage] = {
            'blob_index': index, 'keywords': entry['keywords'],
            'original_sha256': hashlib.sha256(original).hexdigest(),
            'rebuilt_sha256': hashlib.sha256(rebuilt).hexdigest(),
            'byte_identical': original == rebuilt,
            'program_identical': code(old_chunks) == code(new_chunks),
            'original_dxbc_bytes': len(original), 'rebuilt_dxbc_bytes': len(rebuilt),
            'original_code_bytes': len(code(old_chunks)), 'rebuilt_code_bytes': len(code(new_chunks)),
            'original_declarations': parse_dcls(code(old_chunks)),
            'rebuilt_declarations': parse_dcls(code(new_chunks)),
            'original_signatures': old_signatures, 'rebuilt_signatures': new_signatures,
        }
    (ROOT / 'reports/cage-windows-comparison.json').write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps({stage: {'byte_identical': value['byte_identical'], 'keywords': value['keywords']}
                      for stage, value in report['stages'].items()}))


if __name__ == '__main__':
    extract()
