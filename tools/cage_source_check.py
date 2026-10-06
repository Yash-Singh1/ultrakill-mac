#!/usr/bin/env python3
"""Prepare original/rebuilt shader math for an offscreen Metal comparison.

This validates recovered DXBC after translation, not execution on a Windows GPU.
Entry-point attributes are removed only in these test copies so compute kernels
can inspect every varying and render-target value without rasterization noise.
"""
from pathlib import Path
import hashlib
import json
import re

ROOT = Path(__file__).resolve().parents[1]


def namespace(path, name):
    source = path.read_text()
    source = re.sub(r'^#include[^\n]*\n', '', source, flags=re.M)
    source = re.sub(r'^constant .*rp_output_remap[^\n]*\n', '', source, flags=re.M)
    source = re.sub(r'\[\[.*?\]\]', '', source, flags=re.S)
    source = re.sub(r'\b(vertex|fragment) (?=Mtl_)', '', source)
    return 'namespace ' + name + ' {\n' + source + '\n}\n'


def prepare():
    build = json.loads((ROOT / 'reports/cage-source-build.json').read_text())
    assert build['source_sha256'] == hashlib.sha256((ROOT / 'shader-source/Cage.hlsl').read_bytes()).hexdigest(), 'Rebuild the current HLSL before comparison'
    for stage, metadata in build['stages'].items():
        assert metadata['dxbc_sha256'] == hashlib.sha256((ROOT / ('reports/cage-reconstructed-' + stage + '.dxbc')).read_bytes()).hexdigest()
    source = '#include <metal_stdlib>\n#include <metal_texture>\nusing namespace metal;\n'
    source += 'constexpr sampler fixture_repeat(filter::nearest,address::repeat);\n'
    for stage in ['vertex', 'fragment']:
        original = ROOT / ('reports/cage-vertex.metal' if stage == 'vertex' else 'reports/cage-fragment-9483.metal')
        rebuilt = ROOT / ('reports/cage-reconstructed-' + stage + '.metal')
        for label, path in [('original', original), ('rebuilt', rebuilt)]:
            name = label + '_' + stage
            source += namespace(path, name)
            source += f'kernel void {name}_check(constant float4* fixtures [[buffer(0)]], device float4* results [[buffer(1)]], texture2d<float> texture [[texture(0)]], uint id [[thread_position_in_grid]]) {{\n'
            source += f'using namespace {name};\nconstant float4* row = fixtures + id * 512;\n'
            if stage == 'vertex':
                source += '''Mtl_VertexIn input;
input.POSITION0 = row[256]; input.COLOR0 = row[257];
input.TEXCOORD0 = row[258].xy; input.NORMAL0 = row[259].xyz;
'''
                cb_types = ['VGlobals_Type',
                            'UnityPerCamera_Type', 'UnityLighting_Type', 'UnityPerDraw_Type',
                            'UnityPerFrame_Type', 'UnityFog_Type', 'StandardProperties_Type']
                args = [f'*reinterpret_cast<constant {ty}*>(row+{offset})'
                        for ty, offset in zip(cb_types, [0, 32, 64, 128, 160, 192, 224])]
                source += 'auto output = xlatMtlMain(' + ','.join(args + ['input']) + ');\n'
                for i, field in enumerate(['mtl_Position', 'COLOR0', 'COLOR2', 'TEXCOORD0', 'TEXCOORD1', 'TEXCOORD2']):
                    value = 'float4(output.TEXCOORD2,0)' if field == 'TEXCOORD2' else 'output.' + field
                    source += f'results[id*6+{i}] = {value};\n'
            else:
                source += '''Mtl_FragmentIn input;
input.COLOR0=row[260]; input.COLOR2=row[261];
input.TEXCOORD0=row[262]; input.TEXCOORD1=row[263];
'''
                args = ['*reinterpret_cast<constant FGlobals_Type*>(row)', 'fixture_repeat', 'texture', 'input'] if label == 'original' else ['*reinterpret_cast<constant FGlobals_Type*>(row)', 'texture', 'input']
                source += 'auto output=xlatMtlMain(' + ','.join(args) + ');\n'
                source += 'results[id*2]=output.SV_Target0; results[id*2+1]=output.SV_Target1;\n'
            source += '}\n'
    output = ROOT / 'reports/cage-source-comparison.metal'
    output.write_text(source)
    print(output)


if __name__ == '__main__':
    prepare()
