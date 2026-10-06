"""Prepare windowless GPU fixtures for fixed samplers and real portal programs."""
from pathlib import Path
from inline_samplers import repair
from portal_shader_check import namespace

ROOT=Path(__file__).resolve().parents[1]
OUT=ROOT/'reports/portal-shader-investigation'
source='#include <metal_stdlib>\n#include <metal_texture>\nusing namespace metal;\n'
for state in (0,1,84):
    program='''using namespace metal;
float4 read_tex(sampler sampler0 [[sampler(0)]], texture2d<float> tex [[texture(0)]], float4 fixture) {
    return tex.sample(sampler0,fixture.xy,level(fixture.z));
}
'''
    source+=namespace(repair(program,{0:state}),f'state{state}')
    source+=f'''kernel void state_{state}(constant float4* fixtures [[buffer(0)]],device float4* results [[buffer(1)]],
texture2d<float> tex [[texture(0)]],sampler stale [[sampler(0)]],uint id [[thread_position_in_grid]]) {{
results[id]=state{state}::read_tex(tex,fixtures[id]);
}}
'''
    source+=namespace(program,f'reference{state}')
    source+=f'''kernel void reference_{state}(constant float4* fixtures [[buffer(0)]],device float4* results [[buffer(1)]],
texture2d<float> tex [[texture(0)]],sampler bound [[sampler(0)]],uint id [[thread_position_in_grid]]) {{
results[id]=reference{state}::read_tex(bound,tex,fixtures[id]);
}}
'''

for kind,filename,bindings in [('portal','Unlit_PortalShader-3.metal',{1:0}),('composite','Unlit_CopyCompositePortal-3.metal',{0:84})]:
    program=(OUT/'inline-sampler-sources'/filename).read_text()
    # The composite fixtures all stay inside the portal and use opaque texels.
    # Replace the unreachable fragment-only discard for compute compilation.
    if kind=='composite':program=program.replace('discard_fragment();','return Mtl_FragmentOut{};')
    for fixed in (False,True):
        label=f'{kind}_{"after" if fixed else "before"}'
        source+=namespace(repair(program,bindings) if fixed else program,label+'_program')
        if kind=='portal':
            args='*reinterpret_cast<constant FGlobals_Type*>(globals),bound0,'+('' if fixed else 'bound1,')+'tex,tex,input'
            inputs='input.TEXCOORD0=float4(fixtures[id].xy,0,1); input.TEXCOORD1=input.TEXCOORD0;'
        else:
            args='*reinterpret_cast<constant FGlobals_Type*>(globals),'+('' if fixed else 'bound0,')+'tex,input'
            inputs='input.TEXCOORD0=fixtures[id].xy;'
        source+=f'''kernel void {label}(constant float4* fixtures [[buffer(0)]],device float4* results [[buffer(1)]],
constant float4* globals [[buffer(2)]],texture2d<float> tex [[texture(0)]],
sampler bound0 [[sampler(0)]],sampler bound1 [[sampler(1)]],uint id [[thread_position_in_grid]]) {{
using namespace {label}_program; Mtl_FragmentIn input; {inputs}
results[id]=xlatMtlMain({args}).SV_Target0;
}}
'''
(OUT/'inline-sampler-regression.metal').write_text(source)
