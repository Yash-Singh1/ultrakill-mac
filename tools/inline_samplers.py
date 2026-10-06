"""Preserve Unity's serialized fixed sampler states in generated Metal.

Unity 2022.3.29f1 InlineSamplerType packs filter in bits 0..1 and U/V/W
wrap modes in bits 2..3, 4..5, 6..7. Verified in gles::SetTexture and
CreateSamplerState in the matching development player. See the disassembly
and enum table records in reports/portal-shader-investigation/.
"""
import re

ABI = 1
ARGUMENT = re.compile(r'\bsampler\s+(\w+)\s*\[\[\s*sampler\s*\(\s*(\d+)\s*\)\s*\]\]\s*,')
CONSTANT = re.compile(r'constexpr\s+sampler\s+(\w+)\s*\([^;]*\);')


def descriptor(state):
    # Fail closed for comparison/aniso states until those paths are verified.
    if state < 0 or state > 255 or state & 3 == 3:
        raise ValueError(f'Unverified inline sampler encoding {state}')
    filters = ('nearest', 'linear', 'linear')
    mips = ('nearest', 'nearest', 'linear')
    wraps = ('repeat', 'clamp_to_edge', 'mirrored_repeat', 'mirror_clamp_to_edge')
    axes = ('s_address', 't_address', 'r_address')
    parts = ['coord::normalized', 'filter::' + filters[state & 3],
             'mip_filter::' + mips[state & 3]]
    parts += [axis + '::' + wraps[state >> shift & 3]
              for axis, shift in zip(axes, (2, 4, 6))]
    return ', '.join(parts)


def fixed_bindings(prog, parameters):
    states = {}
    records = [(s['bindPoint'], s['sampler'])
               for s in prog.get('m_CommonParameters', {}).get('m_Samplers', [])]
    # Parameter-blob resource records store bindPoint before sampler state,
    # unlike the parsed common table. PostProcessV2 records (0,84),(1,0)
    # correspond to its clamp screen sampler and repeat dither sampler.
    records += [(r['values'][0], r['values'][1])
                for r in parameters.get('resources', []) if r['kind'] == 4]
    for slot, state in records:
        if slot in states and states[slot] != state:
            raise ValueError(f'Conflicting inline sampler slot {slot}')
        descriptor(state)
        states[slot] = state
    return states


def repair(source, states):
    declarations = {}
    for match in ARGUMENT.finditer(source):
        if int(match[2]) in states:
            declarations[match[1]] = descriptor(states[int(match[2])])
    # Previous Master repair already removed the argument. Preserve the name
    # and refresh its descriptor, including the authored nearest mip filter.
    for match in CONSTANT.finditer(source):
        numeric = re.fullmatch(r'sampler(\d+)', match[1])
        if numeric and int(numeric[1]) in states:
            declarations[match[1]] = descriptor(states[int(numeric[1])])
    if not declarations:
        return source
    source = ARGUMENT.sub(lambda m: '' if m[1] in declarations else m[0], source)
    source = CONSTANT.sub(lambda m: '' if m[1] in declarations else m[0], source)
    marker = 'using namespace metal;'
    if source.count(marker) != 1:
        raise ValueError('Unexpected Metal namespace declaration')
    constants = '\n'.join(f'constexpr sampler {name}({desc});'
                          for name, desc in sorted(declarations.items()))
    # Strip only blank lines introduced by replacing existing constants.
    source = re.sub(r'(using namespace metal;)\n(?:\s*\n)+', r'\1\n', source)
    return source.replace(marker, marker + '\n' + constants, 1)
