#!/usr/bin/env python3
"""Prepare real Master programs for a windowless clip/sampler regression test."""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
REPORTS = ROOT / 'reports/portal-shader-investigation'


def namespace(source, name):
    source = re.sub(r'^#include[^\n]*\n', '', source, flags=re.M)
    source = re.sub(r'^constant .*rp_output_remap[^\n]*\n', '', source, flags=re.M)
    source = re.sub(r'\[\[.*?\]\]', '', source, flags=re.S)
    source = re.sub(r'\b(vertex|fragment) (?=Mtl_)', '', source)
    return 'namespace ' + name + ' {\n' + source + '\n}\n'


def prepare():
    source = '#include <metal_stdlib>\n#include <metal_texture>\nusing namespace metal;\n'
    # Poison unassigned outputs in the diagnostic copy. Undefined stack/register
    # values are otherwise permitted to look correct by chance.
    for label, filename in [('before', 'master-2022.metal'), ('after', 'master-2022-fixed.metal')]:
        program = (REPORTS / filename).read_text().replace('Mtl_VertexOut output;',
            'Mtl_VertexOut output; for(int i=0;i<5;i++) output.mtl_ClipDistance[i]=-12345.0;')
        source += namespace(program, label)
        source += f'''kernel void {label}_clip(constant float4* fixtures [[buffer(0)]], device float* results [[buffer(1)]], uint id [[thread_position_in_grid]]) {{
    using namespace {label};
    constant float4* row=fixtures+id*64;
    Mtl_VertexIn input;
    input.POSITION0=row[40]; input.COLOR0=float4(1); input.TEXCOORD0=float2(.5); input.NORMAL0=float3(0,0,1);
    auto output=xlatMtlMain(*reinterpret_cast<constant VGlobals_Type*>(row),
        *reinterpret_cast<constant UnityPerDraw_Type*>(row+12),
        *reinterpret_cast<constant UnityPerFrame_Type*>(row+24),
        *reinterpret_cast<constant StandardProperties_Type*>(row+48),input);
    for(int i=0;i<5;i++) results[id*5+i]=output.mtl_ClipDistance[i];
}}
'''
    # Compute execution inspects the actual fragment arithmetic and explicit LOD
    # without pixel-center/rasterization differences. The sampler is deliberately
    # changed between runs to model the stale state from an earlier draw.
    for label, filename in [('before_sampler', 'master-frag-9878.metal'), ('after_sampler', 'master-frag-9878-fixed.metal')]:
        source += namespace((REPORTS / filename).read_text(), label + '_program')
        args = 'bound_sampler,texture,input' if label.startswith('before') else 'texture,input'
        source += f'''kernel void {label}(constant float4* fixtures [[buffer(0)]], device float4* results [[buffer(1)]],
    texture2d<float> texture [[texture(0)]], sampler bound_sampler [[sampler(0)]], uint id [[thread_position_in_grid]]) {{
    using namespace {label}_program; Mtl_FragmentIn input;
    float4 f=fixtures[id]; input.COLOR0=float4(1); input.TEXCOORD0=float4(f.xy*f.z,0,0);
    input.TEXCOORD1=float4(0,0,0,f.z); input.TEXCOORD2=float3(0,0,1);
    results[id]=xlatMtlMain({args}).SV_Target0;
}}
'''
    (REPORTS / 'regression.metal').write_text(source)


if __name__ == '__main__':
    prepare()
